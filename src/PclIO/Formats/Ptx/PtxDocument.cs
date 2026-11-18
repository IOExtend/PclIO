using System.Globalization;
using PclIO.Internal;

namespace PclIO.Formats.Ptx;

/// <summary>Represents a Leica Cyclone gridded point cloud exchange file (.ptx).</summary>
/// <remarks>
/// <para>
/// Supported: ASCII text point cloud containing one or more gridded scans. Each scan begins with a 10-line header:
/// columns (width), rows (height), scanner position (X, Y, Z), scanner orientation axes (X, Y, Z unit vectors),
/// and a 4x4 transformation matrix. Point records are whitespace-separated numbers: 3 columns (XYZ),
/// 4 columns (XYZ and intensity), 6 columns (XYZ and RGB colour), or 7 columns (XYZ, intensity and RGB colour).
/// </para>
/// <para>
/// Points with coordinates (0, 0, 0) represent empty grid cells (no laser return) and are skipped. Points whose
/// coordinates are not finite are also skipped.
/// </para>
/// <para>
/// Not read (reported with <see cref="InvalidDataException"/>): files with malformed or negative dimensions,
/// lines with unsupported column counts, non-numeric values, or files that end before the declared grid count is satisfied.
/// </para>
/// </remarks>
public sealed class PtxDocument : PclDocument
{
    private const int MaxLineLength = 1 << 20;

    /// <inheritdoc />
    public override PclFileKind Kind => PclFileKind.Ptx;

    /// <inheritdoc />
    public override IReadOnlyList<PclScan> Scans => _scans;
    private readonly List<PclScan> _scans = [];

    private PtxDocument(string path, long fileSize)
        : base(path, fileSize)
    {
    }

    /// <summary>Opens a PTX point cloud file and inspects its structure and scans.</summary>
    /// <param name="path">Path of the .ptx file.</param>
    /// <param name="options">Currently unused; accepted for symmetry with the other readers.</param>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is malformed, truncated or declares an impossible grid count.</exception>
    /// <exception cref="NotSupportedException">The point layout is not supported.</exception>
    public static PtxDocument Open(string path, PclReadOptions? options = null)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("PTX file not found.", path);

        var doc = new PtxDocument(path, fi.Length);
        using var fs = File.OpenRead(path);
        IndexScans(fs, fi.Length, doc);

        doc._metadata["Format"] = "Leica Cyclone PTX";
        doc._metadata["Scans"] = doc._scans.Count.ToString(CultureInfo.InvariantCulture);
        doc._metadata["TotalPoints"] = doc.TotalPointCount.ToString(CultureInfo.InvariantCulture);

