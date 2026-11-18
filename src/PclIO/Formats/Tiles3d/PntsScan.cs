using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using PclIO.Internal;

namespace PclIO.Formats.Tiles3d;

/// <summary>Represents a point cloud scan stored in an OGC 3D Tiles .pnts tile file.</summary>
public sealed class PntsScan : PclScan
{
    private const int PntsHeaderSize = 28;
    private const int MaxTileBytes = 512 * 1024 * 1024;
    private const int MaxPointsLimit = 500_000_000;

    private readonly string _filePath;
    private readonly int _ftJsonOffset;
    private readonly int _ftJsonLen;
    private readonly int _ftBinOffset;
    private readonly int _ftBinLen;
    private readonly int _btJsonOffset;
    private readonly int _btJsonLen;
    private readonly int _btBinOffset;
    private readonly int _btBinLen;

    // Feature table attributes
    private readonly bool _isQuantized;
    private readonly int _posOffset;
    private readonly PclVector3 _qOffset;
    private readonly PclVector3 _qScale;
    private readonly PclVector3? _rtcCenter;

    private readonly ColorFormat _colorFormat;
    private readonly int _colOffset;
    private readonly PclColor _constantColor;

    private readonly NormalFormat _normalFormat;
    private readonly int _normOffset;

    // Batch table attributes
    private readonly BatchProperty? _intensityProp;
    private readonly BatchProperty? _classProp;

    /// <inheritdoc />
    public override string Id { get; }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override long PointCount { get; }

    /// <inheritdoc />
    public override bool HasColors => _colorFormat != ColorFormat.None;

    /// <inheritdoc />
    public override bool HasNormals => _normalFormat != NormalFormat.None;

    /// <inheritdoc />
    public override bool HasIntensities => _intensityProp != null;

    /// <summary>Relative-to-center offset vector defined in the feature table, or null.</summary>
    public PclVector3? RtcCenter => _rtcCenter;

    /// <summary>True when position coordinates are stored as 16-bit quantised values.</summary>
    public bool IsQuantized => _isQuantized;

    private enum ColorFormat { None, Rgb, Rgba, Rgb565, ConstantRgba }
    private enum NormalFormat { None, Vec3Float, Oct16p }

    private sealed class BatchProperty
    {
        public required string Name { get; init; }
        public bool IsBinary { get; init; }
        public int ByteOffset { get; init; }
        public string? ComponentType { get; init; }
        public float[]? JsonArray { get; init; }
    }

    private PntsScan(
        string filePath,
        string id,
        string name,
        long pointCount,
        PclTransform transform,
        PclBounds bounds,
        int ftJsonOffset, int ftJsonLen,
        int ftBinOffset, int ftBinLen,
        int btJsonOffset, int btJsonLen,
        int btBinOffset, int btBinLen,
        bool isQuantized, int posOffset, PclVector3 qOffset, PclVector3 qScale, PclVector3? rtcCenter,
        ColorFormat colorFormat, int colOffset, PclColor constantColor,
        NormalFormat normalFormat, int normOffset,
        BatchProperty? intensityProp, BatchProperty? classProp)
    {
        _filePath = filePath;
        Id = id;
        Name = name;
        PointCount = pointCount;
        Transform = transform;
        Bounds = bounds;

        _ftJsonOffset = ftJsonOffset;
        _ftJsonLen = ftJsonLen;
        _ftBinOffset = ftBinOffset;
        _ftBinLen = ftBinLen;
        _btJsonOffset = btJsonOffset;
        _btJsonLen = btJsonLen;
        _btBinOffset = btBinOffset;
        _btBinLen = btBinLen;

        _isQuantized = isQuantized;
        _posOffset = posOffset;
        _qOffset = qOffset;
        _qScale = qScale;
        _rtcCenter = rtcCenter;

        _colorFormat = colorFormat;
        _colOffset = colOffset;
        _constantColor = constantColor;

        _normalFormat = normalFormat;
        _normOffset = normOffset;

        _intensityProp = intensityProp;
        _classProp = classProp;

        _properties["Format"] = "3D Tiles Point Cloud (.pnts)";
        _properties["PointsLength"] = pointCount.ToString(CultureInfo.InvariantCulture);
        _properties["IsQuantized"] = isQuantized.ToString();
        if (rtcCenter.HasValue)
            _properties["RTC_CENTER"] = string.Create(CultureInfo.InvariantCulture, $"{rtcCenter.Value.X:F6}, {rtcCenter.Value.Y:F6}, {rtcCenter.Value.Z:F6}");
        if (colorFormat != ColorFormat.None)
            _properties["ColorFormat"] = colorFormat.ToString();
        if (normalFormat != NormalFormat.None)
            _properties["NormalFormat"] = normalFormat.ToString();
        if (intensityProp != null)
            _properties["HasIntensity"] = "True";
    }

