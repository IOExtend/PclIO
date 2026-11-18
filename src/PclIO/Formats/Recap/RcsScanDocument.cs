using System.Globalization;
using System.Text;
using PclIO.Internal;

namespace PclIO.Formats.Recap;

/// <summary>
/// Reads the header of a ReCap scan file (.rcs). Point decoding is experimental: only the node layouts that are
/// described in <see cref="RcsLayout"/> are decoded, and any other version is rejected instead of guessed.
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

    /// <summary>Point count declared in the header; it is not cross-checked against the node table.</summary>
    public long DeclaredPointCount { get; }

    /// <summary>True when the file holds colour for its points; it is read from the node table.</summary>
    public bool HasColorData => _scans.Count > 0 && _scans[0].HasColors;

    /// <summary>
    /// True when the file can hold normals for its points; it is read from the node table, and a single point may still
    /// have none.
    /// </summary>
    public bool HasNormalData => _scans.Count > 0 && _scans[0].HasNormals;

    /// <summary>True when the file holds an intensity for its points; it is read from the node table.</summary>
    public bool HasIntensityData => _scans.Count > 0 && _scans[0].HasIntensities;

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
        long pointCount)
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
            pointCount);
    }
}

/// <summary>
/// The layout of the node table of a ReCap scan file, one per container major version.
/// </summary>
/// <remarks>
/// A layout is described only when the files of that version may be used for the tests, so that its offsets can be
/// verified. A file of a version without a described layout is listed with its header, but its points cannot be read:
/// the reader rejects it with a message that names the version.
/// </remarks>
/// <param name="MajorVersion">Major version of the container that the layout belongs to.</param>
/// <param name="NodeTableOffset">Offset of the node table in the file, in bytes.</param>
/// <param name="NodeSize">Size of one entry of the node table, in bytes.</param>
internal sealed record RcsLayout(uint MajorVersion, long NodeTableOffset, int NodeSize)
{
    // The layout of major version 3: the node table starts at a fixed offset and the node payloads occupy the end
    // of the file.
    private static readonly RcsLayout Version3 = new(3, 98605, 368);

    private static readonly RcsLayout[] Described = [Version3];

    /// <summary>The layout of the given major version, or null when the version has no described layout.</summary>
    public static RcsLayout? ForVersion(uint majorVersion) =>
        Described.FirstOrDefault(l => l.MajorVersion == majorVersion);

    /// <summary>The major versions whose layout is described, for a message that names them.</summary>
    public static string DescribedVersions => string.Join(", ", Described.Select(l => l.MajorVersion));
}

/// <summary>Individual scan of an RCS file.</summary>
public sealed class RcsScan : PclScan
{
    // Layout of major version 3: the node table starts at a fixed offset and the node
    // payloads occupy the end of the file. Both are verified against the file length before use.
    private const ulong MaxNodes = 500_000;

    // Bytes of one point record: the position of the point and, when the node holds them, the intensity and the
    // colour. A record without them is shorter.
    private const int PointRecordSize = 16;
    private const int PositionRecordSize = 10;
    private const int NormalRecordSize = 4;
    private const double Quantization = 0.0001;

    private readonly RcsScanDocument _document;
    private readonly string _filePath;

    private (bool Colors, bool Intensities, bool Normals)? _channels;

    /// <inheritdoc />
    public override string Id => _document.ScanGuid;

    /// <inheritdoc />
    public override string Name => Path.GetFileNameWithoutExtension(_filePath);

    /// <summary>Point count declared in the file header; it is not cross-checked against the node table.</summary>
    public override long PointCount => _document.DeclaredPointCount;

    /// <inheritdoc />
    public override PclBounds Bounds => _document.LocalBounds;

    /// <inheritdoc />
    public override PclTransform Transform => _document.ScanTransform;

    /// <summary>True when the file holds colour for its points; it is read from the node table.</summary>
    public override bool HasColors => Channels.Colors;

    /// <summary>True when the file holds normals for its points; it is read from the node table.</summary>
    public override bool HasNormals => Channels.Normals;

    /// <summary>True when the file holds an intensity for its points; it is read from the node table.</summary>
    public override bool HasIntensities => Channels.Intensities;

    internal RcsScan(RcsScanDocument document, string filePath)
    {
        _document = document;
        _filePath = filePath;
    }

    private readonly record struct Node(PclVector3 TightMin, uint Points, uint Bytes);

    // The channels of the points follow from the size of the payload of the nodes.
    private (bool Colors, bool Intensities, bool Normals) Channels => _channels ??= DetectChannels();

