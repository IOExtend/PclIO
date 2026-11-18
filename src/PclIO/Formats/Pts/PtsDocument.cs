using System.Globalization;
using PclIO.Internal;

namespace PclIO.Formats.Pts;

/// <summary>Represents a Leica Cyclone point cloud exchange file (.pts).</summary>
/// <remarks>
/// <para>
/// Supported: ASCII text point clouds with or without an initial point count line. Point records are
/// whitespace-separated numbers: 3 columns (XYZ), 4 columns (XYZ and intensity), 6 columns (XYZ and RGB colour,
/// or XYZ and unit normals), 7 columns (XYZ, intensity and RGB colour, or XYZ, intensity and unit normals),
/// 8 columns (XYZ, intensity, RGB and alpha), 9 columns (XYZ, RGB and normals) or 10 columns (XYZ, intensity,
/// RGB and normals). Points whose coordinates are not finite are skipped.
/// </para>
/// <para>
/// Not read (reported with <see cref="InvalidDataException"/>): files with malformed or negative counts,
/// lines with unsupported column counts or mismatched column counts, non-numeric values, or files that end
/// before the declared point count is satisfied.
/// </para>
/// </remarks>
public sealed class PtsDocument : PclDocument
{
    private const int MaxLineLength = 1 << 20;

    /// <inheritdoc />
    public override PclFileKind Kind => PclFileKind.Pts;

    /// <summary>Number of columns per point line in this file.</summary>
    public int Columns => _header.Columns;

    /// <summary>True when the file begins with an explicit point count line.</summary>
    public bool HasCountHeader => _header.HasCountHeader;

    /// <summary>The declared point count from the header line, or -1 when the count line is absent.</summary>
    public long DeclaredPointCount => _header.DeclaredPointCount;

    /// <inheritdoc />
    public override IReadOnlyList<PclScan> Scans => _scans;
    private readonly List<PclScan> _scans = [];

    private readonly PtsHeader _header;

    private PtsDocument(string path, long fileSize, PtsHeader header)
        : base(path, fileSize)
    {
        _header = header;
        _metadata["Format"] = "Leica PTS";
        _metadata["Columns"] = header.Columns.ToString(CultureInfo.InvariantCulture);
        if (header.HasCountHeader)
            _metadata["DeclaredPoints"] = header.DeclaredPointCount.ToString(CultureInfo.InvariantCulture);

        string name = Path.GetFileNameWithoutExtension(path);
        _scans.Add(new PtsScan("0", name, header.EffectivePointCount, path, header, this));
    }

    /// <summary>Opens a PTS point cloud file and inspects its structure.</summary>
    /// <param name="path">Path of the .pts file.</param>
    /// <param name="options">Currently unused; accepted for symmetry with the other readers.</param>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is malformed, truncated or declares an impossible count.</exception>
    /// <exception cref="NotSupportedException">The point layout is not supported.</exception>
    public static PtsDocument Open(string path, PclReadOptions? options = null)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("PTS file not found.", path);

