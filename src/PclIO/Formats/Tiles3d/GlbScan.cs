using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using PclIO.Internal;

namespace PclIO.Formats.Tiles3d;

/// <summary>Represents a point cloud scan stored in a glTF 2.0 binary (.glb) file.</summary>
public sealed class GlbScan : PclScan
{
    private const int GlbHeaderSize = 12;
    private const int MaxGlbBytes = 512 * 1024 * 1024;
    private const int MaxPointsLimit = 500_000_000;

    private readonly string _filePath;
    private readonly int _binChunkOffset;
    private readonly int _binChunkLength;

    // Attribute accessor descriptors
    private readonly AccessorDesc _posDesc;
    private readonly AccessorDesc? _colDesc;
    private readonly AccessorDesc? _normDesc;
    private readonly AccessorDesc? _intDesc;

    /// <inheritdoc />
    public override string Id { get; }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override long PointCount => _posDesc.Count;

    /// <inheritdoc />
    public override bool HasColors => _colDesc != null;

    /// <inheritdoc />
    public override bool HasNormals => _normDesc != null;

    /// <inheritdoc />
    public override bool HasIntensities => _intDesc != null;

    internal sealed class AccessorDesc
    {
        public int Offset { get; init; }
        public int Stride { get; init; }
        public int Count { get; init; }
        public int ComponentType { get; init; }
        public string Type { get; init; } = "SCALAR";
        public bool Normalized { get; init; }
    }

    private GlbScan(
        string filePath,
        string id,
        string name,
        PclTransform transform,
        PclBounds bounds,
        int binChunkOffset,
        int binChunkLength,
        AccessorDesc posDesc,
        AccessorDesc? colDesc,
        AccessorDesc? normDesc,
        AccessorDesc? intDesc)
    {
        _filePath = filePath;
        Id = id;
        Name = name;
        Transform = transform;
        Bounds = bounds;
        _binChunkOffset = binChunkOffset;
        _binChunkLength = binChunkLength;

        _posDesc = posDesc;
        _colDesc = colDesc;
        _normDesc = normDesc;
        _intDesc = intDesc;

        _properties["Format"] = "glTF 2.0 Binary Point Cloud (.glb)";
        _properties["PointsLength"] = posDesc.Count.ToString(CultureInfo.InvariantCulture);
        if (colDesc != null)
            _properties["HasColors"] = "True";
        if (normDesc != null)
            _properties["HasNormals"] = "True";
        if (intDesc != null)
            _properties["HasIntensity"] = "True";
    }

