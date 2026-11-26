using System.Globalization;
using System.Text;
using PclIO.Internal;

namespace PclIO.Formats.Recap;

/// <summary>
/// Reads header metadata and the stored points of a ReCap scan file (.rcs). Point decoding is experimental and supports container version 3.2.
/// </summary>
public sealed class RcsScanDocument : PclDocument
{
    /// <inheritdoc />
    public override PclFileKind Kind => PclFileKind.RecapScan;

    /// <summary>Major versions of the container whose node layout the reader decodes.</summary>
    public static IReadOnlyList<uint> DecodedNodeLayoutVersions { get; } = [3];

    /// <summary>Container major version from the header.</summary>
    public uint MajorVersion { get; }

    /// <summary>Container minor version from the header.</summary>
    public uint MinorVersion { get; }

    /// <summary>Scan identifier string stored in the header; may be empty.</summary>
    public string ScanGuid { get; }

    /// <summary>Header translation and scale (a zero scale component is read as 1). Rotation is parsed but not applied to points.</summary>
    public PclTransform ScanTransform { get; }

    /// <summary>Local bounding box stored in the header.</summary>
    public PclBounds LocalBounds { get; }

    /// <summary>Octree bounding box stored in the header.</summary>
    public PclBounds OctreeBounds { get; }

    /// <summary>Point count declared in the header. Read a scan's PointCount for the number of stored records.</summary>
    public long DeclaredPointCount { get; }

    /// <summary>True when the header declares point colour.</summary>
    public bool HasColorData { get; }

    /// <summary>True when the header declares point normals.</summary>
    public bool HasNormalData { get; }

    /// <summary>True when the header declares point intensity.</summary>
    public bool HasIntensityData { get; }

    /// <inheritdoc />
    public override IReadOnlyList<PclScan> Scans => _scans;
    private readonly List<PclScan> _scans = [];

    private RcsScanDocument(
        string path,
        long fileSize,
        uint major,
        uint minor,
        string guid,
        string? coordSystem,
        PclTransform transform,
        PclBounds localBounds,
        PclBounds octreeBounds,
        long pointCount,
        bool hasColor,
        bool hasNormal,
        bool hasIntensity)
        : base(path, fileSize)
    {
        MajorVersion = major;
        MinorVersion = minor;
        ScanGuid = guid;
        CoordinateSystem = coordSystem;
        ScanTransform = transform;
        LocalBounds = localBounds;
        OctreeBounds = octreeBounds;
        DeclaredPointCount = pointCount;
        HasColorData = hasColor;
        HasNormalData = hasNormal;
        HasIntensityData = hasIntensity;

        var scan = new RcsScan(this, path);
        _scans.Add(scan);

        _metadata["ScanGuid"] = guid;
        _metadata["OctreeVersion"] = $"{major}.{minor}";
        _metadata["PointCount"] = pointCount.ToString(CultureInfo.InvariantCulture);
        if (coordSystem != null) _metadata["CoordinateSystem"] = coordSystem;
    }

    /// <summary>Opens an .rcs file and parses its fixed-size header.</summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The header is truncated or invalid.</exception>
    public static RcsScanDocument Open(string path, PclReadOptions? options = null)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("RCS file not found.", path);

