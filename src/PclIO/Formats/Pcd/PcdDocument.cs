using PclIO.Internal;

namespace PclIO.Formats.Pcd;

/// <summary>Represents a Point Cloud Library point cloud file (.pcd).</summary>
/// <remarks>
/// <para>
/// Supported: the 0.7 header (entries in the documented order, with or without a COUNT line), ASCII, binary and
/// binary compressed (LZF) point data, the declared fields, organised and unorganised clouds, and the viewpoint
/// (kept as metadata). Positions come from the x, y and z fields; colour from a packed rgb or rgba field;
/// intensity from an intensity field; normals from normal_x, normal_y and normal_z. Points whose coordinates are
/// not finite are not returned, like points the file marks as not-a-number.
/// </para>
/// <para>
/// Not read (reported with <see cref="NotSupportedException"/> when the file is opened): files without x, y and z
/// fields, and x, y or z fields of an uninterpretable size and type combination. Colour, intensity and normals of
/// an uninterpretable shape stay unread instead: the points come without that channel.
/// </para>
/// </remarks>
public sealed class PcdDocument : PclDocument
{
    // Bounds for data taken from an untrusted file.
    private const int MaxHeaderLines = 4096;
    internal const int MaxLineLength = 1 << 20;
    private const int MaxFields = 1024;
    private const int MaxFieldCount = 65536;
    private const long MaxDecompressedBytes = 1L << 30;

    /// <inheritdoc />
    public override PclFileKind Kind => PclFileKind.Pcd;

    /// <summary>Version string from the header.</summary>
    public string Version { get; }

    /// <summary>Viewpoint string from the header (translation plus quaternion).</summary>
    public string Viewpoint { get; }

    /// <inheritdoc />
    public override IReadOnlyList<PclScan> Scans => _scans;
    private readonly List<PclScan> _scans = [];

    private PcdDocument(string path, long fileSize, string version, string viewpoint)
        : base(path, fileSize)
    {
        Version = version;
        Viewpoint = viewpoint;
        _metadata["Format"] = "Point Cloud Library PCD";
        _metadata["Version"] = version;
        _metadata["Viewpoint"] = viewpoint;
    }

    /// <summary>Opens a PCD file and reads its header.</summary>
    /// <param name="path">Path of the .pcd file.</param>
    /// <param name="options">Currently unused; accepted for symmetry with the other readers.</param>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The header is damaged or truncated, or declares sizes outside the file.</exception>
    /// <exception cref="NotSupportedException">The file has no usable positions.</exception>
    public static PcdDocument Open(string path, PclReadOptions? options = null)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("PCD file not found.", path);

        using var fs = File.OpenRead(path);
        var header = ReadHeader(fs);

        // An organised cloud holds WIDTH * HEIGHT points; an unorganised one holds WIDTH points with HEIGHT 1.
        // Either way the product must be the declared point count.
        long gridPoints;
        try
        {
            checked
            {
                gridPoints = header.Width * header.Height;
            }
        }
        catch (OverflowException)
        {
            throw FormatError.Corrupted("PCD WIDTH and HEIGHT declare more points than can be counted.");
        }

        if (gridPoints != header.Points)
            throw FormatError.Corrupted("PCD WIDTH and HEIGHT do not hold the declared number of points.");

        int x = Find(header, "x");
        int y = Find(header, "y");
        int z = Find(header, "z");
        if (x < 0 || y < 0 || z < 0)
            throw FormatError.Unsupported("PCD file has no x, y and z fields.");

        foreach (int axis in new[] { x, y, z })
        {
            var field = header.Fields[axis];
            if (!IsInterpretable(field))
                throw FormatError.Unsupported($"PCD position field '{field.Name}' has size {field.Size} and type '{field.Type}'.");
        }

        var document = new PcdDocument(path, fs.Length, header.Version, header.Viewpoint);
        document._metadata["Data"] = header.Data switch
        {
            PcdData.Ascii => "ascii",
            PcdData.Binary => "binary",
            _ => "binary_compressed",
        };

