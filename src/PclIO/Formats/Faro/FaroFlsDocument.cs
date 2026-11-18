using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using PclIO.Internal;

namespace PclIO.Formats.Faro;

/// <summary>Represents a FARO scan stored as a single container file or as a folder structure (.fls).</summary>
/// <remarks>
/// The scan grid (rows, columns and angular ranges) is read from the scan parameters of the file; when it is
/// missing or inconsistent, opening fails with <see cref="InvalidDataException"/>. The point count is the declared
/// grid size (limited by the available data for folder scans), not a count of valid returns.
/// </remarks>
public sealed class FaroFlsDocument : PclDocument
{
    // Parameters are small text; a larger stream is not a parameter stream.
    private const long MaxMetadataBytes = 1 << 20;

    // Reader limit for the declared grid (2^27 = about 134 million cells). Planes are streamed, so memory does not
    // grow with the grid; the cap bounds the work a crafted grid can cause.
    private const long MaxGridCells = 1L << 27;

    // Largest practical deflate ratio, used to reject grids that the stored data cannot possibly fill.
    private const long MaxDeflateRatio = 1032;

    // Extra bytes allowed in a sample stream besides the samples (text header); also used by FaroScan.
    internal const long HeaderSlack = 64 * 1024;

    private const string Unknown = "unknown";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    public override PclFileKind Kind => PclFileKind.FaroFls;

    /// <summary>Scanner model from the file, or "unknown" when it is not stored.</summary>
    public string ScannerModel { get; }

    /// <summary>Scanner serial number from the file, or "unknown" when it is not stored.</summary>
    public string ScannerSerialNumber { get; }

    /// <summary>Number of rows (vertical samples) of the scan grid.</summary>
    public int NumRows { get; }

    /// <summary>Number of columns (horizontal samples) of the scan grid.</summary>
    public int NumCols { get; }

    /// <summary>Elevation angle of the first row, in radians.</summary>
    public double RowStartAngle { get; }

    /// <summary>Elevation angle after the last row, in radians.</summary>
    public double RowEndAngle { get; }

    /// <summary>Azimuth angle after the last column, in radians.</summary>
    public double ColEndAngle { get; }

    /// <inheritdoc />
    public override IReadOnlyList<PclScan> Scans => _scans;
    private readonly List<PclScan> _scans = [];

    private FaroFlsDocument(
        string path,
        long fileSize,
        Geometry g,
        long pointCount,
        string? dataStreamPath,
        bool isOle2,
        bool hasColors,
        bool hasIntensities,
        int recordSize = FaroScan.DefaultRecordSize)
        : base(path, fileSize)
    {
        ScannerModel = g.Model ?? Unknown;
        ScannerSerialNumber = g.Serial ?? Unknown;
        NumRows = g.Rows;
        NumCols = g.Cols;
        RowStartAngle = g.RowStart;
        RowEndAngle = g.RowEnd;
        ColEndAngle = g.ColEnd;

        if (g.Model is null) _warnings.Add("Scanner model is not stored in the file.");
        if (g.Serial is null) _warnings.Add("Scanner serial number is not stored in the file.");

        _metadata["ScannerModel"] = ScannerModel;
        _metadata["ScannerSerial"] = ScannerSerialNumber;
        _metadata["Resolution"] = FormattableString.Invariant($"{g.Cols} x {g.Rows} ({(long)g.Cols * g.Rows:N0} pts)");
        _metadata["AngularRangeVertical"] = FormattableString.Invariant($"{g.RowStart:F3} .. {g.RowEnd:F3} rad");
        _metadata["AngularRangeHorizontal"] = FormattableString.Invariant($"0.000 .. {g.ColEnd:F3} rad");

        _scans.Add(new FaroScan(
            id: ScannerSerialNumber,
            name: Path.GetFileNameWithoutExtension(path),
            gridSize: pointCount,
            rows: g.Rows,
            cols: g.Cols,
            rowStart: g.RowStart,
            rowEnd: g.RowEnd,
            colEnd: g.ColEnd,
            dataStreamPath: dataStreamPath,
            isOle2: isOle2,
            ole2Path: isOle2 ? path : null,
            hasColors: hasColors,
            hasIntensities: hasIntensities,
            recordSize: recordSize));
    }

