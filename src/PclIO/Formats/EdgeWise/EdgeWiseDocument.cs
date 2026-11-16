using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PclIO.Internal;

namespace PclIO.Formats.EdgeWise;

/// <summary>Represents a ClearEdge3D EdgeWise point cloud database or cluster file (.c3db, .c3ptx).</summary>
public sealed class EdgeWiseDocument : PclDocument
{
    // Bounds for data taken from an untrusted database file.
    private const int MaxLineLength = 1 << 20;
    private const int MaxScans = 1_000_000;

    // Patterns have a timeout so that a crafted line cannot stall the reader.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    // The lookbehind makes a match attempt start only at the beginning of a digit run, so the cost per line stays
    // linear; without it every digit of a long run is a start position and the cost grows quadratically.
    private static readonly Regex NodeRegex = new(
        @"(?<!\d)(\d+)\s*\(\s*Count:(\d+)\s+File:\s*""([^""]+)""", RegexOptions.None, RegexTimeout);

    private static readonly Regex TreeRegex = new(
        @"_Tree\s*\(\s*([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s*\)\s*\(\s*([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s*\)",
        RegexOptions.None, RegexTimeout);

    /// <inheritdoc/>
    public override PclFileKind Kind => PclFileKind.EdgeWise;

    /// <summary>Project name, taken from the file name.</summary>
    public string ProjectName { get; }

    /// <inheritdoc/>
    public override IReadOnlyList<PclScan> Scans => _scans;
    private readonly List<PclScan> _scans = [];

    private EdgeWiseDocument(string path, long fileSize, string projectName)
        : base(path, fileSize)
    {
        ProjectName = projectName;
        _metadata["ProjectName"] = projectName;
        _metadata["Format"] = "ClearEdge3D EdgeWise";
    }

    /// <summary>Opens an EdgeWise database (.c3db) or a single cluster file (.c3ptx).</summary>
    /// <param name="path">Path of the file.</param>
    /// <param name="options">Currently unused; accepted for symmetry with the other readers.</param>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is damaged, is a database without the "_Tree" line, or names unsafe paths.</exception>
    /// <remarks>
    /// A database node whose cluster file is missing is skipped and recorded in <see cref="PclDocument.Warnings"/>.
    /// A database with a "_Tree" line and no nodes is valid and has no scans.
    /// </remarks>
    public static EdgeWiseDocument Open(string path, PclReadOptions? options = null)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("EdgeWise file not found.", path);

        string ext = fi.Extension.ToLowerInvariant();
        string projName = Path.GetFileNameWithoutExtension(path);

        if (ext == ".c3ptx")
        {
            var doc = new EdgeWiseDocument(path, fi.Length, projName);
            long ptCount = EdgeWiseScan.CountPointsInPtx(path);
            doc._scans.Add(new EdgeWiseScan("0", projName, ptCount, path));
            return doc;
        }

        // Parse .c3db database file
        var document = new EdgeWiseDocument(path, fi.Length, projName);
        string baseDir = fi.DirectoryName ?? "";

        using var sr = new StreamReader(path);
        string? line;
        bool sawTree = false;
        int nodesListed = 0;

        try
        {
            while ((line = ReadBoundedLine(sr)) != null)
            {
                // "_Tree" is the key of the line that carries the bounding box: _Tree ( max ) ( min ).
                if (line.Contains("_Tree", StringComparison.OrdinalIgnoreCase))
                {
                    sawTree = true;
                    var m = TreeRegex.Match(line);
                    if (m.Success)
                    {
                        document._metadata["BoundsMax"] = $"{m.Groups[1].Value}, {m.Groups[2].Value}, {m.Groups[3].Value}";
                        document._metadata["BoundsMin"] = $"{m.Groups[4].Value}, {m.Groups[5].Value}, {m.Groups[6].Value}";
                    }
                }

                var nodeMatch = NodeRegex.Match(line);
                if (!nodeMatch.Success) continue;

                string nodeId = nodeMatch.Groups[1].Value;
                if (!long.TryParse(nodeMatch.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long count))
                    throw FormatError.Corrupted("node point count is out of range");
                string relFile = nodeMatch.Groups[3].Value;

                if (count <= 0) continue;

                // Every node counts, also one whose file is missing, so warnings stay bounded by the cap.
                if (++nodesListed > MaxScans)
                    throw FormatError.Corrupted("database lists too many nodes");

                string targetFile = SafePath.Combine(baseDir, relFile.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(targetFile))
                {
                    document._warnings.Add($"Node {nodeId}: cluster file not found.");
                    continue;
                }

                string scanName = Path.GetFileNameWithoutExtension(targetFile);
                document._scans.Add(new EdgeWiseScan(nodeId, scanName, count, targetFile));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            throw FormatError.Corrupted("database line is too complex to parse");
        }

        // Every database carries the "_Tree" line, even one without nodes, so its absence means this is not a database.
        if (!sawTree) throw FormatError.Corrupted("database has no _Tree line");

        return document;
    }

    // Reads one line and rejects lines longer than the limit instead of buffering them without bound.
    private static string? ReadBoundedLine(StreamReader reader)
    {
        var sb = new StringBuilder();
        int ch;
        while ((ch = reader.Read()) >= 0)
        {
            if (ch == '\n') break;
            if (ch == '\r')
            {
                if (reader.Peek() == '\n') reader.Read();
                break;
            }
            if (sb.Length >= MaxLineLength) throw FormatError.Corrupted("database line is too long");
            sb.Append((char)ch);
        }

        return ch < 0 && sb.Length == 0 ? null : sb.ToString();
    }
}

