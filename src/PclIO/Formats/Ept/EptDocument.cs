using System.Globalization;
using System.Text.Json;
using PclIO.Internal;

namespace PclIO.Formats.Ept;

/// <summary>Represents an Entwine Point Tile (EPT) point cloud dataset (ept.json).</summary>
public sealed class EptDocument : PclDocument
{
    private const int MaxManifestBytes = 16 * 1024 * 1024; // 16 MB

    /// <inheritdoc />
    public override PclFileKind Kind => PclFileKind.Ept;

    /// <summary>Root bounding box of the octree space.</summary>
    public PclBounds Bounds { get; }

    /// <summary>Conforming bounding box tightly encapsulating the points.</summary>
    public PclBounds BoundsConforming { get; }

    /// <summary>Storage data type of the point tiles ("laszip", "binary", or "zstandard").</summary>
    public string DataType { get; }

    /// <summary>Format of the hierarchy pages ("json").</summary>
    public string HierarchyType { get; }

    /// <summary>Total point count declared in the manifest.</summary>
    public long DeclaredPoints { get; }

    /// <summary>Voxel tile side dimension in units of points per axis (e.g. 128, 256).</summary>
    public int Span { get; }

    /// <summary>Spatial reference system string (e.g. WKT or EPSG), or null when omitted.</summary>
    public string? Srs { get; }

    /// <summary>EPT specification version string.</summary>
    public string Version { get; }

    /// <summary>Point attribute dimensions declared in the dataset schema.</summary>
    public IReadOnlyList<EptDimension> Schema => _schema;
    private readonly List<EptDimension> _schema;

    /// <summary>Resolved octree nodes containing point records.</summary>
    public IReadOnlyList<EptNode> Nodes => _nodes;
    private readonly List<EptNode> _nodes;

    /// <inheritdoc />
    public override IReadOnlyList<PclScan> Scans => _scans;
    private readonly List<PclScan> _scans = [];
    private readonly EptScan _primaryScan;

    private EptDocument(
        string path,
        long fileSize,
        string version,
        PclBounds bounds,
        PclBounds boundsConforming,
        string dataType,
        string hierarchyType,
        long declaredPoints,
        int span,
        string? srs,
        List<EptDimension> schema,
        List<EptNode> nodes,
        long pointCount)
        : base(path, fileSize)
    {
        Version = version;
        Bounds = bounds;
        BoundsConforming = boundsConforming;
        DataType = dataType;
        HierarchyType = hierarchyType;
        DeclaredPoints = declaredPoints;
        Span = span;
        Srs = srs;
        _schema = schema;
        _nodes = nodes;

        _primaryScan = new EptScan(
            id: "ept_root",
            name: "EPT Octree",
            pointCount: pointCount,
            bounds: boundsConforming.IsEmpty ? bounds : boundsConforming,
            filePath: path,
            nodes: nodes,
            schema: schema,
            dataType: dataType);
        _scans.Add(_primaryScan);

        _metadata["Format"] = "Entwine Point Tile (EPT)";
        _metadata["Version"] = version;
        _metadata["DataType"] = dataType;
        _metadata["HierarchyType"] = hierarchyType;
        _metadata["NodeCount"] = nodes.Count.ToString(CultureInfo.InvariantCulture);
        _metadata["Span"] = span.ToString(CultureInfo.InvariantCulture);
        _metadata["TotalPoints"] = pointCount.ToString(CultureInfo.InvariantCulture);
        if (srs != null) _metadata["CoordinateSystem"] = srs;
    }

