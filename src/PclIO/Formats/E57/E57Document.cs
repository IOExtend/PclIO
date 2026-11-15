using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using PclIO.Internal;

namespace PclIO.Formats.E57;

/// <summary>
/// An ASTM E57 3D Imaging Data File (.e57).
/// Supported: the file header, the checksummed page layout, the XML section, scan names, ids, Cartesian bounds and
/// poses (translation and rotation quaternion), and points stored as uncompressed bit-packed streams where
/// Cartesian X/Y/Z are single or double precision floats and colour or intensity are Integer fields in the range 0..255.
/// Not supported (reported with <see cref="NotSupportedException"/> when points are read): scaled-integer or integer
/// coordinates, other codecs, and packets whose streams hold different record counts.
/// Spherical coordinates, per-point invalid-state flags, images and 2D data are not interpreted.
/// </summary>
public sealed class E57Document : PclDocument
{
    private const int MaxXmlBytes = 32 * 1024 * 1024;
    private const int MaxXmlDepth = 64;
    private const int MaxXmlElements = 1_000_000;
    private const long MaxRecordCount = 1L << 40;

    /// <inheritdoc />
    public override PclFileKind Kind => PclFileKind.E57;

    /// <summary>Major version of the E57 file format from the file header.</summary>
    public uint MajorVersion { get; }

    /// <summary>Minor version of the E57 file format from the file header.</summary>
    public uint MinorVersion { get; }

    /// <summary>Physical page size in bytes from the file header (always 1024).</summary>
    public ulong PageSize { get; }

    /// <summary>Identifier stored in the XML section, or an empty string when the file has none.</summary>
    public string ProjectGuid { get; }

    /// <summary>The XML section of the file as text.</summary>
    public string XmlContent { get; }

    /// <inheritdoc />
    public override IReadOnlyList<PclScan> Scans => _scans;
    private readonly List<PclScan> _scans = [];

    private E57Document(string path, long fileSize, uint major, uint minor, ulong pageSize, string guid, string xmlContent)
        : base(path, fileSize)
    {
        MajorVersion = major;
        MinorVersion = minor;
        PageSize = pageSize;
        ProjectGuid = guid;
        XmlContent = xmlContent;
    }

    /// <summary>Opens an E57 file and reads its header and XML section; point data is read lazily.</summary>
    /// <param name="path">Path of the .e57 file.</param>
    /// <param name="options">Not used when opening; accepted for symmetry with the other readers.</param>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The header, a page checksum or the XML section is malformed or truncated.</exception>
    /// <exception cref="NotSupportedException">The format version is not supported.</exception>
    public static E57Document Open(string path, PclReadOptions? options = null)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("E57 file not found.", path);

