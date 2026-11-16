using PclIO.Internal;

namespace PclIO.Formats.Ply;

/// <summary>Represents a Stanford polygon file with point cloud data (.ply).</summary>
/// <remarks>
/// <para>
/// Supported: ASCII and binary little and big endian files whose header declares a vertex element; positions from
/// the x, y and z properties, colour from red, green and blue (with alpha when present), intensity from an
/// intensity property, and normals from nx, ny and nz, by those conventional names in any numeric type. Faces and
/// all other elements are ignored. Points whose coordinates are not finite are not returned.
/// </para>
/// <para>
/// Not read (reported with <see cref="NotSupportedException"/> when the file is opened): files without a vertex
/// element or without x, y and z properties, vertex elements with list properties, and files whose first vertex
/// element is preceded by an element of unpredictable size.
/// </para>
/// </remarks>
public sealed class PlyDocument : PclDocument
{
    // Bounds for data taken from an untrusted file.
    private const int MaxHeaderLines = 4096;
    private const int MaxLineLength = 1 << 20;
    private const int MaxElements = 1024;
    private const int MaxProperties = 1024;
    private const int MaxVertexStride = 1 << 20;

    /// <inheritdoc />
    public override PclFileKind Kind => PclFileKind.Ply;

    /// <summary>Version string from the format line.</summary>
    public string Version { get; }

    /// <summary>How the vertex data is stored: ascii, binary_little_endian or binary_big_endian.</summary>
    public string Data { get; }

    /// <inheritdoc />
    public override IReadOnlyList<PclScan> Scans => _scans;
    private readonly List<PclScan> _scans = [];

    private PlyDocument(string path, long fileSize, string version, string data)
        : base(path, fileSize)
    {
        Version = version;
        Data = data;
        _metadata["Format"] = "Stanford PLY";
        _metadata["Version"] = version;
        _metadata["Data"] = data;
    }

    /// <summary>Opens a PLY file and reads its header.</summary>
    /// <param name="path">Path of the .ply file.</param>
    /// <param name="options">Currently unused; accepted for symmetry with the other readers.</param>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The header is damaged or truncated, or declares sizes outside the file.</exception>
    /// <exception cref="NotSupportedException">The file has no usable positions.</exception>
    public static PlyDocument Open(string path, PclReadOptions? options = null)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("PLY file not found.", path);

        using var fs = File.OpenRead(path);
        var header = ReadHeader(fs);

        var vertex = header.Elements.FirstOrDefault(e => e.Name.Equals("vertex", StringComparison.Ordinal));
        if (vertex == null) throw FormatError.Unsupported("PLY file has no vertex element.");

        int x = Find(vertex, "x");
        int y = Find(vertex, "y");
        int z = Find(vertex, "z");
        if (x < 0 || y < 0 || z < 0)
            throw FormatError.Unsupported("PLY file has no x, y and z properties.");

        if (vertex.Properties.Any(p => p.IsList))
            throw FormatError.Unsupported("PLY vertex element has list properties.");

        // Elements before the vertices are skipped; only fixed-size ones can be.
        long vertexDataOffset = header.DataStart;
        long vertexLineSkip = 0;
        foreach (var element in header.Elements)
        {
            if (element == vertex) break;
            if (element.Properties.Any(p => p.IsList))
                throw FormatError.Unsupported($"PLY element '{element.Name}' before the vertices has list properties.");
            vertexDataOffset += element.Count * (long)element.Stride;
            vertexLineSkip += element.Count;
            if (vertexDataOffset < 0) throw FormatError.Corrupted("PLY elements declare more bytes than can be counted.");
        }

        if (header.Data != PlyData.Ascii)
        {
            long need = vertex.Count * (long)vertex.Stride;
            if (need > fs.Length - vertexDataOffset) throw FormatError.Truncated("vertex data");
        }

        var document = new PlyDocument(path, fs.Length, header.Version, header.Data switch
        {
            PlyData.Ascii => "ascii",
            PlyData.LittleEndian => "binary_little_endian",
            _ => "binary_big_endian",
        });