    /// <summary>Opens an EPT dataset from a folder, an ept.json manifest, or a file within ept-data.</summary>
    /// <param name="path">Path to the dataset directory or manifest file.</param>
    /// <param name="options">Read options (accepted for symmetry).</param>
    /// <exception cref="FileNotFoundException">The path or ept.json cannot be found.</exception>
    /// <exception cref="InvalidDataException">The manifest or hierarchy is malformed or truncated.</exception>
    /// <exception cref="NotSupportedException">The dataset uses an unsupported data type or feature.</exception>
    public static EptDocument Open(string path, PclReadOptions? options = null)
    {
        string manifestPath;
        if (Directory.Exists(path))
        {
            manifestPath = Path.Combine(path, "ept.json");
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException($"EPT directory does not contain 'ept.json': '{path}'.", manifestPath);
        }
        else if (File.Exists(path))
        {
            string filename = Path.GetFileName(path);
            if (string.Equals(filename, "ept.json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            {
                manifestPath = path;
            }
            else
            {
                // Search ancestor directories for companion ept.json
                string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
                string? found = null;
                for (int depth = 0; depth < 5 && dir != null; depth++)
                {
                    string candidate = Path.Combine(dir, "ept.json");
                    if (File.Exists(candidate))
                    {
                        found = candidate;
                        break;
                    }
                    string? parent = Path.GetDirectoryName(dir);
                    if (parent == dir) break;
                    dir = parent;
                }

                if (found == null)
                    throw new FileNotFoundException($"Could not find companion 'ept.json' for '{path}'.", path);
                manifestPath = found;
            }
        }
        else
        {
            throw new FileNotFoundException($"EPT file or directory not found: '{path}'.", path);
        }

        var fi = new FileInfo(manifestPath);
        if (fi.Length > MaxManifestBytes)
            throw FormatError.Unsupported($"EPT manifest ({fi.Length} bytes) exceeds safety limit of {MaxManifestBytes} bytes.");

        byte[] jsonBytes = File.ReadAllBytes(manifestPath);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonBytes);
        }
        catch (JsonException ex)
        {
            throw FormatError.Corrupted($"Failed to parse EPT manifest JSON: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw FormatError.Corrupted("EPT manifest root must be a JSON object.");

            string version = root.TryGetProperty("version", out var vElem) ? (vElem.GetString() ?? "1.0.0") : "1.0.0";

            if (!root.TryGetProperty("dataType", out var dtElem) || dtElem.ValueKind != JsonValueKind.String)
                throw FormatError.Corrupted("EPT manifest is missing 'dataType' property.");
            string dataType = dtElem.GetString() ?? "laszip";

            if (dataType is not ("laszip" or "binary" or "zstandard"))
                throw FormatError.Unsupported($"EPT data type '{dataType}' is not supported; only 'laszip', 'binary', and 'zstandard' are read.");

            string hierarchyType = root.TryGetProperty("hierarchyType", out var htElem) ? (htElem.GetString() ?? "json") : "json";
            long declaredPoints = root.TryGetProperty("points", out var pElem) ? pElem.GetInt64() : 0;
            int span = root.TryGetProperty("span", out var spElem) ? spElem.GetInt32() : 256;

            if (!root.TryGetProperty("bounds", out var bElem) || bElem.ValueKind != JsonValueKind.Array || bElem.GetArrayLength() != 6)
                throw FormatError.Corrupted("EPT manifest is missing 6-element 'bounds' array.");

            double[] b = new double[6];
            int bIdx = 0;
            foreach (var elem in bElem.EnumerateArray())
            {
                if (elem.ValueKind != JsonValueKind.Number || !elem.TryGetDouble(out b[bIdx]))
                    throw FormatError.Corrupted("EPT bounding box coordinates must be finite numbers.");
                bIdx++;
            }

            var bounds = new PclBounds(new PclVector3(b[0], b[1], b[2]), new PclVector3(b[3], b[4], b[5]));
            if (bounds.IsEmpty || !double.IsFinite(bounds.Min.X) || !double.IsFinite(bounds.Min.Y) || !double.IsFinite(bounds.Min.Z) ||
                !double.IsFinite(bounds.Max.X) || !double.IsFinite(bounds.Max.Y) || !double.IsFinite(bounds.Max.Z))
                throw FormatError.Corrupted("EPT bounding box coordinates must be finite.");

            PclBounds boundsConforming = bounds;
            if (root.TryGetProperty("boundsConforming", out var bcElem) && bcElem.ValueKind == JsonValueKind.Array && bcElem.GetArrayLength() == 6)
            {
                double[] bc = new double[6];
                int bcIdx = 0;
                bool valid = true;
                foreach (var elem in bcElem.EnumerateArray())
                {
                    if (elem.ValueKind != JsonValueKind.Number || !elem.TryGetDouble(out bc[bcIdx++]))
                    {
                        valid = false;
                        break;
                    }
                }
                if (valid)
                {
                    var cBounds = new PclBounds(new PclVector3(bc[0], bc[1], bc[2]), new PclVector3(bc[3], bc[4], bc[5]));
                    if (!cBounds.IsEmpty) boundsConforming = cBounds;
                }
            }

            if (!root.TryGetProperty("schema", out var sElem) || sElem.ValueKind != JsonValueKind.Array)
                throw FormatError.Corrupted("EPT manifest is missing 'schema' array.");

            var schema = new List<EptDimension>();
            foreach (var dimElem in sElem.EnumerateArray())
            {
                if (dimElem.ValueKind != JsonValueKind.Object) continue;
                string name = dimElem.TryGetProperty("name", out var nElem) ? (nElem.GetString() ?? "") : "";
                string type = dimElem.TryGetProperty("type", out var tElem) ? (tElem.GetString() ?? "unsigned") : "unsigned";
                int size = dimElem.TryGetProperty("size", out var szElem) ? szElem.GetInt32() : 4;
                double scale = dimElem.TryGetProperty("scale", out var scElem) ? scElem.GetDouble() : 1.0;
                double offset = dimElem.TryGetProperty("offset", out var offElem) ? offElem.GetDouble() : 0.0;
                if (!string.IsNullOrEmpty(name))
                    schema.Add(new EptDimension(name, type, size, scale, offset));
            }

            if (!schema.Any(d => d.Name.Equals("X", StringComparison.OrdinalIgnoreCase)) ||
                !schema.Any(d => d.Name.Equals("Y", StringComparison.OrdinalIgnoreCase)) ||
                !schema.Any(d => d.Name.Equals("Z", StringComparison.OrdinalIgnoreCase)))
            {
                throw FormatError.Corrupted("EPT schema must declare X, Y, and Z dimensions.");
            }

            string? srs = null;
            if (root.TryGetProperty("srs", out var srsElem))
            {
                if (srsElem.ValueKind == JsonValueKind.String)
                    srs = srsElem.GetString();
                else if (srsElem.ValueKind == JsonValueKind.Object)
                {
                    if (srsElem.TryGetProperty("wkt", out var wktElem) && wktElem.ValueKind == JsonValueKind.String)
                        srs = wktElem.GetString();
                    else if (srsElem.TryGetProperty("horizontal", out var hElem) && hElem.ValueKind == JsonValueKind.String)
                        srs = hElem.GetString();
                }
            }

            string datasetDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? "";
            var nodes = EptHierarchy.ReadHierarchy(datasetDir, bounds, dataType);

            long pointCount = declaredPoints > 0 ? declaredPoints : nodes.Where(n => n.PointCount > 0).Sum(n => n.PointCount);

            return new EptDocument(
                manifestPath,
                fi.Length,
                version,
                bounds,
                boundsConforming,
                dataType,
                hierarchyType,
                declaredPoints,
                span,
                srs,
                schema,
                nodes,
                pointCount);
        }
    }

    /// <summary>Finds octree nodes whose voxel bounding box intersects the given spatial bounds.</summary>
    /// <param name="bounds">Query bounding box.</param>
    /// <param name="maxDepth">Optional maximum octree depth to include.</param>
    public IReadOnlyList<EptNode> FindNodes(PclBounds bounds, int? maxDepth = null)
    {
        if (bounds.IsEmpty) return Array.Empty<EptNode>();
        return _nodes
            .Where(n => (maxDepth == null || n.Key.Depth <= maxDepth.Value) && n.Bounds.Intersects(bounds))
            .ToList();
    }

    /// <summary>Streams points from a single octree node.</summary>
    /// <param name="node">The octree node to read points from.</param>
    /// <param name="options">Read options (subsampling, limits, transforms).</param>
    public IEnumerable<PclPoint> ReadPoints(EptNode node, PclReadOptions? options = null)
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
}