        uint major, minor;
        ulong fileLen, xmlOffset, xmlLength, pageSize;
        using (var fs = File.OpenRead(path))
        {
            Span<byte> header = stackalloc byte[48];
            if (fs.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
                throw FormatError.Truncated("48-byte E57 header");

            if (!header[..8].SequenceEqual("ASTM-E57"u8))
                throw FormatError.Corrupted("Missing 'ASTM-E57' file signature.");

            major = Bytes.ReadUInt32LE(header[8..12]);
            minor = Bytes.ReadUInt32LE(header[12..16]);
            fileLen = Bytes.ReadUInt64LE(header[16..24]);
            xmlOffset = Bytes.ReadUInt64LE(header[24..32]);
            xmlLength = Bytes.ReadUInt64LE(header[32..40]);
            pageSize = Bytes.ReadUInt64LE(header[40..48]);
        }

        if (major != 1) throw FormatError.Unsupported($"E57 major version {major}.");
        if (fileLen > (ulong)fi.Length) throw FormatError.Truncated($"{fileLen} bytes as declared in the header");
        if (xmlLength == 0 || xmlLength > MaxXmlBytes) throw FormatError.Corrupted("E57 XML section has an invalid length.");

        string xmlText;
        using (var reader = new E57StreamReader(path, pageSize))
        {
            byte[] xmlBytes = reader.ReadLogicalBytes(xmlOffset, xmlLength);
            int nullIdx = Array.IndexOf(xmlBytes, (byte)0);
            xmlText = nullIdx >= 0
                ? Encoding.UTF8.GetString(xmlBytes, 0, nullIdx)
                : Encoding.UTF8.GetString(xmlBytes);
        }

        var doc = ParseXml(xmlText);
        if (doc.Root is not { Name.LocalName: "e57Root" } root)
            throw FormatError.Corrupted("E57 XML section has no e57Root element.");
        XNamespace ns = root.Name.Namespace;
        string guid = root.Element(ns + "guid")?.Value ?? "";

        var document = new E57Document(path, fi.Length, major, minor, pageSize, guid, xmlText);
        if (guid.Length > 0) document._metadata["ProjectGuid"] = guid;
        document._metadata["FormatVersion"] = $"{major}.{minor}";
        document._metadata["PageSize"] = pageSize.ToString(CultureInfo.InvariantCulture);

        var data3D = root.Element(ns + "data3D");
        if (data3D != null)
        {
            int index = 0;
            foreach (var elem in data3D.Elements())
            {
                index++;
                string scanGuid = elem.Element(ns + "guid")?.Value ?? $"scan-{index}";
                string scanName = elem.Element(ns + "name")?.Value ?? $"Scan {index}";

                long count = 0;
                ulong fileOffset = 0;
                E57PointLayout? layout = null;
                string? unsupportedReason = null;
                var pointsElem = elem.Element(ns + "points");
                if (pointsElem != null)
                {
                    count = ParseCount(pointsElem.Attribute("recordCount")?.Value, "recordCount");
                    fileOffset = ParseOffset(pointsElem.Attribute("fileOffset")?.Value);
                    layout = E57PointLayout.Create(pointsElem, ns, out unsupportedReason);
                }
                else if (elem.Element(ns + "pointsRecordCount") != null)
                {
                    count = ParseCount(elem.Element(ns + "pointsRecordCount")?.Value, "pointsRecordCount");
                    fileOffset = ParseOffset(elem.Element(ns + "fileOffset")?.Value);
                    unsupportedReason = "the scan has no point prototype";
                }

                if (fileOffset != 0 && fileOffset >= (ulong)fi.Length)
                    throw FormatError.Corrupted("E57 point data offset lies outside the file.");

                var bounds = ParseBounds(elem, ns);
                var transform = ParsePose(elem, ns);

                document._scans.Add(new E57Scan(scanGuid, scanName, count, fileOffset, bounds, transform, pageSize, path, layout, unsupportedReason));
            }
        }

        return document;
    }

    // DTDs are refused (no entity expansion, no external resolution) and the tree is bounded before it is built.
    private static XDocument ParseXml(string xmlText)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxXmlBytes,
        };

        try
        {
            using (var probe = XmlReader.Create(new StringReader(xmlText), settings))
            {
                int elements = 0;
                while (probe.Read())
                {
                    if (probe.Depth > MaxXmlDepth) throw FormatError.Corrupted("E57 XML section is nested too deeply.");
                    if (probe.NodeType == XmlNodeType.Element && ++elements > MaxXmlElements)
                        throw FormatError.Corrupted("E57 XML section has too many elements.");
                }
            }

            using var reader = XmlReader.Create(new StringReader(xmlText), settings);
            return XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw FormatError.Corrupted($"E57 XML section is not valid XML ({ex.Message}).");
        }
    }

    // E57 writes a zero Integer as an empty value, so empty text means 0.
    private static long ParseCount(string? text, string what)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) || value < 0 || value > MaxRecordCount)
            throw FormatError.Corrupted($"E57 {what} is not a valid count.");
        return value;
    }

    private static ulong ParseOffset(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        if (!ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong value))
            throw FormatError.Corrupted("E57 fileOffset is not a valid offset.");
        return value;
    }

    // Returns null when the element is absent; an empty Float element is 0 by the same convention as ParseCount.
    private static double? ParseDouble(XElement? parent, XName name)
    {
        var element = parent?.Element(name);
        if (element == null) return null;
        string text = element.Value;
        if (string.IsNullOrWhiteSpace(text)) return 0;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
            throw FormatError.Corrupted($"E57 element '{name.LocalName}' is not a finite number.");
        return value;
    }

    private static PclBounds ParseBounds(XElement scanElem, XNamespace ns)
    {
        var cartBounds = scanElem.Element(ns + "cartesianBounds");
        if (cartBounds == null) return PclBounds.Empty;

        double? xMin = ParseDouble(cartBounds, ns + "xMinimum");
        double? xMax = ParseDouble(cartBounds, ns + "xMaximum");
        double? yMin = ParseDouble(cartBounds, ns + "yMinimum");
        double? yMax = ParseDouble(cartBounds, ns + "yMaximum");
        double? zMin = ParseDouble(cartBounds, ns + "zMinimum");
        double? zMax = ParseDouble(cartBounds, ns + "zMaximum");

        // Bounds are optional metadata; a partial box is reported as unknown.
        if (xMin is null || xMax is null || yMin is null || yMax is null || zMin is null || zMax is null) return PclBounds.Empty;
        return new PclBounds(new PclVector3(xMin.Value, yMin.Value, zMin.Value), new PclVector3(xMax.Value, yMax.Value, zMax.Value));
    }

    private static PclTransform ParsePose(XElement scanElem, XNamespace ns)
    {
        var pose = scanElem.Element(ns + "pose");
        if (pose == null) return PclTransform.Identity;

        var trans = pose.Element(ns + "translation");
        double tx = ParseDouble(trans, ns + "x") ?? 0;
        double ty = ParseDouble(trans, ns + "y") ?? 0;
        double tz = ParseDouble(trans, ns + "z") ?? 0;
        var translation = new PclVector3(tx, ty, tz);

        var rot = pose.Element(ns + "rotation");
        double w = ParseDouble(rot, ns + "w") ?? 1;
        double x = ParseDouble(rot, ns + "x") ?? 0;
        double y = ParseDouble(rot, ns + "y") ?? 0;
        double z = ParseDouble(rot, ns + "z") ?? 0;

        double norm = Math.Sqrt(w * w + x * x + y * y + z * z);
        if (!(norm > 1e-9)) throw FormatError.Corrupted("E57 pose rotation is not a valid quaternion.");
        w /= norm; x /= norm; y /= norm; z /= norm;

        if (x == 0 && y == 0 && z == 0) return new PclTransform { Translation = translation };

        // Row-major 4x4: rotation block from the unit quaternion, then translation.
        double[] matrix =
        [
            1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w), tx,
            2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w), ty,
            2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y), tz,
            0, 0, 0, 1,
        ];
        return new PclTransform { Translation = translation, Matrix4x4 = matrix };
    }
}

