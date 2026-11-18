using System.Globalization;
using System.Text.Json;
using PclIO.Internal;

namespace PclIO.Formats.Potree;

/// <summary>Represents a Potree octree point cloud dataset (Potree 1.x cloud.js or Potree 2.x metadata.json).</summary>
/// <remarks>
/// <para>
/// Supported:
/// <list type="bullet">
/// <item><description>Potree 1.x datasets with cloud.js manifest, reading bounding boxes, coordinate scale/offset,
/// point attributes (POSITION_CARTESIAN, COLOR_PACKED, RGB, INTENSITY, CLASSIFICATION, NORMAL_SPHEREMAPPED, NORMAL_OCT16, NORMAL_FLOATS),
/// and node binary files (.bin) guided by hierarchy (.hrc) or data node enumeration.</description></item>
/// <item><description>Potree 2.x datasets with metadata.json manifest and octree.bin point streams.</description></item>
/// <item><description>Opening from cloud.js, metadata.json, a .hrc hierarchy file, a .bin node file (with companion manifest),
/// or a dataset directory.</description></item>
/// </list>
/// </para>
/// <para>
/// Not supported (reported with <see cref="InvalidDataException"/> or <see cref="NotSupportedException"/>):
/// malformed JSON manifests, missing or invalid bounding boxes, unsupported attribute encodings, truncated binary nodes,
/// or declared sizes exceeding physical file limits.
/// </para>
/// </remarks>
public sealed class PotreeDocument : PclDocument
{
    private const int MaxManifestBytes = 16 * 1024 * 1024; // 16 MB

    /// <inheritdoc />
    public override PclFileKind Kind => PclFileKind.Potree;

    /// <summary>Potree format version (e.g. "1.7", "2.0").</summary>
    public string PotreeVersion { get; }

    /// <summary>Total point count declared in the dataset manifest or summed across nodes.</summary>
    public long TotalPoints => TotalPointCount;

    /// <summary>Coordinate scale factor applied to raw integer coordinates.</summary>
    public PclVector3 Scale { get; }

    /// <summary>Coordinate offset added to scaled coordinates.</summary>
    public PclVector3 Offset { get; }

    /// <summary>Spacing parameter declared in the manifest, or null when not stated.</summary>
    public double? Spacing { get; }

    /// <inheritdoc />
    public override IReadOnlyList<PclScan> Scans => _scans;
    private readonly List<PclScan> _scans = [];

    private PotreeDocument(
        string path,
        long fileSize,
        string version,
        PclVector3 scale,
        PclVector3 offset,
        double? spacing,
        IEnumerable<PclScan> scans,
        PclBounds bounds,
        IEnumerable<string>? warnings = null)
        : base(path, fileSize)
    {
        PotreeVersion = version;
        Scale = scale;
        Offset = offset;
        Spacing = spacing;

        _metadata["Format"] = version.StartsWith("2.") ? "Potree 2.x (metadata.json)" : "Potree 1.x (cloud.js)";
        _metadata["PotreeVersion"] = version;
        _metadata["Scale"] = string.Create(CultureInfo.InvariantCulture, $"{scale.X:G6}, {scale.Y:G6}, {scale.Z:G6}");
        _metadata["Offset"] = string.Create(CultureInfo.InvariantCulture, $"{offset.X:G6}, {offset.Y:G6}, {offset.Z:G6}");
        if (spacing.HasValue)
            _metadata["Spacing"] = spacing.Value.ToString(CultureInfo.InvariantCulture);

        _scans.AddRange(scans);
        _metadata["ScanCount"] = _scans.Count.ToString(CultureInfo.InvariantCulture);
        _metadata["TotalPoints"] = TotalPointCount.ToString(CultureInfo.InvariantCulture);

        if (warnings != null)
        {
            foreach (string warning in warnings)
                _warnings.Add(warning);
        }
    }

