namespace PclIO;

/// <summary>Represents an individual point cloud scan or setup in a project or file.</summary>
public abstract class PclScan
{
    /// <summary>Identifier of the scan within its document (taken from the file when it has one).</summary>
    public abstract string Id { get; }

    /// <summary>Display name of the scan.</summary>
    public abstract string Name { get; }

    /// <summary>Number of points the file declares for this scan.</summary>
    public abstract long PointCount { get; }

    /// <summary>Bounds of the scan, or <see cref="PclBounds.Empty"/> when the file does not state them.</summary>
    public virtual PclBounds Bounds { get; protected set; } = PclBounds.Empty;

    /// <summary>Transform from the scan's local space to the project space, or <see cref="PclTransform.Identity"/> when none is stored.</summary>
    public virtual PclTransform Transform { get; protected set; } = PclTransform.Identity;

    /// <summary>True when points carry colors.</summary>
    public virtual bool HasColors => false;

    /// <summary>True when points carry normals.</summary>
    public virtual bool HasNormals => false;

    /// <summary>True when points carry intensities.</summary>
    public virtual bool HasIntensities => false;

    /// <summary>Format-specific key/value properties (keys are case-insensitive).</summary>
    public virtual IReadOnlyDictionary<string, string> Properties => _properties;

    /// <summary>Backing store for <see cref="Properties"/>; format readers add to it while opening.</summary>
    protected readonly Dictionary<string, string> _properties = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Streams the points of this scan.</summary>
    /// <param name="options">Read options, or null for <see cref="PclReadOptions.Default"/>.</param>
    public abstract IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null);
}
