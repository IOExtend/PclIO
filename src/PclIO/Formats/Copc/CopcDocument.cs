using System.Globalization;
using PclIO.Internal;

namespace PclIO.Formats.Copc;

/// <summary>Represents a Cloud Optimized Point Cloud (COPC) document containing a clustered octree in LAS 1.4 / LAZ.</summary>
public sealed class CopcDocument : PclDocument
{
    private const int MinLas14HeaderSize = 375;
    private const int MaxVlrs = 4096;

    /// <inheritdoc />
    public override PclFileKind Kind => PclFileKind.Copc;

    /// <summary>Octree center coordinates declared in the COPC info record.</summary>
    public PclVector3 Center => _info.Center;

    /// <summary>Half of the root octree voxel size along each axis.</summary>
    public double HalfSize => _info.HalfSize;

    /// <summary>Spacing parameter declared in the COPC info record.</summary>
    public double Spacing => _info.Spacing;

    /// <summary>LAS coordinate scale factor applied to raw integer coordinates.</summary>
    public PclVector3 Scale { get; }

    /// <summary>LAS coordinate offset added to scaled coordinates.</summary>
    public PclVector3 Offset { get; }

    /// <summary>LAS point data record format (e.g. 0 to 3, 6 to 8).</summary>
    public int PointFormat { get; }

    /// <summary>Point data record length in bytes.</summary>
    public int RecordLength { get; }

    /// <summary>LAS version string (e.g. "1.4").</summary>
    public string Version { get; }

    /// <summary>Absolute file offset of the root octree hierarchy page.</summary>
    public ulong RootHierarchyOffset => _info.RootHierarchyOffset;

    /// <summary>Size of the root octree hierarchy page in bytes.</summary>
    public ulong RootHierarchySize => _info.RootHierarchySize;

    /// <summary>Minimum GPS time declared in the COPC record, or null when absent.</summary>
    public double? GpsTimeMin => _info.GpsTimeMin;

    /// <summary>Maximum GPS time declared in the COPC record, or null when absent.</summary>
    public double? GpsTimeMax => _info.GpsTimeMax;

    /// <summary>Bounding box of the root octree voxel enclosing the point cloud.</summary>
    public PclBounds OctreeBounds { get; }

    /// <summary>Resolved octree nodes containing point records.</summary>
    public IReadOnlyList<CopcNode> Nodes => _nodes;
    private readonly List<CopcNode> _nodes;

    /// <inheritdoc />
    public override IReadOnlyList<PclScan> Scans => _scans;
    private readonly List<PclScan> _scans = [];
    private readonly CopcScan _primaryScan;
    private readonly CopcInfo _info;

