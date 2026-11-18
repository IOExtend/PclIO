namespace PclIO;

/// <summary>Options controlling point cloud parsing and streaming. Values are validated when set.</summary>
public sealed record PclReadOptions
{
    private readonly long? _maxPoints;
    private readonly int _subsampleStep = 1;

    /// <summary>Default options: no point limit, every point, transforms applied.</summary>
    public static PclReadOptions Default { get; } = new();

    /// <summary>
    /// Maximum number of points yielded by one read call (a scan's <c>ReadPoints</c>, or <c>ReadAllPoints</c> across all scans),
    /// or null for all available points. Must not be negative; 0 yields no points.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public long? MaxPoints
    {
        get => _maxPoints;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value ?? 0, nameof(value));
            _maxPoints = value;
        }
    }

    private readonly double? _maxCoordinateMagnitude = 5000.0;

    /// <summary>
    /// Maximum allowed magnitude of a point coordinate in the file's own unit; a point with a larger coordinate is not
    /// returned. Null means no magnitude limit. Coordinates that are NaN or infinite are never returned. Currently only
    /// the PinPoint reader applies this rule; other readers ignore this option. The default is 5000.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public double? MaxCoordinateMagnitude
    {
        get => _maxCoordinateMagnitude;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value ?? 0, nameof(value));
            if (value is double v && !double.IsFinite(v))
                throw new ArgumentOutOfRangeException(nameof(value), "The magnitude limit must be finite; null means no limit.");
            _maxCoordinateMagnitude = value;
        }
    }

    /// <summary>
    /// Subsampling step: 1 keeps every point, N keeps one point in N. Readers apply the step inside their own
    /// internal blocks (packets, chunks, clusters), so the spacing is approximate and depends on the format's block layout.
    /// Must be at least 1.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int SubsampleStep
    {
        get => _subsampleStep;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1, nameof(value));
            _subsampleStep = value;
        }
    }

    /// <summary>
    /// Whether to apply each scan's <see cref="PclScan.Transform"/> to point positions: scale and translation,
    /// or the 4x4 matrix when the format supplies one. Rotation values are not applied.
    /// </summary>
    public bool ApplyTransform { get; init; } = true;

    /// <summary>
    /// Whether a reader that finds incomplete data fails instead of reporting it: a reader that would add a warning
    /// about dropped or skipped data throws <see cref="InvalidDataException"/> then. Readers that cannot detect
    /// incomplete data ignore this option. The default is false, so that a read never fails because of it.
    /// </summary>
    public bool Strict { get; init; }

    /// <summary>
    /// Whether points that the file marks as filtered are excluded from the read. Currently only the PinPoint
    /// sectioned layout marks points: its "FilterBits" channel holds one flag byte per stored point, and a non-zero
    /// flag means the point is filtered. Readers without such a channel ignore this option. The default is false, so
    /// that every stored point that passes the other rules of the reader is returned.
    /// </summary>
    public bool ExcludeFilteredPoints { get; init; }

    private double _edgeWiseCoordinateScale = 0.128 / 32768.0;

    /// <summary>
    /// Fixed-point scale of the 16-bit point offsets in EdgeWise cluster files, in metres per unit: a stored offset
    /// counts this many metres from the cluster centre. The files do not declare the scale, so the default of
    /// 0.128 / 32768 keeps the long-standing assumption of the reader; set it explicitly when the files use another
    /// scale. Other readers ignore this option. Must be finite and above zero.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not finite or not above zero.</exception>
    public double EdgeWiseCoordinateScale
    {
        get => _edgeWiseCoordinateScale;
        init
        {
            if (!double.IsFinite(value) || value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "The EdgeWise coordinate scale must be finite and above zero.");
            _edgeWiseCoordinateScale = value;
        }
    }

    /// <summary>
    /// Blocks of a scan to read, in file order; null reads all of the scan's blocks. A range that runs past the last
    /// block simply ends there. Only readers whose points come in independent blocks use this option; the others read
    /// the whole scan and ignore it. Currently only the Leica reader honours it.
    /// </summary>
    public BlockRange? Blocks { get; init; }

    /// <summary>
    /// Explicit column names or layout for delimited text point clouds (.xyz, .asc, .txt, .csv),
    /// e.g. "x,y,z,i" or "x,y,z,r,g,b". When null, the layout is read from the file header or detected from column counts.
    /// </summary>
    public string? TextColumns { get; init; }

    /// <summary>
    /// Explicit delimiter character for delimited text point clouds (e.g. ',', ';', '\t', ' ').
    /// When null, the delimiter is automatically detected from the file content.
    /// </summary>
    public char? TextDelimiter { get; init; }

    /// <summary>
    /// Line prefix that marks a comment line in delimited text point clouds (default "#"). Lines starting with
    /// this prefix or "//" are skipped.
    /// </summary>
    public string? TextCommentPrefix { get; init; }

    private double _textCoordinateScale = 1.0;

    /// <summary>
    /// Coordinate scale factor applied to X, Y and Z positions for delimited text point clouds (default 1.0).
    /// Must be finite and greater than zero.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not finite or not above zero.</exception>
    public double TextCoordinateScale
    {
        get => _textCoordinateScale;
        init
        {
            if (!double.IsFinite(value) || value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "The text coordinate scale must be finite and above zero.");
            _textCoordinateScale = value;
        }
    }

    private int _maxDegreeOfParallelism = 1;

    /// <summary>
    /// How many blocks a reader may decode at once. The points keep file order whatever the value is.
    /// Only readers with independent blocks use this option; the others read sequentially and ignore it.
    /// Currently only the Leica reader decodes in parallel, holding the decoded points of the blocks in flight.
    /// The default is 1: sequential decoding with constant memory besides the block being read.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int MaxDegreeOfParallelism
    {
        get => _maxDegreeOfParallelism;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _maxDegreeOfParallelism = value;
        }
    }
}