/// <summary>
/// Where each decoded field lives in a data packet, derived from the point prototype in the XML section.
/// Streams are numbered in prototype order.
/// </summary>
internal sealed class E57PointLayout
{
    // Reader limit on the number of fields in a prototype; packet headers would allow 65535 streams.
    private const int MaxFields = 1024;

    public int StreamCount { get; }

    /// <summary>Stream index of Cartesian X, Y and Z.</summary>
    public int[] Coordinate { get; } = [-1, -1, -1];

    /// <summary>Bytes per record (4 single, 8 double) of the Cartesian streams.</summary>
    public int[] CoordinateSize { get; } = new int[3];

    /// <summary>Stream index of red, green and blue, or all -1 when colour is not decoded.</summary>
    public int[] Color { get; } = [-1, -1, -1];

    /// <summary>Stream index of intensity, or -1 when intensity is not decoded.</summary>
    public int Intensity { get; private set; } = -1;

    public bool HasColor => Color[0] >= 0;

    private E57PointLayout(int streamCount) => StreamCount = streamCount;

    /// <summary>Builds the layout, or returns null and a reason when the prototype uses an encoding that is not decoded.</summary>
    public static E57PointLayout? Create(XElement points, XNamespace ns, out string? unsupportedReason)
    {
        unsupportedReason = null;
        if (points.Element(ns + "codecs")?.HasElements == true)
        {
            unsupportedReason = "the scan uses a compressed vector codec other than plain bit-packing";
            return null;
        }

        var prototype = points.Element(ns + "prototype");
        if (prototype == null)
        {
            unsupportedReason = "the scan has no point prototype";
            return null;
        }

        var fields = prototype.Elements().ToList();
        if (fields.Count == 0 || fields.Count > MaxFields)
        {
            unsupportedReason = "the point prototype has no fields or too many fields";
            return null;
        }

        var layout = new E57PointLayout(fields.Count);
        for (int i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            int axis = field.Name.LocalName switch
            {
                "cartesianX" => 0,
                "cartesianY" => 1,
                "cartesianZ" => 2,
                _ => -1,
            };

            if (axis >= 0)
            {
                int size = FloatSize(field);
                if (size == 0 || layout.Coordinate[axis] >= 0)
                {
                    unsupportedReason = "a cartesian field is duplicated or is not a single or double precision float";
                    return null;
                }
                layout.Coordinate[axis] = i;
                layout.CoordinateSize[axis] = size;
            }
            else if (IsByteRange(field))
            {
                int channel = field.Name.LocalName switch
                {
                    "colorRed" => 0,
                    "colorGreen" => 1,
                    "colorBlue" => 2,
                    _ => -1,
                };
                if (channel >= 0 && layout.Color[channel] < 0) layout.Color[channel] = i;
                else if (field.Name.LocalName == "intensity" && layout.Intensity < 0) layout.Intensity = i;
            }
        }

        if (layout.Coordinate.Any(c => c < 0))
        {
            unsupportedReason = "the point prototype lacks cartesianX, cartesianY or cartesianZ";
            return null;
        }

        // Colour is all-or-nothing.
        if (layout.Color.Any(c => c < 0)) Array.Fill(layout.Color, -1);
        return layout;
    }