    /// <summary>Parses a .glb file and creates scans for each mesh primitive with mode 0 (POINTS).</summary>
    public static List<GlbScan> FromFile(string filePath, PclTransform? parentTransform = null, string? scanIdPrefix = null)
    {
        using var stream = File.OpenRead(filePath);
        long fileLength = stream.Length;

        if (fileLength < GlbHeaderSize)
            throw FormatError.Truncated($"glTF header of at least {GlbHeaderSize} bytes");

        Span<byte> header = stackalloc byte[GlbHeaderSize];
        stream.ReadExactly(header);

        if (header[0] != 0x67 || header[1] != 0x6C || header[2] != 0x54 || header[3] != 0x46) // "glTF"
            throw FormatError.Corrupted($"Invalid glTF magic signature in '{filePath}'.");

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
        if (version != 2)
            throw FormatError.Unsupported($"Unsupported glTF version {version}; expected 2.");

        uint totalLength = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
        if (fileLength < totalLength)
            throw FormatError.Truncated($"expected glTF length {totalLength} bytes but file has {fileLength} bytes");

        if (totalLength > MaxGlbBytes)
            throw FormatError.Unsupported($"glTF size ({totalLength} bytes) exceeds safety limit of {MaxGlbBytes} bytes.");

        // Read Chunk 0 (JSON)
        Span<byte> chunk0Header = stackalloc byte[8];
        stream.ReadExactly(chunk0Header);
        uint jsonChunkLen = BinaryPrimitives.ReadUInt32LittleEndian(chunk0Header[0..4]);
        uint jsonChunkType = BinaryPrimitives.ReadUInt32LittleEndian(chunk0Header[4..8]);

        if (jsonChunkType != 0x4E4F534A) // "JSON"
            throw FormatError.Corrupted("First glTF chunk must be of type JSON.");

        if (jsonChunkLen == 0 || jsonChunkLen > totalLength)
            throw FormatError.Corrupted($"Invalid JSON chunk length: {jsonChunkLen}");

        byte[] jsonBytes = new byte[jsonChunkLen];
        stream.ReadExactly(jsonBytes);

        // Read Chunk 1 (BIN) if present
        int binChunkOffset = 12 + 8 + (int)jsonChunkLen;
        int binChunkLen = 0;

        if (binChunkOffset + 8 <= totalLength)
        {
            stream.Position = binChunkOffset;
            Span<byte> chunk1Header = stackalloc byte[8];
            int read = stream.ReadAtLeast(chunk1Header, 8, throwOnEndOfStream: false);
            if (read == 8)
            {
                uint binLen = BinaryPrimitives.ReadUInt32LittleEndian(chunk1Header[0..4]);
                uint binType = BinaryPrimitives.ReadUInt32LittleEndian(chunk1Header[4..8]);
                if (binType == 0x004E4942) // "BIN\0"
                {
                    binChunkOffset += 8;
                    binChunkLen = (int)binLen;
                }
            }
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonBytes);
        }
        catch (JsonException ex)
        {
            throw FormatError.Corrupted($"Invalid JSON in glTF '{filePath}': {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("meshes", out var meshesElem) || meshesElem.ValueKind != JsonValueKind.Array)
                return [];

            if (!root.TryGetProperty("accessors", out var accessorsElem) || accessorsElem.ValueKind != JsonValueKind.Array)
                throw FormatError.Corrupted("glTF is missing 'accessors' array.");

            if (!root.TryGetProperty("bufferViews", out var bufferViewsElem) || bufferViewsElem.ValueKind != JsonValueKind.Array)
                throw FormatError.Corrupted("glTF is missing 'bufferViews' array.");

            var accessors = accessorsElem.EnumerateArray().ToArray();
            var bufferViews = bufferViewsElem.EnumerateArray().ToArray();

            // Build node transform mapping: meshIndex -> PclTransform
            var meshTransforms = new Dictionary<int, PclTransform>();
            if (root.TryGetProperty("nodes", out var nodesElem) && nodesElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in nodesElem.EnumerateArray())
                {
                    if (node.TryGetProperty("mesh", out var meshIdxElem) && meshIdxElem.TryGetInt32(out int mIdx))
                    {
                        var t = ParseNodeTransform(node);
                        meshTransforms[mIdx] = t;
                    }
                }
            }

            var scans = new List<GlbScan>();
            int meshIndex = 0;

            foreach (var mesh in meshesElem.EnumerateArray())
            {
                string meshName = mesh.TryGetProperty("name", out var nameElem) ? (nameElem.GetString() ?? $"mesh_{meshIndex}") : $"mesh_{meshIndex}";

                if (mesh.TryGetProperty("primitives", out var primsElem) && primsElem.ValueKind == JsonValueKind.Array)
                {
                    int primIndex = 0;
                    foreach (var prim in primsElem.EnumerateArray())
                    {
                        // Primitive mode: 0 = POINTS. Default is 4 (TRIANGLES).
                        int mode = prim.TryGetProperty("mode", out var modeElem) ? modeElem.GetInt32() : 4;
                        if (mode != 0)
                        {
                            primIndex++;
                            continue;
                        }

                        if (!prim.TryGetProperty("attributes", out var attrs) || attrs.ValueKind != JsonValueKind.Object)
                        {
                            primIndex++;
                            continue;
                        }

                        if (!attrs.TryGetProperty("POSITION", out var posAccessorElem) || !posAccessorElem.TryGetInt32(out int posAccIdx) || posAccIdx < 0 || posAccIdx >= accessors.Length)
                            throw FormatError.Corrupted("Point primitive is missing valid POSITION accessor.");

                        AccessorDesc posDesc = ParseAccessor(accessors[posAccIdx], bufferViews, binChunkLen);
                        if (posDesc.ComponentType != 5126 || posDesc.Type != "VEC3")
                            throw FormatError.Unsupported("glTF POSITION accessor must have componentType FLOAT (5126) and type VEC3.");

                        if (posDesc.Count > MaxPointsLimit)
                            throw FormatError.Corrupted($"Point count ({posDesc.Count}) exceeds maximum limit.");

                        // Bounds from accessor min/max
                        PclBounds bounds = PclBounds.Empty;
                        var posAcc = accessors[posAccIdx];
                        if (posAcc.TryGetProperty("min", out var minElem) && minElem.ValueKind == JsonValueKind.Array && minElem.GetArrayLength() == 3 &&
                            posAcc.TryGetProperty("max", out var maxElem) && maxElem.ValueKind == JsonValueKind.Array && maxElem.GetArrayLength() == 3)
                        {
                            double[] min = minElem.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                            double[] max = maxElem.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                            bounds = new PclBounds(new PclVector3(min[0], min[1], min[2]), new PclVector3(max[0], max[1], max[2]));
                        }

                        // Optional COLOR_0
                        AccessorDesc? colDesc = null;
                        if (attrs.TryGetProperty("COLOR_0", out var colElem) && colElem.TryGetInt32(out int colAccIdx) && colAccIdx >= 0 && colAccIdx < accessors.Length)
                        {
                            colDesc = ParseAccessor(accessors[colAccIdx], bufferViews, binChunkLen);
                        }

                        // Optional NORMAL
                        AccessorDesc? normDesc = null;
                        if (attrs.TryGetProperty("NORMAL", out var normElem) && normElem.TryGetInt32(out int normAccIdx) && normAccIdx >= 0 && normAccIdx < accessors.Length)
                        {
                            normDesc = ParseAccessor(accessors[normAccIdx], bufferViews, binChunkLen);
                        }

                        // Optional INTENSITY
                        AccessorDesc? intDesc = null;
                        if ((attrs.TryGetProperty("_INTENSITY", out var intElem) || attrs.TryGetProperty("INTENSITY", out intElem)) &&
                            intElem.TryGetInt32(out int intAccIdx) && intAccIdx >= 0 && intAccIdx < accessors.Length)
                        {
                            intDesc = ParseAccessor(accessors[intAccIdx], bufferViews, binChunkLen);
                        }

                        // Transform
                        PclTransform scanTransform = PclTransform.Identity;
                        if (meshTransforms.TryGetValue(meshIndex, out var nodeTransform))
                            scanTransform = nodeTransform;

                        if (parentTransform != null)
                        {
                            scanTransform = CombineTransforms(parentTransform, scanTransform);
                        }

                        string scanId = scanIdPrefix != null ? $"{scanIdPrefix}_{scans.Count}" : scans.Count.ToString(CultureInfo.InvariantCulture);
                        string scanName = primsElem.GetArrayLength() > 1 ? $"{meshName}_primitive_{primIndex}" : meshName;

                        scans.Add(new GlbScan(
                            filePath,
                            scanId,
                            scanName,
                            scanTransform,
                            bounds,
                            binChunkOffset,
                            binChunkLen,
                            posDesc,
                            colDesc,
                            normDesc,
                            intDesc));

                        primIndex++;
                    }
                }
                meshIndex++;
            }

            return scans;
        }
    }

    private static AccessorDesc ParseAccessor(JsonElement accessorElem, JsonElement[] bufferViews, int binChunkLen)
    {
        if (!accessorElem.TryGetProperty("bufferView", out var bvElem) || !bvElem.TryGetInt32(out int bvIdx) || bvIdx < 0 || bvIdx >= bufferViews.Length)
            throw FormatError.Corrupted("Accessor bufferView index out of range.");

        var bv = bufferViews[bvIdx];
        int bvOffset = bv.TryGetProperty("byteOffset", out var bvoElem) ? bvoElem.GetInt32() : 0;
        int bvLength = bv.TryGetProperty("byteLength", out var bvlElem) ? bvlElem.GetInt32() : 0;
        int bvStride = bv.TryGetProperty("byteStride", out var bvsElem) ? bvsElem.GetInt32() : 0;

        int accOffset = accessorElem.TryGetProperty("byteOffset", out var aoElem) ? aoElem.GetInt32() : 0;
        int compType = accessorElem.TryGetProperty("componentType", out var ctElem) ? ctElem.GetInt32() : 0;
        int count = accessorElem.TryGetProperty("count", out var cElem) ? cElem.GetInt32() : 0;
        string type = accessorElem.TryGetProperty("type", out var tElem) ? (tElem.GetString() ?? "SCALAR") : "SCALAR";
        bool normalized = accessorElem.TryGetProperty("normalized", out var nElem) && nElem.GetBoolean();

        int elementSize = GetTypeComponentCount(type) * GetComponentSize(compType);
        int stride = bvStride > 0 ? bvStride : elementSize;

        int totalOffset = bvOffset + accOffset;
        if (count > 0 && totalOffset + (count - 1) * (long)stride + elementSize > binChunkLen)
            throw FormatError.Corrupted("Accessor data range extends beyond glTF binary chunk.");

        return new AccessorDesc
        {
            Offset = totalOffset,
            Stride = stride,
            Count = count,
            ComponentType = compType,
            Type = type,
            Normalized = normalized
        };
    }

    private static int GetComponentSize(int componentType) => componentType switch
    {
        5120 or 5121 => 1, // BYTE, UNSIGNED_BYTE
        5122 or 5123 => 2, // SHORT, UNSIGNED_SHORT
        5125 or 5126 => 4, // UNSIGNED_INT, FLOAT
        _ => 1
    };

    private static int GetTypeComponentCount(string type) => type switch
    {
        "SCALAR" => 1,
        "VEC2" => 2,
        "VEC3" => 3,
        "VEC4" => 4,
        "MAT2" => 4,
        "MAT3" => 9,
        "MAT4" => 16,
        _ => 1
    };

    private static PclTransform ParseNodeTransform(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var matElem) && matElem.ValueKind == JsonValueKind.Array && matElem.GetArrayLength() == 16)
        {
            double[] colMajor = new double[16];
            int i = 0;
            foreach (var item in matElem.EnumerateArray()) colMajor[i++] = item.GetDouble();
            double[] rowMajor = Tiles3dDocument.ColumnMajorToRowMajor(colMajor);
            return new PclTransform { Matrix4x4 = rowMajor };
        }

        PclVector3 translation = PclVector3.Zero;
        PclVector3 scale = PclVector3.One;

        if (node.TryGetProperty("translation", out var tElem) && tElem.ValueKind == JsonValueKind.Array && tElem.GetArrayLength() == 3)
        {
            double[] t = tElem.EnumerateArray().Select(e => e.GetDouble()).ToArray();
            translation = new PclVector3(t[0], t[1], t[2]);
        }

        if (node.TryGetProperty("scale", out var sElem) && sElem.ValueKind == JsonValueKind.Array && sElem.GetArrayLength() == 3)
        {
            double[] s = sElem.EnumerateArray().Select(e => e.GetDouble()).ToArray();
            scale = new PclVector3(s[0], s[1], s[2]);
        }

        return new PclTransform { Translation = translation, Scale = scale };
    }

    private static PclTransform CombineTransforms(PclTransform parent, PclTransform child)
    {
        if (parent.Matrix4x4 is { Length: 16 } pm)
        {
            double[] cm = child.Matrix4x4 ?? [
                child.Scale.X, 0, 0, child.Translation.X,
                0, child.Scale.Y, 0, child.Translation.Y,
                0, 0, child.Scale.Z, child.Translation.Z,
                0, 0, 0, 1
            ];

            double[] result = new double[16];
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    double sum = 0;
                    for (int k = 0; k < 4; k++)
                        sum += pm[r * 4 + k] * cm[k * 4 + c];
                    result[r * 4 + c] = sum;
                }
            }
            return new PclTransform { Matrix4x4 = result };
        }

        return child;
    }

    /// <inheritdoc />
    public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null)
    {
        options ??= PclReadOptions.Default;
        long targetCount = options.MaxPoints ?? PointCount;
        if (targetCount <= 0 || PointCount == 0) yield break;
        targetCount = Math.Min(targetCount, PointCount);
        int step = Math.Max(1, options.SubsampleStep);

        using var fs = File.OpenRead(_filePath);
        fs.Position = _binChunkOffset;

        byte[] binData = new byte[_binChunkLength];
        int got = fs.ReadAtLeast(binData, _binChunkLength, throwOnEndOfStream: false);
        if (got < _binChunkLength)
            throw FormatError.Truncated($"glTF binary chunk ({_binChunkLength} bytes expected, got {got})");

        long yielded = 0;
        for (long i = 0; i < PointCount; i++)
        {
            if (i % step != 0) continue;

            if (!TryDecodePoint(i, binData, out PclPoint pt))
                continue;

            pt = pt with { Index = (ulong)yielded };
            if (options.ApplyTransform && !Transform.IsIdentity)
            {
                pt = pt with { Position = Transform.TransformPoint(pt.Position) };
                if (pt.Normal is { } n)
                    pt = pt with { Normal = Transform.TransformVector(n) };
            }

            yield return pt;
            yielded++;
            if (yielded >= targetCount) yield break;
        }
    }

    private bool TryDecodePoint(long index, ReadOnlySpan<byte> bin, out PclPoint point)
    {
        point = default;

        // Position: float vec3
        int posOffset = _posDesc.Offset + (int)(index * _posDesc.Stride);
        float x = BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(posOffset, 4));
        float y = BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(posOffset + 4, 4));
        float z = BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(posOffset + 8, 4));

        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            return false;

        var pos = new PclVector3(x, y, z);

        // Color
        PclColor? color = null;
        if (_colDesc != null)
        {
            int colOffset = _colDesc.Offset + (int)(index * _colDesc.Stride);
            color = DecodeColor(bin.Slice(colOffset), _colDesc);
        }

        // Normal
        PclVector3? normal = null;
        if (_normDesc != null)
        {
            int normOffset = _normDesc.Offset + (int)(index * _normDesc.Stride);
            float nx = BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(normOffset, 4));
            float ny = BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(normOffset + 4, 4));
            float nz = BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(normOffset + 8, 4));
            if (float.IsFinite(nx) && float.IsFinite(ny) && float.IsFinite(nz))
            {
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                normal = len > 0 ? new PclVector3(nx / len, ny / len, nz / len) : new PclVector3(nx, ny, nz);
            }
        }

        // Intensity
        float? intensity = null;
        if (_intDesc != null)
        {
            int intOffset = _intDesc.Offset + (int)(index * _intDesc.Stride);
            intensity = _intDesc.ComponentType switch
            {
                5121 => bin[intOffset],
                5123 => BinaryPrimitives.ReadUInt16LittleEndian(bin.Slice(intOffset, 2)),
                5126 => BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(intOffset, 4)),
                _ => null
            };
        }

        point = new PclPoint(pos, color, intensity, normal);
        return true;
    }

    private static PclColor DecodeColor(ReadOnlySpan<byte> span, AccessorDesc desc)
    {
        bool hasAlpha = desc.Type == "VEC4";
        switch (desc.ComponentType)
        {
            case 5121: // UNSIGNED_BYTE
                {
                    byte r = span[0];
                    byte g = span[1];
                    byte b = span[2];
                    byte a = hasAlpha ? span[3] : (byte)255;
                    return new PclColor(r, g, b, a);
                }
            case 5123: // UNSIGNED_SHORT
                {
                    ushort r16 = BinaryPrimitives.ReadUInt16LittleEndian(span);
                    ushort g16 = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);
                    ushort b16 = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
                    ushort a16 = hasAlpha ? BinaryPrimitives.ReadUInt16LittleEndian(span[6..]) : (ushort)65535;
                    return new PclColor((byte)(r16 * 255 / 65535), (byte)(g16 * 255 / 65535), (byte)(b16 * 255 / 65535), (byte)(a16 * 255 / 65535));
                }
            case 5126: // FLOAT
                {
                    float rf = BinaryPrimitives.ReadSingleLittleEndian(span);
                    float gf = BinaryPrimitives.ReadSingleLittleEndian(span[4..]);
                    float bf = BinaryPrimitives.ReadSingleLittleEndian(span[8..]);
                    float af = hasAlpha ? BinaryPrimitives.ReadSingleLittleEndian(span[12..]) : 1.0f;
                    return new PclColor(
                        (byte)Math.Clamp((int)Math.Round(rf * 255.0f), 0, 255),
                        (byte)Math.Clamp((int)Math.Round(gf * 255.0f), 0, 255),
                        (byte)Math.Clamp((int)Math.Round(bf * 255.0f), 0, 255),
                        (byte)Math.Clamp((int)Math.Round(af * 255.0f), 0, 255));
                }
            default:
                return new PclColor(255, 255, 255, 255);
        }
    }
}
