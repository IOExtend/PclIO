using System.Globalization;
using PclIO.Internal;

namespace PclIO.Formats.Xyz;

/// <summary>Represents a delimited text point cloud file (.xyz, .asc, .txt, .csv).</summary>
/// <remarks>
/// <para>
/// Supported: ASCII / UTF-8 text point clouds with one point per line, using comma, semicolon, tab or whitespace delimiters.
/// Columns may be determined from a header line (e.g. <c>x,y,z,nx,ny,nz</c>), explicit <see cref="PclReadOptions.TextColumns"/>,
/// or automatically from column counts: 3 columns (XYZ), 4 columns (XYZ and intensity), 6 columns (XYZ and RGB colour or unit normals),
/// 7 columns (XYZ, intensity and RGB/normals), 8 columns (XYZ, intensity, RGB and alpha), 9 columns (XYZ, RGB and normals) or
/// 10 columns (XYZ, intensity, RGB and normals). Comment lines (starting with <c>#</c>, <c>//</c> or <see cref="PclReadOptions.TextCommentPrefix"/>)
/// and points with non-finite coordinates are skipped.
/// </para>
/// <para>
/// Not read (reported with <see cref="InvalidDataException"/>): non-numeric coordinate values, mismatched column counts,
/// lines exceeding the 1 MiB limit, files declaring counts exceeding physical capacity, or empty files with no data.
/// </para>
/// </remarks>
public sealed class XyzDocument : PclDocument
{
    private const int MaxLineLength = 1 << 20;

    /// <inheritdoc />
    public override PclFileKind Kind => PclFileKind.Xyz;

    /// <summary>Delimiter character used in this file (',', ';', '\t' or ' ').</summary>
    public char Delimiter => _header.Delimiter;

    /// <summary>Total number of columns per point line in this file.</summary>
    public int Columns => _header.Layout.TotalColumns;

    /// <summary>True when the file began with a column header line.</summary>
    public bool HasHeader => _header.HasHeader;

    /// <summary>Description of the resolved column layout (e.g. "x,y,z" or "x,y,z,nx,ny,nz").</summary>
    public string ColumnLayout => _header.Layout.Description;

    /// <summary>Coordinate scale factor applied to positions.</summary>
    public double CoordinateScale => _header.CoordinateScale;

    /// <summary>Total number of valid data point lines in the file.</summary>
    public long TotalPoints => _header.PointCount;

    /// <inheritdoc />
    public override IReadOnlyList<PclScan> Scans => _scans;
    private readonly List<PclScan> _scans = [];

    private readonly XyzHeader _header;

    private XyzDocument(string path, long fileSize, XyzHeader header)
        : base(path, fileSize)
    {
        _header = header;
        _metadata["Format"] = "Delimited Text (XYZ)";
        _metadata["Delimiter"] = header.Delimiter == '\t' ? "\\t" : header.Delimiter == ' ' ? "space" : header.Delimiter.ToString();
        _metadata["DelimiterChar"] = header.Delimiter.ToString();
        _metadata["Columns"] = header.Layout.TotalColumns.ToString(CultureInfo.InvariantCulture);
        _metadata["ColumnLayout"] = header.Layout.Description;
        _metadata["HasHeader"] = header.HasHeader.ToString();
        _metadata["CoordinateScale"] = header.CoordinateScale.ToString(CultureInfo.InvariantCulture);
        _metadata["TotalPoints"] = header.PointCount.ToString(CultureInfo.InvariantCulture);

        string name = Path.GetFileNameWithoutExtension(path);
        _scans.Add(new XyzScan("0", name, header.PointCount, path, header, this));
    }

    /// <summary>Opens a delimited text point cloud file (.xyz, .asc, .txt, .csv) and inspects its structure.</summary>
    /// <param name="path">Path of the file.</param>
    /// <param name="options">Options specifying delimiter, column names, comment prefix or coordinate scale.</param>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is malformed, truncated, or contains invalid numbers.</exception>
    /// <exception cref="NotSupportedException">The point layout or column count is not supported.</exception>
    public static XyzDocument Open(string path, PclReadOptions? options = null)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("Delimited text file not found.", path);
        if (fi.Length == 0) throw FormatError.Truncated("Delimited text file is empty.");

