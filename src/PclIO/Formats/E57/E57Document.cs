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
/// Cartesian X/Y/Z are single or double precision floats or scaled integers (raw value times scale plus offset),
/// or spherical range, azimuth and elevation converted to Cartesian points. Colour and intensity are Integer fields
/// with a range from zero (8, 16 or 32 bits), Float fields, or scaled-integer intensity; per-point validity flags
/// skip invalid points and clear invalid channels. Images are not read.
/// Not supported (reported with <see cref="NotSupportedException"/> when points are read): integer coordinates,
/// other codecs, and packets whose streams hold different record counts.
/// Per-point validity flags of another shape, images and 2D data are not interpreted.
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

    /// <summary>Stream index of Cartesian X, Y and Z, or all -1 when the scan stores spherical coordinates.</summary>
    public int[] Coordinate { get; } = [-1, -1, -1];

    /// <summary>Bytes per record of the Cartesian streams: 4 or 8 for floats, 1, 2, 4 or 8 for scaled integers.</summary>
    public int[] CoordinateSize { get; } = new int[3];

    /// <summary>True for the axes stored as scaled integers (raw value times scale plus offset).</summary>
    public bool[] CoordinateScaled { get; } = new bool[3];

    /// <summary>True for the scaled axes whose range is not negative, stored as unsigned integers.</summary>
    public bool[] CoordinateUnsigned { get; } = new bool[3];

    /// <summary>Scale of the scaled axes; 1 for float axes.</summary>
    public double[] CoordinateScale { get; } = [1, 1, 1];

    /// <summary>Offset of the scaled axes; 0 for float axes.</summary>
    public double[] CoordinateOffset { get; } = new double[3];

    /// <summary>True when the scan stores spherical coordinates (range, azimuth, elevation) instead of Cartesian ones.</summary>
    public bool IsSpherical { get; private set; }

    /// <summary>Stream index of spherical range, azimuth and elevation, or all -1 for Cartesian scans.</summary>
    public int[] Spherical { get; } = [-1, -1, -1];

    /// <summary>Bytes per record of the spherical streams, sized as the Cartesian ones are.</summary>
    public int[] SphericalSize { get; } = new int[3];

    /// <summary>True for the spherical components stored as scaled integers.</summary>
    public bool[] SphericalScaled { get; } = new bool[3];

    /// <summary>True for the scaled spherical components whose range is not negative.</summary>
    public bool[] SphericalUnsigned { get; } = new bool[3];

    /// <summary>Scale of the scaled spherical components; 1 otherwise.</summary>
    public double[] SphericalScale { get; } = [1, 1, 1];

    /// <summary>Offset of the scaled spherical components; 0 otherwise.</summary>
    public double[] SphericalOffset { get; } = new double[3];

    /// <summary>Stream index of red, green and blue, or all -1 when colour is not decoded.</summary>
    public int[] Color { get; } = [-1, -1, -1];

    /// <summary>Bytes per record of the colour streams: 1, 2 or 4 for Integer ranges from zero.</summary>
    public int[] ColorSize { get; } = [1, 1, 1];

    /// <summary>Maximum of the colour ranges; an 8-bit channel holds the raw value times 255 divided by it.</summary>
    public ulong[] ColorMax { get; } = [255, 255, 255];

    /// <summary>Stream index of intensity, or -1 when intensity is not decoded.</summary>
    public int Intensity { get; private set; } = -1;

    /// <summary>Bytes per record of the intensity stream.</summary>
    public int IntensitySize { get; private set; } = 1;

    /// <summary>True when intensity is an integer (raw value times scale plus offset); false for Float.</summary>
    public bool IntensityScaled { get; private set; } = true;

    /// <summary>True when the integer intensity range is not negative.</summary>
    public bool IntensityUnsigned { get; private set; } = true;

    /// <summary>Scale of an integer intensity (1 divided by the maximum for a plain Integer); 1 for Float.</summary>
    public double IntensityScale { get; private set; } = 1.0 / 255.0;

    /// <summary>Offset of a scaled integer intensity; 0 otherwise.</summary>
    public double IntensityOffset { get; private set; }

    /// <summary>Stream index of the validity flags (Cartesian point, spherical point, colour, intensity), or -1.</summary>
    public int[] Invalid { get; } = [-1, -1, -1, -1];

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

            int spherical = field.Name.LocalName switch
            {
                "sphericalRange" => 0,
                "sphericalAzimuth" => 1,
                "sphericalElevation" => 2,
                _ => -1,
            };

            int invalidKind = field.Name.LocalName switch
            {
                "cartesianInvalidState" => 0,
                "sphericalInvalidState" => 1,
                "isColorInvalid" => 2,
                "isIntensityInvalid" => 3,
                _ => -1,
            };

            if (axis >= 0)
            {
                if (layout.Coordinate[axis] >= 0)
                {
                    unsupportedReason = "a cartesian field is duplicated";
                    return null;
                }

                int size = FloatSize(field);
                if (size != 0)
                {
                    layout.Coordinate[axis] = i;
                    layout.CoordinateSize[axis] = size;
                    continue;
                }

                int scaledSize = ScaledIntegerSize(field, out double scale, out double offset, out bool unsigned);
                if (scaledSize == 0)
                {
                    unsupportedReason = "a cartesian field is neither a single or double precision float nor a scaled integer with a sized range";
                    return null;
                }

                layout.Coordinate[axis] = i;
                layout.CoordinateSize[axis] = scaledSize;
                layout.CoordinateScaled[axis] = true;
                layout.CoordinateUnsigned[axis] = unsigned;
                layout.CoordinateScale[axis] = scale;
                layout.CoordinateOffset[axis] = offset;
            }
            else if (spherical >= 0)
            {
                if (layout.Spherical[spherical] >= 0)
                {
                    unsupportedReason = "a spherical field is duplicated";
                    return null;
                }

                int size = FloatSize(field);
                if (size != 0)
                {
                    layout.Spherical[spherical] = i;
                    layout.SphericalSize[spherical] = size;
                    continue;
                }

                int scaledSize = ScaledIntegerSize(field, out double scale, out double offset, out bool unsigned);
                if (scaledSize == 0)
                {
                    unsupportedReason = "a spherical field is neither a single or double precision float nor a scaled integer with a sized range";
                    return null;
                }

                layout.Spherical[spherical] = i;
                layout.SphericalSize[spherical] = scaledSize;
                layout.SphericalScaled[spherical] = true;
                layout.SphericalUnsigned[spherical] = unsigned;
                layout.SphericalScale[spherical] = scale;
                layout.SphericalOffset[spherical] = offset;
            }
            else if (invalidKind >= 0)
            {
                // Validity flags hold one flag byte per record; any other shape stays unread like an unknown field.
                if (layout.Invalid[invalidKind] < 0 && TryValidityField(field)) layout.Invalid[invalidKind] = i;
            }
            else if (field.Name.LocalName is "colorRed" or "colorGreen" or "colorBlue")
            {
                int channel = field.Name.LocalName switch
                {
                    "colorRed" => 0,
                    "colorGreen" => 1,
                    _ => 2,
                };
                if (layout.Color[channel] < 0 && TryColorField(field, out int size, out ulong max))
                {
                    layout.Color[channel] = i;
                    layout.ColorSize[channel] = size;
                    layout.ColorMax[channel] = max;
                }
            }
            else if (field.Name.LocalName == "intensity" && layout.Intensity < 0 &&
                TryIntensityField(field, out int size, out bool isFloat, out bool unsigned, out double scale, out double offset))
            {
                layout.Intensity = i;
                layout.IntensitySize = size;
                layout.IntensityScaled = !isFloat;
                layout.IntensityUnsigned = unsigned;
                layout.IntensityScale = scale;
                layout.IntensityOffset = offset;
            }
        }

        bool hasCartesian = layout.Coordinate.All(c => c >= 0);
        bool hasSpherical = layout.Spherical.All(c => c >= 0);
        if (hasCartesian && hasSpherical)
        {
            unsupportedReason = "the point prototype has both cartesian and spherical coordinates";
            return null;
        }

        if (!hasCartesian && !hasSpherical)
        {
            bool sawSpherical = layout.Spherical.Any(c => c >= 0);
            unsupportedReason = sawSpherical
                ? "the point prototype lacks sphericalRange, sphericalAzimuth or sphericalElevation"
                : "the point prototype lacks cartesianX, cartesianY or cartesianZ";
            return null;
        }

        layout.IsSpherical = hasSpherical;

        // Colour is all-or-nothing with one shared encoding.
        if (layout.Color.Any(c => c < 0) || layout.ColorSize.Distinct().Count() > 1 || layout.ColorMax.Distinct().Count() > 1)
            Array.Fill(layout.Color, -1);
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

    // The binary size of a ScaledInteger field follows from its range, as the standard sizes it: the smallest of
    // 1, 2, 4 or 8 bytes that holds every value from minimum to maximum, unsigned when the minimum is not negative
    // and signed otherwise. 0 when the field is no ScaledInteger. Scale defaults to 1 and offset to 0.
    private static int ScaledIntegerSize(XElement field, out double scale, out double offset, out bool unsigned)
    {
        scale = 1;
        offset = 0;
        unsigned = true;
        if ((string?)field.Attribute("type") != "ScaledInteger") return 0;

        if (!long.TryParse((string?)field.Attribute("minimum"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long min) ||
            !long.TryParse((string?)field.Attribute("maximum"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long max))
            throw FormatError.Corrupted("E57 scaled integer minimum or maximum is not a valid integer.");
        if (min > max) throw FormatError.Corrupted("E57 scaled integer minimum exceeds its maximum.");

        string? scaleText = (string?)field.Attribute("scale");
        if (!string.IsNullOrWhiteSpace(scaleText) &&
            (!double.TryParse(scaleText, NumberStyles.Float, CultureInfo.InvariantCulture, out scale) || !double.IsFinite(scale)))
            throw FormatError.Corrupted("E57 scaled integer scale is not a finite number.");

        string? offsetText = (string?)field.Attribute("offset");
        if (!string.IsNullOrWhiteSpace(offsetText) &&
            (!double.TryParse(offsetText, NumberStyles.Float, CultureInfo.InvariantCulture, out offset) || !double.IsFinite(offset)))
            throw FormatError.Corrupted("E57 scaled integer offset is not a finite number.");

        return IntegerByteSize(min, max, out unsigned);
    }

    // The binary size of an Integer field follows from its range like a ScaledInteger's: the smallest of 1, 2, 4 or
    // 8 bytes that holds it. 0 when the field is no Integer or its range is invalid; auxiliary fields with such a
    // range stay unread instead of failing the whole file.
    private static int IntegerSize(XElement field, out long min, out long max, out bool unsigned)
    {
        min = 0;
        max = 0;
        unsigned = true;
        if ((string?)field.Attribute("type") != "Integer") return 0;
        if (!long.TryParse((string?)field.Attribute("minimum"), NumberStyles.Integer, CultureInfo.InvariantCulture, out min) ||
            !long.TryParse((string?)field.Attribute("maximum"), NumberStyles.Integer, CultureInfo.InvariantCulture, out max) ||
            min > max)
            return 0;

        return IntegerByteSize(min, max, out unsigned);
    }

    // The smallest of 1, 2, 4 or 8 bytes that holds every value from minimum to maximum, unsigned when the minimum
    // is not negative and signed otherwise.
    private static int IntegerByteSize(long min, long max, out bool unsigned)
    {
        unsigned = true;
        if (min >= 0)
        {
            if ((ulong)max <= 0xFF) return 1;
            if ((ulong)max <= 0xFFFF) return 2;
            if ((ulong)max <= 0xFFFFFFFF) return 4;
            return 8;
        }

        unsigned = false;
        if (min >= -0x80 && max <= 0x7F) return 1;
        if (min >= -0x8000 && max <= 0x7FFF) return 2;
        if (min >= -0x80000000L && max <= 0x7FFFFFFF) return 4;
        return 8;
    }

    // A colour channel is an Integer range from zero with a byte-aligned maximum, decoded to 8 bits.
    private static bool TryColorField(XElement field, out int size, out ulong max)
    {
        size = 0;
        max = 0;
        if (IntegerSize(field, out long min, out long rangeMax, out _) != 0 && min == 0 &&
            (rangeMax == 0xFF || rangeMax == 0xFFFF || rangeMax == 0xFFFFFFFFL))
        {
            size = IntegerByteSize(min, rangeMax, out _);
            max = (ulong)rangeMax;
            return true;
        }

        return false;
    }

    // Intensity is a Float used as-is, a ScaledInteger decoded with its scale and offset, or an Integer range from
    // zero normalized by its maximum. A malformed scaled intensity leaves the channel unread instead of failing the file.
    private static bool TryIntensityField(XElement field, out int size, out bool isFloat, out bool unsigned, out double scale, out double offset)
    {
        size = 1;
        isFloat = false;
        unsigned = true;
        scale = 1.0 / 255.0;
        offset = 0;

        int floatSize = FloatSize(field);
        if (floatSize != 0)
        {
            size = floatSize;
            isFloat = true;
            scale = 1;
            return true;
        }

        try
        {
            int scaledSize = ScaledIntegerSize(field, out scale, out offset, out unsigned);
            if (scaledSize != 0)
            {
                size = scaledSize;
                return true;
            }
        }
        catch (InvalidDataException)
        {
            return false;
        }

        if (IntegerSize(field, out long min, out long max, out _) == 0 || min != 0 ||
            (max != 0xFF && max != 0xFFFF && max != 0xFFFFFFFFL))
            return false;

        size = IntegerByteSize(min, max, out _);
        scale = 1.0 / max;
        return true;
    }

    // A validity flag is an Integer field that holds one flag byte per record. Any other shape stays unread like an
    // unknown field, so files the reader cannot interpret keep today's behaviour instead of failing.
    private static bool TryValidityField(XElement field) =>
        IntegerSize(field, out _, out _, out _) == 1;
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

    /// <summary>True when the prototype has red, green and blue fields with a supported encoding.</summary>
    public override bool HasColors => _layout?.HasColor == true;

    /// <summary>True when the prototype has an intensity field with a supported encoding.</summary>
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
    /// Streams the points of this scan. Points flagged invalid by the validity fields of the standard are skipped;
    /// a flagged colour or intensity leaves the point without that channel. Intensity is the stored value divided by
    /// the maximum of its range, or the decoded value for Float and scaled fields.
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

                // A validity stream holds one flag byte per record of its packet; any other shape is not interpreted.
                int pointInvalid = layout.IsSpherical ? layout.Invalid[1] : layout.Invalid[0];
                bool skipInvalid = HonorsFlags(lengths, pointInvalid, records);
                if (skipInvalid && pkt[starts[pointInvalid] + i] != 0) continue;

                var coords = layout.IsSpherical
                    ? ReadSpherical(layout, pkt, starts, i)
                    : new PclVector3(ReadAxis(layout, pkt, starts, 0, i), ReadAxis(layout, pkt, starts, 1, i), ReadAxis(layout, pkt, starts, 2, i));

                bool colorInvalid = HonorsFlags(lengths, layout.Invalid[2], records) && pkt[starts[layout.Invalid[2]] + i] != 0;
                PclColor? color = null;
                if (layout.HasColor && !colorInvalid)
                {
                    color = new PclColor(
                        ReadColorChannel(pkt, starts[layout.Color[0]], i, layout.ColorSize[0], layout.ColorMax[0]),
                        ReadColorChannel(pkt, starts[layout.Color[1]], i, layout.ColorSize[1], layout.ColorMax[1]),
                        ReadColorChannel(pkt, starts[layout.Color[2]], i, layout.ColorSize[2], layout.ColorMax[2]));
                }

                bool intensityInvalid = HonorsFlags(lengths, layout.Invalid[3], records) && pkt[starts[layout.Invalid[3]] + i] != 0;
                float? intensity = null;
                if (layout.Intensity >= 0 && !intensityInvalid)
                {
                    intensity = layout.IntensityScaled
                        ? (float)(ReadRawInteger(pkt, starts[layout.Intensity], layout.IntensitySize, i, layout.IntensityUnsigned) * layout.IntensityScale + layout.IntensityOffset)
                        : (float)ReadCoordinate(pkt, starts[layout.Intensity], layout.IntensitySize, i);
                }

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
        if (layout.IsSpherical)
        {
            for (int component = 0; component < 3; component++)
            {
                int size = layout.SphericalSize[component];
                int length = lengths[layout.Spherical[component]];
                if (length % size != 0) throw FormatError.Corrupted("E57 spherical stream holds a partial record.");
                Agree(ref records, length / size);
            }
        }
        else
        {
            for (int axis = 0; axis < 3; axis++)
            {
                int size = layout.CoordinateSize[axis];
                int length = lengths[layout.Coordinate[axis]];
                if (length % size != 0) throw FormatError.Corrupted("E57 coordinate stream holds a partial record.");
                Agree(ref records, length / size);
            }
        }

        if (layout.HasColor)
        {
            for (int c = 0; c < 3; c++)
            {
                int size = layout.ColorSize[c];
                int length = lengths[layout.Color[c]];
                if (length % size != 0) throw FormatError.Corrupted("E57 colour stream holds a partial record.");
                Agree(ref records, length / size);
            }
        }

        if (layout.Intensity >= 0)
        {
            int length = lengths[layout.Intensity];
            if (length % layout.IntensitySize != 0) throw FormatError.Corrupted("E57 intensity stream holds a partial record.");
            Agree(ref records, length / layout.IntensitySize);
        }
        return records;
    }

    private static void Agree(ref int records, int count)
    {
        if (records >= 0 && records != count)
            throw FormatError.Unsupported("E57 data packets whose streams hold different record counts.");
        records = count;
    }

    private static double ReadAxis(E57PointLayout layout, byte[] packet, int[] starts, int axis, int index) =>
        ReadNumber(packet, starts[layout.Coordinate[axis]], layout.CoordinateSize[axis], index,
            layout.CoordinateScaled[axis], layout.CoordinateUnsigned[axis], layout.CoordinateScale[axis], layout.CoordinateOffset[axis]);

    // Spherical coordinates as the E57 standard defines them: range in the file's unit, azimuth and elevation in
    // radians. The azimuth is the counterclockwise rotation of the positive X axis about the positive Z axis, and
    // the elevation is measured up from the XY plane, so x = r cos(el) cos(az), y = r cos(el) sin(az), z = r sin(el).
    private static PclVector3 ReadSpherical(E57PointLayout layout, byte[] packet, int[] starts, int index)
    {
        double range = ReadNumber(packet, starts[layout.Spherical[0]], layout.SphericalSize[0], index,
            layout.SphericalScaled[0], layout.SphericalUnsigned[0], layout.SphericalScale[0], layout.SphericalOffset[0]);
        double azimuth = ReadNumber(packet, starts[layout.Spherical[1]], layout.SphericalSize[1], index,
            layout.SphericalScaled[1], layout.SphericalUnsigned[1], layout.SphericalScale[1], layout.SphericalOffset[1]);
        double elevation = ReadNumber(packet, starts[layout.Spherical[2]], layout.SphericalSize[2], index,
            layout.SphericalScaled[2], layout.SphericalUnsigned[2], layout.SphericalScale[2], layout.SphericalOffset[2]);

        double cosElevation = Math.Cos(elevation);
        return new PclVector3(
            range * cosElevation * Math.Cos(azimuth),
            range * cosElevation * Math.Sin(azimuth),
            range * Math.Sin(elevation));
    }

    private static double ReadNumber(byte[] packet, int start, int size, int index, bool scaled, bool unsigned, double scale, double offset) =>
        scaled
            ? ReadScaledInteger(packet, start, size, index, unsigned, scale, offset)
            : ReadCoordinate(packet, start, size, index);

    private static double ReadCoordinate(byte[] packet, int start, int size, int index) =>
        size == 4
            ? Bytes.ReadSingleLE(packet.AsSpan(start + index * 4, 4))
            : Bytes.ReadDoubleLE(packet.AsSpan(start + index * 8, 8));

    // A validity stream holds one flag byte per record of its packet; any other shape is not interpreted.
    private static bool HonorsFlags(int[] lengths, int stream, int records) =>
        stream >= 0 && lengths[stream] == records;

    // An 8-bit channel holds the raw value times 255 divided by the maximum of its range.
    private static byte ReadColorChannel(byte[] packet, int start, int index, int size, ulong max)
    {
        double raw = ReadRawInteger(packet, start, size, index, unsigned: true);
        return (byte)Math.Round(raw * 255.0 / max);
    }

    // A raw value of the sized width, signed or unsigned as the range requires; the value is raw times scale plus offset.
    private static double ReadScaledInteger(byte[] packet, int start, int size, int index, bool unsigned, double scale, double offset) =>
        ReadRawInteger(packet, start, size, index, unsigned) * scale + offset;

    // A raw value of the sized width, signed or unsigned as the range requires.
    private static double ReadRawInteger(byte[] packet, int start, int size, int index, bool unsigned) =>
        (size, unsigned) switch
        {
            (1, true) => packet[start + index],
            (1, false) => (sbyte)packet[start + index],
            (2, true) => Bytes.ReadUInt16LE(packet.AsSpan(start + index * 2, 2)),
            (2, false) => Bytes.ReadInt16LE(packet.AsSpan(start + index * 2, 2)),
            (4, true) => Bytes.ReadUInt32LE(packet.AsSpan(start + index * 4, 4)),
            (4, false) => Bytes.ReadInt32LE(packet.AsSpan(start + index * 4, 4)),
            (8, true) => Bytes.ReadUInt64LE(packet.AsSpan(start + index * 8, 8)),
            _ => Bytes.ReadInt64LE(packet.AsSpan(start + index * 8, 8)),
        };
}
