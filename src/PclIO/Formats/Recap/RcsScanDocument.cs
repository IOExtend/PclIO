using System.Globalization;
using System.Text;
using PclIO.Internal;

namespace PclIO.Formats.Recap;

/// <summary>
/// Reads a ReCap scan file (.rcs). Point decoding covers the container major versions named by
/// <see cref="DecodedNodeLayoutVersions"/>; a file of another version is listed with its header, and reading its points
/// fails with a message that names the version instead of guessing its layout.
/// </summary>
public sealed class RcsScanDocument : PclDocument
{
    /// <inheritdoc />
    public override PclFileKind Kind => PclFileKind.RecapScan;

    /// <summary>Major versions of the container whose layout the reader decodes.</summary>
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

    /// <summary>
    /// Point count stated in the header. It is not the number of points the file stores, which can be fewer and is the
    /// <see cref="PclScan.PointCount"/> of the scan.
    /// </summary>
    public long DeclaredPointCount { get; }

    /// <summary>True when the header states that the points hold a colour.</summary>
    public bool HasColorData { get; }

    /// <summary>
    /// True when the header states that the points can hold normals; a single point may still have none.
    /// </summary>
    public bool HasNormalData { get; }

    /// <summary>True when the header states that the points hold an intensity.</summary>
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
        bool hasNormals,
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
        HasNormalData = hasNormals;
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

        // Channel flags: a non-zero byte states that the points hold the channel.
        bool hasColor = header[0xE0] != 0;
        bool hasNormals = header[0xE1] != 0;
        bool hasIntensity = header[0xE2] != 0;

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
            hasColor,
            hasNormals,
            hasIntensity);
    }
}

/// <summary>Individual scan of an RCS file.</summary>
public sealed class RcsScan : PclScan
{
    // Limits of what a file may declare; a file that exceeds one of them is malformed.
    private const int MaxSections = 64;
    private const long MaxLeaves = 500_000;

    // Layout of the decoded container versions. A table in the header names the sections of the file; reading needs the
    // leaves section, one record per leaf of the octree, and the points section, one block per leaf in the same order.
    // A block holds the index words of the leaf followed by its points as records of 16 bytes.
    private const int SectionTableOffset = 0x129;
    private const int SectionEntrySize = 24;
    private const ulong SectionPoints = 1;
    private const ulong SectionLeaves = 2;
    private const int LeafRecordSize = 368;
    private const int PointRecordSize = 16;

    // A record holds three 18-bit fields of a 64-bit word, one per axis: millimetres from the corner of the leaf's cube.
    private const int FieldBits = 18;
    private const ulong FieldMask = (1UL << FieldBits) - 1;
    private const double GridStep = 0.001;

    // The normal is a 16-bit field whose low 14 bits name a cell on a cube face; any other bit set means there is none.
    private const ushort NormalFlags = 0xC000;

    // Records are read in chunks of whole records; the chunk is a multiple of the record size.
    private const int ChunkBytes = 64 * 1024;

    private readonly RcsScanDocument _document;
    private readonly string _filePath;

    // The leaves of the file, read and checked as a whole on first use.
    private LeafTable? _table;

    /// <inheritdoc />
    public override string Id => _document.ScanGuid;

    /// <inheritdoc />
    public override string Name => Path.GetFileNameWithoutExtension(_filePath);

    /// <summary>
    /// Number of points the file stores, summed over its leaves; a full read returns exactly this many. The header states
    /// a count of its own (<see cref="RcsScanDocument.DeclaredPointCount"/>), which is not the number of stored points. A
    /// file of a version the reader does not decode reports the count of the header. Reading a malformed leaf table fails
    /// here, as it does when the points are read.
    /// </summary>
    public override long PointCount => IsDecoded ? Table.PointCount : _document.DeclaredPointCount;

    /// <inheritdoc />
    public override PclBounds Bounds => _document.LocalBounds;

    /// <inheritdoc />
    public override PclTransform Transform => _document.ScanTransform;

    /// <summary>True when the header states that the points hold a colour.</summary>
    public override bool HasColors => _document.HasColorData;

    /// <summary>True when the header states that the points can hold normals; a single point may still have none.</summary>
    public override bool HasNormals => _document.HasNormalData;

    /// <summary>True when the header states that the points hold an intensity.</summary>
    public override bool HasIntensities => _document.HasIntensityData;

    private bool IsDecoded => RcsScanDocument.DecodedNodeLayoutVersions.Contains(_document.MajorVersion);

    internal RcsScan(RcsScanDocument document, string filePath)
    {
        _document = document;
        _filePath = filePath;
    }

