using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using PclIO.Internal;

namespace PclIO.Formats.Potree;

/// <summary>Represents a point cloud scan or level of detail in a Potree dataset.</summary>
public sealed class PotreeScan : PclScan
{
    private const int BatchRecords = 8192;

    private readonly PotreeLayout _layout;
    private readonly IReadOnlyList<PotreeNode> _nodes;

    /// <inheritdoc />
    public override string Id { get; }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override long PointCount { get; }

    /// <inheritdoc />
    public override bool HasColors => _layout.ColorOffset >= 0;

    /// <inheritdoc />
    public override bool HasNormals => _layout.NormalOffset >= 0;

    /// <inheritdoc />
    public override bool HasIntensities => _layout.IntensityOffset >= 0;

    /// <summary>Internal representation of the point layout.</summary>
    internal PotreeLayout Layout => _layout;

    internal PotreeScan(
        string id,
        string name,
        long pointCount,
        PclBounds bounds,
        PotreeLayout layout,
        IReadOnlyList<PotreeNode> nodes)
    {
        Id = id;
        Name = name;
        PointCount = pointCount;
        Bounds = bounds;
        _layout = layout;
        _nodes = nodes;

        _properties["Format"] = "Potree Octree Point Cloud";
        _properties["RecordSize"] = layout.RecordSize.ToString(CultureInfo.InvariantCulture);
        _properties["NodeCount"] = nodes.Count.ToString(CultureInfo.InvariantCulture);
        if (layout.ColorOffset >= 0)
            _properties["HasColors"] = "True";
        if (layout.NormalOffset >= 0)
            _properties["NormalEncoding"] = layout.NormalEnc.ToString();
        if (layout.IntensityOffset >= 0)
            _properties["HasIntensity"] = "True";
        if (layout.ClassOffset >= 0)
            _properties["HasClassification"] = "True";
    }