        string name = Path.GetFileNameWithoutExtension(path);
        document._scans.Add(new PlyScan("0", name, vertex.Count, path, header, vertex, vertexDataOffset, vertexLineSkip));
        return document;
    }

    private static int Find(PlyElement element, string name)
    {
        for (int i = 0; i < element.Properties.Length; i++)
            if (element.Properties[i].Name.Equals(name, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>How the vertex data is stored.</summary>
    internal enum PlyData
    {
        Ascii,
        LittleEndian,
        BigEndian,
    }

    /// <summary>One property: its name, size, kind, signedness, list shape and byte offset in a record.</summary>
    internal sealed record PlyProperty(string Name, int Size, bool IsFloat, bool IsSigned, bool IsList, int Offset);

    /// <summary>One element: its name, record count, properties and record stride.</summary>
    internal sealed record PlyElement(string Name, long Count, PlyProperty[] Properties, int Stride);

    /// <summary>The parsed header: everything a read needs.</summary>
    internal sealed record PlyHeader(string Version, PlyData Data, PlyElement[] Elements, long DataStart);

    // Reads the ASCII header up to end_header: ply first, then the format line, then elements, properties
    // and comments in any order. Returns the header and the offset where the data starts.
    private static PlyHeader ReadHeader(FileStream fs)
    {
        PlyData? data = null;
        var elements = new List<PlyElement>();
        PlyElementBuilder? current = null;
        int lines = 0;

        string first = ReadHeaderLine(fs);
        if (!first.Equals("ply", StringComparison.Ordinal)) throw FormatError.Corrupted("not a PLY file, signature mismatch.");

        while (true)
        {
            if (++lines > MaxHeaderLines) throw FormatError.Corrupted("PLY header has too many lines.");
            string line = ReadHeaderLine(fs);
            if (line.Length == 0 || line.StartsWith("comment", StringComparison.Ordinal)) continue;
            if (line.StartsWith("obj_info", StringComparison.Ordinal)) continue;

            if (line.Equals("end_header", StringComparison.Ordinal)) break;

            if (line.StartsWith("format ", StringComparison.Ordinal))
            {
                if (data != null) throw FormatError.Corrupted("PLY header names the format twice.");
                string[] parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3) throw FormatError.Corrupted("PLY format line needs a value and a version.");
                if (!parts[2].Equals("1.0", StringComparison.Ordinal))
                    throw FormatError.Unsupported($"PLY version {parts[2]} is not supported; version 1.0 is read.");
                data = parts[1] switch
                {
                    "ascii" => PlyData.Ascii,
                    "binary_little_endian" => PlyData.LittleEndian,
                    "binary_big_endian" => PlyData.BigEndian,
                    _ => throw FormatError.Corrupted($"PLY data type '{parts[1]}' is not ascii or binary."),
                };

                continue;
            }

            if (line.StartsWith("element ", StringComparison.Ordinal))
            {
                if (data == null) throw FormatError.Corrupted("PLY element comes before the format line.");
                string[] parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3) throw FormatError.Corrupted("PLY element line needs a name and a count.");
                if (elements.Count >= MaxElements) throw FormatError.Corrupted("PLY header declares too many elements.");
                if (!long.TryParse(parts[2], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out long count) || count < 0)
                    throw FormatError.Corrupted($"PLY element count '{parts[2]}' is not a count.");
                current = new PlyElementBuilder(parts[1]);
                elements.Add(current.ToElement(count));
                continue;
            }

            if (line.StartsWith("property ", StringComparison.Ordinal))
            {
                if (current == null) throw FormatError.Corrupted("PLY property comes before any element.");
                current.Add(ParseProperty(line));
                elements[^1] = current.ToElement(elements[^1].Count);
                continue;
            }

            throw FormatError.Corrupted($"PLY header line '{line}' is not recognised.");
        }

        if (data == null) throw FormatError.Corrupted("PLY header misses the format line.");
        return new PlyHeader("1.0", data.Value, [.. elements], fs.Position);
    }

    // One property line: a scalar type and name, or a list with count and item types.
    private static (string Name, int Size, bool IsFloat, bool IsSigned, bool IsList) ParseProperty(string line)
    {
        string[] parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts is ["property", "list", _, _, _])
        {
            int countSize = ScalarSize(parts[2]);
            int itemSize = ScalarSize(parts[3]);
            if (countSize == 0 || itemSize == 0)
                throw FormatError.Corrupted($"PLY list property '{line}' has an unknown type.");
            return (parts[4], itemSize, false, false, true);
        }

        if (parts is ["property", _, _])
        {
            int size = ScalarSize(parts[1]);
            if (size == 0) throw FormatError.Corrupted($"PLY property type '{parts[1]}' is not supported.");
            return (parts[2], size, IsFloatType(parts[1]), IsSignedType(parts[1]), false);
        }

        throw FormatError.Corrupted($"PLY property line '{line}' is not recognised.");
    }

    // The byte size of a scalar type, with the common fixed-width aliases; 0 for anything else.
    private static int ScalarSize(string type) => type switch
    {
        "char" or "int8" => 1,
        "uchar" or "uint8" => 1,
        "short" or "int16" => 2,
        "ushort" or "uint16" => 2,
        "int" or "int32" => 4,
        "uint" or "uint32" => 4,
        "float" or "float32" => 4,
        "double" or "float64" => 8,
        _ => 0,
    };

    private static bool IsFloatType(string type) => type is "float" or "float32" or "double" or "float64";

    private static bool IsSignedType(string type) => type is "char" or "int8" or "short" or "int16" or "int" or "int32";

    private sealed class PlyElementBuilder(string name)
    {
        private readonly List<(string Name, int Size, bool IsFloat, bool IsSigned, bool IsList)> _properties = [];

        public void Add((string Name, int Size, bool IsFloat, bool IsSigned, bool IsList) property)
        {
            if (_properties.Count >= MaxProperties) throw FormatError.Corrupted("PLY element declares too many properties.");
            if (_properties.Any(p => p.Name.Equals(property.Name, StringComparison.Ordinal)))
                throw FormatError.Corrupted($"PLY property '{property.Name}' is declared twice.");
            _properties.Add(property);
        }

        public PlyElement ToElement(long count)
        {
            var properties = new PlyProperty[_properties.Count];
            int offset = 0;
            for (int i = 0; i < _properties.Count; i++)
            {
                var (propName, size, isFloat, isSigned, isList) = _properties[i];
                // List properties have no fixed stride; the reader rejects them where it cannot skip them.
                properties[i] = new PlyProperty(propName, size, isFloat, isSigned, isList, offset);
                if (!isList)
                {
                    offset += size;
                    if (offset < 0 || offset > MaxVertexStride)
                        throw FormatError.Corrupted("PLY properties declare more bytes than can be counted.");
                }
            }

            return new PlyElement(name, count, properties, offset);
        }
    }

    // One ASCII header line without its line break; the file position ends after the break.
    private static string ReadHeaderLine(FileStream fs)
    {
        var bytes = new List<byte>(128);
        while (true)
        {
            int b = fs.ReadByte();
            if (b < 0) throw FormatError.Truncated("PLY header");
            if (b == '\n') break;
            if (b != '\r')
            {
                if (b > 127) throw FormatError.Corrupted("PLY header is not ASCII.");
                bytes.Add((byte)b);
                if (bytes.Count > MaxLineLength) throw FormatError.Corrupted("PLY header line is too long.");
            }
        }

        return System.Text.Encoding.ASCII.GetString(bytes.ToArray());
    }
}