    /// <summary>Opens a Potree dataset from a directory, a cloud.js file, a metadata.json file, a .hrc file, or a .bin file.</summary>
    /// <param name="path">Path of the dataset folder or manifest file.</param>
    /// <param name="options">Read options (currently unused during open; accepted for symmetry).</param>
    /// <exception cref="FileNotFoundException">The path or its manifest cannot be found.</exception>
    /// <exception cref="InvalidDataException">The manifest or binary data is malformed or truncated.</exception>
    /// <exception cref="NotSupportedException">The format features are not supported.</exception>
    public static PotreeDocument Open(string path, PclReadOptions? options = null)
    {
        if (Directory.Exists(path))
        {
            string cloudJs = Path.Combine(path, "cloud.js");
            if (File.Exists(cloudJs))
                return OpenPotree1x(cloudJs);

            string metadataJson = Path.Combine(path, "metadata.json");
            if (File.Exists(metadataJson))
                return OpenPotree2x(metadataJson);

            throw new FileNotFoundException($"Potree directory does not contain 'cloud.js' or 'metadata.json': '{path}'.", cloudJs);
        }

        if (!File.Exists(path))
            throw new FileNotFoundException($"Potree file not found: '{path}'.", path);

        string filename = Path.GetFileName(path);
        string ext = Path.GetExtension(path).ToLowerInvariant();

        if (string.Equals(filename, "cloud.js", StringComparison.OrdinalIgnoreCase))
            return OpenPotree1x(path);

        if (string.Equals(filename, "metadata.json", StringComparison.OrdinalIgnoreCase))
            return OpenPotree2x(path);

        if (ext is ".js" or ".json")
        {
            // Inspect content to distinguish 1.x vs 2.x
            if (IsPotree2xFile(path))
                return OpenPotree2x(path);
            return OpenPotree1x(path);
        }

        if (ext is ".hrc" or ".bin")
        {
            // Search ancestor directories for cloud.js or metadata.json
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            for (int depth = 0; depth < 5 && dir != null; depth++)
            {
                string cjs = Path.Combine(dir, "cloud.js");
                if (File.Exists(cjs))
                    return OpenPotree1x(cjs);

                string mjs = Path.Combine(dir, "metadata.json");
                if (File.Exists(mjs))
                    return OpenPotree2x(mjs);

                string? parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }

            throw new FileNotFoundException($"Could not find companion 'cloud.js' or 'metadata.json' for '{path}'.", path);
        }

        throw FormatError.Corrupted($"Unrecognized Potree file: '{path}'.");
    }