    /// <summary>Opens a FARO scan container file or scan folder and reads its scan parameters.</summary>
    /// <param name="path">Path of a container file, of a scan folder, or of a file inside a scan folder.</param>
    /// <param name="options">Read options; currently unused when opening.</param>
    /// <exception cref="InvalidDataException">The scan grid parameters are missing, malformed or inconsistent.</exception>
    public static FaroFlsDocument Open(string path, PclReadOptions? options = null)
    {
        _ = options;

        if (File.Exists(path) && (path.EndsWith(".fls", StringComparison.OrdinalIgnoreCase) || Ole2Storage.IsOle2File(path)))
            return OpenContainer(path);

        string fullPath = Path.GetFullPath(path);
        string rootDir = Directory.Exists(fullPath) ? fullPath : (Path.GetDirectoryName(fullPath) ?? fullPath);

        string? paramsFile = FindFile(rootDir, "Params");
        if (paramsFile is null)
            throw FormatError.Corrupted("scan parameter file not found");
        if (new FileInfo(paramsFile).Length > MaxMetadataBytes)
            throw FormatError.Corrupted("scan parameter file is too large");

        Geometry g = Geometry.Parse(File.ReadAllText(paramsFile));

        string? streamPath = FindFile(rootDir, "ScanDataStream0");
        long size = streamPath != null ? new FileInfo(streamPath).Length : 0;

        // The layout of the samples is derived from the sizes of the files when they fit.
        long cells = (long)g.Rows * g.Cols;
        int recordSize = FaroScan.DetermineRecordSize(size, cells, out bool layoutKnown);

        // Never claim more samples than the stream can hold.
        long pointCount = streamPath != null ? Math.Min(cells, size / recordSize) : cells;

        var document = new FaroFlsDocument(path, size, g, pointCount, streamPath, isOle2: false, hasColors: false, hasIntensities: true,
            recordSize: recordSize);
        if (streamPath != null)
        {
            if (layoutKnown)
            {
                document._metadata["RecordSize"] = recordSize.ToString(CultureInfo.InvariantCulture);
                document._warnings.Add(
                    $"The record layout of the folder scan was derived from the size of its data stream: {recordSize} bytes per sample.");
            }
            else
            {
                document._warnings.Add("Folder scan samples are read with an assumed record layout and intensity scale.");
            }
        }

        return document;
    }

    private static FaroFlsDocument OpenContainer(string path)
    {
        long fileSize = new FileInfo(path).Length;
        using var storage = Ole2Storage.Open(path);

        string? metaStream = FirstExisting(storage, "Main", "Params", "Scans/1/Params")
            ?? throw FormatError.Corrupted("scan parameter stream not found");
        if (storage.GetStreamLength(metaStream) > (ulong)MaxMetadataBytes)
            throw FormatError.Corrupted("scan parameter stream is too large");

        Geometry g = Geometry.Parse(Encoding.ASCII.GetString(storage.ReadStream(metaStream)));
        long cells = (long)g.Rows * g.Cols;

        string? dist = FirstExisting(storage, "Scans/1/Dist0", "Dist0");
        if (dist != null)
        {
            ulong len = storage.GetStreamLength(dist);
            if (len > (ulong)fileSize || (cells * 2 - HeaderSlack) / MaxDeflateRatio > (long)len)
                throw FormatError.Corrupted("distance stream is too small for the declared scan grid");
        }

        bool hasColors = FirstExisting(storage, "Scans/1/Color0", "Color0") != null;
        bool hasIntensities = FirstExisting(storage, "Scans/1/Grey0", "Grey0") != null;
        return new FaroFlsDocument(path, fileSize, g, cells, null, isOle2: true, hasColors, hasIntensities);
    }