/// <summary>The single scan of a PLY file: its vertex element.</summary>
public sealed class PlyScan : PclScan
{
    // Binary records stream in chunks of at most this many bytes; memory use does not depend on the point count.
    private const int MaxChunkBytes = 64 * 1024;

    /// <inheritdoc />
    public override string Id { get; }

    /// <inheritdoc />
    public override string Name { get; }

    /// <summary>Number of vertices the header declares.</summary>
    public override long PointCount { get; }

    /// <summary>Path of the PLY file.</summary>
    public string FilePath { get; }

    /// <inheritdoc />
    public override bool HasColors { get; }

    /// <inheritdoc />
    public override bool HasIntensities { get; }

    /// <inheritdoc />
    public override bool HasNormals { get; }

    private readonly PlyDocument.PlyHeader _header;
    private readonly PlyDocument.PlyElement _vertex;
    private readonly long _vertexDataOffset;
    private readonly long _vertexLineSkip;
    private readonly int _x;
    private readonly int _y;
    private readonly int _z;
    private readonly int _red;
    private readonly int _green;
    private readonly int _blue;
    private readonly int _alpha;
    private readonly int _intensity;
    private readonly int _normalX;
    private readonly int _normalY;
    private readonly int _normalZ;

    internal PlyScan(string id, string name, long pointCount, string filePath,
        PlyDocument.PlyHeader header, PlyDocument.PlyElement vertex, long vertexDataOffset, long vertexLineSkip)
    {
        Id = id;
        Name = name;
        PointCount = pointCount;
        FilePath = filePath;
        _header = header;
        _vertex = vertex;
        _vertexDataOffset = vertexDataOffset;
        _vertexLineSkip = vertexLineSkip;
        _x = Find("x");
        _y = Find("y");
        _z = Find("z");
        _red = Find("red");
        _green = Find("green");
        _blue = Find("blue");
        _alpha = Find("alpha");
        _intensity = Find("intensity");
        _normalX = Find("nx");
        _normalY = Find("ny");
        _normalZ = Find("nz");

        var props = vertex.Properties;
        bool ByteChannel(int i) => i >= 0 && props[i].Size == 1 && !props[i].IsFloat;
        HasColors = _red >= 0 && _green >= 0 && _blue >= 0 && ByteChannel(_red) && ByteChannel(_green) && ByteChannel(_blue) &&
            (_alpha < 0 || ByteChannel(_alpha));
        HasIntensities = _intensity >= 0;
        HasNormals = _normalX >= 0 && _normalY >= 0 && _normalZ >= 0;
    }

