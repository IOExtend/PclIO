using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using PclIO.Internal;

namespace PclIO.Formats.Recap;

/// <summary>
/// Reads a ReCap project archive (.rcp) and lists its scan nodes. Referenced .rcs files are looked up next to the
/// project; paths stored in the archive are never followed outside the project folder. The transform of a scan node
/// places the scan in the project and is applied to its points unless reading asks not to; when the manifest states
/// no transform for a node, the transform of the scan file is used.
/// </summary>
public sealed class RcpProjectDocument : PclDocument
{
    // Limits for untrusted archive content; exceeding any of them is reported as a corrupt file.
    private const int MaxManifestBytes = 64 * 1024 * 1024;
    private const int MaxXmlDepth = 64;
    private const int MaxXmlElements = 1_000_000;
    private const int MaxScans = 65_536;

    /// <inheritdoc />
    public override PclFileKind Kind => PclFileKind.RecapProject;

    /// <summary>Project identifier from the manifest; empty when the manifest has none.</summary>
    public string ProjectId { get; }

    /// <inheritdoc />
    public override IReadOnlyList<PclScan> Scans => _scans;
    private readonly List<PclScan> _scans = [];

    private RcpProjectDocument(string path, long fileSize, string projectId)
        : base(path, fileSize)
    {
        ProjectId = projectId;
        if (projectId.Length > 0) _metadata["ProjectId"] = projectId;
    }

    /// <summary>
    /// Opens an .rcp archive and parses its XML manifest. The manifest is limited to 64 MiB, 64 levels of nesting,
    /// 1,000,000 elements and 65,536 scan nodes; larger input is rejected.
    /// </summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The archive or manifest is missing, malformed or exceeds a limit.</exception>
    public static RcpProjectDocument Open(string path, PclReadOptions? options = null)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("RCP file not found.", path);