    // 4 or 8 for a Float field (double is the E57 default precision), 0 for any other type.
    private static int FloatSize(XElement field)
    {
        if ((string?)field.Attribute("type") != "Float") return 0;
        return (string?)field.Attribute("precision") switch
        {
            "single" => 4,
            null or "double" => 8,
            _ => 0,
        };
    }

    // An Integer in 0..255 is bit-packed into exactly one byte per record.
    private static bool IsByteRange(XElement field) =>
        (string?)field.Attribute("type") == "Integer" &&
        long.TryParse((string?)field.Attribute("minimum"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long min) && min == 0 &&
        long.TryParse((string?)field.Attribute("maximum"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long max) && max == 255;
}

/// <summary>An individual scan inside an ASTM E57 file.</summary>
public sealed class E57Scan : PclScan
{
    private const int SectionHeaderSize = 32;
    private const int MaxIdlePackets = 4096;

    private readonly ulong _pageSize;
    private readonly string _filePath;
    private readonly E57PointLayout? _layout;
    private readonly string? _unsupportedReason;

    /// <inheritdoc />
    public override string Id { get; }

    /// <inheritdoc />
    public override string Name { get; }

    /// <summary>Number of points declared by the file; the count is not checked against the point data until it is read.</summary>
    public override long PointCount { get; }

    /// <summary>Physical file offset of the compressed-vector section that holds the points, or 0 when there is none.</summary>
    public ulong FileOffset { get; }

    /// <summary>True when the prototype has red, green and blue fields in the range 0..255.</summary>
    public override bool HasColors => _layout?.HasColor == true;

    /// <summary>True when the prototype has an intensity field in the range 0..255.</summary>
    public override bool HasIntensities => _layout is { Intensity: >= 0 };

    internal E57Scan(string id, string name, long pointCount, ulong fileOffset, PclBounds bounds, PclTransform transform, ulong pageSize, string filePath, E57PointLayout? layout, string? unsupportedReason)
    {
        Id = id;
        Name = name;
        PointCount = pointCount;
        FileOffset = fileOffset;
        Bounds = bounds;
        Transform = transform;
        _pageSize = pageSize;
        _filePath = filePath;
        _layout = layout;
        _unsupportedReason = unsupportedReason;
    }

    /// <summary>
    /// Streams the points of this scan. Intensity is the stored byte divided by 255.
    /// <see cref="PclReadOptions.SubsampleStep"/> is applied to the record index across the whole scan.
    /// </summary>
    /// <param name="options">Read options, or null for <see cref="PclReadOptions.Default"/>.</param>
    /// <exception cref="NotSupportedException">The point encoding is not decoded (see <see cref="E57Document"/>).</exception>
    /// <exception cref="InvalidDataException">A page checksum, packet or section header is invalid, or the data ends early.</exception>
    public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null)
    {
        options ??= PclReadOptions.Default;
        if (PointCount == 0 || FileOffset == 0) yield break;
        if (_layout is null) throw FormatError.Unsupported($"E57 points cannot be decoded: {_unsupportedReason}.");

        long limit = options.MaxPoints ?? long.MaxValue;
        if (limit == 0) yield break;
        int step = Math.Max(1, options.SubsampleStep);
        bool applyTransform = options.ApplyTransform;
        var layout = _layout;

        using var reader = new E57StreamReader(_filePath, _pageSize);
        byte[] secHdr = reader.ReadLogical(reader.ToLogicalOffset(FileOffset), SectionHeaderSize);
        if (secHdr[0] != 1) throw FormatError.Corrupted("E57 compressed vector section header expected.");

        ulong dataOffset = Bytes.ReadUInt64LE(secHdr.AsSpan(16, 8));
        if (dataOffset == 0) throw FormatError.Corrupted("E57 compressed vector section has no data packets.");
        ulong position = reader.ToLogicalOffset(dataOffset);

        var starts = new int[layout.StreamCount];
        var lengths = new int[layout.StreamCount];
        long consumed = 0;
        long yielded = 0;
        int idlePackets = 0;

        while (consumed < PointCount && yielded < limit)
        {
            // Each packet advances the position by at least its 4-byte header, and reads past the end of the file throw,
            // so this loop always ends; the idle counter additionally bounds runs of packets that carry no points.
            byte[] head = reader.ReadLogical(position, 4);
            int packetLength = Bytes.ReadUInt16LE(head.AsSpan(2, 2)) + 1;
            if (packetLength < 4) throw FormatError.Corrupted("E57 packet is shorter than its header.");
            if (head[0] > 2) throw FormatError.Corrupted("E57 packet has an unknown type.");

            if (head[0] != 1)
            {
                // Index and empty packets carry no points.
                if (++idlePackets > MaxIdlePackets) throw FormatError.Corrupted("E57 data has too many consecutive packets without points.");
                position += (ulong)packetLength;
                continue;
            }

            if (packetLength < 6) throw FormatError.Corrupted("E57 data packet is shorter than its header.");
            byte[] pkt = reader.ReadLogical(position, (ulong)packetLength);
            position += (ulong)packetLength;

            int streamCount = Bytes.ReadUInt16LE(pkt.AsSpan(4, 2));
            if (streamCount != layout.StreamCount)
                throw FormatError.Corrupted("E57 data packet stream count does not match the point prototype.");

            int offset = 6 + streamCount * 2;
            if (offset > packetLength) throw FormatError.Corrupted("E57 data packet header exceeds the packet length.");
            for (int s = 0; s < streamCount; s++)
            {
                lengths[s] = Bytes.ReadUInt16LE(pkt.AsSpan(6 + s * 2, 2));
                starts[s] = offset;
                offset += lengths[s];
                if (offset > packetLength) throw FormatError.Corrupted("E57 data packet streams exceed the packet length.");
            }

            int records = RecordsInPacket(layout, lengths);
            long take = Math.Min(records, PointCount - consumed);
            if (take == 0 && ++idlePackets > MaxIdlePackets)
                throw FormatError.Corrupted("E57 data has too many consecutive packets without points.");
            if (take > 0) idlePackets = 0;

            for (int i = 0; i < take; i++)
            {
                if ((consumed + i) % step != 0) continue;

                var coords = new PclVector3(
                    ReadCoordinate(pkt, starts[layout.Coordinate[0]], layout.CoordinateSize[0], i),
                    ReadCoordinate(pkt, starts[layout.Coordinate[1]], layout.CoordinateSize[1], i),
                    ReadCoordinate(pkt, starts[layout.Coordinate[2]], layout.CoordinateSize[2], i));

                PclColor? color = null;
                if (layout.HasColor)
                {
                    color = new PclColor(
                        pkt[starts[layout.Color[0]] + i],
                        pkt[starts[layout.Color[1]] + i],
                        pkt[starts[layout.Color[2]] + i]);
                }

                float? intensity = null;
                if (layout.Intensity >= 0) intensity = pkt[starts[layout.Intensity] + i] / 255.0f;

                var pt = new PclPoint(coords, Color: color, Intensity: intensity, Index: (ulong)yielded);
                if (applyTransform) pt = pt with { Position = Transform.TransformPoint(pt.Position) };

                yield return pt;
                yielded++;
                if (yielded >= limit) yield break;
            }

            consumed += take;
        }
    }

    // The decoded streams must hold the same number of whole records; otherwise records cannot be matched up.
    private static int RecordsInPacket(E57PointLayout layout, int[] lengths)
    {
        int records = -1;
        for (int axis = 0; axis < 3; axis++)
        {
            int size = layout.CoordinateSize[axis];
            int length = lengths[layout.Coordinate[axis]];
            if (length % size != 0) throw FormatError.Corrupted("E57 coordinate stream holds a partial record.");
            Agree(ref records, length / size);
        }

        if (layout.HasColor)
        {
            foreach (int stream in layout.Color) Agree(ref records, lengths[stream]);
        }

        if (layout.Intensity >= 0) Agree(ref records, lengths[layout.Intensity]);
        return records;
    }

    private static void Agree(ref int records, int count)
    {
        if (records >= 0 && records != count)
            throw FormatError.Unsupported("E57 data packets whose streams hold different record counts.");
        records = count;
    }

    private static double ReadCoordinate(byte[] packet, int start, int size, int index) =>
        size == 4
            ? Bytes.ReadSingleLE(packet.AsSpan(start + index * 4, 4))
            : Bytes.ReadDoubleLE(packet.AsSpan(start + index * 8, 8));
}