    private int Find(string name)
    {
        for (int i = 0; i < _vertex.Properties.Length; i++)
            if (_vertex.Properties[i].Name.Equals(name, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>Streams the vertices of the file; faces and other elements are ignored.</summary>
    /// <remarks>
    /// Vertices whose coordinates are not finite are not returned.
    /// <see cref="PclReadOptions.Blocks"/> and <see cref="PclReadOptions.MaxDegreeOfParallelism"/> are ignored:
    /// the vertex stream has no independent blocks to select or decode in parallel.
    /// </remarks>
    /// <exception cref="InvalidDataException">The vertex data is truncated or malformed.</exception>
    public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null)
    {
        options ??= PclReadOptions.Default;
        long targetCount = options.MaxPoints ?? PointCount;
        if (targetCount <= 0) yield break;
        targetCount = Math.Min(targetCount, PointCount);
        int step = Math.Max(1, options.SubsampleStep);

        using var fs = File.OpenRead(FilePath);
        long yielded = 0;

        if (_header.Data == PlyDocument.PlyData.Ascii)
        {
            fs.Position = _header.DataStart;
            using var reader = new StreamReader(fs, System.Text.Encoding.ASCII, detectEncodingFromByteOrderMarks: false,
                bufferSize: 65536, leaveOpen: true);
            for (long s = 0; s < _vertexLineSkip; s++)
                if (reader.ReadLine() == null) throw FormatError.Truncated("vertex data");
            for (long i = 0; i < targetCount; i++)
            {
                string? line = reader.ReadLine();
                if (line == null) throw FormatError.Truncated("vertex data");
                if ((i % step) != 0) continue;

                var point = ParseAsciiVertex(line, out bool valid);
                if (!valid) continue;
                point = point with { Index = (ulong)yielded };
                if (options.ApplyTransform)
                    point = point with { Position = Transform.TransformPoint(point.Position) };

                yield return point;
                yielded++;
                if (yielded >= targetCount) yield break;
            }

            yield break;
        }

        bool littleEndian = _header.Data == PlyDocument.PlyData.LittleEndian;
        int stride = _vertex.Stride;
        int recordsPerChunk = Math.Max(1, MaxChunkBytes / Math.Max(1, stride));
        byte[] chunk = new byte[(long)recordsPerChunk * stride];
        var record = new byte[stride];

        for (long start = 0; start < targetCount; start += recordsPerChunk)
        {
            int want = (int)Math.Min(recordsPerChunk, targetCount - start);
            long byteOffset = _vertexDataOffset + (start * (long)stride);
            if (byteOffset < 0 || want > (fs.Length - byteOffset) / stride)
                throw FormatError.Truncated("vertex data");

            fs.Position = byteOffset;
            fs.ReadExactly(chunk, 0, want * stride);

            for (int i = 0; i < want; i++)
            {
                if ((start + i) % step != 0) continue;
                Array.Copy(chunk, i * stride, record, 0, stride);

                var point = ParseBinaryVertex(record, littleEndian, out bool valid);
                if (!valid) continue;
                point = point with { Index = (ulong)yielded };
                if (options.ApplyTransform)
                    point = point with { Position = Transform.TransformPoint(point.Position) };

                yield return point;
                yielded++;
                if (yielded >= targetCount) yield break;
            }
        }
    }

    // Parses one ASCII vertex line with exactly one value per property.
    private PclPoint ParseAsciiVertex(string line, out bool valid)
    {
        string[] tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != _vertex.Properties.Length)
            throw FormatError.Corrupted("PLY vertex line holds the wrong number of values.");

        double[] numbers = new double[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
            numbers[i] = ParseNumber(tokens[i], _vertex.Properties[i]);

        return AssemblePoint(numbers, out valid);
    }

    private static double ParseNumber(string token, PlyDocument.PlyProperty property)
    {
        if (property.IsFloat)
        {
            if (token.Equals("inf", StringComparison.OrdinalIgnoreCase)) return double.PositiveInfinity;
            if (token.Equals("+inf", StringComparison.OrdinalIgnoreCase)) return double.PositiveInfinity;
            if (token.Equals("-inf", StringComparison.OrdinalIgnoreCase)) return double.NegativeInfinity;
            if (!double.TryParse(token, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value))
                throw FormatError.Corrupted($"PLY value '{token}' is not a number.");
            return value;
        }

        if (!long.TryParse(token, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out long integer))
            throw FormatError.Corrupted($"PLY value '{token}' is not an integer.");

        (long min, long max) = IntegerRange(property);
        if (integer < min || integer > max) throw FormatError.Corrupted($"PLY value '{token}' does not fit its property.");
        return integer;
    }

    // The value range of an integer property from its width and signedness.
    private static (long Min, long Max) IntegerRange(PlyDocument.PlyProperty property) =>
        (property.Size, property.IsSigned) switch
        {
            (1, true) => (sbyte.MinValue, sbyte.MaxValue),
            (2, true) => (short.MinValue, short.MaxValue),
            (4, true) => (int.MinValue, int.MaxValue),
            (8, true) => (long.MinValue, long.MaxValue),
            (1, false) => (byte.MinValue, byte.MaxValue),
            (2, false) => (ushort.MinValue, ushort.MaxValue),
            (4, false) => (uint.MinValue, uint.MaxValue),
            _ => (long.MinValue, long.MaxValue),
        };

    // Reads one binary vertex: every property in declared order.
    private PclPoint ParseBinaryVertex(byte[] record, bool littleEndian, out bool valid)
    {
        double[] numbers = new double[_vertex.Properties.Length];
        for (int i = 0; i < _vertex.Properties.Length; i++)
        {
            var property = _vertex.Properties[i];
            numbers[i] = ReadValue(record.AsSpan(property.Offset, property.Size), property, littleEndian);
        }

        return AssemblePoint(numbers, out valid);
    }

    private static double ReadValue(ReadOnlySpan<byte> bytes, PlyDocument.PlyProperty property, bool littleEndian)
    {
        if (property.IsFloat)
        {
            if (property.Size == 4)
                return littleEndian ? Bytes.ReadSingleLE(bytes) : System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(bytes);
            return littleEndian ? Bytes.ReadDoubleLE(bytes) : System.Buffers.Binary.BinaryPrimitives.ReadDoubleBigEndian(bytes);
        }

        if (property.IsSigned)
        {
            return property.Size switch
            {
                1 => (sbyte)bytes[0],
                2 => littleEndian ? Bytes.ReadInt16LE(bytes) : System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(bytes),
                4 => littleEndian ? Bytes.ReadInt32LE(bytes) : System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes),
                _ => littleEndian ? Bytes.ReadInt64LE(bytes) : System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(bytes),
            };
        }

        return property.Size switch
        {
            1 => bytes[0],
            2 => littleEndian ? Bytes.ReadUInt16LE(bytes) : System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes),
            4 => littleEndian ? Bytes.ReadUInt32LE(bytes) : System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes),
            _ => (double)(littleEndian ? Bytes.ReadUInt64LE(bytes) : System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes)),
        };
    }

    // Builds a point from the flat values: x, y and z decide validity, the rest fills the channels.
    private PclPoint AssemblePoint(double[] numbers, out bool valid)
    {
        valid = true;
        double x = numbers[_x];
        double y = numbers[_y];
        double z = numbers[_z];
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
        {
            valid = false;
            return default;
        }

        PclColor? color = null;
        if (HasColors)
        {
            byte alpha = _alpha >= 0 ? ClampByte(numbers[_alpha]) : (byte)255;
            color = new PclColor(ClampByte(numbers[_red]), ClampByte(numbers[_green]), ClampByte(numbers[_blue]), alpha);
        }

        float? intensity = _intensity >= 0 ? (float)numbers[_intensity] : null;

        PclVector3? normal = null;
        if (HasNormals)
        {
            var n = new PclVector3(numbers[_normalX], numbers[_normalY], numbers[_normalZ]);
            if (double.IsFinite(n.X) && double.IsFinite(n.Y) && double.IsFinite(n.Z)) normal = n;
        }

        return new PclPoint(new PclVector3(x, y, z), Color: color, Intensity: intensity, Normal: normal);
    }

    private static byte ClampByte(double value)
    {
        if (double.IsNaN(value)) return 0;
        if (value <= 0) return 0;
        if (value >= 255) return 255;
        return (byte)Math.Round(value);
    }
}