        using var zip = ZipFile.OpenRead(path);
        ZipArchiveEntry? projectXmlEntry = null;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                projectXmlEntry = entry;
                break;
            }
        }

        if (projectXmlEntry == null)
            throw FormatError.Corrupted("RCP archive does not contain project XML manifest.");

        var doc = LoadManifest(projectXmlEntry);

        string projectId = doc.Root?.Element("ProjectId")?.Attribute("UUID")?.Value
            ?? doc.Root?.Attribute("ProjectId")?.Value
            ?? doc.Root?.Element("ProjectId")?.Value
            ?? string.Empty;
        var project = new RcpProjectDocument(path, fi.Length, projectId);

        var coordElem = doc.Root?.Element("CoordinateSystem");
        project.CoordinateSystem = coordElem is { Value.Length: > 0 }
            ? coordElem.Value
            : coordElem?.Attribute("Value")?.Value;

        string projectDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        string projectName = Path.GetFileNameWithoutExtension(path);
        string supportDir = Path.Combine(projectDir, $"{projectName} Support");

        // Scan node element name; the parser needs it as a key.
        // Resolution cost per scan is constant (at most four File.Exists calls), so the whole pass is linear.
        int index = 0;
        foreach (var node in doc.Root?.Descendants("VoxelTreeRunTime") ?? [])
        {
            if (index >= MaxScans) throw FormatError.Corrupted("RCP project lists too many scans.");

            // Accepted spellings of each field; when several are present, the first one listed wins.
            string id = node.Attribute("Id")?.Value
                ?? node.Attribute("ScanId")?.Value
                ?? $"scan-{index}";
            string name = node.Element("NodeName")?.Attribute("Value")?.Value
                ?? node.Attribute("ScanName")?.Value
                ?? id;
            string? rawRcsPath = node.Element("Path")?.Attribute("Value")?.Value
                ?? node.Element("RelativePath")?.Value;

            // A missing count stays 0 (unknown); an unparsable one is an error.
            long numPoints = 0;
            string? numPointsStr = node.Element("NumPoints")?.Attribute("Value")?.Value
                ?? node.Attribute("TotalPoints")?.Value;
            if (!string.IsNullOrEmpty(numPointsStr) &&
                (!long.TryParse(numPointsStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out numPoints) || numPoints < 0))
                throw FormatError.Corrupted($"Invalid point count for scan '{name}'.");

            var (transform, transformStated) = ParseNodeTransform(node);

            string? resolvedRcsPath = ResolveRcsPath(projectDir, supportDir, rawRcsPath, name);
            if (resolvedRcsPath == null)
                project._warnings.Add($"Scan '{name}': scan file was not found next to the project or its path was rejected; it yields no points.");

            project._scans.Add(new RcpScan(id, name, numPoints, transform, resolvedRcsPath, transformStated));
            index++;
        }

        return project;
    }

    // Loads the manifest with size, depth and element-count caps and without DTD processing
    // (no entity expansion, no external resolution).
    private static XDocument LoadManifest(ZipArchiveEntry entry)
    {
        // The declared size is checked first; the copy below checks the real size, since the header can lie.
        if (entry.Length > MaxManifestBytes)
            throw FormatError.Corrupted("RCP project manifest is too large.");

        using var source = entry.Open();
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaxManifestBytes)
                throw FormatError.Corrupted("RCP project manifest is too large.");
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxManifestBytes,
            MaxCharactersFromEntities = 1024,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
        };

        try
        {
            using var reader = XmlReader.Create(buffer, settings);
            return BuildDocument(reader);
        }
        catch (Exception ex) when (ex is XmlException or ArgumentException)
        {
            // ArgumentException: XLinq rejects reserved namespace declarations that the reader let through.
            throw FormatError.Corrupted($"RCP project manifest is not valid XML ({ex.Message}).");
        }
    }

    // Builds the tree node by node (like XDocument.Load) so that depth and element count can be limited.
    private static XDocument BuildDocument(XmlReader reader)
    {
        var doc = new XDocument();
        XContainer current = doc;
        int elements = 0;

        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    if (reader.Depth >= MaxXmlDepth)
                        throw FormatError.Corrupted("RCP project manifest is nested too deeply.");
                    if (++elements > MaxXmlElements)
                        throw FormatError.Corrupted("RCP project manifest has too many elements.");

                    var element = new XElement(XNamespace.Get(reader.NamespaceURI).GetName(reader.LocalName));
                    bool isEmpty = reader.IsEmptyElement;
                    while (reader.MoveToNextAttribute())
                    {
                        var ns = reader.Prefix.Length == 0 ? XNamespace.None : XNamespace.Get(reader.NamespaceURI);
                        element.Add(new XAttribute(ns.GetName(reader.LocalName), reader.Value));
                    }

                    current.Add(element);
                    if (!isEmpty) current = element;
                    break;

                case XmlNodeType.EndElement:
                    current = ((XElement)current).Parent ?? (XContainer)doc;
                    break;

                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.SignificantWhitespace:
                    if (current is XElement) current.Add(new XText(reader.Value));
                    break;
            }
        }

        return doc;
    }

    // Components may be written as x/y/z, X/Y/Z, or (for rotation) Yaw/Pitch/Roll; the first one present is used.
    // Rotation is informational only: Yaw, Pitch and Roll fill the X, Y and Z components in that order, with no
    // rotation convention implied, and PclTransform does not apply them.
    // The second value tells whether the project states a transform for the node at all.
    private static (PclTransform Transform, bool Stated) ParseNodeTransform(XElement node)
    {
        var transElem = node.Element("Translation");
        var rotElem = node.Element("Rotation");
        var scaleElem = node.Element("Scale");

        bool stated = transElem != null || rotElem != null || scaleElem != null;

        double tx = ParseDouble(transElem?.Attribute("x")?.Value ?? transElem?.Attribute("X")?.Value, 0);
        double ty = ParseDouble(transElem?.Attribute("y")?.Value ?? transElem?.Attribute("Y")?.Value, 0);
        double tz = ParseDouble(transElem?.Attribute("z")?.Value ?? transElem?.Attribute("Z")?.Value, 0);

        double rx = ParseDouble(rotElem?.Attribute("x")?.Value ?? rotElem?.Attribute("X")?.Value ?? rotElem?.Attribute("Yaw")?.Value, 0);
        double ry = ParseDouble(rotElem?.Attribute("y")?.Value ?? rotElem?.Attribute("Y")?.Value ?? rotElem?.Attribute("Pitch")?.Value, 0);
        double rz = ParseDouble(rotElem?.Attribute("z")?.Value ?? rotElem?.Attribute("Z")?.Value ?? rotElem?.Attribute("Roll")?.Value, 0);

        double sx = ParseDouble(scaleElem?.Attribute("x")?.Value ?? scaleElem?.Attribute("X")?.Value, 1);
        double sy = ParseDouble(scaleElem?.Attribute("y")?.Value ?? scaleElem?.Attribute("Y")?.Value, 1);
        double sz = ParseDouble(scaleElem?.Attribute("z")?.Value ?? scaleElem?.Attribute("Z")?.Value, 1);

        var transform = new PclTransform
        {
            Translation = new PclVector3(tx, ty, tz),
            Rotation = new PclVector3(rx, ry, rz),
            Scale = new PclVector3(sx, sy, sz)
        };

        return (transform, stated);
    }

    // Looks for the scan file inside the project folder only; every candidate goes through SafePath.
    private static string? ResolveRcsPath(string projectDir, string supportDir, string? rawPath, string nodeName)
    {
        if (!string.IsNullOrEmpty(rawPath))
        {
            // The path as written (relative to the project), then just its file name next to the project.
            string fileName = Path.GetFileName(rawPath);
            string? found = FindFile(projectDir, rawPath)
                ?? FindFile(supportDir, fileName)
                ?? FindFile(projectDir, fileName);
            if (found != null) return found;
        }

        return FindFile(supportDir, $"{nodeName}.rcs");
    }

    private static string? FindFile(string baseDirectory, string relativePath)
    {
        string candidate;
        try
        {
            candidate = SafePath.Combine(baseDirectory, relativePath);
        }
        catch (InvalidDataException)
        {
            // Rooted, UNC or escaping paths are never opened; the caller records a warning when nothing resolves.
            return null;
        }

        return File.Exists(candidate) ? candidate : null;
    }

    private static double ParseDouble(string? text, double fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double val) || !double.IsFinite(val))
            throw FormatError.Corrupted("Invalid number in RCP project transform.");
        return val;
    }
}