    private static string? FirstExisting(Ole2Storage storage, params string[] names)
    {
        foreach (string name in names)
            if (storage.StreamExists(name)) return name;
        return null;
    }

    // The standard layout is Scans/1/<name>; otherwise take the first match in ordinal order so results are stable.
    private static string? FindFile(string rootDir, string name)
    {
        string direct = Path.Combine(rootDir, "Scans", "1", name);
        if (File.Exists(direct)) return direct;

        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 8 };
        string? first = null;
        foreach (string file in Directory.EnumerateFiles(rootDir, name, enumeration))
            if (first is null || string.CompareOrdinal(file, first) < 0) first = file;
        return first;
    }

    /// <summary>Scan grid and identity read from the scan parameters.</summary>
    private readonly record struct Geometry(
        string? Model, string? Serial, int Rows, int Cols, double RowStart, double RowEnd, double ColEnd)
    {
        private const double AngleTolerance = 1e-6;

        public static Geometry Parse(string text)
        {
            string? model = Find(text, "Name\\s+\"ScannerModel\"\\s+Value\\s+\"([^\"\\r\\n]{1,128})\"");
            string? serial = Find(text, "Name\\s+\"ScannerSerNo\"\\s+Value\\s+\"?([^\"\\r\\n\\s]{1,64})\"?");

            int rows = RequireInt(text, "(?:Total)?NumRows", "row count");
            int cols = RequireInt(text, "NumCols", "column count");
            double rowStart = RequireAngle(text, "RowStartAngle", "start elevation");
            double rowEnd = RequireAngle(text, "RowEndAngle", "end elevation");
            double colEnd = RequireAngle(text, "ColEndAngle", "end azimuth");

            if ((long)rows * cols > MaxGridCells)
                throw FormatError.Unsupported("scan grid is larger than the supported maximum");
            if (Math.Abs(rowStart) > Math.PI + AngleTolerance || Math.Abs(rowEnd) > Math.PI + AngleTolerance || rowStart == rowEnd)
                throw FormatError.Corrupted("elevation range is invalid");
            if (colEnd == 0 || Math.Abs(colEnd) > Math.Tau + AngleTolerance)
                throw FormatError.Corrupted("azimuth range is invalid");

            return new Geometry(model, serial, rows, cols, rowStart, rowEnd, colEnd);
        }

        // A parameter is stored either as 'Name "key" Value v' or as 'key = v'.
        private static string KeyPattern(string key, string value) =>
            "(?:Name\\s+\"" + key + "\"\\s+Value\\s+|" + key + "\\s*=?\\s*)" + value;

        private static string? Find(string text, string pattern)
        {
            try
            {
                Match m = Regex.Match(text, pattern, RegexOptions.CultureInvariant, RegexTimeout);
                return m.Success ? m.Groups[1].Value : null;
            }
            catch (RegexMatchTimeoutException)
            {
                throw FormatError.Corrupted("scan parameters could not be parsed in reasonable time");
            }
        }

        private static int RequireInt(string text, string key, string label)
        {
            string value = Find(text, KeyPattern(key, "([0-9]{1,10})"))
                ?? throw FormatError.Corrupted($"{label} is missing");
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n <= 0)
                throw FormatError.Corrupted($"{label} is out of range");
            return n;
        }

        private static double RequireAngle(string text, string key, string label)
        {
            string value = Find(text, KeyPattern(key, "(-?[0-9]*\\.?[0-9]+(?:[eE][-+]?[0-9]+)?)"))
                ?? throw FormatError.Corrupted($"{label} is missing");
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || !double.IsFinite(d))
                throw FormatError.Corrupted($"{label} is not a finite number");
            return d;
        }
    }
}

/// <summary>Scan of a FARO container file or scan folder, read as a regular spherical grid.</summary>
public sealed class FaroScan : PclScan
{
    // Record size of folder scan streams that the sizes of the files do not reveal.
    internal const int DefaultRecordSize = 8;

    // A folder scan stream holds one record per cell of the grid; a record is never longer than this.
    private const int MaxRecordSize = 64;