        return doc;
    }

    private static void IndexScans(FileStream fs, long fileLength, PtxDocument doc)
    {
        string baseName = Path.GetFileNameWithoutExtension(doc.FilePath);
        int scanIndex = 0;
        Span<Range> ranges = stackalloc Range[16];

        while (true)
        {
            // Skip leading whitespace / blank lines before next scan header
            string? firstLine;
            while ((firstLine = ReadLine(fs)) != null && string.IsNullOrWhiteSpace(firstLine)) { }

            if (firstLine == null)
            {
                if (scanIndex == 0)
                    throw FormatError.Truncated("PTX header");
                break; // Clean EOF
            }

            var headerLines = new string[10];
            headerLines[0] = firstLine.Trim();

            for (int i = 1; i < 10; i++)
            {
                string? hLine;
                while ((hLine = ReadLine(fs)) != null && string.IsNullOrWhiteSpace(hLine)) { }
                if (hLine == null)
                    throw FormatError.Truncated("PTX scan header");
                headerLines[i] = hLine.Trim();
            }

            // Parse grid dimensions
            if (!int.TryParse(headerLines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int columns) ||
                !int.TryParse(headerLines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rows))
            {
                throw FormatError.Corrupted("PTX grid dimensions must be integers.");
            }

            if (columns < 0 || rows < 0)
                throw FormatError.Corrupted("PTX grid dimensions cannot be negative.");

            long totalGrid = checked((long)columns * rows);
            if (totalGrid > fileLength / 5)
                throw FormatError.Corrupted("Declared PTX grid count exceeds the physical capacity of the file.");

            // Parse scanner position and axes
            PclVector3 scannerPosition = ParseVector3(headerLines[2], "scanner position");
            PclVector3 scannerXAxis = ParseVector3(headerLines[3], "scanner X axis");
            PclVector3 scannerYAxis = ParseVector3(headerLines[4], "scanner Y axis");
            PclVector3 scannerZAxis = ParseVector3(headerLines[5], "scanner Z axis");

            // Parse 4x4 transformation matrix
            ParseMatrixRow(headerLines[6], out double r11, out double r12, out double r13, out double r14);
            ParseMatrixRow(headerLines[7], out double r21, out double r22, out double r23, out double r24);
            ParseMatrixRow(headerLines[8], out double r31, out double r32, out double r33, out double r34);
            ParseMatrixRow(headerLines[9], out double r41, out double r42, out double r43, out double r44);

            double[] matrix =
            [
                r11, r21, r31, r41,
                r12, r22, r32, r42,
                r13, r23, r33, r43,
                r14, r24, r34, r44
            ];

            PclTransform transform = IsIdentityMatrix(matrix)
                ? PclTransform.Identity
                : new PclTransform
                {
                    Translation = new PclVector3(r41, r42, r43),
                    Matrix4x4 = matrix
                };

            long pointDataOffset = fs.Position;
            int pointColumns = 7;
            bool hasColors = false;
            bool hasIntensities = false;

            if (totalGrid > 0)
            {
                // Inspect the first point line to determine column layout
                string? firstPtLine;
                while ((firstPtLine = ReadLine(fs)) != null && string.IsNullOrWhiteSpace(firstPtLine)) { }
                if (firstPtLine == null)
                    throw FormatError.Truncated("PTX points");

                pointColumns = SplitTokens(firstPtLine.AsSpan(), ranges);
                if (pointColumns is not (3 or 4 or 6 or 7))
                    throw FormatError.Unsupported($"PTX column count {pointColumns} is not supported; expected 3, 4, 6 or 7 columns.");

                DetermineLayout(firstPtLine.AsSpan(), ranges, pointColumns,
                    out hasColors, out hasIntensities);

                // Skip the remaining (totalGrid - 1) lines to position at the next scan header
                bool complete = TrySkipLines(fs, totalGrid - 1);
                string scanId = scanIndex.ToString(CultureInfo.InvariantCulture);
                string scanName = $"{baseName}_{scanIndex}";

                var scan = new PtxScan(scanId, scanName, columns, rows, scannerPosition,
                    scannerXAxis, scannerYAxis, scannerZAxis, transform, pointDataOffset,
                    pointColumns, hasColors, hasIntensities, doc.FilePath);
                doc._scans.Add(scan);
                scanIndex++;

                if (!complete)
                {
                    // Stream reached EOF while skipping point lines
                    break;
                }
            }
            else
            {
                string scanId = scanIndex.ToString(CultureInfo.InvariantCulture);
                string scanName = $"{baseName}_{scanIndex}";

                var scan = new PtxScan(scanId, scanName, columns, rows, scannerPosition,
                    scannerXAxis, scannerYAxis, scannerZAxis, transform, pointDataOffset,
                    pointColumns, hasColors, hasIntensities, doc.FilePath);
                doc._scans.Add(scan);
                scanIndex++;
            }
        }

        // If only 1 scan was found, simplify its name to the file name without extension
        if (doc._scans.Count == 1)
        {
            var single = (PtxScan)doc._scans[0];
            single.SetName(baseName);
        }
    }

    private static PclVector3 ParseVector3(string line, string name)
    {
        Span<Range> ranges = stackalloc Range[8];
        int count = SplitTokens(line.AsSpan(), ranges);
        if (count != 3)
            throw FormatError.Corrupted($"Invalid {name} in PTX header: expected 3 values, found {count}.");

        ReadOnlySpan<char> span = line.AsSpan();
        if (!double.TryParse(span[ranges[0]], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ||
            !double.TryParse(span[ranges[1]], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) ||
            !double.TryParse(span[ranges[2]], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
        {
            throw FormatError.Corrupted($"Invalid numeric values for {name} in PTX header.");
        }

        return new PclVector3(x, y, z);
    }

    private static void ParseMatrixRow(string line, out double c1, out double c2, out double c3, out double c4)
    {
        Span<Range> ranges = stackalloc Range[8];
        int count = SplitTokens(line.AsSpan(), ranges);
        if (count != 4)
            throw FormatError.Corrupted($"Invalid matrix row in PTX header: expected 4 values, found {count}.");

        ReadOnlySpan<char> span = line.AsSpan();
        if (!double.TryParse(span[ranges[0]], NumberStyles.Float, CultureInfo.InvariantCulture, out c1) ||
            !double.TryParse(span[ranges[1]], NumberStyles.Float, CultureInfo.InvariantCulture, out c2) ||
            !double.TryParse(span[ranges[2]], NumberStyles.Float, CultureInfo.InvariantCulture, out c3) ||
            !double.TryParse(span[ranges[3]], NumberStyles.Float, CultureInfo.InvariantCulture, out c4))
        {
            throw FormatError.Corrupted("Invalid numeric values for matrix row in PTX header.");
        }
    }

    private static bool IsIdentityMatrix(double[] m)
    {
        return m[0] == 1.0 && m[1] == 0.0 && m[2] == 0.0 && m[3] == 0.0 &&
               m[4] == 0.0 && m[5] == 1.0 && m[6] == 0.0 && m[7] == 0.0 &&
               m[8] == 0.0 && m[9] == 0.0 && m[10] == 1.0 && m[11] == 0.0 &&
               m[12] == 0.0 && m[13] == 0.0 && m[14] == 0.0 && m[15] == 1.0;
    }

    private static void DetermineLayout(ReadOnlySpan<char> span, ReadOnlySpan<Range> ranges, int columns,
        out bool hasColors, out bool hasIntensities)
    {
        hasColors = false;
        hasIntensities = false;

        // Verify X, Y, Z
        if (!double.TryParse(span[ranges[0]], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
            !double.TryParse(span[ranges[1]], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
            !double.TryParse(span[ranges[2]], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            throw FormatError.Corrupted("Invalid coordinate values on PTX point line.");
        }

        switch (columns)
        {
            case 3:
                // X Y Z
                break;
            case 4:
                // X Y Z Intensity
                if (!float.TryParse(span[ranges[3]], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    throw FormatError.Corrupted("Invalid intensity value on PTX point line.");
                hasIntensities = true;
                break;
            case 6:
                // X Y Z R G B
                if (!double.TryParse(span[ranges[3]], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
                    !double.TryParse(span[ranges[4]], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
                    !double.TryParse(span[ranges[5]], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    throw FormatError.Corrupted("Invalid color values on PTX point line.");
                }
                hasColors = true;
                break;
            case 7:
                // X Y Z Intensity R G B
                if (!float.TryParse(span[ranges[3]], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    throw FormatError.Corrupted("Invalid intensity value on PTX point line.");
                if (!double.TryParse(span[ranges[4]], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
                    !double.TryParse(span[ranges[5]], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
                    !double.TryParse(span[ranges[6]], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    throw FormatError.Corrupted("Invalid color values on PTX point line.");
                }
                hasIntensities = true;
                hasColors = true;
                break;
        }
    }

    internal static string? ReadLine(FileStream fs)
    {
        var bytes = new List<byte>(64);
        while (true)
        {
            int b = fs.ReadByte();
            if (b < 0)
            {
                if (bytes.Count == 0) return null;
                break;
            }
            if (b == '\n') break;
            if (b != '\r')
            {
                bytes.Add((byte)b);
                if (bytes.Count > MaxLineLength)
                    throw FormatError.Corrupted("PTX line exceeds maximum length.");
            }
        }
        return System.Text.Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static bool TrySkipLines(FileStream fs, long linesToSkip)
    {
        if (linesToSkip <= 0) return true;

        byte[] buffer = new byte[65536];
        long skipped = 0;

        while (skipped < linesToSkip)
        {
            long chunkStart = fs.Position;
            int read = fs.Read(buffer, 0, buffer.Length);
            if (read == 0) return false;

            ReadOnlySpan<byte> span = buffer.AsSpan(0, read);
            int pos = 0;
            while (pos < read && skipped < linesToSkip)
            {
                int nl = span[pos..].IndexOf((byte)'\n');
                if (nl < 0) break;

                skipped++;
                pos += nl + 1;

                if (skipped == linesToSkip)
                {
                    fs.Position = chunkStart + pos;
                    return true;
                }
            }
        }

        return true;
    }

    internal static int SplitTokens(ReadOnlySpan<char> line, Span<Range> ranges)
    {
        int count = 0;
        int length = line.Length;
        int i = 0;
        while (i < length)
        {
            while (i < length && (line[i] == ' ' || line[i] == '\t')) i++;
            if (i >= length) break;
            int start = i;
            while (i < length && line[i] != ' ' && line[i] != '\t') i++;
            if (count < ranges.Length)
            {
                ranges[count] = start..i;
            }
            count++;
        }
        return count;
    }

    internal static byte ClampByte(double value)
    {
        if (double.IsNaN(value) || value <= 0) return 0;
        if (value >= 255) return 255;
        return (byte)Math.Round(value);
    }
}

/// <summary>Represents a point scan inside a Leica PTX file.</summary>
public sealed class PtxScan : PclScan
{
    private const int MaxLineLength = 1 << 20;

    private readonly string _filePath;

    /// <inheritdoc />
    public override string Id { get; }

    /// <inheritdoc />
    public override string Name => _name;
    private string _name;

    /// <summary>Number of columns in the scan grid.</summary>
    public int Columns { get; }

    /// <summary>Number of rows in the scan grid.</summary>
    public int Rows { get; }

    /// <summary>Total number of cells in the scan grid (Columns * Rows).</summary>
    public long GridSize { get; }

    /// <inheritdoc />
    public override long PointCount => GridSize;

    /// <summary>Scanner position in coordinates of this scan.</summary>
    public PclVector3 ScannerPosition { get; }

    /// <summary>Scanner X axis unit vector.</summary>
    public PclVector3 ScannerXAxis { get; }

    /// <summary>Scanner Y axis unit vector.</summary>
    public PclVector3 ScannerYAxis { get; }

    /// <summary>Scanner Z axis unit vector.</summary>
    public PclVector3 ScannerZAxis { get; }

    /// <summary>Byte offset where the point records of this scan begin.</summary>
    public long PointDataOffset { get; }

    /// <summary>Number of columns per point record in this scan (3, 4, 6 or 7).</summary>
    public int ColumnsPerPoint { get; }

    /// <inheritdoc />
    public override bool HasColors { get; }

    /// <inheritdoc />
    public override bool HasIntensities { get; }

    /// <inheritdoc />
    public override bool HasNormals => false;

    internal PtxScan(string id, string name, int columns, int rows,
        PclVector3 scannerPosition, PclVector3 scannerXAxis, PclVector3 scannerYAxis, PclVector3 scannerZAxis,
        PclTransform transform, long pointDataOffset, int columnsPerPoint,
        bool hasColors, bool hasIntensities, string filePath)
    {
        Id = id;
        _name = name;
        Columns = columns;
        Rows = rows;
        GridSize = checked((long)columns * rows);
        ScannerPosition = scannerPosition;
        ScannerXAxis = scannerXAxis;
        ScannerYAxis = scannerYAxis;
        ScannerZAxis = scannerZAxis;
        Transform = transform;
        PointDataOffset = pointDataOffset;
        ColumnsPerPoint = columnsPerPoint;
        HasColors = hasColors;
        HasIntensities = hasIntensities;
        _filePath = filePath;

        _properties["Columns"] = columns.ToString(CultureInfo.InvariantCulture);
        _properties["Rows"] = rows.ToString(CultureInfo.InvariantCulture);
        _properties["GridSize"] = GridSize.ToString(CultureInfo.InvariantCulture);
        _properties["ColumnsPerPoint"] = columnsPerPoint.ToString(CultureInfo.InvariantCulture);
        _properties["ScannerPosition"] = string.Create(CultureInfo.InvariantCulture,
            $"{scannerPosition.X:F6}, {scannerPosition.Y:F6}, {scannerPosition.Z:F6}");
    }

    internal void SetName(string name) => _name = name;

    /// <inheritdoc />
    public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null)
    {
        options ??= PclReadOptions.Default;
        long targetCount = options.MaxPoints ?? long.MaxValue;
        if (targetCount <= 0 || GridSize == 0) yield break;
        int step = Math.Max(1, options.SubsampleStep);

        using var fs = File.OpenRead(_filePath);
        fs.Seek(PointDataOffset, SeekOrigin.Begin);
        using var reader = new StreamReader(fs, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 65536, leaveOpen: true);

        long yielded = 0;
        long pointIndex = 0;

        while (pointIndex < GridSize)
        {
            string? line = reader.ReadLine();
            if (line == null)
                throw FormatError.Truncated($"PTX scan '{Name}' truncated: expected {GridSize} points but found {pointIndex}.");

            if (line.Length > MaxLineLength)
                throw FormatError.Corrupted($"PTX line exceeds maximum length.");

            if (string.IsNullOrWhiteSpace(line)) continue;

            pointIndex++;

            if ((pointIndex - 1) % step != 0) continue;

            if (!TryParsePointLine(line, ColumnsPerPoint, HasColors, HasIntensities, out PclPoint pt))
                continue;

            pt = pt with { Index = (ulong)yielded };
            if (options.ApplyTransform && !Transform.IsIdentity)
                pt = pt with { Position = Transform.TransformPoint(pt.Position) };

            yield return pt;
            yielded++;
            if (yielded >= targetCount) yield break;
        }
    }

    private static bool TryParsePointLine(string line, int expectedColumns, bool hasColors, bool hasIntensities, out PclPoint point)
    {
        point = default;
        Span<Range> ranges = stackalloc Range[16];
        int count = PtxDocument.SplitTokens(line.AsSpan(), ranges);
        if (count != expectedColumns)
            throw FormatError.Corrupted($"PTX point line has {count} columns; expected {expectedColumns}.");

        ReadOnlySpan<char> span = line.AsSpan();

        if (!double.TryParse(span[ranges[0]], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ||
            !double.TryParse(span[ranges[1]], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) ||
            !double.TryParse(span[ranges[2]], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
        {
            throw FormatError.Corrupted("Invalid coordinate values on PTX point line.");
        }

        // Empty grid cell: coordinates (0, 0, 0) represent no laser return and are skipped
        if (x == 0.0 && y == 0.0 && z == 0.0)
            return false;

        // Non-finite coordinates are skipped
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
            return false;

        float? intensity = null;
        if (hasIntensities)
        {
            if (!float.TryParse(span[ranges[3]], NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                throw FormatError.Corrupted("Invalid intensity value on PTX point line.");
            intensity = val;
        }

        PclColor? color = null;
        if (hasColors)
        {
            int colorOffset = expectedColumns == 7 ? 4 : 3;
            if (!double.TryParse(span[ranges[colorOffset]], NumberStyles.Float, CultureInfo.InvariantCulture, out double r) ||
                !double.TryParse(span[ranges[colorOffset + 1]], NumberStyles.Float, CultureInfo.InvariantCulture, out double g) ||
                !double.TryParse(span[ranges[colorOffset + 2]], NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
            {
                throw FormatError.Corrupted("Invalid color values on PTX point line.");
            }

            color = new PclColor(PtxDocument.ClampByte(r), PtxDocument.ClampByte(g), PtxDocument.ClampByte(b));
        }

        point = new PclPoint(new PclVector3(x, y, z), Color: color, Intensity: intensity);
        return true;
    }
}