/// <summary>Scan representation of an EdgeWise .c3ptx cluster node.</summary>
public sealed class EdgeWiseScan : PclScan
{
    // Cluster file: a cluster count, then per cluster a centre, a point count and its points.
    // A point record holds three 16-bit offsets from the centre; wider records are not decoded.
    private const int ClusterHeaderSize = 16;
    private const int PointSize = 6;
    private const int ChunkPoints = 8192;

    /// <inheritdoc/>
    public override string Id { get; }

    /// <inheritdoc/>
    public override string Name { get; }

    /// <summary>Point count recorded for the node (declared by the database or counted from the cluster file).</summary>
    public override long PointCount { get; }

    /// <summary>Path of the cluster file.</summary>
    public string FilePath { get; }

    /// <inheritdoc/>
    public override bool HasColors => false;

    /// <inheritdoc/>
    public override bool HasIntensities => false;

    /// <inheritdoc/>
    public override bool HasNormals => false;

    internal EdgeWiseScan(string id, string name, long pointCount, string filePath)
    {
        Id = id;
        Name = name;
        PointCount = pointCount;
        FilePath = filePath;
    }

    /// <summary>Streams points from the cluster file.</summary>
    /// <remarks>
    /// The binary layout has no signature or version, so it is checked only for size consistency. A point record holds
    /// three 16-bit offsets from the cluster centre; the scale comes from <see cref="PclReadOptions.EdgeWiseCoordinateScale"/>,
    /// whose default keeps the long-standing assumption of the reader (32768 units = 0.128 m). Only positions are returned.
    /// </remarks>
    /// <exception cref="InvalidDataException">The cluster file is truncated or malformed.</exception>
    public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null)
    {
        options ??= PclReadOptions.Default;

        long targetCount = options.MaxPoints ?? PointCount;
        int step = Math.Max(1, options.SubsampleStep);
        double scale = options.EdgeWiseCoordinateScale;
        long yielded = 0;

        using var fs = File.OpenRead(FilePath);
        using var reader = new BinaryReader(fs);

        if (fs.Length < 4) throw FormatError.Truncated("cluster count");
        uint numClusters = reader.ReadUInt32();

        byte[] pointBuffer = new byte[ChunkPoints * PointSize];

        for (uint c = 0; c < numClusters && yielded < targetCount; c++)
        {
            if (fs.Length - fs.Position < ClusterHeaderSize) throw FormatError.Truncated("cluster header");

            float cx = reader.ReadSingle();
            float cy = reader.ReadSingle();
            float cz = reader.ReadSingle();
            int ptCount = reader.ReadInt32();

            if (!float.IsFinite(cx) || !float.IsFinite(cy) || !float.IsFinite(cz))
                throw FormatError.Corrupted("cluster centre is not finite");
            if (ptCount < 0) throw FormatError.Corrupted("negative cluster point count");
            if ((long)ptCount * PointSize > fs.Length - fs.Position) throw FormatError.Truncated("cluster point data");

            int index = 0;
            int remaining = ptCount;
            while (remaining > 0)
            {
                int n = Math.Min(remaining, ChunkPoints);
                fs.ReadExactly(pointBuffer, 0, n * PointSize);
                remaining -= n;

                for (int i = 0; i < n; i++)
                {
                    if (index++ % step != 0) continue;

                    int off = i * PointSize;
                    short dx = Bytes.ReadInt16LE(pointBuffer.AsSpan(off, 2));
                    short dy = Bytes.ReadInt16LE(pointBuffer.AsSpan(off + 2, 2));
                    short dz = Bytes.ReadInt16LE(pointBuffer.AsSpan(off + 4, 2));

                    var pt = new PclPoint(
                        new PclVector3(cx + (dx * scale), cy + (dy * scale), cz + (dz * scale)),
                        Index: (ulong)yielded);

                    if (options.ApplyTransform)
                        pt = pt with { Position = Transform.TransformPoint(pt.Position) };

                    yield return pt;
                    yielded++;
                    if (yielded >= targetCount) yield break;
                }
            }
        }
    }

    /// <summary>Sums the point counts of all clusters by walking the headers.</summary>
    /// <exception cref="InvalidDataException">The file is truncated or malformed.</exception>
    internal static long CountPointsInPtx(string path)
    {
        using var fs = File.OpenRead(path);
        using var reader = new BinaryReader(fs);
        if (fs.Length < 4) throw FormatError.Truncated("cluster count");

        uint numClusters = reader.ReadUInt32();
        long total = 0;
        for (uint c = 0; c < numClusters; c++)
        {
            if (fs.Length - fs.Position < ClusterHeaderSize) throw FormatError.Truncated("cluster header");

            fs.Seek(12, SeekOrigin.Current); // centre
            int count = reader.ReadInt32();
            if (count < 0) throw FormatError.Corrupted("negative cluster point count");

            long bytes = (long)count * PointSize;
            if (bytes > fs.Length - fs.Position) throw FormatError.Truncated("cluster point data");
            fs.Seek(bytes, SeekOrigin.Current);
            total += count;
        }
        return total;
    }
}
