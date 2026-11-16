namespace PclIO.Export;

/// <summary>LAS version written by <see cref="PclLasExporter"/>.</summary>
public enum LasVersion : byte
{
    /// <summary>ASPRS LAS 1.2.</summary>
    V12 = 2,

    /// <summary>ASPRS LAS 1.4.</summary>
    V14 = 4,
}

/// <summary>Options selecting the LAS version, point format, scale, offset and coordinate system of <see cref="PclLasExporter"/>.</summary>
public sealed record PclLasOptions
{
    private int _pointFormat = 2;
    private double _scale = 0.001;

    /// <summary>Default options: LAS 1.2, point format 2, 1 mm scale, offset from the data, no coordinate system record.</summary>
    public static PclLasOptions Default { get; } = new();

    /// <summary>LAS version to write: 1.2 or 1.4.</summary>
    public LasVersion Version { get; init; } = LasVersion.V12;

    /// <summary>
    /// Point format to write: 2 with LAS 1.2; 6, 7 or 8 with LAS 1.4. Format 6 holds XYZ, intensity, classification and
    /// GPS time; format 7 adds RGB, format 8 adds near infrared (always zero, points carry none).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not 2, 6, 7 or 8.</exception>
    public int PointFormat
    {
        get => _pointFormat;
        init
        {
            if (value is not (2 or 6 or 7 or 8))
                throw new ArgumentOutOfRangeException(nameof(value), "The LAS point format must be 2, 6, 7 or 8.");
            _pointFormat = value;
        }
    }

    /// <summary>
    /// Metres per stored coordinate unit, applied to every axis. Record coordinates are 32-bit integers, so the extent
    /// of each axis must not exceed about 2147 km times the scale in millimetres. Must be finite and above zero.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not finite or not above zero.</exception>
    public double Scale
    {
        get => _scale;
        init
        {
            if (!double.IsFinite(value) || value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "The LAS scale must be finite and above zero.");
            _scale = value;
        }
    }

    /// <summary>
    /// Coordinate offset subtracted before scaling, or null to use the minimum of the data on every axis.
    /// </summary>
    public PclVector3? Offset { get; init; }

    /// <summary>
    /// Whether to write the coordinate system of the source as a GeoTIFF variable length record when it names an
    /// EPSG code that fits in 16 bits (the <c>EPSG:NNNN</c> form, transcribed as a projected reference). Sources carry
    /// only the code, so anything else is skipped without a record. The default is false.
    /// </summary>
    public bool WriteCoordinateSystem { get; init; }
}