        using var fs = File.OpenRead(path);
        Span<byte> header = stackalloc byte[512];
        if (fs.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
            throw FormatError.Truncated("512-byte RCS header");

        // Container signature.
        if (!header[..5].SequenceEqual("ADOCT"u8))
            throw FormatError.Corrupted("Missing container signature in RCS file.");

        uint major = Bytes.ReadUInt32LE(header[8..12]);
        uint minor = Bytes.ReadUInt32LE(header[12..16]);

        // Translation, rotation, scale.
        double tx = Bytes.ReadDoubleLE(header[16..24]);
        double ty = Bytes.ReadDoubleLE(header[24..32]);
        double tz = Bytes.ReadDoubleLE(header[32..40]);

        double rx = Bytes.ReadDoubleLE(header[40..48]);
        double ry = Bytes.ReadDoubleLE(header[48..56]);
        double rz = Bytes.ReadDoubleLE(header[56..64]);

        double sx = Bytes.ReadDoubleLE(header[64..72]);
        double sy = Bytes.ReadDoubleLE(header[72..80]);
        double sz = Bytes.ReadDoubleLE(header[80..88]);

        if (!double.IsFinite(tx) || !double.IsFinite(ty) || !double.IsFinite(tz) ||
            !double.IsFinite(rx) || !double.IsFinite(ry) || !double.IsFinite(rz) ||
            !double.IsFinite(sx) || !double.IsFinite(sy) || !double.IsFinite(sz))
            throw FormatError.Corrupted("RCS header transform contains non-finite values.");

        // Octree bounds.
        double oMinX = Bytes.ReadDoubleLE(header[112..120]);
        double oMinY = Bytes.ReadDoubleLE(header[120..128]);
        double oMinZ = Bytes.ReadDoubleLE(header[128..136]);
        double oMaxX = Bytes.ReadDoubleLE(header[136..144]);
        double oMaxY = Bytes.ReadDoubleLE(header[144..152]);
        double oMaxZ = Bytes.ReadDoubleLE(header[152..160]);

        // Local bounding box.
        double lMinX = Bytes.ReadDoubleLE(header[160..168]);
        double lMinY = Bytes.ReadDoubleLE(header[168..176]);
        double lMinZ = Bytes.ReadDoubleLE(header[176..184]);
        double lMaxX = Bytes.ReadDoubleLE(header[184..192]);
        double lMaxY = Bytes.ReadDoubleLE(header[192..200]);
        double lMaxZ = Bytes.ReadDoubleLE(header[200..208]);

        ulong rawPoints = Bytes.ReadUInt64LE(header[216..224]);
        if (rawPoints > long.MaxValue) throw FormatError.Corrupted("RCS point count is out of range.");
        long pointCount = (long)rawPoints;

        // Scan identifier text at a fixed header offset.
        string guid = Encoding.ASCII.GetString(header.Slice(227, 38)).Trim('\0');

        // Optional coordinate system name.
        string? coordSystem = null;
        string cs = Bytes.ReadAsciiNullTerminated(header.Slice(0x1B9, 32)).Trim();
        if (!string.IsNullOrEmpty(cs)) coordSystem = cs;

        var transform = new PclTransform
        {
            Translation = new PclVector3(tx, ty, tz),
            Rotation = new PclVector3(rx, ry, rz),
            // A zero scale would collapse every point, so it is read as "not set".
            Scale = new PclVector3(sx == 0 ? 1 : sx, sy == 0 ? 1 : sy, sz == 0 ? 1 : sz)
        };

        var octreeBounds = new PclBounds(new PclVector3(oMinX, oMinY, oMinZ), new PclVector3(oMaxX, oMaxY, oMaxZ));
        var localBounds = new PclBounds(new PclVector3(lMinX, lMinY, lMinZ), new PclVector3(lMaxX, lMaxY, lMaxZ));

        return new RcsScanDocument(
            path,
            fi.Length,
            major,
            minor,
            guid,
            coordSystem,
            transform,
            localBounds,
            octreeBounds,
            pointCount,
            header[224] != 0,
            header[225] != 0,
            header[226] != 0);
    }
}


/// <summary>Individual scan of an RCS file.</summary>
public sealed class RcsScan : PclScan
{
    private const int SectionCountOffset = 297;
    private const int SectionEntrySize = 24;
    private const int LeafSize = 368;
    private const int PointSize = 16;
    private const int CellSide = 52;
    private const int FaceCells = CellSide * CellSide;
    private const ulong AxisMask = (1UL << 18) - 1;

    private readonly RcsScanDocument _document;
    private readonly string _path;
    private long _pointCount = -1;

    internal RcsScan(RcsScanDocument document, string filePath)
    {
        _document = document;
        _path = filePath;
    }

    private bool CanReadPoints => _document is { MajorVersion: 3, MinorVersion: 2 };

    /// <inheritdoc />
    public override string Id => _document.ScanGuid;

    /// <inheritdoc />
    public override string Name => Path.GetFileNameWithoutExtension(_path);