    /// <summary>Creates a <see cref="PntsScan"/> by inspecting a .pnts tile file on disk.</summary>
    public static PntsScan FromFile(
        string filePath,
        string id,
        string name,
        PclTransform transform,
        PclBounds? declaredBounds = null)
    {
        using var stream = File.OpenRead(filePath);
        long fileLength = stream.Length;

        if (fileLength < PntsHeaderSize)
            throw FormatError.Truncated($"header of at least {PntsHeaderSize} bytes");

        Span<byte> header = stackalloc byte[PntsHeaderSize];
        stream.ReadExactly(header);

        if (header[0] != 0x70 || header[1] != 0x6E || header[2] != 0x74 || header[3] != 0x73) // "pnts"
            throw FormatError.Corrupted($"Invalid magic signature in '{filePath}'.");

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
        if (version != 1)
            throw FormatError.Unsupported($"Unsupported .pnts version {version}; expected 1.");

        uint byteLength = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
        uint ftJsonLen = BinaryPrimitives.ReadUInt32LittleEndian(header[12..16]);
        uint ftBinLen = BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]);
        uint btJsonLen = BinaryPrimitives.ReadUInt32LittleEndian(header[20..24]);
        uint btBinLen = BinaryPrimitives.ReadUInt32LittleEndian(header[24..28]);

        if (byteLength < PntsHeaderSize)
            throw FormatError.Corrupted($"Invalid tile byte length: {byteLength}");

        ulong calculatedSize = 28UL + ftJsonLen + ftBinLen + btJsonLen + btBinLen;
        if (calculatedSize != byteLength)
            throw FormatError.Corrupted($"Tile byteLength ({byteLength}) does not match sum of sections ({calculatedSize}).");

        if (fileLength < byteLength)
            throw FormatError.Truncated($"expected {byteLength} bytes but file has {fileLength} bytes");

        if (fileLength != byteLength && declaredBounds == null)
            throw FormatError.Corrupted($"File length ({fileLength}) does not match tile byte length ({byteLength}).");

        if (byteLength > MaxTileBytes)
            throw FormatError.Unsupported($"Tile size ({byteLength} bytes) exceeds safety limit of {MaxTileBytes} bytes.");

        int ftJsonOffset = 28;
        int ftBinOffset = ftJsonOffset + (int)ftJsonLen;
        int btJsonOffset = ftBinOffset + (int)ftBinLen;
        int btBinOffset = btJsonOffset + (int)btJsonLen;

        // Read Feature Table JSON
        byte[] ftJsonBytes = new byte[ftJsonLen];
        stream.Position = ftJsonOffset;
        stream.ReadExactly(ftJsonBytes);

        JsonDocument ftDoc;
        try
        {
            ftDoc = JsonDocument.Parse(ftJsonBytes);
        }
        catch (JsonException ex)
        {
            throw FormatError.Corrupted($"Malformed Feature Table JSON in '{filePath}': {ex.Message}");
        }

        long pointCount;
        bool isQuantized = false;
        int posOffset = -1;
        PclVector3 qOffset = PclVector3.Zero;
        PclVector3 qScale = PclVector3.One;
        PclVector3? rtcCenter = null;

        ColorFormat colorFormat = ColorFormat.None;
        int colOffset = -1;
        PclColor constantColor = new(255, 255, 255, 255);

        NormalFormat normalFormat = NormalFormat.None;
        int normOffset = -1;

        using (ftDoc)
        {
            var ftRoot = ftDoc.RootElement;
            if (ftRoot.ValueKind != JsonValueKind.Object)
                throw FormatError.Corrupted("Feature table root must be a JSON object.");

            if (!ftRoot.TryGetProperty("POINTS_LENGTH", out var plElem) || !plElem.TryGetInt64(out pointCount) || pointCount < 0)
                throw FormatError.Corrupted("Feature table is missing valid 'POINTS_LENGTH'.");

            if (pointCount > MaxPointsLimit)
                throw FormatError.Corrupted($"Point count ({pointCount}) exceeds maximum limit of {MaxPointsLimit}.");

            // RTC_CENTER
            if (ftRoot.TryGetProperty("RTC_CENTER", out var rtcElem) && rtcElem.ValueKind == JsonValueKind.Array && rtcElem.GetArrayLength() == 3)
            {
                double[] rtc = new double[3];
                int i = 0;
                foreach (var item in rtcElem.EnumerateArray()) rtc[i++] = item.GetDouble();
                rtcCenter = new PclVector3(rtc[0], rtc[1], rtc[2]);
            }

            // Positions: POSITION or POSITION_QUANTIZED
            if (ftRoot.TryGetProperty("POSITION", out var posElem) && posElem.ValueKind == JsonValueKind.Object)
            {
                if (!posElem.TryGetProperty("byteOffset", out var boElem) || !boElem.TryGetInt32(out posOffset) || posOffset < 0)
                    throw FormatError.Corrupted("POSITION byteOffset is invalid.");

                long requiredBytes = posOffset + pointCount * 12L;
                if (requiredBytes > ftBinLen)
                    throw FormatError.Corrupted($"POSITION buffer ({requiredBytes} bytes) exceeds feature table binary length ({ftBinLen}).");
            }
            else if (ftRoot.TryGetProperty("POSITION_QUANTIZED", out var pqElem) && pqElem.ValueKind == JsonValueKind.Object)
            {
                isQuantized = true;
                if (!pqElem.TryGetProperty("byteOffset", out var boElem) || !boElem.TryGetInt32(out posOffset) || posOffset < 0)
                    throw FormatError.Corrupted("POSITION_QUANTIZED byteOffset is invalid.");

                long requiredBytes = posOffset + pointCount * 6L;
                if (requiredBytes > ftBinLen)
                    throw FormatError.Corrupted($"POSITION_QUANTIZED buffer ({requiredBytes} bytes) exceeds feature table binary length ({ftBinLen}).");

                if (!ftRoot.TryGetProperty("QUANTIZED_VOLUME_OFFSET", out var qvoElem) || qvoElem.ValueKind != JsonValueKind.Array || qvoElem.GetArrayLength() != 3 ||
                    !ftRoot.TryGetProperty("QUANTIZED_VOLUME_SCALE", out var qvsElem) || qvsElem.ValueKind != JsonValueKind.Array || qvsElem.GetArrayLength() != 3)
                {
                    throw FormatError.Corrupted("POSITION_QUANTIZED requires QUANTIZED_VOLUME_OFFSET and QUANTIZED_VOLUME_SCALE.");
                }

                double[] qvo = new double[3];
                int idx = 0;
                foreach (var item in qvoElem.EnumerateArray()) qvo[idx++] = item.GetDouble();
                qOffset = new PclVector3(qvo[0], qvo[1], qvo[2]);

                double[] qvs = new double[3];
                idx = 0;
                foreach (var item in qvsElem.EnumerateArray()) qvs[idx++] = item.GetDouble();
                qScale = new PclVector3(qvs[0], qvs[1], qvs[2]);
            }
            else if (pointCount > 0)
            {
                throw FormatError.Corrupted("Feature table must contain either POSITION or POSITION_QUANTIZED.");
            }

            // Colors: RGBA, RGB, RGB565, CONSTANT_RGBA
            if (ftRoot.TryGetProperty("RGBA", out var rgbaElem) && rgbaElem.ValueKind == JsonValueKind.Object)
            {
                colorFormat = ColorFormat.Rgba;
                if (!rgbaElem.TryGetProperty("byteOffset", out var boElem) || !boElem.TryGetInt32(out colOffset) || colOffset < 0)
                    throw FormatError.Corrupted("RGBA byteOffset is invalid.");
                if (colOffset + pointCount * 4L > ftBinLen)
                    throw FormatError.Corrupted("RGBA buffer exceeds feature table binary length.");
            }
            else if (ftRoot.TryGetProperty("RGB", out var rgbElem) && rgbElem.ValueKind == JsonValueKind.Object)
            {
                colorFormat = ColorFormat.Rgb;
                if (!rgbElem.TryGetProperty("byteOffset", out var boElem) || !boElem.TryGetInt32(out colOffset) || colOffset < 0)
                    throw FormatError.Corrupted("RGB byteOffset is invalid.");
                if (colOffset + pointCount * 3L > ftBinLen)
                    throw FormatError.Corrupted("RGB buffer exceeds feature table binary length.");
            }
            else if (ftRoot.TryGetProperty("RGB565", out var rgb565Elem) && rgb565Elem.ValueKind == JsonValueKind.Object)
            {
                colorFormat = ColorFormat.Rgb565;
                if (!rgb565Elem.TryGetProperty("byteOffset", out var boElem) || !boElem.TryGetInt32(out colOffset) || colOffset < 0)
                    throw FormatError.Corrupted("RGB565 byteOffset is invalid.");
                if (colOffset + pointCount * 2L > ftBinLen)
                    throw FormatError.Corrupted("RGB565 buffer exceeds feature table binary length.");
            }
            else if (ftRoot.TryGetProperty("CONSTANT_RGBA", out var crgbaElem) && crgbaElem.ValueKind == JsonValueKind.Array && crgbaElem.GetArrayLength() == 4)
            {
                colorFormat = ColorFormat.ConstantRgba;
                byte[] rgba = new byte[4];
                int idx = 0;
                foreach (var item in crgbaElem.EnumerateArray()) rgba[idx++] = (byte)item.GetInt32();
                constantColor = new PclColor(rgba[0], rgba[1], rgba[2], rgba[3]);
            }

            // Normals: NORMAL, NORMAL_OCT16P
            if (ftRoot.TryGetProperty("NORMAL", out var normElem) && normElem.ValueKind == JsonValueKind.Object)
            {
                normalFormat = NormalFormat.Vec3Float;
                if (!normElem.TryGetProperty("byteOffset", out var boElem) || !boElem.TryGetInt32(out normOffset) || normOffset < 0)
                    throw FormatError.Corrupted("NORMAL byteOffset is invalid.");
                if (normOffset + pointCount * 12L > ftBinLen)
                    throw FormatError.Corrupted("NORMAL buffer exceeds feature table binary length.");
            }
            else if (ftRoot.TryGetProperty("NORMAL_OCT16P", out var octElem) && octElem.ValueKind == JsonValueKind.Object)
            {
                normalFormat = NormalFormat.Oct16p;
                if (!octElem.TryGetProperty("byteOffset", out var boElem) || !boElem.TryGetInt32(out normOffset) || normOffset < 0)
                    throw FormatError.Corrupted("NORMAL_OCT16P byteOffset is invalid.");
                if (normOffset + pointCount * 2L > ftBinLen)
                    throw FormatError.Corrupted("NORMAL_OCT16P buffer exceeds feature table binary length.");
            }
        }

        // Parse Batch Table if present
        BatchProperty? intensityProp = null;
        BatchProperty? classProp = null;
        if (btJsonLen > 0)
        {
            byte[] btJsonBytes = new byte[btJsonLen];
            stream.Position = btJsonOffset;
            stream.ReadExactly(btJsonBytes);

            try
            {
                using var btDoc = JsonDocument.Parse(btJsonBytes);
                var btRoot = btDoc.RootElement;
                if (btRoot.ValueKind == JsonValueKind.Object)
                {
                    intensityProp = FindBatchProperty(btRoot, pointCount, (int)btBinLen, ["Intensity", "intensity", "Intensity_"]);
                    classProp = FindBatchProperty(btRoot, pointCount, (int)btBinLen, ["Classification", "classification"]);
                }
            }
            catch (JsonException ex)
            {
                throw FormatError.Corrupted($"Malformed Batch Table JSON in '{filePath}': {ex.Message}");
            }
        }

        // Combine RTC with transform
        PclTransform effectiveTransform = rtcCenter.HasValue
            ? Tiles3dDocument.CombineRtcWithTransform(transform, rtcCenter.Value)
            : transform;

        // Compute bounds
        PclBounds effectiveBounds = declaredBounds ?? PclBounds.Empty;
        if (effectiveBounds.IsEmpty && isQuantized)
        {
            effectiveBounds = new PclBounds(qOffset, new PclVector3(qOffset.X + qScale.X, qOffset.Y + qScale.Y, qOffset.Z + qScale.Z));
        }

        return new PntsScan(
            filePath,
            id,
            name,
            pointCount,
            effectiveTransform,
            effectiveBounds,
            ftJsonOffset, (int)ftJsonLen,
            ftBinOffset, (int)ftBinLen,
            btJsonOffset, (int)btJsonLen,
            btBinOffset, (int)btBinLen,
            isQuantized, posOffset, qOffset, qScale, rtcCenter,
            colorFormat, colOffset, constantColor,
            normalFormat, normOffset,
            intensityProp, classProp);
    }

    private static BatchProperty? FindBatchProperty(JsonElement root, long pointCount, int btBinLen, string[] candidateNames)
    {
        foreach (string name in candidateNames)
        {
            if (root.TryGetProperty(name, out var propElem))
            {
                if (propElem.ValueKind == JsonValueKind.Array)
                {
                    if (propElem.GetArrayLength() != pointCount) continue;
                    float[] values = new float[pointCount];
                    int i = 0;
                    foreach (var item in propElem.EnumerateArray())
                    {
                        values[i++] = (float)item.GetDouble();
                    }
                    return new BatchProperty { Name = name, IsBinary = false, JsonArray = values };
                }

                if (propElem.ValueKind == JsonValueKind.Object &&
                    propElem.TryGetProperty("byteOffset", out var boElem) && boElem.TryGetInt32(out int byteOffset) && byteOffset >= 0 &&
                    propElem.TryGetProperty("componentType", out var ctElem))
                {
                    string? compType = ctElem.GetString();
                    int compBytes = GetComponentTypeSize(compType);
                    if (compBytes > 0 && byteOffset + pointCount * (long)compBytes <= btBinLen)
                    {
                        return new BatchProperty
                        {
                            Name = name,
                            IsBinary = true,
                            ByteOffset = byteOffset,
                            ComponentType = compType
                        };
                    }
                }
            }
        }
        return null;
    }

    private static int GetComponentTypeSize(string? componentType) => componentType switch
    {
        "BYTE" or "UNSIGNED_BYTE" => 1,
        "SHORT" or "UNSIGNED_SHORT" => 2,
        "INT" or "UNSIGNED_INT" or "FLOAT" => 4,
        "DOUBLE" => 8,
        _ => 0
    };

    /// <inheritdoc />
    public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null)
    {
        options ??= PclReadOptions.Default;
        long targetCount = options.MaxPoints ?? PointCount;
        if (targetCount <= 0 || PointCount == 0) yield break;
        targetCount = Math.Min(targetCount, PointCount);
        int step = Math.Max(1, options.SubsampleStep);

        using var fs = File.OpenRead(_filePath);

        // Read Feature Table Binary
        byte[] ftBin = new byte[_ftBinLen];
        if (_ftBinLen > 0)
        {
            fs.Position = _ftBinOffset;
            int got = fs.ReadAtLeast(ftBin, _ftBinLen, throwOnEndOfStream: false);
            if (got < _ftBinLen)
                throw FormatError.Truncated($"feature table binary data ({_ftBinLen} bytes expected, got {got})");
        }

        // Read Batch Table Binary
        byte[] btBin = new byte[_btBinLen];
        if (_btBinLen > 0)
        {
            fs.Position = _btBinOffset;
            int got = fs.ReadAtLeast(btBin, _btBinLen, throwOnEndOfStream: false);
            if (got < _btBinLen)
                throw FormatError.Truncated($"batch table binary data ({_btBinLen} bytes expected, got {got})");
        }

        long yielded = 0;
        for (long i = 0; i < PointCount; i++)
        {
            if (i % step != 0) continue;

            if (!TryDecodePoint(i, ftBin, btBin, out PclPoint pt))
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

    private bool TryDecodePoint(long index, ReadOnlySpan<byte> ftBin, ReadOnlySpan<byte> btBin, out PclPoint point)
    {
        point = default;

        // Position
        PclVector3 pos;
        if (_isQuantized)
        {
            int offset = _posOffset + (int)(index * 6);
            ushort qx = BinaryPrimitives.ReadUInt16LittleEndian(ftBin.Slice(offset, 2));
            ushort qy = BinaryPrimitives.ReadUInt16LittleEndian(ftBin.Slice(offset + 2, 2));
            ushort qz = BinaryPrimitives.ReadUInt16LittleEndian(ftBin.Slice(offset + 4, 2));

            double x = _qOffset.X + (qx / 65535.0) * _qScale.X;
            double y = _qOffset.Y + (qy / 65535.0) * _qScale.Y;
            double z = _qOffset.Z + (qz / 65535.0) * _qScale.Z;
            pos = new PclVector3(x, y, z);
        }
        else
        {
            int offset = _posOffset + (int)(index * 12);
            float x = BinaryPrimitives.ReadSingleLittleEndian(ftBin.Slice(offset, 4));
            float y = BinaryPrimitives.ReadSingleLittleEndian(ftBin.Slice(offset + 4, 4));
            float z = BinaryPrimitives.ReadSingleLittleEndian(ftBin.Slice(offset + 8, 4));

            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                return false;

            pos = new PclVector3(x, y, z);
        }

        // Color
        PclColor? color = null;
        switch (_colorFormat)
        {
            case ColorFormat.Rgba:
            {
                int offset = _colOffset + (int)(index * 4);
                color = new PclColor(ftBin[offset], ftBin[offset + 1], ftBin[offset + 2], ftBin[offset + 3]);
                break;
            }
            case ColorFormat.Rgb:
            {
                int offset = _colOffset + (int)(index * 3);
                color = new PclColor(ftBin[offset], ftBin[offset + 1], ftBin[offset + 2], 255);
                break;
            }
            case ColorFormat.Rgb565:
            {
                int offset = _colOffset + (int)(index * 2);
                ushort c = BinaryPrimitives.ReadUInt16LittleEndian(ftBin.Slice(offset, 2));
                color = DecodeRgb565(c);
                break;
            }
            case ColorFormat.ConstantRgba:
                color = _constantColor;
                break;
        }

        // Normal
        PclVector3? normal = null;
        switch (_normalFormat)
        {
            case NormalFormat.Vec3Float:
            {
                int offset = _normOffset + (int)(index * 12);
                float nx = BinaryPrimitives.ReadSingleLittleEndian(ftBin.Slice(offset, 4));
                float ny = BinaryPrimitives.ReadSingleLittleEndian(ftBin.Slice(offset + 4, 4));
                float nz = BinaryPrimitives.ReadSingleLittleEndian(ftBin.Slice(offset + 8, 4));
                if (float.IsFinite(nx) && float.IsFinite(ny) && float.IsFinite(nz))
                {
                    double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    normal = len > 0 ? new PclVector3(nx / len, ny / len, nz / len) : new PclVector3(nx, ny, nz);
                }
                break;
            }
            case NormalFormat.Oct16p:
            {
                int offset = _normOffset + (int)(index * 2);
                normal = DecodeOct16p(ftBin[offset], ftBin[offset + 1]);
                break;
            }
        }

        // Intensity
        float? intensity = null;
        if (_intensityProp != null)
        {
            if (_intensityProp.IsBinary)
                intensity = ReadBatchScalarFloat(_intensityProp, btBin, index);
            else if (_intensityProp.JsonArray != null && index < _intensityProp.JsonArray.Length)
                intensity = _intensityProp.JsonArray[index];
        }

        // Classification
        byte? classification = null;
        if (_classProp != null)
        {
            if (_classProp.IsBinary)
            {
                float val = ReadBatchScalarFloat(_classProp, btBin, index);
                classification = (byte)Math.Clamp((int)Math.Round(val), 0, 255);
            }
            else if (_classProp.JsonArray != null && index < _classProp.JsonArray.Length)
            {
                classification = (byte)Math.Clamp((int)Math.Round(_classProp.JsonArray[index]), 0, 255);
            }
        }

        point = new PclPoint(pos, color, intensity, normal, classification);
        return true;
    }

    private static float ReadBatchScalarFloat(BatchProperty prop, ReadOnlySpan<byte> btBin, long index)
    {
        return prop.ComponentType switch
        {
            "BYTE" => (sbyte)btBin[prop.ByteOffset + (int)index],
            "UNSIGNED_BYTE" => btBin[prop.ByteOffset + (int)index],
            "SHORT" => BinaryPrimitives.ReadInt16LittleEndian(btBin.Slice(prop.ByteOffset + (int)(index * 2), 2)),
            "UNSIGNED_SHORT" => BinaryPrimitives.ReadUInt16LittleEndian(btBin.Slice(prop.ByteOffset + (int)(index * 2), 2)),
            "INT" => BinaryPrimitives.ReadInt32LittleEndian(btBin.Slice(prop.ByteOffset + (int)(index * 4), 4)),
            "UNSIGNED_INT" => BinaryPrimitives.ReadUInt32LittleEndian(btBin.Slice(prop.ByteOffset + (int)(index * 4), 4)),
            "FLOAT" => BinaryPrimitives.ReadSingleLittleEndian(btBin.Slice(prop.ByteOffset + (int)(index * 4), 4)),
            "DOUBLE" => (float)BinaryPrimitives.ReadDoubleLittleEndian(btBin.Slice(prop.ByteOffset + (int)(index * 8), 8)),
            _ => 0f
        };
    }

    internal static PclColor DecodeRgb565(ushort c)
    {
        byte r = (byte)(((c >> 11) & 0x1F) * 255 / 31);
        byte g = (byte)(((c >> 5) & 0x3F) * 255 / 63);
        byte b = (byte)((c & 0x1F) * 255 / 31);
        return new PclColor(r, g, b, 255);
    }

    internal static PclVector3 DecodeOct16p(byte b0, byte b1)
    {
        double x = (b0 / 255.0) * 2.0 - 1.0;
        double y = (b1 / 255.0) * 2.0 - 1.0;
        double z = 1.0 - Math.Abs(x) - Math.Abs(y);
        if (z < 0.0)
        {
            double oldX = x;
            double oldY = y;
            x = (1.0 - Math.Abs(oldY)) * (oldX >= 0.0 ? 1.0 : -1.0);
            y = (1.0 - Math.Abs(oldX)) * (oldY >= 0.0 ? 1.0 : -1.0);
        }
        double len = Math.Sqrt(x * x + y * y + z * z);
        return len > 0.0 ? new PclVector3(x / len, y / len, z / len) : new PclVector3(x, y, z);
    }
}