        var header = ReadHeaderAndCount(path, fi.Length, options);
        return new XyzDocument(path, fi.Length, header);
    }

    private static XyzHeader ReadHeaderAndCount(string path, long fileLength, PclReadOptions? options)
    {
        string commentPrefix = options?.TextCommentPrefix ?? "#";
        double coordinateScale = options?.TextCoordinateScale ?? 1.0;

        using var fs = File.OpenRead(path);
        using var reader = new StreamReader(fs, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 65536, leaveOpen: true);

        string? line;
        long lineIndex = 0;
        string? firstCandidateLine = null;
        long firstCandidateLineIndex = 0;
        bool firstCandidateIsCommentHeader = false;

        Span<Range> ranges = stackalloc Range[64];

        // Find the first relevant line (header candidate or first data line)
        while ((line = reader.ReadLine()) != null)
        {
            lineIndex++;
            if (line.Length > MaxLineLength)
                throw FormatError.Corrupted($"Delimited text line {lineIndex} exceeds maximum length.");

            if (string.IsNullOrWhiteSpace(line)) continue;

            string trimmed = line.Trim();
            if (trimmed.StartsWith(commentPrefix, StringComparison.Ordinal) ||
                trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                // Check if this comment line is an explicit header, e.g. "# x,y,z" or "// x y z"
                string stripped = trimmed.TrimStart('#', '/', ' ');
                if (stripped.Length > 0 && options?.TextColumns == null)
                {
                    char candDelim = options?.TextDelimiter ?? DetectDelimiter(stripped);
                    int candCount = SplitTokens(stripped.AsSpan(), candDelim, ranges);
                    if (IsHeaderTokens(stripped.AsSpan(), ranges, candCount, out _, out _, out _))
                    {
                        firstCandidateLine = stripped;
                        firstCandidateLineIndex = lineIndex;
                        firstCandidateIsCommentHeader = true;
                        break;
                    }
                }
                continue;
            }

            firstCandidateLine = trimmed;
            firstCandidateLineIndex = lineIndex;
            break;
        }

        if (firstCandidateLine == null)
            throw FormatError.Truncated("No data lines found in delimited text file.");

        // Detect or apply delimiter
        char delimiter = options?.TextDelimiter ?? DetectDelimiter(firstCandidateLine);

        // Determine column layout and whether first line is a header
        XyzColumnLayout layout;
        bool hasHeader = false;
        string? firstDataLine = null;
        long firstDataLineIndex = 0;

        if (options?.TextColumns != null)
        {
            layout = ParseColumnLayoutFromText(options.TextColumns);

            // Check if firstCandidateLine is a header line matching layout column names or non-numeric
            int candTokens = SplitTokens(firstCandidateLine.AsSpan(), delimiter, ranges);
            if (firstCandidateIsCommentHeader ||
                IsHeaderTokens(firstCandidateLine.AsSpan(), ranges, candTokens, out _, out _, out _) ||
                !IsCoordinateLineNumeric(firstCandidateLine.AsSpan(), ranges, layout))
            {
                hasHeader = true;
            }
            else
            {
                hasHeader = false;
                firstDataLine = firstCandidateLine;
                firstDataLineIndex = firstCandidateLineIndex;
            }
        }
        else if (firstCandidateIsCommentHeader)
        {
            hasHeader = true;
            int candTokens = SplitTokens(firstCandidateLine.AsSpan(), delimiter, ranges);
            layout = ParseHeaderLayout(firstCandidateLine.AsSpan(), ranges, candTokens);
        }
        else
        {
            int candTokens = SplitTokens(firstCandidateLine.AsSpan(), delimiter, ranges);
            if (IsHeaderTokens(firstCandidateLine.AsSpan(), ranges, candTokens, out _, out _, out _))
            {
                hasHeader = true;
                layout = ParseHeaderLayout(firstCandidateLine.AsSpan(), ranges, candTokens);
            }
            else
            {
                hasHeader = false;
                layout = DetermineLayoutFromDataTokens(firstCandidateLine.AsSpan(), ranges, candTokens, firstCandidateLineIndex);
                firstDataLine = firstCandidateLine;
                firstDataLineIndex = firstCandidateLineIndex;
            }
        }

        // Count data lines and validate first data line
        long pointCount = 0;
        if (firstDataLine != null)
        {
            ValidatePointLine(firstDataLine.AsSpan(), delimiter, layout, firstDataLineIndex, ranges);
            pointCount++;
        }

        while ((line = reader.ReadLine()) != null)
        {
            lineIndex++;
            if (line.Length > MaxLineLength)
                throw FormatError.Corrupted($"Delimited text line {lineIndex} exceeds maximum length.");

            if (string.IsNullOrWhiteSpace(line)) continue;

            string trimmed = line.Trim();
            if (trimmed.StartsWith(commentPrefix, StringComparison.Ordinal) ||
                trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (pointCount == 0)
            {
                // Validate first data line when header was consumed
                ValidatePointLine(line.AsSpan(), delimiter, layout, lineIndex, ranges);
            }

            pointCount++;

            // Physical capacity check: each point line requires at least 5 bytes
            if (pointCount > fileLength / 5)
                throw FormatError.Corrupted("Delimited text point count exceeds physical capacity of the file.");
        }

        if (pointCount == 0 && !hasHeader)
            throw FormatError.Truncated("No point data found in delimited text file.");

        return new XyzHeader(
            Layout: layout,
            Delimiter: delimiter,
            HasHeader: hasHeader,
            HeaderIsComment: firstCandidateIsCommentHeader,
            PointCount: pointCount,
            CoordinateScale: coordinateScale,
            CommentPrefix: commentPrefix);
    }

    private static void ValidatePointLine(ReadOnlySpan<char> span, char delimiter, XyzColumnLayout layout, long lineIndex, Span<Range> ranges)
    {
        int count = SplitTokens(span, delimiter, ranges);
        if (count != layout.TotalColumns)
            throw FormatError.Corrupted($"Delimited text line {lineIndex} has {count} columns; expected {layout.TotalColumns}.");

        if (!double.TryParse(span[ranges[layout.XIndex]], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
            !double.TryParse(span[ranges[layout.YIndex]], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
            !double.TryParse(span[ranges[layout.ZIndex]], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            throw FormatError.Corrupted($"Invalid coordinate values on delimited text line {lineIndex}.");
        }
    }

    private static bool IsCoordinateLineNumeric(ReadOnlySpan<char> span, ReadOnlySpan<Range> ranges, XyzColumnLayout layout)
    {
        int maxIdx = Math.Max(layout.XIndex, Math.Max(layout.YIndex, layout.ZIndex));
        if (ranges.Length <= maxIdx) return false;
        return double.TryParse(span[ranges[layout.XIndex]], NumberStyles.Float, CultureInfo.InvariantCulture, out _) &&
               double.TryParse(span[ranges[layout.YIndex]], NumberStyles.Float, CultureInfo.InvariantCulture, out _) &&
               double.TryParse(span[ranges[layout.ZIndex]], NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    internal static XyzColumnLayout ParseColumnLayoutFromText(string text)
    {
        string[] tokens = text.Split([',', ';', '\t', ' '], StringSplitOptions.RemoveEmptyEntries);
        return BuildLayoutFromTokens(tokens);
    }

    private static XyzColumnLayout ParseHeaderLayout(ReadOnlySpan<char> span, ReadOnlySpan<Range> ranges, int count)
    {
        string[] tokens = new string[count];
        for (int i = 0; i < count; i++)
            tokens[i] = span[ranges[i]].Trim([' ', '\t', '\"', '\'']).ToString();
        return BuildLayoutFromTokens(tokens);
    }

    private static XyzColumnLayout BuildLayoutFromTokens(string[] tokens)
    {
        int xIdx = -1, yIdx = -1, zIdx = -1;
        int iIdx = -1, rIdx = -1, gIdx = -1, bIdx = -1, aIdx = -1;
        int nxIdx = -1, nyIdx = -1, nzIdx = -1;

        for (int i = 0; i < tokens.Length; i++)
        {
            string t = tokens[i].Trim([' ', '\t', '\"', '\'']).ToLowerInvariant();
            switch (t)
            {
                case "x" or "coord_x" or "coordx" or "pos_x" or "posx" or "easting" or "x_coord":
                    xIdx = i;
                    break;
                case "y" or "coord_y" or "coordy" or "pos_y" or "posy" or "northing" or "y_coord":
                    yIdx = i;
                    break;
                case "z" or "coord_z" or "coordz" or "pos_z" or "posz" or "elevation" or "height" or "altitude" or "depth" or "z_coord":
                    zIdx = i;
                    break;
                case "i" or "intensity" or "scalar_intensity" or "reflectance" or "scalar" or "intensities":
                    iIdx = i;
                    break;
                case "r" or "red" or "diffuse_r" or "color_r":
                    rIdx = i;
                    break;
                case "g" or "green" or "diffuse_g" or "color_g":
                    gIdx = i;
                    break;
                case "b" or "blue" or "diffuse_b" or "color_b":
                    bIdx = i;
                    break;
                case "a" or "alpha" or "diffuse_a" or "color_a":
                    aIdx = i;
                    break;
                case "nx" or "norm_x" or "normx" or "normal_x" or "normalx":
                    nxIdx = i;
                    break;
                case "ny" or "norm_y" or "normy" or "normal_y" or "normaly":
                    nyIdx = i;
                    break;
                case "nz" or "norm_z" or "normz" or "normal_z" or "normalz":
                    nzIdx = i;
                    break;
            }
        }

        if (xIdx < 0 || yIdx < 0 || zIdx < 0)
            throw new ArgumentException("Column specification must contain at least X, Y, and Z coordinates.");

        string description = string.Join(",", tokens.Select(t => t.Trim([' ', '\t', '\"', '\''])));
        return new XyzColumnLayout(
            TotalColumns: tokens.Length,
            XIndex: xIdx,
            YIndex: yIdx,
            ZIndex: zIdx,
            IntensityIndex: iIdx,
            RedIndex: rIdx,
            GreenIndex: gIdx,
            BlueIndex: bIdx,
            AlphaIndex: aIdx,
            NxIndex: nxIdx,
            NyIndex: nyIdx,
            NzIndex: nzIdx,
            Description: description);
    }

    private static XyzColumnLayout DetermineLayoutFromDataTokens(ReadOnlySpan<char> span, ReadOnlySpan<Range> ranges, int count, long lineIndex)
    {
        if (count is not (3 or 4 or 6 or 7 or 8 or 9 or 10))
            throw FormatError.Unsupported($"Delimited text column count {count} on line {lineIndex} is not supported without a header or TextColumns option; expected 3, 4, 6, 7, 8, 9 or 10 columns.");

        if (!double.TryParse(span[ranges[0]], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
            !double.TryParse(span[ranges[1]], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
            !double.TryParse(span[ranges[2]], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            throw FormatError.Corrupted($"Invalid coordinate values on delimited text line {lineIndex}.");
        }

        return count switch
        {
            3 => new XyzColumnLayout(3, 0, 1, 2, Description: "x,y,z"),
            4 => new XyzColumnLayout(4, 0, 1, 2, IntensityIndex: 3, Description: "x,y,z,intensity"),
            6 => Determine6Columns(span, ranges, lineIndex),
            7 => Determine7Columns(span, ranges, lineIndex),
            8 => new XyzColumnLayout(8, 0, 1, 2, IntensityIndex: 3, RedIndex: 4, GreenIndex: 5, BlueIndex: 6, AlphaIndex: 7, Description: "x,y,z,intensity,r,g,b,alpha"),
            9 => new XyzColumnLayout(9, 0, 1, 2, RedIndex: 3, GreenIndex: 4, BlueIndex: 5, NxIndex: 6, NyIndex: 7, NzIndex: 8, Description: "x,y,z,r,g,b,nx,ny,nz"),
            10 => new XyzColumnLayout(10, 0, 1, 2, IntensityIndex: 3, RedIndex: 4, GreenIndex: 5, BlueIndex: 6, NxIndex: 7, NyIndex: 8, NzIndex: 9, Description: "x,y,z,intensity,r,g,b,nx,ny,nz"),
            _ => throw FormatError.Unsupported($"Delimited text column count {count} is not supported."),
        };
    }

    private static XyzColumnLayout Determine6Columns(ReadOnlySpan<char> span, ReadOnlySpan<Range> ranges, long lineIndex)
    {
        if (!double.TryParse(span[ranges[3]], NumberStyles.Float, CultureInfo.InvariantCulture, out double c3) ||
            !double.TryParse(span[ranges[4]], NumberStyles.Float, CultureInfo.InvariantCulture, out double c4) ||
            !double.TryParse(span[ranges[5]], NumberStyles.Float, CultureInfo.InvariantCulture, out double c5))
        {
            throw FormatError.Corrupted($"Invalid values on delimited text line {lineIndex}.");
        }

        bool isNormals = (c3 < 0 || c4 < 0 || c5 < 0);
        if (!isNormals)
        {
            double lenSq = c3 * c3 + c4 * c4 + c5 * c5;
            if (lenSq > 0.5 && lenSq < 1.5 && (c3 < 1.0 || c4 < 1.0 || c5 < 1.0))
                isNormals = true;
        }

        return isNormals
            ? new XyzColumnLayout(6, 0, 1, 2, NxIndex: 3, NyIndex: 4, NzIndex: 5, Description: "x,y,z,nx,ny,nz")
            : new XyzColumnLayout(6, 0, 1, 2, RedIndex: 3, GreenIndex: 4, BlueIndex: 5, Description: "x,y,z,r,g,b");
    }

    private static XyzColumnLayout Determine7Columns(ReadOnlySpan<char> span, ReadOnlySpan<Range> ranges, long lineIndex)
    {
        if (!float.TryParse(span[ranges[3]], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
            !double.TryParse(span[ranges[4]], NumberStyles.Float, CultureInfo.InvariantCulture, out double c4) ||
            !double.TryParse(span[ranges[5]], NumberStyles.Float, CultureInfo.InvariantCulture, out double c5) ||
            !double.TryParse(span[ranges[6]], NumberStyles.Float, CultureInfo.InvariantCulture, out double c6))
        {
            throw FormatError.Corrupted($"Invalid values on delimited text line {lineIndex}.");
        }

        bool isNormals = (c4 < 0 || c5 < 0 || c6 < 0);
        if (!isNormals)
        {
            double lenSq = c4 * c4 + c5 * c5 + c6 * c6;
            if (lenSq > 0.5 && lenSq < 1.5 && (c4 < 1.0 || c5 < 1.0 || c6 < 1.0))
                isNormals = true;
        }

        return isNormals
            ? new XyzColumnLayout(7, 0, 1, 2, IntensityIndex: 3, NxIndex: 4, NyIndex: 5, NzIndex: 6, Description: "x,y,z,intensity,nx,ny,nz")
            : new XyzColumnLayout(7, 0, 1, 2, IntensityIndex: 3, RedIndex: 4, GreenIndex: 5, BlueIndex: 6, Description: "x,y,z,intensity,r,g,b");
    }

    internal static char DetectDelimiter(string line)
    {
        int commas = 0, semicolons = 0, tabs = 0;
        foreach (char c in line)
        {
            if (c == ',') commas++;
            else if (c == ';') semicolons++;
            else if (c == '\t') tabs++;
        }
        if (commas >= 2 && commas >= semicolons && commas >= tabs) return ',';
        if (semicolons >= 2 && semicolons >= commas && semicolons >= tabs) return ';';
        if (tabs >= 2 && tabs >= commas && tabs >= semicolons) return '\t';
        if (commas == 1 && semicolons == 0 && tabs == 0) return ',';
        if (semicolons == 1 && commas == 0 && tabs == 0) return ';';
        if (tabs == 1 && commas == 0 && semicolons == 0) return '\t';
        return ' ';
    }

    internal static int SplitTokens(ReadOnlySpan<char> line, char delimiter, Span<Range> ranges)
    {
        int count = 0;
        int length = line.Length;
        if (delimiter == ' ')
        {
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
        else
        {
            int start = 0;
            for (int i = 0; i <= length; i++)
            {
                if (i == length || line[i] == delimiter)
                {
                    int tokenStart = start;
                    int tokenEnd = i;
                    while (tokenStart < tokenEnd && (line[tokenStart] == ' ' || line[tokenStart] == '\t')) tokenStart++;
                    while (tokenEnd > tokenStart && (line[tokenEnd - 1] == ' ' || line[tokenEnd - 1] == '\t')) tokenEnd--;

                    if (count < ranges.Length)
                    {
                        ranges[count] = tokenStart..tokenEnd;
                    }
                    count++;
                    start = i + 1;
                }
            }
            return count;
        }
    }

    private static bool IsHeaderTokens(ReadOnlySpan<char> span, ReadOnlySpan<Range> ranges, int count, out int xIdx, out int yIdx, out int zIdx)
    {
        xIdx = -1;
        yIdx = -1;
        zIdx = -1;
        if (count < 3) return false;

        for (int i = 0; i < count; i++)
        {
            string t = span[ranges[i]].Trim([' ', '\t', '\"', '\'']).ToString().ToLowerInvariant();
            if (t is "x" or "coord_x" or "coordx" or "pos_x" or "posx" or "easting" or "x_coord") xIdx = i;
            else if (t is "y" or "coord_y" or "coordy" or "pos_y" or "posy" or "northing" or "y_coord") yIdx = i;
            else if (t is "z" or "coord_z" or "coordz" or "pos_z" or "posz" or "elevation" or "height" or "altitude" or "depth" or "z_coord") zIdx = i;
        }

        return xIdx >= 0 && yIdx >= 0 && zIdx >= 0;
    }

    internal static byte ClampByte(double value)
    {
        if (double.IsNaN(value) || value <= 0) return 0;
        if (value >= 255) return 255;
        return (byte)Math.Round(value);
    }

    internal sealed record XyzColumnLayout(
        int TotalColumns,
        int XIndex,
        int YIndex,
        int ZIndex,
        int IntensityIndex = -1,
        int RedIndex = -1,
        int GreenIndex = -1,
        int BlueIndex = -1,
        int AlphaIndex = -1,
        int NxIndex = -1,
        int NyIndex = -1,
        int NzIndex = -1,
        string Description = "")
    {
        public bool HasColors => RedIndex >= 0 && GreenIndex >= 0 && BlueIndex >= 0;
        public bool HasIntensities => IntensityIndex >= 0;
        public bool HasNormals => NxIndex >= 0 && NyIndex >= 0 && NzIndex >= 0;
    }

    internal sealed record XyzHeader(
        XyzColumnLayout Layout,
        char Delimiter,
        bool HasHeader,
        bool HeaderIsComment,
        long PointCount,
        double CoordinateScale,
        string CommentPrefix);
}

/// <summary>Represents a point scan inside a delimited text point cloud file.</summary>
public sealed class XyzScan : PclScan
{
    private const int MaxLineLength = 1 << 20;

    private readonly string _filePath;
    private readonly XyzDocument.XyzHeader _header;
    private readonly XyzDocument _document;

    /// <inheritdoc />
    public override string Id { get; }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override long PointCount => _header.PointCount;

    /// <inheritdoc />
    public override bool HasColors => _header.Layout.HasColors;

    /// <inheritdoc />
    public override bool HasIntensities => _header.Layout.HasIntensities;

    /// <inheritdoc />
    public override bool HasNormals => _header.Layout.HasNormals;

    /// <summary>Delimiter character used in this file.</summary>
    public char Delimiter => _header.Delimiter;

    /// <summary>Coordinate scale factor applied to positions.</summary>
    public double CoordinateScale => _header.CoordinateScale;

    /// <summary>Column layout description for this scan.</summary>
    public string ColumnLayout => _header.Layout.Description;

    internal XyzScan(string id, string name, long pointCount, string filePath,
        XyzDocument.XyzHeader header, XyzDocument document)
    {
        Id = id;
        Name = name;
        _filePath = filePath;
        _header = header;
        _document = document;
        _properties["Columns"] = header.Layout.TotalColumns.ToString(CultureInfo.InvariantCulture);
        _properties["ColumnLayout"] = header.Layout.Description;
        _properties["Delimiter"] = header.Delimiter == '\t' ? "\\t" : header.Delimiter == ' ' ? "space" : header.Delimiter.ToString();
        _properties["DelimiterChar"] = header.Delimiter.ToString();
        _properties["HasHeader"] = header.HasHeader.ToString();
        _properties["CoordinateScale"] = header.CoordinateScale.ToString(CultureInfo.InvariantCulture);
        _properties["TotalPoints"] = header.PointCount.ToString(CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null)
    {
        options ??= PclReadOptions.Default;
        long targetCount = options.MaxPoints ?? PointCount;
        if (targetCount <= 0) yield break;
        targetCount = Math.Min(targetCount, PointCount);
        int step = Math.Max(1, options.SubsampleStep);

        double scale = (options.TextCoordinateScale != 1.0) ? options.TextCoordinateScale : _header.CoordinateScale;
        string commentPrefix = options.TextCommentPrefix ?? _header.CommentPrefix;

        using var fs = File.OpenRead(_filePath);
        using var reader = new StreamReader(fs, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 65536, leaveOpen: true);

        string? line;
        long lineIndex = 0;
        long pointIndex = 0;
        long yielded = 0;
        bool headerSkipped = !_header.HasHeader || _header.HeaderIsComment;

        while ((line = reader.ReadLine()) != null)
        {
            lineIndex++;
            if (line.Length > MaxLineLength)
                throw FormatError.Corrupted($"Delimited text line {lineIndex} exceeds maximum length.");

            if (string.IsNullOrWhiteSpace(line)) continue;

            string trimmed = line.Trim();
            if (trimmed.StartsWith(commentPrefix, StringComparison.Ordinal) ||
                trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (!headerSkipped)
            {
                headerSkipped = true;
                continue;
            }

            pointIndex++;
            if ((pointIndex - 1) % step != 0) continue;

            if (!TryParsePointLine(line, _header.Delimiter, _header.Layout, scale, lineIndex, out PclPoint pt))
                continue;

            pt = pt with { Index = (ulong)yielded };
            if (options.ApplyTransform && !Transform.IsIdentity)
            {
                pt = pt with { Position = Transform.TransformPoint(pt.Position) };
                if (pt.Normal is { } n)
                    pt = pt with { Normal = Transform.TransformVector(n) };
            }

            yield return pt;
            yielded++;
            if (yielded >= targetCount) yield break;
        }
    }

    private static bool TryParsePointLine(string line, char delimiter, XyzDocument.XyzColumnLayout layout,
        double coordinateScale, long lineIndex, out PclPoint point)
    {
        point = default;
        Span<Range> ranges = stackalloc Range[64];
        int count = XyzDocument.SplitTokens(line.AsSpan(), delimiter, ranges);
        if (count != layout.TotalColumns)
            throw FormatError.Corrupted($"Delimited text line {lineIndex} has {count} columns; expected {layout.TotalColumns}.");

        ReadOnlySpan<char> span = line.AsSpan();

        if (!double.TryParse(span[ranges[layout.XIndex]], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ||
            !double.TryParse(span[ranges[layout.YIndex]], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) ||
            !double.TryParse(span[ranges[layout.ZIndex]], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
        {
            throw FormatError.Corrupted($"Invalid coordinate values on delimited text line {lineIndex}.");
        }

        // Skip points with non-finite coordinates
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
            return false;

        if (coordinateScale != 1.0)
        {
            x *= coordinateScale;
            y *= coordinateScale;
            z *= coordinateScale;
        }

        float? intensity = null;
        if (layout.HasIntensities)
        {
            if (!float.TryParse(span[ranges[layout.IntensityIndex]], NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                throw FormatError.Corrupted($"Invalid intensity value on delimited text line {lineIndex}.");
            intensity = val;
        }

        PclColor? color = null;
        if (layout.HasColors)
        {
            if (!double.TryParse(span[ranges[layout.RedIndex]], NumberStyles.Float, CultureInfo.InvariantCulture, out double r) ||
                !double.TryParse(span[ranges[layout.GreenIndex]], NumberStyles.Float, CultureInfo.InvariantCulture, out double g) ||
                !double.TryParse(span[ranges[layout.BlueIndex]], NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
            {
                throw FormatError.Corrupted($"Invalid color values on delimited text line {lineIndex}.");
            }

            byte alpha = 255;
            if (layout.AlphaIndex >= 0 && double.TryParse(span[ranges[layout.AlphaIndex]], NumberStyles.Float, CultureInfo.InvariantCulture, out double a))
            {
                alpha = XyzDocument.ClampByte(a);
            }

            color = new PclColor(XyzDocument.ClampByte(r), XyzDocument.ClampByte(g), XyzDocument.ClampByte(b), alpha);
        }

        PclVector3? normal = null;
        if (layout.HasNormals)
        {
            if (!double.TryParse(span[ranges[layout.NxIndex]], NumberStyles.Float, CultureInfo.InvariantCulture, out double nx) ||
                !double.TryParse(span[ranges[layout.NyIndex]], NumberStyles.Float, CultureInfo.InvariantCulture, out double ny) ||
                !double.TryParse(span[ranges[layout.NzIndex]], NumberStyles.Float, CultureInfo.InvariantCulture, out double nz))
            {
                throw FormatError.Corrupted($"Invalid normal values on delimited text line {lineIndex}.");
            }

            if (double.IsFinite(nx) && double.IsFinite(ny) && double.IsFinite(nz))
                normal = new PclVector3(nx, ny, nz);
        }

        point = new PclPoint(new PclVector3(x, y, z), Color: color, Intensity: intensity, Normal: normal);
        return true;
    }
}