    // The intensity of a folder scan is stored in a 16-bit field. Values that fit in 12 bits are the 12-bit
    // intensities of the scanner, which the format scales by 2048; wider values are 16-bit intensities.
    private const ushort MaxTwelveBitIntensity = 4095;
    private const double TwelveBitScale = 2048.0;
    private const double SixteenBitScale = 65535.0;

    // Container streams may start with a short text header ending in an opening brace; look for it near the start only.
    private const int HeaderSearchWindow = 256;

    // Reader policy: returns outside this range (metres) are not returned.
    private const double MinRange = 0.1;
    private const double MaxRange = 350.0;

    private readonly int _rows;
    private readonly int _cols;
    private readonly double _rowStart;
    private readonly double _rowEnd;
    private readonly double _colEnd;
    private readonly string? _streamPath;
    private readonly bool _isOle2;
    private readonly string? _ole2Path;
    private readonly bool _hasColors;
    private readonly bool _hasIntensities;
    private readonly int _recordSize;

    private readonly Lazy<long> _validReturns;
    private double _intensityScale = TwelveBitScale;
    private bool _folderInspected;

    /// <inheritdoc />
    public override string Id { get; }

    /// <inheritdoc />
    public override string Name { get; }

    /// <summary>
    /// Number of points that a full read returns: the cells of the grid with a valid return. The count is made on
    /// the first access, which reads the distance data of the scan.
    /// </summary>
    /// <exception cref="InvalidDataException">The data of the scan is missing or damaged.</exception>
    public override long PointCount => _validReturns.Value;

    /// <summary>Number of cells of the scan grid, also the number of samples the file holds.</summary>
    public long GridSize { get; }

    /// <summary>
    /// Bytes that one sample of a folder scan takes. The files do not declare the layout; it is derived from the
    /// size of the data stream and the size of the grid.
    /// </summary>
    public int RecordSize => _recordSize;

    /// <inheritdoc />
    public override bool HasIntensities => _hasIntensities;

    /// <inheritdoc />
    public override bool HasColors => _hasColors;

    internal FaroScan(
        string id,
        string name,
        long gridSize,
        int rows,
        int cols,
        double rowStart,
        double rowEnd,
        double colEnd,
        string? dataStreamPath,
        bool isOle2 = false,
        string? ole2Path = null,
        bool hasColors = false,
        bool hasIntensities = false,
        int recordSize = DefaultRecordSize)
    {
        if (rows <= 0 || cols <= 0)
            throw new ArgumentOutOfRangeException(nameof(rows), "Scan grid dimensions must be positive.");

        Id = id;
        Name = name;
        GridSize = gridSize;
        _rows = rows;
        _cols = cols;
        _rowStart = rowStart;
        _rowEnd = rowEnd;
        _colEnd = colEnd;
        _streamPath = dataStreamPath;
        _isOle2 = isOle2;
        _ole2Path = ole2Path;
        _hasColors = hasColors;
        _hasIntensities = hasIntensities;
        _recordSize = recordSize;

        // The count is made once even when several threads reach it at the same time, and the value is published
        // whole, so a concurrent first access cannot observe a partial count.
        _validReturns = new Lazy<long>(CountValidReturns);
    }

    /// <summary>
    /// The record layout of a folder scan is not declared by the files. It is derived from the size of the data
    /// stream and the size of the grid: the stream holds one record per cell of the grid, so its size must be a
    /// multiple of it. The assumed layout is used when the sizes do not fit.
    /// </summary>
    /// <param name="streamLength">Length of the data stream in bytes.</param>
    /// <param name="cells">Number of cells of the scan grid.</param>
    /// <param name="determined">True when the layout was derived from the sizes.</param>
    /// <returns>Bytes that one sample takes.</returns>
    internal static int DetermineRecordSize(long streamLength, long cells, out bool determined)
    {
        determined = false;
        if (cells <= 0 || streamLength <= 0 || streamLength % cells != 0) return DefaultRecordSize;

        long size = streamLength / cells;

        // A record holds a 16-bit distance and a 16-bit intensity; a smaller derived size cannot hold both, and
        // reading the intensity of its last record would run past the buffer. Use the assumed layout instead.
        if (size < 4 || size > MaxRecordSize) return DefaultRecordSize;

        determined = true;
        return (int)size;
    }