    /// <summary>
    /// Streams the points of the file (decoded container versions only). Throws <see cref="NotSupportedException"/> for other
    /// versions, and <see cref="InvalidDataException"/> when a section, a leaf or a leaf block is inconsistent with the
    /// file; the file is checked as a whole, so nothing is returned before it fails. Scale and translation of the header
    /// transform are applied when requested; rotation is not.
    /// </summary>
    public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null)
    {
        options ??= PclReadOptions.Default;
        if (!IsDecoded)
            throw FormatError.Unsupported(
                $"RCS container version {_document.MajorVersion}.{_document.MinorVersion}: its layout is not described. The described versions are {string.Join(", ", RcsScanDocument.DecodedNodeLayoutVersions)}.");

        // The whole file is checked before the first point is returned, so a malformed file yields nothing.
        LeafTable table = Table;
        if (table.PointCount == 0) yield break;

        long targetCount = options.MaxPoints ?? long.MaxValue;
        int step = Math.Max(1, options.SubsampleStep);
        bool transform = options.ApplyTransform;
        bool hasColor = _document.HasColorData, hasIntensity = _document.HasIntensityData, hasNormals = _document.HasNormalData;

        // The transform of an RCS file is a scale and a translation. A unit scale leaves a normal as it is.
        PclTransform scanTransform = _document.ScanTransform;
        double scaleX = scanTransform.Scale.X, scaleY = scanTransform.Scale.Y, scaleZ = scanTransform.Scale.Z;
        double shiftX = scanTransform.Translation.X, shiftY = scanTransform.Translation.Y, shiftZ = scanTransform.Translation.Z;
        bool scaleNormals = transform && scanTransform.Scale != PclVector3.One;

        using var fs = File.OpenRead(_filePath);
        byte[] chunk = new byte[ChunkBytes];
        long yielded = 0;

        foreach (Leaf leaf in table.Leaves)
        {
            if (yielded >= targetCount) yield break;
            if (leaf.Points == 0) continue;

            fs.Position = leaf.RecordsOffset;
            long remaining = (long)leaf.Points * PointRecordSize;
            uint pointInLeaf = 0;

            while (remaining > 0)
            {
                int toRead = (int)Math.Min(ChunkBytes, remaining);
                fs.ReadExactly(chunk, 0, toRead);
                remaining -= toRead;

                for (int offset = 0; offset < toRead; offset += PointRecordSize, pointInLeaf++)
                {
                    // The step counts the points of each leaf, like the blocks of the other readers.
                    if (pointInLeaf % (uint)step != 0) continue;

                    ReadOnlySpan<byte> record = chunk.AsSpan(offset, PointRecordSize);

                    // The three fields hold the position; the top ten bits of the word hold other data and are ignored.
                    ulong packed = Bytes.ReadUInt64LE(record);
                    double x = leaf.CubeMin.X + ((packed >> (FieldBits * 0)) & FieldMask) * GridStep;
                    double y = leaf.CubeMin.Y + ((packed >> (FieldBits * 1)) & FieldMask) * GridStep;
                    double z = leaf.CubeMin.Z + ((packed >> (FieldBits * 2)) & FieldMask) * GridStep;

                    PclVector3? normal = null;
                    if (hasNormals)
                    {
                        // A plain cell code names the direction of the normal; anything else leaves the point without one.
                        ushort field = Bytes.ReadUInt16LE(record[12..]);
                        if (field != 0 && (field & NormalFlags) == 0 && field < NormalDirections.Count)
                        {
                            normal = NormalDirections.All[field];
                            if (scaleNormals) normal = scanTransform.TransformVector(normal.Value);
                        }
                    }

                    if (transform)
                    {
                        // Scale, then translation, as PclTransform applies them.
                        x = x * scaleX + shiftX;
                        y = y * scaleY + shiftY;
                        z = z * scaleZ + shiftZ;
                    }

                    // Intensity at 8; blue, green and red from 9.
                    yield return new PclPoint(
                        new PclVector3(x, y, z),
                        Color: hasColor ? new PclColor(record[11], record[10], record[9]) : null,
                        Intensity: hasIntensity ? record[8] / 255.0f : null,
                        Normal: normal,
                        Index: (ulong)yielded);

                    yielded++;
                    if (yielded >= targetCount) yield break;
                }
            }
        }
    }

    private LeafTable Table => _table ??= ReadLeafTable();

    // Reads the leaves section and checks every leaf block against the points section and the size of the file.
    private LeafTable ReadLeafTable()
    {
        using var fs = File.OpenRead(_filePath);
        long fileLength = fs.Length;

        fs.Position = SectionTableOffset;
        Span<byte> word = stackalloc byte[8];
        fs.ReadExactly(word[..4]);
        uint sectionCount = Bytes.ReadUInt32LE(word[..4]);
        if (sectionCount > MaxSections) throw FormatError.Corrupted("RCS section count is out of range.");
        if (SectionTableOffset + 4L + (long)sectionCount * SectionEntrySize > fileLength)
            throw FormatError.Truncated("RCS section table");

        long leavesOffset = -1, leavesSize = 0, pointsOffset = -1, pointsSize = 0;
        Span<byte> entry = stackalloc byte[SectionEntrySize];
        for (uint i = 0; i < sectionCount; i++)
        {
            fs.ReadExactly(entry);
            ulong type = Bytes.ReadUInt64LE(entry[..8]);
            long offset = (long)Bytes.ReadUInt64LE(entry[8..16]);
            long size = (long)Bytes.ReadUInt64LE(entry[16..24]);
            if (size < 0 || size > fileLength || offset < 0 || offset > fileLength - size)
                throw FormatError.Corrupted("RCS section does not fit in the file.");

            if (type == SectionLeaves)
            {
                if (leavesOffset >= 0) throw FormatError.Corrupted("RCS section table names the leaves section twice.");
                (leavesOffset, leavesSize) = (offset, size);
            }
            else if (type == SectionPoints)
            {
                if (pointsOffset >= 0) throw FormatError.Corrupted("RCS section table names the points section twice.");
                (pointsOffset, pointsSize) = (offset, size);
            }
        }

        if (leavesOffset < 0 || pointsOffset < 0)
            throw FormatError.Corrupted("RCS file has no leaves section or no points section.");

        if (leavesSize < 8) throw FormatError.Truncated("RCS leaf table");
        fs.Position = leavesOffset;
        fs.ReadExactly(word);
        uint leafCount = Bytes.ReadUInt32LE(word[..4]);
        if (leafCount > MaxLeaves) throw FormatError.Corrupted($"RCS leaf table declares {leafCount} leaves; at most {MaxLeaves} are read.");
        if (8L + (long)leafCount * LeafRecordSize > leavesSize)
            throw FormatError.Corrupted("RCS leaf table does not fit in its section.");

        long pointsEnd = pointsOffset + pointsSize;
        long blockOffset = pointsOffset;
        long pointCount = 0;
        var leaves = new Leaf[leafCount];
        Span<byte> record = stackalloc byte[LeafRecordSize];
        for (int i = 0; i < leaves.Length; i++)
        {
            fs.ReadExactly(record);
            double minX = Bytes.ReadDoubleLE(record[..8]);
            double minY = Bytes.ReadDoubleLE(record[8..16]);
            double minZ = Bytes.ReadDoubleLE(record[16..24]);
            if (!double.IsFinite(minX) || !double.IsFinite(minY) || !double.IsFinite(minZ))
                throw FormatError.Corrupted("RCS leaf cube corner is not finite.");

            // The number of points of the leaf, and the size of its block in the points section.
            uint points = Bytes.ReadUInt32LE(record[100..104]);
            uint blockSize = Bytes.ReadUInt32LE(record[104..108]);

            // Every point has an index word of four bytes, which precede the records in the block of the leaf.
            long indexBytes = (long)blockSize - (long)points * PointRecordSize;
            if (indexBytes < 4L * points || indexBytes % 4 != 0 || blockSize > pointsEnd - blockOffset)
                throw FormatError.Corrupted("RCS leaf block does not fit its point count and the points section.");

            leaves[i] = new Leaf(new PclVector3(minX, minY, minZ), points, blockOffset + indexBytes);
            blockOffset += blockSize;
            pointCount += points;
        }

        if (blockOffset != pointsEnd) throw FormatError.Corrupted("RCS leaf blocks do not fill the points section.");

        return new LeafTable(leaves, pointCount);
    }

    // The leaves of a file in file order, and the number of points they hold.
    private readonly record struct LeafTable(Leaf[] Leaves, long PointCount);

    // One leaf of the octree: the corner its point positions count from, its number of points and where its records start.
    private readonly record struct Leaf(PclVector3 CubeMin, uint Points, long RecordsOffset);

    // The unit direction of every normal code. A code names a cell on one of the six faces of a cube, the faces along
    // +x, -x, +y, -y, +z and -z in that order, with 52 by 52 cells of equal angle across each face.
    private static class NormalDirections
    {
        private const int Cells = 52;
        private const int FaceCodes = Cells * Cells;
        public const int Count = 6 * FaceCodes;

        public static readonly PclVector3[] All = Build();

        private static PclVector3[] Build()
        {
            var table = new PclVector3[Count];
            for (int code = 0; code < Count; code++)
            {
                int face = code / FaceCodes;
                int i = (code % FaceCodes) / Cells;
                int j = code % Cells;
                int axis = face / 2;
                int first = axis == 0 ? 1 : 0;
                int second = axis == 2 ? 1 : 2;

                double[] direction = new double[3];
                direction[axis] = face % 2 == 0 ? 1 : -1;
                direction[first] = Across(j);
                direction[second] = Across(i);

                double length = Math.Sqrt(direction[0] * direction[0] + direction[1] * direction[1] + direction[2] * direction[2]);
                table[code] = new PclVector3(direction[0] / length, direction[1] / length, direction[2] / length);
            }
            return table;
        }

        // Where a cell row leaves the centre line of the face: equal angles from -45 to +45 degrees.
        private static double Across(int cell) => Math.Tan((2.0 * (cell + 0.5) / Cells - 1.0) * Math.PI / 4.0);
    }
}
