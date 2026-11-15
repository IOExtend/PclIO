namespace PclIO;

/// <summary>Abstract base class representing an opened point cloud file or dataset.</summary>
public abstract class PclDocument : IDisposable
{
    /// <summary>The format kind of this document.</summary>
    public abstract PclFileKind Kind { get; }

    /// <summary>The path this document was opened from, as given by the caller.</summary>
    public string FilePath { get; }

    /// <summary>Size in bytes of the main file the reader parsed, as reported by the format reader.</summary>
    public long FileSize { get; }

    /// <summary>The scans contained in the document, in file order.</summary>
    public abstract IReadOnlyList<PclScan> Scans { get; }

    /// <summary>
    /// Sum of <see cref="PclScan.PointCount"/> over all scans. Each scan reports the count its format reader
    /// declares or derives (see the reader's own documentation); the value is not verified against the stored points.
    /// The sum saturates at <see cref="long.MaxValue"/> instead of overflowing, and negative counts add nothing.
    /// </summary>
    public virtual long TotalPointCount
    {
        get
        {
            long total = 0;
            foreach (var scan in Scans)
            {
                long count = scan.PointCount;
                if (count <= 0) continue;
                if (count > long.MaxValue - total) return long.MaxValue;
                total += count;
            }
            return total;
        }
    }

    /// <summary>Union of the non-empty scan bounds, or <see cref="PclBounds.Empty"/> when no scan declares bounds.</summary>
    public virtual PclBounds OverallBounds
    {
        get
        {
            var bounds = PclBounds.Empty;
            foreach (var scan in Scans)
            {
                if (!scan.Bounds.IsEmpty) bounds = bounds.Union(scan.Bounds);
            }
            return bounds;
        }
    }

    /// <summary>Coordinate system name stored in the file, or null when the file does not state one.</summary>
    public virtual string? CoordinateSystem { get; protected set; }

    /// <summary>Format-specific key/value metadata (keys are case-insensitive).</summary>
    public IReadOnlyDictionary<string, string> Metadata => _metadata;

    /// <summary>Non-fatal problems found while reading. The library never writes diagnostics to the console.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Backing store for <see cref="Metadata"/>; format readers add to it while opening.</summary>
    protected readonly Dictionary<string, string> _metadata = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Backing store for <see cref="Warnings"/>; format readers add to it while opening.</summary>
    protected readonly List<string> _warnings = [];

    /// <summary>Initializes the base document.</summary>
    /// <param name="filePath">Path the document was opened from.</param>
    /// <param name="fileSize">Size in bytes reported by <see cref="FileSize"/>.</param>
    protected PclDocument(string filePath, long fileSize)
    {
        FilePath = filePath;
        FileSize = fileSize;
    }

    /// <summary>
    /// Streams the points of all scans in order. <see cref="PclReadOptions.MaxPoints"/> limits the total
    /// across all scans.
    /// </summary>
    /// <param name="options">Read options, or null for <see cref="PclReadOptions.Default"/>.</param>
    public virtual IEnumerable<PclPoint> ReadAllPoints(PclReadOptions? options = null)
    {
        options ??= PclReadOptions.Default;
        if (options.MaxPoints == 0) yield break;

        long yielded = 0;
        foreach (var scan in Scans)
        {
            foreach (var pt in scan.ReadPoints(options))
            {
                yield return pt;
                yielded++;
                if (options.MaxPoints.HasValue && yielded >= options.MaxPoints.Value)
                    yield break;
            }
        }
    }

    /// <summary>Releases resources held by the document. The base class holds none.</summary>
    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