    private static bool IsPotree2xFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            byte[] buf = new byte[Math.Min(4096, fs.Length)];
            fs.ReadExactly(buf);
            using var doc = JsonDocument.Parse(buf);
            return doc.RootElement.TryGetProperty("version", out var ver) &&
                   ver.GetString() is { } v && v.StartsWith("2.");
        }
        catch { return false; }
    }

    private static PotreeDocument OpenPotree1x(string cloudJsPath)
    {
        var fi = new FileInfo(cloudJsPath);
        if (fi.Length > MaxManifestBytes)
            throw FormatError.Unsupported($"Potree manifest ({fi.Length} bytes) exceeds safety limit of {MaxManifestBytes} bytes.");

        byte[] jsonBytes = File.ReadAllBytes(cloudJsPath);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonBytes);
        }
        catch (JsonException ex)
        {
            throw FormatError.Corrupted($"Invalid JSON in Potree manifest '{cloudJsPath}': {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw FormatError.Corrupted("Potree manifest root must be a JSON object.");

            string version = root.TryGetProperty("version", out var vElem) ? (vElem.GetString() ?? "1.0") : "1.0";
            string octreeDir = root.TryGetProperty("octreeDir", out var odElem) ? (odElem.GetString() ?? "data") : "data";

            double? spacing = null;
            if (root.TryGetProperty("spacing", out var spElem) && spElem.TryGetDouble(out double spVal))
                spacing = spVal;

            PclVector3 scale = PclVector3.One;
            if (root.TryGetProperty("scale", out var scElem))
            {
                if (scElem.ValueKind == JsonValueKind.Number)
                {
                    double s = scElem.GetDouble();
                    scale = new PclVector3(s, s, s);
                }
                else if (scElem.ValueKind == JsonValueKind.Array && scElem.GetArrayLength() == 3)
                {
                    double[] s = scElem.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                    scale = new PclVector3(s[0], s[1], s[2]);
                }
            }

            // Bounding box
            PclVector3 min = PclVector3.Zero;
            PclVector3 max = PclVector3.Zero;
            bool hasBoundingBox = false;

            if (root.TryGetProperty("boundingBox", out var bbElem) && bbElem.ValueKind == JsonValueKind.Object)
            {
                double lx = bbElem.TryGetProperty("lx", out var lxElem) ? lxElem.GetDouble() : 0;
                double ly = bbElem.TryGetProperty("ly", out var lyElem) ? lyElem.GetDouble() : 0;
                double lz = bbElem.TryGetProperty("lz", out var lzElem) ? lzElem.GetDouble() : 0;
                double ux = bbElem.TryGetProperty("ux", out var uxElem) ? uxElem.GetDouble() : 0;
                double uy = bbElem.TryGetProperty("uy", out var uyElem) ? uyElem.GetDouble() : 0;
                double uz = bbElem.TryGetProperty("uz", out var uzElem) ? uzElem.GetDouble() : 0;
                min = new PclVector3(lx, ly, lz);
                max = new PclVector3(ux, uy, uz);
                hasBoundingBox = true;
            }

            PclBounds bounds = hasBoundingBox ? new PclBounds(min, max) : PclBounds.Empty;
            PclVector3 offset = min;

            // Tight bounding box overrides bounds if available
            if (root.TryGetProperty("tightBoundingBox", out var tbbElem) && tbbElem.ValueKind == JsonValueKind.Object)
            {
                double lx = tbbElem.TryGetProperty("lx", out var lxElem) ? lxElem.GetDouble() : min.X;
                double ly = tbbElem.TryGetProperty("ly", out var lyElem) ? lyElem.GetDouble() : min.Y;
                double lz = tbbElem.TryGetProperty("lz", out var lzElem) ? lzElem.GetDouble() : min.Z;
                double ux = tbbElem.TryGetProperty("ux", out var uxElem) ? uxElem.GetDouble() : max.X;
                double uy = tbbElem.TryGetProperty("uy", out var uyElem) ? uyElem.GetDouble() : max.Y;
                double uz = tbbElem.TryGetProperty("uz", out var uzElem) ? uzElem.GetDouble() : max.Z;
                bounds = new PclBounds(new PclVector3(lx, ly, lz), new PclVector3(ux, uy, uz));
            }

            // Parse point attributes
            if (!root.TryGetProperty("pointAttributes", out var paElem) || paElem.ValueKind != JsonValueKind.Array)
                throw FormatError.Corrupted("Potree manifest is missing 'pointAttributes' array.");

            if (!double.IsFinite(scale.X) || !double.IsFinite(scale.Y) || !double.IsFinite(scale.Z) ||
                scale.X == 0 || scale.Y == 0 || scale.Z == 0)
                throw FormatError.Corrupted("Potree scale factors must be finite and non-zero.");

            if (hasBoundingBox && (!double.IsFinite(min.X) || !double.IsFinite(min.Y) || !double.IsFinite(min.Z) ||
                                   !double.IsFinite(max.X) || !double.IsFinite(max.Y) || !double.IsFinite(max.Z)))
                throw FormatError.Corrupted("Potree boundingBox coordinates must be finite.");

            PotreeLayout layout = PotreeLayout.Parse1x(paElem, scale, offset);
            if (layout.RecordSize <= 0)
                throw FormatError.Corrupted("Potree pointAttributes resulted in an invalid record size of 0.");

            string baseDir = Path.GetDirectoryName(Path.GetFullPath(cloudJsPath)) ?? "";
            string dataDir = Path.Combine(baseDir, octreeDir);

            // Locate node binary files
            List<PotreeNode> nodes = CollectNodes1x(dataDir, layout.RecordSize);
            if (nodes.Count == 0)
                throw FormatError.Corrupted($"No Potree data files (.bin) found in '{dataDir}'.");

            long totalPoints = nodes.Sum(n => n.PointCount);
            string scanName = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(cloudJsPath)) ?? "Potree");
            var scan = new PotreeScan("0", scanName, totalPoints, bounds, layout, nodes);

            return new PotreeDocument(
                cloudJsPath,
                fi.Length,
                version,
                scale,
                offset,
                spacing,
                [scan],
                bounds);
        }
    }

    private static PotreeDocument OpenPotree2x(string metadataJsonPath)
    {
        var fi = new FileInfo(metadataJsonPath);
        if (fi.Length > MaxManifestBytes)
            throw FormatError.Unsupported($"Potree manifest ({fi.Length} bytes) exceeds safety limit of {MaxManifestBytes} bytes.");

        byte[] jsonBytes = File.ReadAllBytes(metadataJsonPath);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonBytes);
        }
        catch (JsonException ex)
        {
            throw FormatError.Corrupted($"Invalid JSON in Potree metadata '{metadataJsonPath}': {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw FormatError.Corrupted("Potree metadata root must be a JSON object.");

            string version = root.TryGetProperty("version", out var vElem) ? (vElem.GetString() ?? "2.0") : "2.0";
            long declaredPoints = root.TryGetProperty("points", out var pElem) ? pElem.GetInt64() : 0;

            PclVector3 scale = PclVector3.One;
            if (root.TryGetProperty("scale", out var scElem) && scElem.ValueKind == JsonValueKind.Array && scElem.GetArrayLength() == 3)
            {
                double[] s = scElem.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                scale = new PclVector3(s[0], s[1], s[2]);
            }

            PclVector3 offset = PclVector3.Zero;
            if (root.TryGetProperty("offset", out var offElem) && offElem.ValueKind == JsonValueKind.Array && offElem.GetArrayLength() == 3)
            {
                double[] o = offElem.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                offset = new PclVector3(o[0], o[1], o[2]);
            }

            PclBounds bounds = PclBounds.Empty;
            if (root.TryGetProperty("boundingBox", out var bbElem) && bbElem.ValueKind == JsonValueKind.Object)
            {
                if (bbElem.TryGetProperty("min", out var minElem) && minElem.ValueKind == JsonValueKind.Array && minElem.GetArrayLength() == 3 &&
                    bbElem.TryGetProperty("max", out var maxElem) && maxElem.ValueKind == JsonValueKind.Array && maxElem.GetArrayLength() == 3)
                {
                    double[] min = minElem.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                    double[] max = maxElem.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                    bounds = new PclBounds(new PclVector3(min[0], min[1], min[2]), new PclVector3(max[0], max[1], max[2]));
                }
            }

            if (!double.IsFinite(scale.X) || !double.IsFinite(scale.Y) || !double.IsFinite(scale.Z) ||
                scale.X == 0 || scale.Y == 0 || scale.Z == 0)
                throw FormatError.Corrupted("Potree scale factors must be finite and non-zero.");

            if (!bounds.IsEmpty && (!double.IsFinite(bounds.Min.X) || !double.IsFinite(bounds.Min.Y) || !double.IsFinite(bounds.Min.Z) ||
                                    !double.IsFinite(bounds.Max.X) || !double.IsFinite(bounds.Max.Y) || !double.IsFinite(bounds.Max.Z)))
                throw FormatError.Corrupted("Potree boundingBox coordinates must be finite.");

            if (!root.TryGetProperty("attributes", out var attrsElem) || attrsElem.ValueKind != JsonValueKind.Array)
                throw FormatError.Corrupted("Potree 2.x metadata is missing 'attributes' array.");

            PotreeLayout layout = PotreeLayout.Parse2x(attrsElem, scale, offset);
            if (layout.RecordSize <= 0)
                throw FormatError.Corrupted("Potree point record size is 0 or invalid.");

            string baseDir = Path.GetDirectoryName(Path.GetFullPath(metadataJsonPath)) ?? "";
            string octreeBinPath = Path.Combine(baseDir, "octree.bin");

            if (!File.Exists(octreeBinPath))
                throw new FileNotFoundException($"Potree 2.x data file 'octree.bin' not found in '{baseDir}'.", octreeBinPath);

            var binFi = new FileInfo(octreeBinPath);
            long binLength = binFi.Length;
            if (binLength % layout.RecordSize != 0)
                throw FormatError.Truncated($"Potree 2.x 'octree.bin' has size {binLength} bytes which is not a multiple of point record size {layout.RecordSize} bytes.");

            long pointCount = binLength / layout.RecordSize;

            var node = new PotreeNode(octreeBinPath, 0, binLength, pointCount);
            string scanName = root.TryGetProperty("name", out var nameElem) ? (nameElem.GetString() ?? "Potree") : "Potree";
            var scan = new PotreeScan("0", scanName, pointCount, bounds, layout, [node]);

            return new PotreeDocument(
                metadataJsonPath,
                fi.Length,
                version,
                scale,
                offset,
                null,
                [scan],
                bounds);
        }
    }

    private static List<PotreeNode> CollectNodes1x(string dataDir, int recordSize)
    {
        var nodes = new List<PotreeNode>();
        if (!Directory.Exists(dataDir)) return nodes;

        // Enumerate all .bin files inside dataDir (e.g. data/r/r.bin, data/r/r0.bin, etc.)
        var binFiles = Directory.EnumerateFiles(dataDir, "*.bin", SearchOption.AllDirectories).Order(StringComparer.Ordinal);
        foreach (string binFile in binFiles)
        {
            var fi = new FileInfo(binFile);
            long length = fi.Length;
            if (recordSize <= 0) continue;
            if (length % recordSize != 0)
                throw FormatError.Truncated($"Potree binary file '{binFile}' has size {length} bytes which is not a multiple of point record size {recordSize} bytes.");
            long count = length / recordSize;
            nodes.Add(new PotreeNode(binFile, 0, length, count));
        }

        return nodes;
    }
}
