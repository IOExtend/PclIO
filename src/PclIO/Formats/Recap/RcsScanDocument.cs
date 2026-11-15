using System.Globalization;
using System.Text;
using PclIO.Internal;

namespace PclIO.Formats.Recap;

/// <summary>
/// Reads the header of a ReCap scan file (.rcs). Point decoding is experimental: only the node layout of
/// major version 3 is supported, and any other layout is rejected instead of guessed.
/// </summary>
public sealed class RcsScanDocument : PclDocument
{
    /// <inheritdoc />
    public override PclFileKind Kind => PclFileKind.RecapScan;

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

    /// <summary>Always true: a constant of the supported layout, whose point record always has colour fields. It is not read from the file.</summary>
    public bool HasColorData { get; }

    /// <summary>
    /// Always true: a constant of the supported layout, which can carry normals. It is not read from the file; each
    /// node decides whether its points have one, so single points may have none.
    /// </summary>
    public bool HasNormalData { get; }

    /// <summary>Always true: a constant of the supported layout, whose point record always has an intensity field. It is not read from the file.</summary>
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
        HasColorData = true;
        HasNormalData = true;
        HasIntensityData = true;

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

/// <summary>Individual scan of an RCS file.</summary>
public sealed class RcsScan : PclScan
{
    // Layout of major version 3: the node table starts at a fixed offset and the node
    // payloads occupy the end of the file. Both are verified against the file length before use.
    private const uint SupportedMajorVersion = 3;
    private const long NodeTableOffset = 98605;
    private const int NodeSize = 368;
    private const ulong MaxNodes = 500_000;
    private const int PointRecordSize = 16;
    private const int NormalRecordSize = 4;
    private const double Quantization = 0.0001;

    private readonly RcsScanDocument _document;
    private readonly string _filePath;

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

    /// <summary>Always true: a fixed constant of the supported layout, not read from the file.</summary>
    public override bool HasColors => _document.HasColorData;

    /// <summary>Always true: a fixed constant of the supported layout, not read from the file; single points may still have no normal.</summary>
    public override bool HasNormals => _document.HasNormalData;

    /// <summary>Always true: a fixed constant of the supported layout, not read from the file.</summary>
    public override bool HasIntensities => _document.HasIntensityData;

    internal RcsScan(RcsScanDocument document, string filePath)
    {
        _document = document;
        _filePath = filePath;
    }

    private readonly record struct Node(PclVector3 TightMin, uint Points, uint Bytes);

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
        if (_document.MajorVersion != SupportedMajorVersion)
            throw FormatError.Unsupported(
                $"RCS container version {_document.MajorVersion}.{_document.MinorVersion}; only major version {SupportedMajorVersion} is decoded.");

        long targetCount = options.MaxPoints ?? PointCount;
        int step = Math.Max(1, options.SubsampleStep);
        long yielded = 0;

        using var fs = File.OpenRead(_filePath);
        var nodes = ReadNodeTable(fs, out long payloadStart);

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

            // The optional normal plane follows the point records.
            bool hasNormals = node.Bytes >= (long)node.Points * (PointRecordSize + NormalRecordSize);
            long normalStart = curOffset + ((long)node.Points * PointRecordSize);

            fs.Position = curOffset;
            long remaining = (long)node.Points * PointRecordSize;
            long pointIdx = 0;

            while (remaining > 0)
            {
                int toRead = (int)Math.Min(chunkBuf.Length, remaining);
                fs.ReadExactly(chunkBuf, 0, toRead);
                remaining -= toRead;

                int ptsInBlock = toRead / PointRecordSize;
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

                    int off = p * PointRecordSize;
                    ushort v1 = Bytes.ReadUInt16LE(chunkBuf.AsSpan(off + 4, 2));
                    ushort v2 = Bytes.ReadUInt16LE(chunkBuf.AsSpan(off + 6, 2));
                    ushort v3 = Bytes.ReadUInt16LE(chunkBuf.AsSpan(off + 8, 2));

                    byte intensityByte = chunkBuf[off + 12];
                    byte red = chunkBuf[off + 13];
                    byte green = chunkBuf[off + 14];
                    byte blue = chunkBuf[off + 15];

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

                    var pt = new PclPoint(
                        new PclVector3(x, y, z),
                        Color: new PclColor(red, green, blue),
                        Intensity: intensityByte / 255.0f,
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
    private static List<Node> ReadNodeTable(FileStream fs, out long payloadStart)
    {
        if (fs.Length < NodeTableOffset + 8) throw FormatError.Truncated("RCS node table");

        fs.Position = NodeTableOffset;
        Span<byte> countBuf = stackalloc byte[8];
        fs.ReadExactly(countBuf);
        ulong numNodes = Bytes.ReadUInt64LE(countBuf);

        long tableEnd = NodeTableOffset + 8 + ((long)Math.Min(numNodes, MaxNodes + 1) * NodeSize);
        if (numNodes > MaxNodes || tableEnd > fs.Length)
            throw FormatError.Corrupted("RCS node table does not fit in the file.");

        var nodes = new List<Node>((int)numNodes);
        byte[] nodeBuf = new byte[NodeSize];
        long totalPayloadBytes = 0;

        for (ulong i = 0; i < numNodes; i++)
        {
            fs.ReadExactly(nodeBuf, 0, NodeSize);

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

                if (nbytes < (long)npts * PointRecordSize)
                    throw FormatError.Unsupported("RCS node payload is smaller than the known point record layout.");
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