    private (bool, bool, bool) DetectChannels()
    {
        var layout = RcsLayout.ForVersion(_document.MajorVersion);
        if (layout == null) return (false, false, false);

        try
        {
            using var fs = File.OpenRead(_filePath);
            var nodes = ReadNodeTable(fs, layout, out _);

            bool colors = false, intensities = false, normals = false;
            foreach (var node in nodes)
            {
                if (node.Points == 0) continue;
                long bytesPerPoint = node.Bytes / node.Points;
                if (bytesPerPoint >= PointRecordSize) { colors = true; intensities = true; }
                if (node.Bytes >= (long)node.Points * (PointRecordSize + NormalRecordSize)) normals = true;
            }

            return (colors, intensities, normals);
        }
        catch (InvalidDataException)
        {
            // A damaged node table leaves the channels unknown.
            return (false, false, false);
        }
    }

    /// <summary>
    /// Streams points from the node table (experimental, major version 3 only). Throws
    /// <see cref="NotSupportedException"/> for other versions or node payloads that do not match the known layout,
    /// and <see cref="InvalidDataException"/> when the node table is inconsistent with the file size.
    /// Scale and translation of the header transform are applied when requested; rotation is not.
    /// </summary>
    public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null)
    {
        options ??= PclReadOptions.Default;
        if (PointCount == 0) yield break;

        var layout = RcsLayout.ForVersion(_document.MajorVersion)
            ?? throw FormatError.Unsupported(
                $"RCS container version {_document.MajorVersion}.{_document.MinorVersion}: the layout of its node table is not described. The described versions are {RcsLayout.DescribedVersions}.");

        long targetCount = options.MaxPoints ?? PointCount;
        int step = Math.Max(1, options.SubsampleStep);
        long yielded = 0;

        using var fs = File.OpenRead(_filePath);
        var nodes = ReadNodeTable(fs, layout, out long payloadStart);

        long curOffset = payloadStart;
        byte[] chunkBuf = new byte[65536];
        // Normals are read per chunk, so memory stays constant however large a node is.
        byte[] normalBuf = new byte[chunkBuf.Length / PointRecordSize * NormalRecordSize];

        foreach (var node in nodes)
        {
            if (yielded >= targetCount) yield break;
            if (node.Points == 0 || node.Bytes == 0)
            {
                curOffset += node.Bytes;
                continue;
            }

            // The size of the payload tells which channels the points of the node hold: a record with the intensity
            // and the colour is longer than one with the position only.
            long bytesPerPoint = node.Bytes / node.Points;
            bool hasColor = bytesPerPoint >= PointRecordSize;
            int recordSize = hasColor ? PointRecordSize : PositionRecordSize;

            // The optional normal plane follows the point records.
            bool hasNormals = node.Bytes >= (long)node.Points * (PointRecordSize + NormalRecordSize);
            long normalStart = curOffset + ((long)node.Points * recordSize);

            fs.Position = curOffset;
            long remaining = (long)node.Points * recordSize;
            long pointIdx = 0;

            while (remaining > 0)
            {
                // A chunk holds whole records only, so the next one starts at a record boundary.
                int toRead = (int)Math.Min(chunkBuf.Length / recordSize * recordSize, remaining);
                fs.ReadExactly(chunkBuf, 0, toRead);
                remaining -= toRead;

                int ptsInBlock = toRead / recordSize;
                if (hasNormals)
                {
                    long resume = fs.Position;
                    fs.Position = normalStart + (pointIdx * NormalRecordSize);
                    fs.ReadExactly(normalBuf, 0, ptsInBlock * NormalRecordSize);
                    fs.Position = resume;
                }

                for (int p = 0; p < ptsInBlock; p++, pointIdx++)
                {
                    if (pointIdx % step != 0) continue;

                    int off = p * recordSize;
                    ushort v1 = Bytes.ReadUInt16LE(chunkBuf.AsSpan(off + 4, 2));
                    ushort v2 = Bytes.ReadUInt16LE(chunkBuf.AsSpan(off + 6, 2));
                    ushort v3 = Bytes.ReadUInt16LE(chunkBuf.AsSpan(off + 8, 2));

                    // Fixed-step quantization relative to the node minimum.
                    double x = node.TightMin.X + (v1 * Quantization);
                    double y = node.TightMin.Y + (v2 * Quantization);
                    double z = node.TightMin.Z + (v3 * Quantization);

                    // Packed normal: signed byte components; an all-zero normal means none.
                    PclVector3? normal = null;
                    if (hasNormals)
                    {
                        int n = p * NormalRecordSize;
                        sbyte nx = (sbyte)normalBuf[n];
                        sbyte ny = (sbyte)normalBuf[n + 1];
                        sbyte nz = (sbyte)normalBuf[n + 2];
                        if (nx != 0 || ny != 0 || nz != 0)
                        {
                            double len = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
                            var norm = new PclVector3(nx / len, ny / len, nz / len);
                            normal = options.ApplyTransform ? Transform.TransformVector(norm) : norm;
                        }
                    }

                    // A node without colour and intensity holds the position of the point only.
                    PclColor? color = null;
                    float? intensity = null;
                    if (hasColor)
                    {
                        color = new PclColor(chunkBuf[off + 13], chunkBuf[off + 14], chunkBuf[off + 15]);
                        intensity = chunkBuf[off + 12] / 255.0f;
                    }

                    var pt = new PclPoint(
                        new PclVector3(x, y, z),
                        Color: color,
                        Intensity: intensity,
                        Normal: normal,
                        Index: (ulong)yielded);

                    if (options.ApplyTransform)
                        pt = pt with { Position = Transform.TransformPoint(pt.Position) };

                    yield return pt;
                    yielded++;
                    if (yielded >= targetCount) yield break;
                }
            }

            curOffset += node.Bytes;
        }
    }

    // Reads and validates the node table; payloadStart is where the first node payload begins.
    private static List<Node> ReadNodeTable(FileStream fs, RcsLayout layout, out long payloadStart)
    {
        if (fs.Length < layout.NodeTableOffset + 8) throw FormatError.Truncated("RCS node table");

        fs.Position = layout.NodeTableOffset;
        Span<byte> countBuf = stackalloc byte[8];
        fs.ReadExactly(countBuf);
        ulong numNodes = Bytes.ReadUInt64LE(countBuf);

        long tableEnd = layout.NodeTableOffset + 8 + ((long)Math.Min(numNodes, MaxNodes + 1) * layout.NodeSize);
        if (numNodes > MaxNodes || tableEnd > fs.Length)
            throw FormatError.Corrupted("RCS node table does not fit in the file.");

        var nodes = new List<Node>((int)numNodes);
        byte[] nodeBuf = new byte[layout.NodeSize];
        long totalPayloadBytes = 0;

        for (ulong i = 0; i < numNodes; i++)
        {
            fs.ReadExactly(nodeBuf, 0, layout.NodeSize);

            double tMinX = Bytes.ReadDoubleLE(nodeBuf.AsSpan(0x30, 8));
            double tMinY = Bytes.ReadDoubleLE(nodeBuf.AsSpan(0x38, 8));
            double tMinZ = Bytes.ReadDoubleLE(nodeBuf.AsSpan(0x40, 8));
            double tMaxX = Bytes.ReadDoubleLE(nodeBuf.AsSpan(0x48, 8));
            double tMaxY = Bytes.ReadDoubleLE(nodeBuf.AsSpan(0x50, 8));
            double tMaxZ = Bytes.ReadDoubleLE(nodeBuf.AsSpan(0x58, 8));

            uint npts = Bytes.ReadUInt32LE(nodeBuf.AsSpan(0x64, 4));
            uint nbytes = Bytes.ReadUInt32LE(nodeBuf.AsSpan(0x68, 4));

            if (npts > 0 && nbytes > 0)
            {
                if (!double.IsFinite(tMinX) || !double.IsFinite(tMinY) || !double.IsFinite(tMinZ) ||
                    !double.IsFinite(tMaxX) || !double.IsFinite(tMaxY) || !double.IsFinite(tMaxZ) ||
                    tMinX > tMaxX || tMinY > tMaxY || tMinZ > tMaxZ)
                    throw FormatError.Corrupted("RCS node bounds are invalid.");

                // The decoder reads records of exactly ten bytes (position), sixteen (position, colour and
                // intensity) or twenty (the same plus the plane of the normals). Any other payload size would be
                // read with the wrong stride and return wrong positions, so it is rejected instead of guessed.
                if (nbytes != (long)npts * PositionRecordSize &&
                    nbytes != (long)npts * PointRecordSize &&
                    nbytes != (long)npts * (PointRecordSize + NormalRecordSize))
                    throw FormatError.Unsupported("RCS node payload does not match a known point record layout.");
            }

            nodes.Add(new Node(new PclVector3(tMinX, tMinY, tMinZ), npts, nbytes));
            totalPayloadBytes += nbytes;
        }

        payloadStart = fs.Length - totalPayloadBytes;
        if (payloadStart < tableEnd)
            throw FormatError.Corrupted("RCS node payload sizes exceed the file size.");

        return nodes;
    }
}