        // The binary layouts state their sizes up front, so a short file is rejected here and not mid-read.
        if (header.Data == PcdData.Binary)
        {
            long need = CheckedPointBytes(header.Points, header.RecordSize);
            if (need > fs.Length - header.DataStart) throw FormatError.Truncated("point data");
        }
        else if (header.Data == PcdData.Compressed)
        {
            if (header.DataStart + 8 > fs.Length) throw FormatError.Truncated("compressed point data");
            fs.Position = header.DataStart;
            byte[] sizes = new byte[8];
            fs.ReadExactly(sizes);
            uint compressedSize = Bytes.ReadUInt32LE(sizes.AsSpan(0, 4));
            uint uncompressedSize = Bytes.ReadUInt32LE(sizes.AsSpan(4, 4));
            if (uncompressedSize != CheckedPointBytes(header.Points, header.RecordSize))
                throw FormatError.Corrupted("PCD compressed data does not hold the declared points.");
            if (uncompressedSize > MaxDecompressedBytes)
                throw FormatError.Corrupted("PCD compressed data is larger than the accepted size.");
            if (compressedSize > fs.Length - header.DataStart - 8) throw FormatError.Truncated("compressed point data");
        }

        string name = Path.GetFileNameWithoutExtension(path);
        document._scans.Add(new PcdScan("0", name, header.Points, path, header));
        return document;
    }

    private static int Find(PcdHeader header, string name)
    {
        for (int i = 0; i < header.Fields.Length; i++)
            if (header.Fields[i].Name.Equals(name, StringComparison.Ordinal)) return i;
        return -1;
    }

    // A field size and type pair the reader converts; anything else stays unread.
    internal static bool IsInterpretable(PcdField field) =>
        (field.Type, field.Size) is ('I', 1 or 2 or 4) or ('U', 1 or 2 or 4) or ('F', 4 or 8);

    // The product of the declared point count and the record size, checked: a crafted header can wrap it past
    // zero and slip a short file past the size check, so the overflow is rejected rather than read.
    internal static long CheckedPointBytes(long points, int recordSize)
    {
        try
        {
            checked { return points * recordSize; }
        }
        catch (OverflowException)
        {
            throw FormatError.Corrupted("PCD point count and record size declare more bytes than can be counted.");
        }
    }

    /// <summary>How the points are stored after the header.</summary>
    internal enum PcdData
    {
        Ascii,
        Binary,
        Compressed,
    }

    /// <summary>One declared field: its name, size, type, element count and byte offset in a record.</summary>
    internal sealed record PcdField(string Name, int Size, char Type, int Count, int Offset)
    {
        public int Bytes => Size * Count;
    }

    /// <summary>The parsed header: everything a read needs.</summary>
    internal sealed record PcdHeader(
        string Version, PcdField[] Fields, PcdData Data, long Width, long Height, long Points,
        string Viewpoint, long DataStart, int RecordSize);

    // Reads the ASCII header: the documented entries in order, with or without a COUNT line, skipping
    // comment and empty lines. Returns the header and the offset where the point data starts.
    private static PcdHeader ReadHeader(FileStream fs)
    {
        string? version = null;
        string[]? names = null;
        int[]? sizes = null;
        char[]? types = null;
        int[]? counts = null;
        long width = -1, height = -1, points = -1;
        string? viewpoint = null;
        PcdData? data = null;

        // The header entries are dispatched by keyword: VERSION, WIDTH, HEIGHT and VIEWPOINT may appear anywhere,
        // while FIELDS, SIZE, TYPE, COUNT and POINTS keep their relative order (COUNT optional) and DATA ends the
        // header. Each entry appears once.
        string[] ordered = ["FIELDS", "SIZE", "TYPE", "COUNT", "POINTS"];
        int lastOrdered = -1;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int lines = 0;

        while (data == null)
        {
            if (++lines > MaxHeaderLines) throw FormatError.Corrupted("PCD header has too many lines.");
            string line = ReadHeaderLine(fs);
            if (line.StartsWith('#') || line.Length == 0) continue;

            int space = line.IndexOfAny([' ', '\t']);
            if (space < 0) throw FormatError.Corrupted($"PCD header line '{line}' has no value.");
            string keyword = line[..space];
            string[] values = line[(space + 1)..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

            if (!seen.Add(keyword)) throw FormatError.Corrupted($"PCD header entry '{keyword}' appears twice.");
            int orderIndex = Array.IndexOf(ordered, keyword);
            if (orderIndex >= 0)
            {
                if (orderIndex <= lastOrdered) throw FormatError.Corrupted($"PCD header entry '{keyword}' is out of order.");
                lastOrdered = orderIndex;
            }

            switch (keyword)
            {
                case "VERSION":
                    if (values.Length != 1) throw FormatError.Corrupted("PCD VERSION needs one value.");
                    version = values[0];
                    break;
                case "FIELDS":
                    if (values.Length == 0 || values.Length > MaxFields)
                        throw FormatError.Corrupted("PCD FIELDS declares no fields or too many fields.");
                    // "_" is the padding placeholder and may repeat; the real field names must be unique.
                    var realNames = values.Where(v => !v.Equals("_", StringComparison.Ordinal)).ToArray();
                    if (realNames.Distinct(StringComparer.Ordinal).Count() != realNames.Length)
                        throw FormatError.Corrupted("PCD FIELDS names a field twice.");
                    names = values;
                    break;
                case "SIZE":
                    sizes = ParseCounts(values, "SIZE");
                    foreach (int size in sizes)
                        if (size is not (1 or 2 or 4 or 8)) throw FormatError.Corrupted($"PCD field size {size} is not 1, 2, 4 or 8.");
                    break;
                case "TYPE":
                    if (values.Any(v => v.Length != 1 || v[0] is not ('I' or 'U' or 'F')))
                        throw FormatError.Corrupted("PCD field type is not I, U or F.");
                    types = values.Select(v => v[0]).ToArray();
                    break;
                case "COUNT":
                    counts = ParseCounts(values, "COUNT");
                    foreach (int count in counts)
                        if (count < 1 || count > MaxFieldCount) throw FormatError.Corrupted("PCD field count is out of range.");
                    break;
                case "WIDTH":
                    width = ParseCount(values, "WIDTH");
                    break;
                case "HEIGHT":
                    height = ParseCount(values, "HEIGHT");
                    break;
                case "VIEWPOINT":
                    if (values.Length != 7) throw FormatError.Corrupted("PCD VIEWPOINT needs seven values.");
                    foreach (string value in values)
                        if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double d) || !double.IsFinite(d))
                            throw FormatError.Corrupted("PCD VIEWPOINT is not usable.");
                    viewpoint = string.Join(' ', values);
                    break;
                case "POINTS":
                    points = ParseCount(values, "POINTS");
                    break;
                case "DATA":
                    if (values.Length != 1) throw FormatError.Corrupted("PCD DATA needs one value.");
                    data = values[0] switch
                    {
                        "ascii" => PcdData.Ascii,
                        "binary" => PcdData.Binary,
                        "binary_compressed" => PcdData.Compressed,
                        _ => throw FormatError.Corrupted($"PCD data type '{values[0]}' is not ascii, binary or binary_compressed."),
                    };
                    break;
            }
        }

        if (version == null || names == null || sizes == null || types == null || viewpoint == null ||
            width < 0 || height < 0 || points < 0)
            throw FormatError.Corrupted("PCD header misses an entry.");
        if (names.Length != sizes.Length || names.Length != types.Length)
            throw FormatError.Corrupted("PCD FIELDS, SIZE and TYPE declare different field counts.");
        counts ??= Enumerable.Repeat(1, names.Length).ToArray();
        if (counts.Length != names.Length)
            throw FormatError.Corrupted("PCD COUNT declares a different field count.");

        var fields = new PcdField[names.Length];
        int offset = 0;
        for (int i = 0; i < names.Length; i++)
        {
            fields[i] = new PcdField(names[i], sizes[i], types[i], counts[i], offset);
            offset += sizes[i] * counts[i];
            if (offset < 0) throw FormatError.Corrupted("PCD fields declare more bytes than can be counted.");
        }

        return new PcdHeader(version, fields, data.Value, width, height, points, viewpoint, fs.Position, offset);
    }

    private static int[] ParseCounts(string[] values, string what)
    {
        if (values.Length == 0 || values.Length > MaxFields)
            throw FormatError.Corrupted($"PCD {what} declares no fields or too many fields.");
        var result = new int[values.Length];
        for (int i = 0; i < values.Length; i++)
            if (!int.TryParse(values[i], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out result[i]))
                throw FormatError.Corrupted($"PCD {what} value '{values[i]}' is not a count.");
        return result;
    }

    private static long ParseCount(string[] values, string what)
    {
        if (values.Length != 1 ||
            !long.TryParse(values[0], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out long result) || result < 0)
            throw FormatError.Corrupted($"PCD {what} is not a count.");
        return result;
    }

    // One ASCII header line without its line break; the file position ends after the break.
    private static string ReadHeaderLine(FileStream fs)
    {
        var bytes = new List<byte>(128);
        while (true)
        {
            int b = fs.ReadByte();
            if (b < 0) throw FormatError.Truncated("PCD header");
            if (b == '\n') break;
            if (b != '\r')
            {
                if (b > 127) throw FormatError.Corrupted("PCD header is not ASCII.");
                bytes.Add((byte)b);
                if (bytes.Count > MaxLineLength) throw FormatError.Corrupted("PCD header line is too long.");
            }
        }

        return System.Text.Encoding.ASCII.GetString(bytes.ToArray());
    }
}

