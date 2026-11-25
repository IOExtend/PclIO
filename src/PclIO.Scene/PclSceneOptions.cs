namespace PclIO.Scene;

/// <summary>Options for <see cref="PclSceneReader"/>.</summary>
public sealed record PclSceneOptions
{
    /// <summary>Default options: the file name as model key, at most 1,000,000 points, every point, coordinates taken as metres.</summary>
    public static PclSceneOptions Default { get; } = new();

    /// <summary>
    /// The key of the scene model (<c>SceneModel.Key</c>), which with each scan's <c>SourceId</c> forms the identity that may
    /// be stored outside the scene. Null uses the file name with its extension, for example <c>Site.e57</c>. A document
    /// without a file name needs it. An empty key is invalid, and <see cref="PclSceneReader"/> throws
    /// <see cref="ArgumentException"/> for it.
    /// </summary>
    public string? ModelKey { get; init; }

    private readonly long? _maxPoints = 1_000_000;

    /// <summary>
    /// Maximum number of points in the scene, across all scans; null for no limit. The IOScene contract holds small
    /// clouds in memory and puts large point clouds outside the model, so the default caps the scene at 1,000,000 points.
    /// Points beyond the cap are left out and reported with a <c>points-capped</c> diagnostic. Must not be negative;
    /// 0 makes a scene without geometry.
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

    private readonly int _subsampleStep = 1;

    /// <summary>
    /// Subsampling step passed to the engine: 1 keeps every point, N keeps one point in N. Readers apply the step inside
    /// their own internal blocks, so the spacing is approximate and depends on the format's block layout. Must be at least 1.
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

    private readonly double _metresPerUnit = 1.0;

    /// <summary>
    /// Metres in one unit of the source coordinates, applied to every point position. The scene is in metres; most
    /// scanner formats store metres, so the default is 1. Set it when the source uses another unit, for example 0.3048
    /// for a LAS file in feet. Must be finite and above zero.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not finite or not above zero.</exception>
    public double MetresPerUnit
    {
        get => _metresPerUnit;
        init
        {
            if (!double.IsFinite(value) || value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "The metres-per-unit factor must be finite and above zero.");
            _metresPerUnit = value;
        }
    }
}