    private CopcDocument(
        string path,
        long fileSize,
        string version,
        int pointFormat,
        int recordLength,
        PclVector3 scale,
        PclVector3 offset,
        PclBounds bounds,
        PclBounds octreeBounds,
        CopcInfo info,
        List<CopcNode> nodes,
        long pointCount)
        : base(path, fileSize)
    {
        Version = version;
        PointFormat = pointFormat;
        RecordLength = recordLength;
        Scale = scale;
        Offset = offset;
        OctreeBounds = octreeBounds;
        _info = info;
        _nodes = nodes;

        _primaryScan = new CopcScan(
            id: "copc_root",
            name: "COPC Octree",
            pointCount: pointCount,
            bounds: bounds,
            filePath: path,
            pointFormat: pointFormat,
            recordLength: recordLength,
            scale: scale,
            offset: offset,
            nodes: nodes);
        _scans.Add(_primaryScan);

        _metadata["Format"] = "Cloud Optimized Point Cloud (COPC)";
        _metadata["Version"] = version;
        _metadata["PointFormat"] = pointFormat.ToString(CultureInfo.InvariantCulture);
        _metadata["NodeCount"] = nodes.Count.ToString(CultureInfo.InvariantCulture);
        _metadata["Center"] = string.Create(CultureInfo.InvariantCulture, $"{Center.X:G6}, {Center.Y:G6}, {Center.Z:G6}");
        _metadata["HalfSize"] = HalfSize.ToString(CultureInfo.InvariantCulture);
        _metadata["Spacing"] = Spacing.ToString(CultureInfo.InvariantCulture);
        _metadata["TotalPoints"] = pointCount.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Opens a COPC file and reads its header, COPC metadata and octree hierarchy.</summary>
    /// <param name="path">Path of the COPC file (.copc.laz or .laz).</param>
    /// <param name="options">Read options (accepted for symmetry).</param>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is malformed, truncated, or has corrupted hierarchy pages.</exception>
    /// <exception cref="NotSupportedException">The LAS version or compression feature is not supported.</exception>
    public static CopcDocument Open(string path, PclReadOptions? options = null)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("COPC file not found.", path);

        using var fs = File.OpenRead(path);
        long fileLength = fs.Length;

        if (fileLength < 4) throw FormatError.Truncated("COPC header");
        byte[] signature = new byte[4];
        fs.ReadExactly(signature);
        if (!signature.AsSpan().SequenceEqual("LASF"u8))
            throw FormatError.Corrupted("not a LAS/COPC file, signature mismatch.");

        if (fileLength < MinLas14HeaderSize) throw FormatError.Truncated("COPC LAS 1.4 header");
        fs.Position = 0;
        byte[] header = new byte[MinLas14HeaderSize];
        fs.ReadExactly(header);

        int major = header[24];
        int minor = header[25];
        if (major != 1 || minor < 4)
            throw FormatError.Unsupported($"COPC requires LAS version 1.4 or higher; file declares version {major}.{minor}.");

        ushort headerSize = Bytes.ReadUInt16LE(header.AsSpan(94, 2));
        if (headerSize < MinLas14HeaderSize)
            throw FormatError.Corrupted($"COPC header size {headerSize} is smaller than minimum LAS 1.4 size {MinLas14HeaderSize}.");
        if (headerSize > fileLength) throw FormatError.Truncated("COPC header");

        uint pointDataOffset = Bytes.ReadUInt32LE(header.AsSpan(96, 4));
        if (pointDataOffset < headerSize || pointDataOffset > fileLength)
            throw FormatError.Corrupted("COPC offset to point data is outside the file.");

        uint vlrCount = Bytes.ReadUInt32LE(header.AsSpan(100, 4));
        if (vlrCount > MaxVlrs)
            throw FormatError.Corrupted($"COPC file declares {vlrCount} variable length records; at most {MaxVlrs} are accepted.");

        int rawPointFormatByte = header[104];
        int pointFormat = rawPointFormatByte & 0x3F;
        int recordLength = Bytes.ReadUInt16LE(header.AsSpan(105, 2));
        if (recordLength < 20)
            throw FormatError.Corrupted($"COPC point record length {recordLength} is smaller than 20 bytes.");

        uint legacyPointCount = Bytes.ReadUInt32LE(header.AsSpan(107, 4));
        ulong extendedPointCount = Bytes.ReadUInt64LE(header.AsSpan(247, 8));
        long pointCount = (long)(extendedPointCount != 0 ? extendedPointCount : legacyPointCount);

        double scaleX = Bytes.ReadDoubleLE(header.AsSpan(131, 8));
        double scaleY = Bytes.ReadDoubleLE(header.AsSpan(139, 8));
        double scaleZ = Bytes.ReadDoubleLE(header.AsSpan(147, 8));
        if (!double.IsFinite(scaleX) || !double.IsFinite(scaleY) || !double.IsFinite(scaleZ) ||
            scaleX == 0 || scaleY == 0 || scaleZ == 0)
            throw FormatError.Corrupted("COPC scale factors must be non-zero and finite.");

        double offsetX = Bytes.ReadDoubleLE(header.AsSpan(155, 8));
        double offsetY = Bytes.ReadDoubleLE(header.AsSpan(163, 8));
        double offsetZ = Bytes.ReadDoubleLE(header.AsSpan(171, 8));
        if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY) || !double.IsFinite(offsetZ))
            throw FormatError.Corrupted("COPC coordinate offset values must be finite.");

        double maxX = Bytes.ReadDoubleLE(header.AsSpan(179, 8));
        double minX = Bytes.ReadDoubleLE(header.AsSpan(187, 8));
        double maxY = Bytes.ReadDoubleLE(header.AsSpan(195, 8));
        double minY = Bytes.ReadDoubleLE(header.AsSpan(203, 8));
        double maxZ = Bytes.ReadDoubleLE(header.AsSpan(211, 8));
        double minZ = Bytes.ReadDoubleLE(header.AsSpan(219, 8));

        var bounds = (!double.IsFinite(maxX) || !double.IsFinite(minX) ||
                      !double.IsFinite(maxY) || !double.IsFinite(minY) ||
                      !double.IsFinite(maxZ) || !double.IsFinite(minZ))
            ? PclBounds.Empty
            : new PclBounds(new PclVector3(minX, minY, minZ), new PclVector3(maxX, maxY, maxZ));

        // Read VLRs to find the COPC info VLR and optional coordinate system.
        var (copcInfo, crs) = ReadVlrs(fs, headerSize, pointDataOffset, vlrCount);
        if (copcInfo == null)
            throw FormatError.Corrupted("COPC file is missing the required COPC info VLR (user 'copc', record 1).");

        if (copcInfo.HalfSize <= 0 || !double.IsFinite(copcInfo.HalfSize))
            throw FormatError.Corrupted($"Invalid COPC halfsize {copcInfo.HalfSize}; must be positive and finite.");

        var octreeBounds = new PclBounds(
            new PclVector3(copcInfo.Center.X - copcInfo.HalfSize, copcInfo.Center.Y - copcInfo.HalfSize, copcInfo.Center.Z - copcInfo.HalfSize),
            new PclVector3(copcInfo.Center.X + copcInfo.HalfSize, copcInfo.Center.Y + copcInfo.HalfSize, copcInfo.Center.Z + copcInfo.HalfSize));

        // Read octree hierarchy
        var nodes = CopcHierarchy.ReadHierarchy(fs, copcInfo);

        var doc = new CopcDocument(
            path,
            fileLength,
            $"{major}.{minor}",
            pointFormat,
            recordLength,
            new PclVector3(scaleX, scaleY, scaleZ),
            new PclVector3(offsetX, offsetY, offsetZ),
            bounds,
            octreeBounds,
            copcInfo,
            nodes,
            pointCount);

        if (crs != null) doc._metadata["CoordinateSystem"] = crs;
        return doc;
    }

    /// <summary>Finds octree nodes whose voxel bounding box intersects the given spatial bounds.</summary>
    /// <param name="bounds">Query bounding box.</param>
    /// <param name="maxLevel">Optional maximum octree level to include.</param>
    public IReadOnlyList<CopcNode> FindNodes(PclBounds bounds, int? maxLevel = null)
    {
        if (bounds.IsEmpty) return Array.Empty<CopcNode>();
        return _nodes
            .Where(n => (maxLevel == null || n.Key.Level <= maxLevel.Value) && n.Bounds.Intersects(bounds))
            .ToList();
    }

    /// <summary>Streams points from a single octree node.</summary>
    /// <param name="node">The octree node to read points from.</param>
    /// <param name="options">Read options (subsampling, limits, transforms).</param>
    public IEnumerable<PclPoint> ReadPoints(CopcNode node, PclReadOptions? options = null)
    {
        return _primaryScan.ReadPointsFromNodes([node], options);
    }

    /// <summary>Streams points from octree nodes intersecting the given bounding box.</summary>
    /// <param name="bounds">Spatial bounding box filter.</param>
    /// <param name="options">Read options (subsampling, limits, transforms).</param>
    public IEnumerable<PclPoint> ReadPoints(PclBounds bounds, PclReadOptions? options = null)
    {
        var matchingNodes = FindNodes(bounds);
        return _primaryScan.ReadPointsFromNodes(matchingNodes, options);
    }

    private static (CopcInfo? Info, string? Crs) ReadVlrs(FileStream fs, int headerSize, uint pointDataOffset, uint vlrCount)
    {
        CopcInfo? copcInfo = null;
        string? crs = null;
        long position = headerSize;
        byte[] vlrHeader = new byte[54];

        for (uint i = 0; i < vlrCount; i++)
        {
            if (position + 54 > pointDataOffset)
                throw FormatError.Corrupted("COPC variable length record reaches past the point data.");

            fs.Position = position;
            fs.ReadExactly(vlrHeader);
            position += 54;

            string userId = ReadFixedAscii(vlrHeader.AsSpan(2, 16));
            ushort recordId = Bytes.ReadUInt16LE(vlrHeader.AsSpan(18, 2));
            ushort dataLength = Bytes.ReadUInt16LE(vlrHeader.AsSpan(20, 2));

            if (position + dataLength > pointDataOffset)
                throw FormatError.Corrupted("COPC variable length record reaches past the point data.");

            if (userId.Equals("copc", StringComparison.Ordinal) && recordId == 1)
            {
                if (dataLength != 160)
                    throw FormatError.Corrupted($"COPC info record has invalid length {dataLength} bytes (expected 160).");

                byte[] copcData = new byte[160];
                fs.ReadExactly(copcData);

                double cx = Bytes.ReadDoubleLE(copcData.AsSpan(0, 8));
                double cy = Bytes.ReadDoubleLE(copcData.AsSpan(8, 8));
                double cz = Bytes.ReadDoubleLE(copcData.AsSpan(16, 8));
                double halfSize = Bytes.ReadDoubleLE(copcData.AsSpan(24, 8));
                double spacing = Bytes.ReadDoubleLE(copcData.AsSpan(32, 8));
                ulong rootHierOffset = Bytes.ReadUInt64LE(copcData.AsSpan(40, 8));
                ulong rootHierSize = Bytes.ReadUInt64LE(copcData.AsSpan(48, 8));
                double gpsMin = Bytes.ReadDoubleLE(copcData.AsSpan(56, 8));
                double gpsMax = Bytes.ReadDoubleLE(copcData.AsSpan(64, 8));

                copcInfo = new CopcInfo(
                    new PclVector3(cx, cy, cz),
                    halfSize,
                    spacing,
                    rootHierOffset,
                    rootHierSize,
                    double.IsFinite(gpsMin) && gpsMin != 0 ? gpsMin : null,
                    double.IsFinite(gpsMax) && gpsMax != 0 ? gpsMax : null);
            }
            else if (userId.Equals("LASF_Projection", StringComparison.Ordinal) && recordId == 2112)
            {
                byte[] wktBytes = new byte[dataLength];
                if (dataLength > 0) fs.ReadExactly(wktBytes);
                string wkt = System.Text.Encoding.UTF8.GetString(wktBytes).TrimEnd('\0', ' ', '\r', '\n');
                if (wkt.Length > 0) crs = wkt;
            }
            else if (userId.Equals("LASF_Projection", StringComparison.Ordinal) && recordId == 34735 && crs == null)
            {
                byte[] geoData = new byte[dataLength];
                if (dataLength > 0) fs.ReadExactly(geoData);
                if (TryReadEpsg(geoData, out ushort epsg))
                    crs = $"EPSG:{epsg}";
            }

            position += dataLength;
        }

        return (copcInfo, crs);
    }

    private static bool TryReadEpsg(byte[] data, out ushort code)
    {
        code = 0;
        if (data.Length < 8) return false;
        int keyCount = Bytes.ReadUInt16LE(data.AsSpan(6, 2));
        if (keyCount <= 0 || keyCount > 1024 || 8 + (keyCount * 8) > data.Length) return false;

        for (int pass = 0; pass < 2; pass++)
        {
            int wanted = pass == 0 ? 3072 : 2048;
            for (int k = 0; k < keyCount; k++)
            {
                var entry = data.AsSpan(8 + (k * 8), 8);
                if (Bytes.ReadUInt16LE(entry[..2]) != wanted) continue;
                int value = Bytes.ReadUInt16LE(entry.Slice(6, 2));
                if (value is >= 1 and <= ushort.MaxValue && value != 32767)
                {
                    code = (ushort)value;
                    return true;
                }
            }
        }
        return false;
    }

    private static string ReadFixedAscii(ReadOnlySpan<byte> bytes)
    {
        int length = bytes.IndexOf((byte)0);
        if (length < 0) length = bytes.Length;
        return System.Text.Encoding.ASCII.GetString(bytes[..length]);
    }
}
