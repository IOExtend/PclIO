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
}