/// <summary>Scan node of a ReCap project.</summary>
public sealed class RcpScan : PclScan
{
    // True when the project states a transform for the node; it then places the scan.
    private readonly bool _transformStated;

    /// <inheritdoc />
    public override string Id { get; }

    /// <inheritdoc />
    public override string Name { get; }

    /// <summary>Point count stated by the project manifest; 0 when it states none.</summary>
    public override long PointCount { get; }

    /// <summary>Resolved .rcs file next to the project, or null when it was not found.</summary>
    public string? RcsFilePath { get; }

    /// <inheritdoc />
    public override bool HasColors => RcsFilePath != null;

    /// <inheritdoc />
    public override bool HasNormals => RcsFilePath != null;

    /// <inheritdoc />
    public override bool HasIntensities => RcsFilePath != null;

    internal RcpScan(string id, string name, long pointCount, PclTransform transform, string? rcsFilePath, bool transformStated)
    {
        Id = id;
        Name = name;
        PointCount = pointCount;
        Transform = transform;
        RcsFilePath = rcsFilePath;
        _transformStated = transformStated;
    }

    /// <summary>
    /// Streams the points of the referenced .rcs file. The transform of the project places the scan in the project, so
    /// it is applied to the points instead of the transform of the scan file; when the project states no transform, the
    /// transform of the scan file is used. Yields nothing when the file was not found (the document records a warning).
    /// </summary>
    public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null)
    {
        options ??= PclReadOptions.Default;
        if (RcsFilePath == null) yield break;

        using var rcsDoc = RcsScanDocument.Open(RcsFilePath, options);

        // The transform of the project and the one of the scan file place the scan in the same way, so only one of
        // them is applied.
        bool place = options.ApplyTransform && _transformStated;
        PclReadOptions readOptions = place ? options with { ApplyTransform = false } : options;

        foreach (var pt in rcsDoc.ReadAllPoints(readOptions))
        {
            yield return place ? pt with { Position = Transform.TransformPoint(pt.Position) } : pt;
        }
    }
}