    /// <inheritdoc />
    public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null)
    {
        options ??= PclReadOptions.Default;
        long targetCount = options.MaxPoints ?? PointCount;
        if (targetCount <= 0 || PointCount == 0) yield break;
        targetCount = Math.Min(targetCount, PointCount);
        int step = Math.Max(1, options.SubsampleStep);

        int recSize = _layout.RecordSize;
        if (recSize <= 0) yield break;

        byte[] rawBuffer = new byte[BatchRecords * recSize];
        long globalIndex = 0;
        long yielded = 0;

        foreach (var node in _nodes)
        {
            if (node.PointCount <= 0) continue;
            if (!File.Exists(node.FilePath)) continue;

            using var fs = File.OpenRead(node.FilePath);
            fs.Position = node.ByteOffset;

            long pointsRemaining = node.PointCount;
            while (pointsRemaining > 0)
            {
                int toReadPoints = (int)Math.Min(BatchRecords, pointsRemaining);
                int bytesToRead = toReadPoints * recSize;

                int got = fs.ReadAtLeast(rawBuffer.AsSpan(0, bytesToRead), bytesToRead, throwOnEndOfStream: false);
                if (got < bytesToRead)
                    throw FormatError.Truncated($"Potree node '{node.FilePath}' ended prematurely while reading points.");

                for (int p = 0; p < toReadPoints; p++)
                {
                    globalIndex++;
                    if ((globalIndex - 1) % step != 0) continue;

                    int offset = p * recSize;
                    if (!TryDecodePoint(rawBuffer.AsSpan(offset, recSize), _layout, out PclPoint pt))
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

                pointsRemaining -= toReadPoints;
            }
        }
    }

    private static bool TryDecodePoint(ReadOnlySpan<byte> record, PotreeLayout layout, out PclPoint point)
    {
        point = default;

        // Position
        PclVector3 pos;
        if (layout.PosIsInt32)
        {
            int rx = BinaryPrimitives.ReadInt32LittleEndian(record.Slice(layout.PosOffset, 4));
            int ry = BinaryPrimitives.ReadInt32LittleEndian(record.Slice(layout.PosOffset + 4, 4));
            int rz = BinaryPrimitives.ReadInt32LittleEndian(record.Slice(layout.PosOffset + 8, 4));

            double x = layout.Offset.X + rx * layout.Scale.X;
            double y = layout.Offset.Y + ry * layout.Scale.Y;
            double z = layout.Offset.Z + rz * layout.Scale.Z;
            pos = new PclVector3(x, y, z);
        }
        else
        {
            float fx = BinaryPrimitives.ReadSingleLittleEndian(record.Slice(layout.PosOffset, 4));
            float fy = BinaryPrimitives.ReadSingleLittleEndian(record.Slice(layout.PosOffset + 4, 4));
            float fz = BinaryPrimitives.ReadSingleLittleEndian(record.Slice(layout.PosOffset + 8, 4));

            if (!float.IsFinite(fx) || !float.IsFinite(fy) || !float.IsFinite(fz))
                return false;

            pos = new PclVector3(fx, fy, fz);
        }

        // Color
        PclColor? color = null;
        if (layout.ColorOffset >= 0)
        {
            int off = layout.ColorOffset;
            if (layout.ColorIs16Bit)
            {
                ushort r16 = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(off, 2));
                ushort g16 = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(off + 2, 2));
                ushort b16 = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(off + 4, 2));
                byte a = layout.ColorHasAlpha ? (byte)(BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(off + 6, 2)) >> 8) : (byte)255;
                color = new PclColor((byte)(r16 >> 8), (byte)(g16 >> 8), (byte)(b16 >> 8), a);
            }
            else
            {
                byte r = record[off];
                byte g = record[off + 1];
                byte b = record[off + 2];
                byte a = layout.ColorHasAlpha ? record[off + 3] : (byte)255;
                color = new PclColor(r, g, b, a);
            }
        }

        // Normal
        PclVector3? normal = null;
        if (layout.NormalOffset >= 0)
        {
            int off = layout.NormalOffset;
            switch (layout.NormalEnc)
            {
                case NormalEncoding.Spheremap:
                    normal = DecodeSpheremap(record[off], record[off + 1]);
                    break;
                case NormalEncoding.Oct16:
                    normal = DecodeOct16p(record[off], record[off + 1]);
                    break;
                case NormalEncoding.Floats:
                    {
                        float nx = BinaryPrimitives.ReadSingleLittleEndian(record.Slice(off, 4));
                        float ny = BinaryPrimitives.ReadSingleLittleEndian(record.Slice(off + 4, 4));
                        float nz = BinaryPrimitives.ReadSingleLittleEndian(record.Slice(off + 8, 4));
                        if (float.IsFinite(nx) && float.IsFinite(ny) && float.IsFinite(nz))
                        {
                            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                            normal = len > 0 ? new PclVector3(nx / len, ny / len, nz / len) : new PclVector3(nx, ny, nz);
                        }
                        break;
                    }
            }
        }

        // Intensity
        float? intensity = null;
        if (layout.IntensityOffset >= 0)
        {
            int off = layout.IntensityOffset;
            intensity = layout.IntensityBytes switch
            {
                1 => record[off],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(off, 2)),
                4 => BinaryPrimitives.ReadSingleLittleEndian(record.Slice(off, 4)),
                _ => null
            };
        }

        // Classification
        byte? classification = null;
        if (layout.ClassOffset >= 0)
        {
            classification = record[layout.ClassOffset];
        }

        point = new PclPoint(pos, color, intensity, normal, classification);
        return true;
    }

    internal static PclVector3 DecodeSpheremap(byte b0, byte b1)
    {
        double u = (b0 / 255.0) * 2.0 - 1.0;
        double v = (b1 / 255.0) * 2.0 - 1.0;
        double g = u * u + v * v;
        if (g > 1.0)
            return new PclVector3(0, 0, 1);

        double p = Math.Sqrt(1.0 - g);
        double nx = 2.0 * u * p;
        double ny = 2.0 * v * p;
        double nz = 1.0 - 2.0 * g;
        return new PclVector3(nx, ny, nz);
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

/// <summary>Identifies a single node data file in a Potree octree.</summary>
internal sealed record PotreeNode(string FilePath, long ByteOffset, long ByteLength, long PointCount);

internal enum NormalEncoding { None, Spheremap, Oct16, Floats }

/// <summary>Describes point record attribute offsets and types for Potree point streaming.</summary>
internal sealed class PotreeLayout
{
    public int RecordSize { get; init; }
    public int PosOffset { get; init; }
    public bool PosIsInt32 { get; init; }
    public PclVector3 Scale { get; init; } = PclVector3.One;
    public PclVector3 Offset { get; init; } = PclVector3.Zero;
    public int ColorOffset { get; init; } = -1;
    public bool ColorHasAlpha { get; init; }
    public bool ColorIs16Bit { get; init; }
    public int IntensityOffset { get; init; } = -1;
    public int IntensityBytes { get; init; }
    public int NormalOffset { get; init; } = -1;
    public NormalEncoding NormalEnc { get; init; } = NormalEncoding.None;
    public int ClassOffset { get; init; } = -1;

    public static PotreeLayout Parse1x(JsonElement pointAttrsElem, PclVector3 scale, PclVector3 offset)
    {
        int curOffset = 0;
        int posOffset = 0;
        bool posIsInt32 = true;
        int colOffset = -1;
        bool colAlpha = false;
        bool col16Bit = false;
        int intOffset = -1;
        int intBytes = 2;
        int normOffset = -1;
        var normEnc = NormalEncoding.None;
        int classOffset = -1;

        foreach (var item in pointAttrsElem.EnumerateArray())
        {
            string name = "";
            int size = 0;
            string type = "";

            if (item.ValueKind == JsonValueKind.String)
            {
                name = item.GetString() ?? "";
                size = GetAttributeDefaultSize1x(name);
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                if (item.TryGetProperty("name", out var nElem))
                    name = nElem.GetString() ?? "";
                if (item.TryGetProperty("size", out var sElem))
                    size = sElem.GetInt32();
                else
                    size = GetAttributeDefaultSize1x(name);
                if (item.TryGetProperty("type", out var tElem))
                    type = tElem.GetString() ?? "";
            }

            string upper = name.ToUpperInvariant();
            if (upper is "POSITION_CARTESIAN" or "POSITION")
            {
                posOffset = curOffset;
                posIsInt32 = type != "float" && type != "double";
            }
            else if (upper is "COLOR_PACKED" or "RGBA")
            {
                colOffset = curOffset;
                colAlpha = true;
                col16Bit = size == 8 || type == "uint16";
            }
            else if (upper is "RGB")
            {
                colOffset = curOffset;
                colAlpha = false;
                col16Bit = size == 6 || type == "uint16";
            }
            else if (upper is "INTENSITY")
            {
                intOffset = curOffset;
                intBytes = size > 0 ? size : 2;
            }
            else if (upper is "NORMAL_SPHEREMAPPED")
            {
                normOffset = curOffset;
                normEnc = NormalEncoding.Spheremap;
            }
            else if (upper is "NORMAL_OCT16")
            {
                normOffset = curOffset;
                normEnc = NormalEncoding.Oct16;
            }
            else if (upper is "NORMAL_FLOATS" or "NORMAL")
            {
                normOffset = curOffset;
                normEnc = NormalEncoding.Floats;
            }
            else if (upper is "CLASSIFICATION")
            {
                classOffset = curOffset;
            }

            curOffset += size;
        }

        return new PotreeLayout
        {
            RecordSize = curOffset,
            PosOffset = posOffset,
            PosIsInt32 = posIsInt32,
            Scale = scale,
            Offset = offset,
            ColorOffset = colOffset,
            ColorHasAlpha = colAlpha,
            ColorIs16Bit = col16Bit,
            IntensityOffset = intOffset,
            IntensityBytes = intBytes,
            NormalOffset = normOffset,
            NormalEnc = normEnc,
            ClassOffset = classOffset
        };
    }

    public static PotreeLayout Parse2x(JsonElement attrsElem, PclVector3 scale, PclVector3 offset)
    {
        int curOffset = 0;
        int posOffset = 0;
        bool posIsInt32 = true;
        int colOffset = -1;
        bool colAlpha = false;
        bool col16Bit = false;
        int intOffset = -1;
        int intBytes = 2;
        int normOffset = -1;
        var normEnc = NormalEncoding.None;
        int classOffset = -1;

        foreach (var item in attrsElem.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            string name = item.TryGetProperty("name", out var nElem) ? (nElem.GetString() ?? "") : "";
            int size = item.TryGetProperty("size", out var sElem) ? sElem.GetInt32() : 0;
            string type = item.TryGetProperty("type", out var tElem) ? (tElem.GetString() ?? "") : "";
            int numElements = item.TryGetProperty("numElements", out var neElem) ? neElem.GetInt32() : 1;

            string upper = name.ToUpperInvariant();
            if (upper is "POSITION" or "POSITION_CARTESIAN")
            {
                posOffset = curOffset;
                posIsInt32 = type != "float" && type != "double";
            }
            else if (upper is "RGBA")
            {
                colOffset = curOffset;
                colAlpha = true;
                col16Bit = type == "uint16" || size == 8;
            }
            else if (upper is "RGB")
            {
                colOffset = curOffset;
                colAlpha = false;
                col16Bit = type == "uint16" || size == 6;
            }
            else if (upper is "INTENSITY")
            {
                intOffset = curOffset;
                intBytes = size > 0 ? size : 2;
            }
            else if (upper is "NORMAL" or "NORMALS")
            {
                normOffset = curOffset;
                if (size == 2) normEnc = NormalEncoding.Oct16;
                else normEnc = NormalEncoding.Floats;
            }
            else if (upper is "CLASSIFICATION")
            {
                classOffset = curOffset;
            }

            curOffset += size;
        }

        return new PotreeLayout
        {
            RecordSize = curOffset,
            PosOffset = posOffset,
            PosIsInt32 = posIsInt32,
            Scale = scale,
            Offset = offset,
            ColorOffset = colOffset,
            ColorHasAlpha = colAlpha,
            ColorIs16Bit = col16Bit,
            IntensityOffset = intOffset,
            IntensityBytes = intBytes,
            NormalOffset = normOffset,
            NormalEnc = normEnc,
            ClassOffset = classOffset
        };
    }

    private static int GetAttributeDefaultSize1x(string name) => name.ToUpperInvariant() switch
    {
        "POSITION_CARTESIAN" => 12,
        "COLOR_PACKED" or "RGBA" => 4,
        "RGB" => 3,
        "INTENSITY" => 2,
        "CLASSIFICATION" => 1,
        "NORMAL_SPHEREMAPPED" or "NORMAL_OCT16" => 2,
        "NORMAL_FLOATS" => 12,
        "RETURN_NUMBER" or "NUMBER_OF_RETURNS" => 1,
        "SOURCE_ID" => 2,
        "GPS_TIME" => 8,
        _ => 0
    };
}