    /// <summary>Streams the valid returns of the scan.</summary>
    /// <remarks>
    /// Container files supply distance, optional colour and optional grey (intensity 0..1) planes. Folder scans are
    /// read with an assumed record layout and an assumed intensity scale, which the files do not declare; the document
    /// carries a warning for them. Returns with a range outside 0.1 m to 350 m are not returned.
    /// </remarks>
    /// <exception cref="InvalidDataException">The data streams are missing, oversized or malformed.</exception>
    public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null)
    {
        options ??= PclReadOptions.Default;
        return _isOle2 ? ReadContainerPoints(options) : ReadFolderPoints(options);
    }

    // Counts the cells of the grid with a valid return; these are the points that a full read returns.
    private long CountValidReturns()
    {
        long count = _isOle2 ? CountContainerReturns() : CountFolderReturns();
        return count;
    }

    private long CountContainerReturns()
    {
        string path = _ole2Path ?? throw new InvalidOperationException("Container path is not set.");
        long fileLength = new FileInfo(path).Length;

        using var storage = Ole2Storage.Open(path);
        using var dist = Plane.Open(storage, "Dist0", 2, GridSize, fileLength)
            ?? throw FormatError.Corrupted("distance stream not found");

        long count = 0;
        byte[] cell = new byte[2];
        for (long idx = 0; idx < GridSize; idx++)
        {
            // A distance plane shorter than the grid ends the scan early.
            if (!dist.TryRead(idx, cell)) break;
            if (IsValidDistance(cell)) count++;
        }

        return count;
    }

    private long CountFolderReturns()
    {
        string streamPath = _streamPath ?? throw FormatError.Corrupted("scan data stream not found");

        using var fs = File.OpenRead(streamPath);
        byte[] buffer = new byte[_recordSize * 8192];

        long count = 0;
        long baseIdx = 0;
        ushort maxIntensity = 0;
        while (baseIdx < GridSize)
        {
            int read = fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            int records = read / _recordSize;
            if (records == 0) break;

            for (int i = 0; i < records && baseIdx + i < GridSize; i++)
            {
                int off = i * _recordSize;
                ushort intensity = Bytes.ReadUInt16LE(buffer.AsSpan(off + 2, 2));
                if (intensity > maxIntensity) maxIntensity = intensity;
                if (IsValidDistance(buffer.AsSpan(off, 2))) count++;
            }

            baseIdx += records;
        }

        // The scale of the intensity follows from the values that are stored.
        _intensityScale = maxIntensity > MaxTwelveBitIntensity ? SixteenBitScale : TwelveBitScale;
        _folderInspected = true;

        return count;
    }

    // A distance is usable when the cell holds a return and the range is inside the accepted one.
    private static bool IsValidDistance(ReadOnlySpan<byte> cell)
    {
        ushort distMm = Bytes.ReadUInt16LE(cell);
        if (distMm == 0 || distMm == 0xFFFF) return false;

        double range = distMm * 0.001;
        return range >= MinRange && range <= MaxRange;
    }

    private IEnumerable<PclPoint> ReadContainerPoints(PclReadOptions options)
    {
        string path = _ole2Path ?? throw new InvalidOperationException("Container path is not set.");
        long fileLength = new FileInfo(path).Length;
        long cells = (long)_rows * _cols;

        using var storage = Ole2Storage.Open(path);

        // Planes are inflated sequentially in small chunks, so memory stays bounded by the compressed data of the file.
        using var dist = Plane.Open(storage, "Dist0", 2, cells, fileLength)
            ?? throw FormatError.Corrupted("distance stream not found");
        using var color = Plane.Open(storage, "Color0", 3, cells, fileLength);
        using var grey = Plane.Open(storage, "Grey0", 1, cells, fileLength);

        long target = options.MaxPoints ?? long.MaxValue;
        int step = Math.Max(1, options.SubsampleStep);
        long yielded = 0;

        byte[] distCell = new byte[2];
        byte[] colorCell = new byte[3];
        byte[] greyCell = new byte[1];

        // Samples beyond the declared grid cannot be placed, so they are not read.
        for (long idx = 0; idx < GridSize && yielded < target; idx += step)
        {
            // A distance plane shorter than the grid ends the scan early.
            if (!dist.TryRead(idx, distCell)) yield break;

            if (!IsValidDistance(distCell)) continue;

            double range = Bytes.ReadUInt16LE(distCell) * 0.001;

            // A colour or grey plane shorter than the grid leaves the remaining points without that channel.
            PclColor? c = color != null && color.TryRead(idx, colorCell)
                ? new PclColor(colorCell[0], colorCell[1], colorCell[2])
                : null;

            float? intensity = grey != null && grey.TryRead(idx, greyCell) ? greyCell[0] / 255.0f : null;

            var pt = new PclPoint(Project(idx, range), Color: c, Intensity: intensity, Index: (ulong)yielded);
            if (options.ApplyTransform)
                pt = pt with { Position = Transform.TransformPoint(pt.Position) };

            yield return pt;
            yielded++;
        }
    }

    private IEnumerable<PclPoint> ReadFolderPoints(PclReadOptions options)
    {
        string streamPath = _streamPath ?? throw FormatError.Corrupted("scan data stream not found");

        // The scale of the intensity is known after the data has been looked at once.
        if (!_folderInspected) _ = CountFolderReturns();

        long target = options.MaxPoints ?? long.MaxValue;
        int step = Math.Max(1, options.SubsampleStep);
        long yielded = 0;
        long baseIdx = 0;

        using var fs = File.OpenRead(streamPath);
        byte[] buffer = new byte[_recordSize * 8192];

        // Every pass consumes at least one record or ends the loop, so scanning always terminates.
        while (yielded < target && baseIdx < GridSize)
        {
            int read = fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            int count = read / _recordSize;
            if (count == 0) break;

            for (int i = 0; i < count && baseIdx + i < GridSize; i++)
            {
                long idx = baseIdx + i;
                if (idx % step != 0) continue;

                int off = i * _recordSize;
                if (!IsValidDistance(buffer.AsSpan(off, 2))) continue;

                ushort intensityRaw = Bytes.ReadUInt16LE(buffer.AsSpan(off + 2, 2));
                double range = Bytes.ReadUInt16LE(buffer.AsSpan(off, 2)) * 0.001;

                var pt = new PclPoint(Project(idx, range), Intensity: (float)(intensityRaw / _intensityScale), Index: (ulong)yielded);
                if (options.ApplyTransform)
                    pt = pt with { Position = Transform.TransformPoint(pt.Position) };

                yield return pt;
                yielded++;
                if (yielded >= target) yield break;
            }

            baseIdx += count;
        }
    }

    // Samples are stored column by column, so the sample index maps to a row and a column of the grid.
    private PclVector3 Project(long idx, double range)
    {
        int row = (int)(idx % _rows);
        int col = (int)(idx / _rows % _cols);

        double elevation = _rowStart + ((double)row / _rows) * (_rowEnd - _rowStart);
        double azimuth = ((double)col / _cols) * _colEnd;

        double cosElevation = Math.Cos(elevation);
        return new PclVector3(
            range * cosElevation * Math.Sin(azimuth),
            range * cosElevation * Math.Cos(azimuth),
            range * Math.Sin(elevation));
    }

    /// <summary>
    /// Forward-only reader of one sample plane. The stream may be gzip-compressed and may start with a short text
    /// header ending in an opening brace; both are handled here. At most the declared grid size plus the header
    /// slack is ever inflated, so a crafted stream cannot grow without bound.
    /// </summary>
    private sealed class Plane : IDisposable
    {
        private readonly Stream _stream;
        private readonly int _cellBytes;
        private readonly long _limit;
        private readonly byte[] _head = new byte[HeaderSearchWindow];
        private readonly byte[] _skip = new byte[8192];
        private int _headPos;
        private int _headLen;
        private long _consumed;
        private long _position;

        private Plane(Stream stream, int cellBytes, long limit)
        {
            _stream = stream;
            _cellBytes = cellBytes;
            _limit = limit;
        }

        /// <summary>Opens a plane from the container (top-level or under the first scan), or returns null when it is absent.</summary>
        public static Plane? Open(Ole2Storage storage, string name, int cellBytes, long cells, long fileLength)
        {
            string path = storage.StreamExists("Scans/1/" + name) ? "Scans/1/" + name
                : storage.StreamExists(name) ? name
                : "";
            if (path.Length == 0) return null;

            // A stream cannot be larger than the file that holds it.
            if (storage.GetStreamLength(path) > (ulong)fileLength)
                throw FormatError.Corrupted("stream length exceeds the file size");

            long limit = cells * cellBytes + FaroFlsDocument.HeaderSlack;
            byte[] data = storage.ReadStream(path);
            bool gzip = data.Length >= 3 && data[0] == 0x1F && data[1] == 0x8B && data[2] == 0x08;
            if (!gzip && data.Length > limit)
                throw FormatError.Corrupted("stream is larger than the declared scan grid allows");

            var input = new MemoryStream(data, writable: false);
            Stream stream = gzip ? new GZipStream(input, CompressionMode.Decompress) : input;
            var plane = new Plane(stream, cellBytes, limit);
            try
            {
                plane.SkipHeader();
            }
            catch
            {
                // Release the stream before the error reaches the caller; the error itself is rethrown unchanged.
                plane.Dispose();
                throw;
            }
            return plane;
        }

        /// <summary>Reads the cell at <paramref name="index"/>; indices must not decrease. Returns false when the plane ends first.</summary>
        public bool TryRead(long index, byte[] cell)
        {
            long gap = (index - _position) * _cellBytes;
            while (gap > 0)
            {
                int n = Read(_skip.AsSpan(0, (int)Math.Min(gap, _skip.Length)));
                if (n == 0) return false;
                gap -= n;
            }

            if (Read(cell.AsSpan(0, _cellBytes)) < _cellBytes) return false;
            _position = index + 1;
            return true;
        }

        // Skips the optional text header: everything up to and including an opening brace near the start, then blanks.
        // A stream without a brace in its first bytes has no header and is read from its first byte.
        private void SkipHeader()
        {
            _headLen = Pull(_head);
            int brace = Array.IndexOf(_head, (byte)'{', 0, _headLen);
            _headPos = brace >= 0 ? brace + 1 : 0;

            while (true)
            {
                while (_headPos < _headLen && (_head[_headPos] == 0x20 || _head[_headPos] == 0x0A || _head[_headPos] == 0x0D))
                    _headPos++;
                if (_headPos < _headLen || _headLen < _head.Length) break;
                _headLen = Pull(_head);
                _headPos = 0;
            }
        }

        // Serves the bytes buffered by SkipHeader first, then the stream.
        private int Read(Span<byte> dest)
        {
            int total = 0;
            if (_headPos < _headLen)
            {
                total = Math.Min(dest.Length, _headLen - _headPos);
                _head.AsSpan(_headPos, total).CopyTo(dest);
                _headPos += total;
            }

            return total + Pull(dest[total..]);
        }

        // Fills dest from the stream unless it ends first, refusing to inflate more than the limit.
        private int Pull(Span<byte> dest)
        {
            int total = 0;
            while (total < dest.Length)
            {
                int n = _stream.Read(dest[total..]);
                if (n == 0) break;
                total += n;
            }

            _consumed += total;
            if (_consumed > _limit)
                throw FormatError.Corrupted("decompressed stream is larger than the declared scan grid allows");
            return total;
        }

        public void Dispose() => _stream.Dispose();
    }
}