        using var fs = File.OpenRead(path);
        var header = ReadHeader(fs, fi.Length);
        return new PtsDocument(path, fi.Length, header);
    }

    private static PtsHeader ReadHeader(FileStream fs, long fileLength)
    {
        using var reader = new StreamReader(fs, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 65536, leaveOpen: true);

        string? line;
        string? firstNonEmpty = null;
        long lineIndex = 0;

        while ((line = reader.ReadLine()) != null)
        {
            lineIndex++;
            if (line.Length > MaxLineLength)
                throw FormatError.Corrupted($"PTS line {lineIndex} exceeds maximum length.");

            if (!string.IsNullOrWhiteSpace(line))
            {
                firstNonEmpty = line.Trim();
                break;
            }
        }

        if (firstNonEmpty == null)
            throw FormatError.Truncated("PTS header");

        Span<Range> ranges = stackalloc Range[16];
        int firstTokens = SplitTokens(firstNonEmpty.AsSpan(), ranges);

        bool hasCount = false;
        long declaredCount = -1;
        string? firstPointLine = null;
        int pointLineIndex = 1;

        if (firstTokens == 1 && long.TryParse(firstNonEmpty.AsSpan(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long count))
        {
            hasCount = true;
            declaredCount = count;
            if (declaredCount < 0)
                throw FormatError.Corrupted("PTS point count cannot be negative.");

            // A point record requires at least 5 bytes (e.g. "0 0 0\n").
            if (declaredCount > fileLength / 5)
                throw FormatError.Corrupted("Declared PTS point count exceeds the physical capacity of the file.");

            if (declaredCount == 0)
            {
                return new PtsHeader(
                    HasCountHeader: true,
                    DeclaredPointCount: 0,
                    EffectivePointCount: 0,
                    Columns: 7,
                    HasColors: false,
                    HasIntensities: false,
                    HasNormals: false,
                    NormalsAt: -1,
                    IntensityAt: -1,
                    ColorAt: -1,
                    AlphaAt: -1);
            }

            while ((line = reader.ReadLine()) != null)
            {
                lineIndex++;
                if (line.Length > MaxLineLength)
                    throw FormatError.Corrupted($"PTS line {lineIndex} exceeds maximum length.");

                if (!string.IsNullOrWhiteSpace(line))
                {
                    firstPointLine = line.Trim();
                    pointLineIndex = (int)lineIndex;
                    break;
                }
            }

            if (firstPointLine == null)
                throw FormatError.Truncated("PTS points");
        }
        else
        {
            firstPointLine = firstNonEmpty;
            pointLineIndex = (int)lineIndex;
        }

        int columns = SplitTokens(firstPointLine.AsSpan(), ranges);
        if (columns is not (3 or 4 or 6 or 7 or 8 or 9 or 10))
            throw FormatError.Unsupported($"PTS column count {columns} is not supported; expected 3, 4, 6, 7, 8, 9 or 10 columns.");

        DetermineLayout(firstPointLine.AsSpan(), ranges, columns, pointLineIndex,
            out bool hasColors, out bool hasIntensities, out bool hasNormals,
            out int intensityAt, out int colorAt, out int alphaAt, out int normalsAt);

        long effectiveCount = declaredCount;
        if (!hasCount)
        {
            // Headerless PTS: count remaining lines to find total point count.
            long countLines = 1;
            while ((line = reader.ReadLine()) != null)
            {
                lineIndex++;
                if (line.Length > MaxLineLength)
                    throw FormatError.Corrupted($"PTS line {lineIndex} exceeds maximum length.");
                if (!string.IsNullOrWhiteSpace(line))
                    countLines++;
            }
            effectiveCount = countLines;
        }

        return new PtsHeader(
            HasCountHeader: hasCount,
            DeclaredPointCount: declaredCount,
            EffectivePointCount: effectiveCount,
            Columns: columns,
            HasColors: hasColors,
            HasIntensities: hasIntensities,
            HasNormals: hasNormals,
            NormalsAt: normalsAt,
            IntensityAt: intensityAt,
            ColorAt: colorAt,
            AlphaAt: alphaAt);
    }

    private static void DetermineLayout(ReadOnlySpan<char> span, ReadOnlySpan<Range> ranges, int columns, int lineNumber,
        out bool hasColors, out bool hasIntensities, out bool hasNormals,
        out int intensityAt, out int colorAt, out int alphaAt, out int normalsAt)
    {
        hasColors = false;
        hasIntensities = false;
        hasNormals = false;
        intensityAt = -1;
        colorAt = -1;
        alphaAt = -1;
        normalsAt = -1;

        // Verify that X, Y, Z coordinates parse as valid numbers.
        if (!double.TryParse(span[ranges[0]], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
            !double.TryParse(span[ranges[1]], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
            !double.TryParse(span[ranges[2]], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            throw FormatError.Corrupted($"Invalid coordinate values on PTS line {lineNumber}.");
        }

        switch (columns)
        {
            case 3:
                // X Y Z
                break;

            case 4:
                // X Y Z Intensity
                if (!float.TryParse(span[ranges[3]], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    throw FormatError.Corrupted($"Invalid intensity value on PTS line {lineNumber}.");
                hasIntensities = true;
                intensityAt = 3;
                break;

            case 6:
                // Either X Y Z R G B or X Y Z Nx Ny Nz.
                // If any of the remaining 3 tokens is negative, they are normals, not RGB colours.
                if (!double.TryParse(span[ranges[3]], NumberStyles.Float, CultureInfo.InvariantCulture, out double c3) ||
                    !double.TryParse(span[ranges[4]], NumberStyles.Float, CultureInfo.InvariantCulture, out double c4) ||
                    !double.TryParse(span[ranges[5]], NumberStyles.Float, CultureInfo.InvariantCulture, out double c5))
                {
                    throw FormatError.Corrupted($"Invalid values on PTS line {lineNumber}.");
                }

                if (c3 < 0 || c4 < 0 || c5 < 0)
                {
                    hasNormals = true;
                    normalsAt = 3;
                }
                else
                {
                    hasColors = true;
                    colorAt = 3;
                }
                break;

            case 7:
                // Standard Leica Cyclone PTS: X Y Z Intensity R G B
                if (!float.TryParse(span[ranges[3]], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    throw FormatError.Corrupted($"Invalid intensity value on PTS line {lineNumber}.");
                if (!double.TryParse(span[ranges[4]], NumberStyles.Float, CultureInfo.InvariantCulture, out double c4_7) ||
                    !double.TryParse(span[ranges[5]], NumberStyles.Float, CultureInfo.InvariantCulture, out double c5_7) ||
                    !double.TryParse(span[ranges[6]], NumberStyles.Float, CultureInfo.InvariantCulture, out double c6_7))
                {
                    throw FormatError.Corrupted($"Invalid values on PTS line {lineNumber}.");
                }

                hasIntensities = true;
                intensityAt = 3;
                if (c4_7 < 0 || c5_7 < 0 || c6_7 < 0)
                {
                    hasNormals = true;
                    normalsAt = 4;
                }
                else
                {
                    hasColors = true;
                    colorAt = 4;
                }
                break;

            case 8:
                // X Y Z Intensity R G B Alpha
                if (!float.TryParse(span[ranges[3]], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    throw FormatError.Corrupted($"Invalid intensity value on PTS line {lineNumber}.");
                if (!double.TryParse(span[ranges[4]], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
                    !double.TryParse(span[ranges[5]], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
                    !double.TryParse(span[ranges[6]], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
                    !double.TryParse(span[ranges[7]], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    throw FormatError.Corrupted($"Invalid color values on PTS line {lineNumber}.");
                }

                hasIntensities = true;
                intensityAt = 3;
                hasColors = true;
                colorAt = 4;
                alphaAt = 7;
                break;

            case 9:
                // X Y Z R G B Nx Ny Nz
                if (!double.TryParse(span[ranges[3]], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
                    !double.TryParse(span[ranges[4]], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
                    !double.TryParse(span[ranges[5]], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
                    !double.TryParse(span[ranges[6]], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
                    !double.TryParse(span[ranges[7]], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
                    !double.TryParse(span[ranges[8]], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    throw FormatError.Corrupted($"Invalid values on PTS line {lineNumber}.");
                }

                hasColors = true;
                colorAt = 3;
                hasNormals = true;
                normalsAt = 6;
                break;

            case 10:
                // X Y Z Intensity R G B Nx Ny Nz
                if (!float.TryParse(span[ranges[3]], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    throw FormatError.Corrupted($"Invalid intensity value on PTS line {lineNumber}.");
                if (!double.TryParse(span[ranges[4]], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
                    !double.TryParse(span[ranges[5]], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
                    !double.TryParse(span[ranges[6]], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
                    !double.TryParse(span[ranges[7]], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
                    !double.TryParse(span[ranges[8]], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
                    !double.TryParse(span[ranges[9]], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    throw FormatError.Corrupted($"Invalid values on PTS line {lineNumber}.");
                }

                hasIntensities = true;
                intensityAt = 3;
                hasColors = true;
                colorAt = 4;
                hasNormals = true;
                normalsAt = 7;
                break;
        }
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

    internal sealed record PtsHeader(
        bool HasCountHeader,
        long DeclaredPointCount,
        long EffectivePointCount,
        int Columns,
        bool HasColors,
        bool HasIntensities,
        bool HasNormals,
        int NormalsAt,
        int IntensityAt,
        int ColorAt,
        int AlphaAt);
}

/// <summary>Represents a point scan inside a Leica PTS file.</summary>
public sealed class PtsScan : PclScan
{
    private const int MaxLineLength = 1 << 20;

    private readonly string _filePath;
    private readonly PtsDocument.PtsHeader _header;
    private readonly PtsDocument _document;

    /// <inheritdoc />
    public override string Id { get; }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override long PointCount => _header.EffectivePointCount;

    /// <inheritdoc />
    public override bool HasColors => _header.HasColors;

    /// <inheritdoc />
    public override bool HasIntensities => _header.HasIntensities;

    /// <inheritdoc />
    public override bool HasNormals => _header.HasNormals;

    internal PtsScan(string id, string name, long pointCount, string filePath,
        PtsDocument.PtsHeader header, PtsDocument document)
    {
        Id = id;
        Name = name;
        _filePath = filePath;
        _header = header;
        _document = document;
        _properties["Columns"] = header.Columns.ToString(CultureInfo.InvariantCulture);
        _properties["HasCountHeader"] = header.HasCountHeader.ToString();
    }

    /// <inheritdoc />
    public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null)
    {
        options ??= PclReadOptions.Default;
        long targetCount = options.MaxPoints ?? PointCount;
        if (targetCount <= 0) yield break;
        targetCount = Math.Min(targetCount, PointCount);
        int step = Math.Max(1, options.SubsampleStep);

        using var fs = File.OpenRead(_filePath);
        using var reader = new StreamReader(fs, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 65536, leaveOpen: true);

        string? line;
        long lineIndex = 0;

        // Skip leading whitespace / blank lines and count header line if present
        if (_header.HasCountHeader)
        {
            while ((line = reader.ReadLine()) != null)
            {
                lineIndex++;
                if (line.Length > MaxLineLength)
                    throw FormatError.Corrupted($"PTS line {lineIndex} exceeds maximum length.");
                if (!string.IsNullOrWhiteSpace(line)) break;
            }
        }

        long yielded = 0;
        long pointIndex = 0;

        while ((line = reader.ReadLine()) != null)
        {
            lineIndex++;
            if (line.Length > MaxLineLength)
                throw FormatError.Corrupted($"PTS line {lineIndex} exceeds maximum length.");

            if (string.IsNullOrWhiteSpace(line)) continue;

            pointIndex++;

            if ((pointIndex - 1) % step != 0)
            {
                if (_header.HasCountHeader && pointIndex == _header.DeclaredPointCount) break;
                continue;
            }

            if (!TryParsePointLine(line, _header, lineIndex, out PclPoint pt))
            {
                if (_header.HasCountHeader && pointIndex == _header.DeclaredPointCount) break;
                continue;
            }

            pt = pt with { Index = (ulong)yielded };
            if (options.ApplyTransform && !Transform.IsIdentity)
                pt = pt with { Position = Transform.TransformPoint(pt.Position) };

            yield return pt;
            yielded++;
            if (yielded >= targetCount) yield break;

            if (_header.HasCountHeader && pointIndex == _header.DeclaredPointCount) break;
        }

        if (_header.HasCountHeader && pointIndex < _header.DeclaredPointCount)
            throw FormatError.Truncated("PTS points");
    }

    private static bool TryParsePointLine(string line, PtsDocument.PtsHeader header, long lineIndex, out PclPoint point)
    {
        point = default;
        Span<Range> ranges = stackalloc Range[16];
        int count = PtsDocument.SplitTokens(line.AsSpan(), ranges);
        if (count != header.Columns)
            throw FormatError.Corrupted($"PTS line {lineIndex} has {count} columns; expected {header.Columns}.");

        ReadOnlySpan<char> span = line.AsSpan();

        if (!double.TryParse(span[ranges[0]], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ||
            !double.TryParse(span[ranges[1]], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) ||
            !double.TryParse(span[ranges[2]], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
        {
            throw FormatError.Corrupted($"Invalid coordinate values on PTS line {lineIndex}.");
        }

        // Skip points with non-finite coordinates
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
            return false;

        float? intensity = null;
        if (header.HasIntensities)
        {
            if (!float.TryParse(span[ranges[header.IntensityAt]], NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                throw FormatError.Corrupted($"Invalid intensity value on PTS line {lineIndex}.");
            intensity = val;
        }

        PclColor? color = null;
        if (header.HasColors)
        {
            if (!double.TryParse(span[ranges[header.ColorAt]], NumberStyles.Float, CultureInfo.InvariantCulture, out double r) ||
                !double.TryParse(span[ranges[header.ColorAt + 1]], NumberStyles.Float, CultureInfo.InvariantCulture, out double g) ||
                !double.TryParse(span[ranges[header.ColorAt + 2]], NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
            {
                throw FormatError.Corrupted($"Invalid color values on PTS line {lineIndex}.");
            }

            byte alpha = 255;
            if (header.AlphaAt >= 0 && double.TryParse(span[ranges[header.AlphaAt]], NumberStyles.Float, CultureInfo.InvariantCulture, out double a))
            {
                alpha = PtsDocument.ClampByte(a);
            }

            color = new PclColor(PtsDocument.ClampByte(r), PtsDocument.ClampByte(g), PtsDocument.ClampByte(b), alpha);
        }

        PclVector3? normal = null;
        if (header.HasNormals)
        {
            if (!double.TryParse(span[ranges[header.NormalsAt]], NumberStyles.Float, CultureInfo.InvariantCulture, out double nx) ||
                !double.TryParse(span[ranges[header.NormalsAt + 1]], NumberStyles.Float, CultureInfo.InvariantCulture, out double ny) ||
                !double.TryParse(span[ranges[header.NormalsAt + 2]], NumberStyles.Float, CultureInfo.InvariantCulture, out double nz))
            {
                throw FormatError.Corrupted($"Invalid normal values on PTS line {lineIndex}.");
            }

            if (double.IsFinite(nx) && double.IsFinite(ny) && double.IsFinite(nz))
                normal = new PclVector3(nx, ny, nz);
        }

        point = new PclPoint(new PclVector3(x, y, z), Color: color, Intensity: intensity, Normal: normal);
        return true;
    }
}