/// <summary>The single scan of a PCD file.</summary>
public sealed class PcdScan : PclScan
{
    // Binary records stream in chunks of at most this many bytes; memory use does not depend on the point count.
    private const int MaxChunkBytes = 64 * 1024;

    /// <inheritdoc />
    public override string Id { get; }

    /// <inheritdoc />
    public override string Name { get; }

    /// <summary>Number of points the header declares (WIDTH times HEIGHT).</summary>
    public override long PointCount { get; }

    /// <summary>Path of the PCD file.</summary>
    public string FilePath { get; }

    /// <inheritdoc />
    public override bool HasColors { get; }

    /// <inheritdoc />
    public override bool HasIntensities { get; }

    /// <inheritdoc />
    public override bool HasNormals { get; }

    private readonly PcdDocument.PcdHeader _header;
    private readonly int _x;
    private readonly int _y;
    private readonly int _z;
    private readonly int _rgb;
    private readonly int _intensity;
    private readonly int _normalX;
    private readonly int _normalY;
    private readonly int _normalZ;
    private readonly int[] _starts;

    internal PcdScan(string id, string name, long pointCount, string filePath, PcdDocument.PcdHeader header)
    {
        Id = id;
        Name = name;
        PointCount = pointCount;
        FilePath = filePath;
        _header = header;
        _x = Find("x");
        _y = Find("y");
        _z = Find("z");
        _rgb = FindColor();
        _intensity = InterpretableIndex("intensity");
        _normalX = InterpretableIndex("normal_x");
        _normalY = InterpretableIndex("normal_y");
        _normalZ = InterpretableIndex("normal_z");
        HasColors = _rgb >= 0;
        HasIntensities = _intensity >= 0;
        HasNormals = _normalX >= 0 && _normalY >= 0 && _normalZ >= 0;

        _starts = new int[header.Fields.Length];
        int total = 0;
        for (int i = 0; i < _starts.Length; i++)
        {
            _starts[i] = total;
            total += header.Fields[i].Count;
        }
    }