    /// <summary>Number of records in the stored leaves, or the header count for an unsupported version.</summary>
    public override long PointCount
    {
        get
        {
            if (!CanReadPoints) return _document.DeclaredPointCount;
            long stored = Volatile.Read(ref _pointCount);
            if (stored >= 0) return stored;
            using var stream = File.OpenRead(_path);
            ReadLeaves(stream, out long count);
            Volatile.Write(ref _pointCount, count);
            return count;
        }
    }

    /// <inheritdoc />
    public override PclBounds Bounds => _document.LocalBounds;

    /// <inheritdoc />
    public override PclTransform Transform => _document.ScanTransform;

    /// <inheritdoc />
    public override bool HasColors => _document.HasColorData;

    /// <inheritdoc />
    public override bool HasNormals => _document.HasNormalData;

    /// <inheritdoc />
    public override bool HasIntensities => _document.HasIntensityData;

    /// <summary>Streams the stored points in leaf order after validating the section and leaf ranges.</summary>
    public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null)
    {
        if (!CanReadPoints)
            throw FormatError.Unsupported($"RCS container version {_document.MajorVersion}.{_document.MinorVersion}; point decoding supports 3.2.");

        options ??= PclReadOptions.Default;
        long limit = options.MaxPoints ?? long.MaxValue;
        if (limit <= 0) yield break;

        using var stream = File.OpenRead(_path);
        List<Leaf> leaves = ReadLeaves(stream, out long count);
        Volatile.Write(ref _pointCount, count);
        var chunk = new byte[64 * 1024];
        int step = Math.Max(1, options.SubsampleStep);
        long read = 0, emitted = 0;
        foreach (Leaf leaf in leaves)
        {
            stream.Position = leaf.PointsAt;
            long remaining = leaf.Count;
            while (remaining > 0)
            {
                int records = (int)Math.Min(remaining, chunk.Length / PointSize);
                ReadExactly(stream, chunk.AsSpan(0, records * PointSize));
                for (int i = 0; i < records; i++)
                {
                    if (read++ % step != 0) continue;
                    PclPoint point = DecodePoint(chunk.AsSpan(i * PointSize, PointSize), leaf.Corner, options.ApplyTransform, (ulong)emitted);
                    yield return point;
                    if (++emitted == limit) yield break;
                }

                remaining -= records;
            }
        }
    }

    private PclPoint DecodePoint(ReadOnlySpan<byte> record, PclVector3 corner, bool applyTransform, ulong index)
    {
        ulong positionBits = Bytes.ReadUInt64LE(record);
        var position = corner + new PclVector3((positionBits & AxisMask) * 0.001,
            ((positionBits >> 18) & AxisMask) * 0.001, ((positionBits >> 36) & AxisMask) * 0.001);
        PclVector3? normal = HasNormals ? DecodeNormal(Bytes.ReadUInt16LE(record[12..])) : null;
        if (applyTransform)
        {
            position = Transform.TransformPoint(position);
            if (normal.HasValue && Transform.Scale != PclVector3.One)
                normal = Transform.TransformVector(normal.Value);
        }

        return new PclPoint(position, Color: HasColors ? new PclColor(record[11], record[10], record[9]) : null,
            Intensity: HasIntensities ? record[8] / 255f : null, Normal: normal, Index: index);
    }

    private static PclVector3? DecodeNormal(ushort code)
    {
        if (code == 0 || code >= 6 * FaceCells) return null;

        int face = code / FaceCells;
        int cell = code % FaceCells;
        double u = CellSlope(cell % CellSide);
        double v = CellSlope(cell / CellSide);
        double side = (face & 1) == 0 ? 1 : -1;
        var direction = (face / 2) switch
        {
            0 => new PclVector3(side, u, v),
            1 => new PclVector3(u, side, v),
            _ => new PclVector3(u, v, side),
        };
        return direction * (1 / Math.Sqrt(1 + u * u + v * v));

        static double CellSlope(int index) => Math.Tan(Math.PI / 2 * ((index + 0.5) / CellSide - 0.5));
    }

    private readonly record struct FileRange(long Start, long End)
    {
        public long Length => End - Start;
    }

    private readonly record struct Leaf(long PointsAt, uint Count, PclVector3 Corner);

    private static List<Leaf> ReadLeaves(FileStream stream, out long pointCount)
    {
        var ranges = ReadSectionRanges(stream);
        if (!ranges.TryGetValue(1, out FileRange points) || !ranges.TryGetValue(2, out FileRange leaves))
            throw FormatError.Corrupted("RCS needs both a points section and a leaf section.");

        if (points.Length != 0 && points.Start < leaves.End && leaves.Start < points.End)
            throw FormatError.Corrupted("RCS point and leaf sections overlap.");
        if (leaves.Length < 8)
            throw FormatError.Truncated("the RCS leaf section header");
        stream.Position = leaves.Start;
        Span<byte> scratch = stackalloc byte[LeafSize];
        ReadExactly(stream, scratch[..8]);
        uint leafCount = Bytes.ReadUInt32LE(scratch);
        if (leafCount > 500_000 || leaves.Length != 8 + (long)leafCount * LeafSize)
            throw FormatError.Corrupted("RCS leaf count does not match the section size or exceeds the limit.");

        var plan = new List<Leaf>((int)leafCount);
        long cursor = points.Start;
        pointCount = 0;
        for (int i = 0; i < leafCount; i++)
        {
            ReadExactly(stream, scratch);
            uint records = Bytes.ReadUInt32LE(scratch[100..]);
            uint blockBytes = Bytes.ReadUInt32LE(scratch[104..]);
            long recordBytes = (long)records * PointSize;
            long prefixBytes = blockBytes - recordBytes;
            if (prefixBytes < (long)records * sizeof(uint) || prefixBytes % sizeof(uint) != 0 ||
                blockBytes > points.End - cursor)
                throw FormatError.Corrupted("RCS leaf block does not fit its point count or the points section.");

            var corner = new PclVector3(Bytes.ReadDoubleLE(scratch), Bytes.ReadDoubleLE(scratch[8..]), Bytes.ReadDoubleLE(scratch[16..]));
            if (!double.IsFinite(corner.X) || !double.IsFinite(corner.Y) || !double.IsFinite(corner.Z))
                throw FormatError.Corrupted("RCS leaf origin is not finite.");

            plan.Add(new Leaf(cursor + prefixBytes, records, corner));
            cursor += blockBytes;
            pointCount += records;
        }

        if (cursor != points.End)
            throw FormatError.Corrupted("RCS leaf blocks do not fill the points section.");
        return plan;
    }

    private static Dictionary<ulong, FileRange> ReadSectionRanges(FileStream stream)
    {
        stream.Position = SectionCountOffset;
        Span<byte> entry = stackalloc byte[SectionEntrySize];
        ReadExactly(stream, entry[..4]);
        uint count = Bytes.ReadUInt32LE(entry);
        if (count is 0 or > 64) throw FormatError.Corrupted("RCS section count is out of range.");
        long tableEnd = stream.Position + (long)count * SectionEntrySize;
        if (tableEnd > stream.Length) throw FormatError.Truncated("the RCS section table");

        var result = new Dictionary<ulong, FileRange>((int)count);
        ulong length = (ulong)stream.Length;
        for (int i = 0; i < count; i++)
        {
            ReadExactly(stream, entry);
            ulong type = Bytes.ReadUInt64LE(entry);
            ulong start = Bytes.ReadUInt64LE(entry[8..]);
            ulong size = Bytes.ReadUInt64LE(entry[16..]);
            if (start > length || size > length - start || (size > 0 && start < (ulong)tableEnd))
                throw FormatError.Truncated("an RCS section outside the file");
            if (!result.TryAdd(type, new FileRange((long)start, (long)(start + size))))
                throw FormatError.Corrupted("RCS section type appears twice.");
        }
        return result;
    }

    private static void ReadExactly(FileStream stream, Span<byte> buffer)
    {
        if (stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) != buffer.Length)
            throw FormatError.Truncated("the RCS point data");
    }
}