    private int Find(string name)
    {
        for (int i = 0; i < _header.Fields.Length; i++)
            if (_header.Fields[i].Name.Equals(name, StringComparison.Ordinal)) return i;
        return -1;
    }

    // A channel is read only when its field has a shape the reader converts; any other shape stays unread, so the
    // points come without that channel rather than failing or misreading it.
    private int InterpretableIndex(string name)
    {
        int i = Find(name);
        return i >= 0 && PcdDocument.IsInterpretable(_header.Fields[i]) ? i : -1;
    }

    // A packed rgb or rgba field holds its colour in a single four-byte float or unsigned integer; anything else
    // stays unread.
    private int FindColor()
    {
        foreach (string name in new[] { "rgb", "rgba" })
        {
            int i = Find(name);
            if (i < 0) continue;
            var field = _header.Fields[i];
            if (field.Count == 1 && ((field.Type == 'F' && field.Size == 4) || (field.Type == 'U' && field.Size == 4)))
                return i;
        }

        return -1;
    }

    /// <summary>Streams the points of the file.</summary>
    /// <remarks>
    /// Points whose coordinates are not finite are not returned. Organised clouds stream row after row.
    /// <see cref="PclReadOptions.Blocks"/> and <see cref="PclReadOptions.MaxDegreeOfParallelism"/> are ignored:
    /// the point stream has no independent blocks to select or decode in parallel.
    /// </remarks>
    /// <exception cref="InvalidDataException">The point data is truncated or malformed.</exception>
    public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null)
    {
        options ??= PclReadOptions.Default;
        long targetCount = options.MaxPoints ?? PointCount;
        if (targetCount <= 0) yield break;
        targetCount = Math.Min(targetCount, PointCount);
        int step = Math.Max(1, options.SubsampleStep);

        using var fs = File.OpenRead(FilePath);
        long yielded = 0;

        if (_header.Data == PcdDocument.PcdData.Ascii)
        {
            fs.Position = _header.DataStart;
            using var reader = new StreamReader(fs, System.Text.Encoding.ASCII, detectEncodingFromByteOrderMarks: false,
                bufferSize: 65536, leaveOpen: true);
            for (long i = 0; i < targetCount; i++)
            {
                string? line = ReadDataLine(reader);
                if (line == null) throw FormatError.Truncated("point data");
                if ((i % step) != 0) continue;

                var point = ParseAsciiPoint(line, out bool valid);
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

        if (_header.Data == PcdDocument.PcdData.Binary)
        {
            foreach (var pt in ReadBinaryStreaming(fs, targetCount, step, options.ApplyTransform)) yield return pt;
            yield break;
        }

        byte[] records = ReadDecompressedRecords(fs);
        int recordSize = _header.RecordSize;
        for (long i = 0; i < targetCount; i++)
        {
            if ((i % step) != 0) continue;
            var point = ParseBinaryPoint(records.AsSpan((int)(i * recordSize), recordSize), out bool valid);
            if (!valid) continue;
            point = point with { Index = (ulong)yielded };
            if (options.ApplyTransform)
                point = point with { Position = Transform.TransformPoint(point.Position) };

            yield return point;
            yielded++;
            if (yielded >= targetCount) yield break;
        }
    }

    // Streams the binary point data in bounded chunks, carrying a record that straddles a chunk boundary, so
    // memory use does not depend on the point count. The open path checked that the declared records fit the file.
    private IEnumerable<PclPoint> ReadBinaryStreaming(FileStream fs, long targetCount, int step, bool applyTransform)
    {
        int recordSize = _header.RecordSize;
        fs.Position = _header.DataStart;
        byte[] chunk = new byte[MaxChunkBytes];
        byte[] carry = new byte[recordSize];
        int carryLen = 0;
        long parsed = 0;
        long yielded = 0;

        while (parsed < targetCount)
        {
            int read = fs.Read(chunk, 0, chunk.Length);
            if (read == 0) throw FormatError.Truncated("point data");
            int pos = 0;

            // Complete a record carried over from the previous chunk.
            if (carryLen > 0)
            {
                int take = Math.Min(recordSize - carryLen, read);
                chunk.AsSpan(0, take).CopyTo(carry.AsSpan(carryLen));
                carryLen += take;
                pos = take;
                if (carryLen == recordSize)
                {
                    carryLen = 0;
                    if (parsed % step == 0)
                    {
                        var point = ParseBinaryPoint(carry, out bool valid);
                        if (valid)
                        {
                            point = point with { Index = (ulong)yielded };
                            if (applyTransform) point = point with { Position = Transform.TransformPoint(point.Position) };
                            yield return point;
                            yielded++;
                            if (yielded >= targetCount) yield break;
                        }
                    }
                    parsed++;
                }
            }

            // Parse the whole records of this chunk.
            while (pos + recordSize <= read && parsed < targetCount)
            {
                if (parsed % step == 0)
                {
                    var point = ParseBinaryPoint(chunk.AsSpan(pos, recordSize), out bool valid);
                    if (valid)
                    {
                        point = point with { Index = (ulong)yielded };
                        if (applyTransform) point = point with { Position = Transform.TransformPoint(point.Position) };
                        yield return point;
                        yielded++;
                        if (yielded >= targetCount) yield break;
                    }
                }
                pos += recordSize;
                parsed++;
            }

            // Carry the partial record into the next chunk.
            if (pos < read && parsed < targetCount)
            {
                chunk.AsSpan(pos, read - pos).CopyTo(carry);
                carryLen = read - pos;
            }
        }
    }

    // Decompresses the whole LZF payload: its structure-of-arrays layout needs every field to read any point.
    private byte[] ReadDecompressedRecords(FileStream fs)
    {
        fs.Position = _header.DataStart;
        byte[] sizes = new byte[8];
        fs.ReadExactly(sizes);
        uint compressedSize = Bytes.ReadUInt32LE(sizes.AsSpan(0, 4));
        uint uncompressedSize = Bytes.ReadUInt32LE(sizes.AsSpan(4, 4));

        long expect = PcdDocument.CheckedPointBytes(PointCount, _header.RecordSize);
        if (uncompressedSize != expect)
            throw FormatError.Corrupted("PCD compressed data does not hold the declared points.");
        if (compressedSize > fs.Length - _header.DataStart - 8) throw FormatError.Truncated("compressed point data");

        byte[] compressed = new byte[compressedSize];
        fs.ReadExactly(compressed);
        byte[] flat = LzfDecoder.Decompress(compressed, (int)uncompressedSize);

        // The payload holds one block per field with every point: gather point after point. A field with
        // several elements keeps them together per point.
        byte[] records = new byte[uncompressedSize];
        int recordSize = _header.RecordSize;
        long fieldBase = 0;
        foreach (var field in _header.Fields)
        {
            for (long i = 0; i < PointCount; i++)
                flat.AsSpan((int)(fieldBase + (i * field.Bytes)), field.Bytes)
                    .CopyTo(records.AsSpan((int)(i * recordSize) + field.Offset, field.Bytes));
            fieldBase += PointCount * (long)field.Bytes;
        }

        return records;
    }

    // Reads one data line with a bounded length, so a crafted multi-gigabyte line fails instead of exhausting
    // memory. Returns null at the end of the stream.
    private static string? ReadDataLine(StreamReader reader)
    {
        var sb = new System.Text.StringBuilder();
        int c;
        while ((c = reader.Read()) >= 0)
        {
            if (c == '\n') break;
            if (c == '\r')
            {
                if (reader.Peek() == '\n') reader.Read();
                break;
            }
            sb.Append((char)c);
            if (sb.Length > PcdDocument.MaxLineLength) throw FormatError.Corrupted("PCD point line is too long.");
        }
        return c < 0 && sb.Length == 0 ? null : sb.ToString();
    }

    // Parses one ASCII line with exactly one value per field element.
    private PclPoint ParseAsciiPoint(string line, out bool valid)
    {
        string[] tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        double[] numbers = new double[tokens.Length];
        int t = 0;
        foreach (var field in _header.Fields)
        {
            for (int c = 0; c < field.Count; c++)
            {
                if (t >= tokens.Length) throw FormatError.Corrupted("PCD point line holds the wrong number of values.");
                numbers[t] = ParseNumber(tokens[t], field);
                t++;
            }
        }

        if (t != tokens.Length) throw FormatError.Corrupted("PCD point line holds the wrong number of values.");
        return AssemblePoint(numbers, out valid);
    }

    private static double ParseNumber(string token, PcdDocument.PcdField field)
    {
        if (field.Type == 'F')
        {
            if (token.Equals("inf", StringComparison.OrdinalIgnoreCase)) return double.PositiveInfinity;
            if (token.Equals("+inf", StringComparison.OrdinalIgnoreCase)) return double.PositiveInfinity;
            if (token.Equals("-inf", StringComparison.OrdinalIgnoreCase)) return double.NegativeInfinity;
            if (!double.TryParse(token, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value))
                throw FormatError.Corrupted($"PCD value '{token}' is not a number.");
            if (field.Size == 4 && double.IsFinite(value) &&
                (value > float.MaxValue || value < float.MinValue))
                throw FormatError.Corrupted($"PCD value '{token}' does not fit a float.");
            return value;
        }

        if (field.Type == 'I')
        {
            if (!long.TryParse(token, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out long value))
                throw FormatError.Corrupted($"PCD value '{token}' is not an integer.");
            long min = field.Size == 1 ? sbyte.MinValue : field.Size == 2 ? short.MinValue : field.Size == 4 ? int.MinValue : long.MinValue;
            long max = field.Size == 1 ? sbyte.MaxValue : field.Size == 2 ? short.MaxValue : field.Size == 4 ? int.MaxValue : long.MaxValue;
            if (value < min || value > max) throw FormatError.Corrupted($"PCD value '{token}' does not fit its field.");
            return value;
        }

        if (!ulong.TryParse(token, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out ulong unsigned))
            throw FormatError.Corrupted($"PCD value '{token}' is not an integer.");
        ulong umax = field.Size == 1 ? byte.MaxValue : field.Size == 2 ? ushort.MaxValue : field.Size == 4 ? uint.MaxValue : ulong.MaxValue;
        if (unsigned > umax) throw FormatError.Corrupted($"PCD value '{token}' does not fit its field.");
        return unsigned;
    }

    // Reads one binary record: the fields in declared order, COUNT elements each.
    private PclPoint ParseBinaryPoint(ReadOnlySpan<byte> record, out bool valid)
    {
        double[] numbers = new double[_starts[^1] + _header.Fields[^1].Count];
        for (int f = 0; f < _header.Fields.Length; f++)
        {
            var field = _header.Fields[f];
            var bytes = record.Slice(field.Offset, field.Bytes);
            for (int c = 0; c < field.Count; c++)
                numbers[_starts[f] + c] = ReadValue(bytes.Slice(c * field.Size, field.Size), field);
        }

        return AssemblePoint(numbers, out valid);
    }

    private static double ReadValue(ReadOnlySpan<byte> bytes, PcdDocument.PcdField field) =>
        (field.Type, field.Size) switch
        {
            ('I', 1) => (sbyte)bytes[0],
            ('I', 2) => Bytes.ReadInt16LE(bytes),
            ('I', 4) => Bytes.ReadInt32LE(bytes),
            ('I', 8) => Bytes.ReadInt64LE(bytes),
            ('U', 1) => bytes[0],
            ('U', 2) => Bytes.ReadUInt16LE(bytes),
            ('U', 4) => Bytes.ReadUInt32LE(bytes),
            ('U', 8) => Bytes.ReadUInt64LE(bytes),
            ('F', 4) => Bytes.ReadSingleLE(bytes),
            ('F', 8) => Bytes.ReadDoubleLE(bytes),
            // A one- or two-byte float has no representation; such a field is gated out of every channel and its
            // value is never used, so it reads as zero rather than running past the span.
            _ => 0,
        };

    // Builds a point from the flat values: x, y and z decide validity, the rest fills the channels.
    private PclPoint AssemblePoint(double[] numbers, out bool valid)
    {
        valid = true;
        double x = numbers[_starts[_x]];
        double y = numbers[_starts[_y]];
        double z = numbers[_starts[_z]];
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
        {
            valid = false;
            return default;
        }

        PclColor? color = null;
        if (_rgb >= 0)
        {
            uint bits = ColorBits(numbers[_starts[_rgb]], _header.Fields[_rgb]);
            color = new PclColor((byte)((bits >> 16) & 0xFF), (byte)((bits >> 8) & 0xFF), (byte)(bits & 0xFF));
        }

        float? intensity = _intensity >= 0 ? (float)numbers[_starts[_intensity]] : null;

        PclVector3? normal = null;
        if (HasNormals)
        {
            var n = new PclVector3(numbers[_starts[_normalX]], numbers[_starts[_normalY]], numbers[_starts[_normalZ]]);
            if (double.IsFinite(n.X) && double.IsFinite(n.Y) && double.IsFinite(n.Z)) normal = n;
        }

        return new PclPoint(new PclVector3(x, y, z), Color: color, Intensity: intensity, Normal: normal);
    }

    // The first four bytes of a colour field as an unsigned integer: the float bits for a float field,
    // the stored value otherwise.
    private static uint ColorBits(double value, PcdDocument.PcdField field)
    {
        if (field.Type == 'F' && field.Size == 4) return BitConverter.SingleToUInt32Bits((float)value);
        if (value < 0 || value > uint.MaxValue) return 0;
        return (uint)(ulong)value;
    }
}
