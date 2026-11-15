using System.Globalization;

namespace PclIO;

/// <summary>Represents a single 3D point cloud sample.</summary>
/// <param name="Position">Position, in the scan's local space or transformed when <see cref="PclReadOptions.ApplyTransform"/> is set.</param>
/// <param name="Color">Color, or null when the source has none.</param>
/// <param name="Intensity">Intensity as stored by the format (scale is format specific), or null.</param>
/// <param name="Normal">Unit normal, or null when the source has none.</param>
/// <param name="Classification">Class code, or null when the source has none.</param>
/// <param name="Index">Running number of the point within the read call that produced it; not an index into the source file.</param>
public readonly record struct PclPoint(
    PclVector3 Position,
    PclColor? Color = null,
    float? Intensity = null,
    PclVector3? Normal = null,
    byte? Classification = null,
    ulong Index = 0)
{
    /// <summary>X coordinate of <see cref="Position"/>.</summary>
    public double X => Position.X;

    /// <summary>Y coordinate of <see cref="Position"/>.</summary>
    public double Y => Position.Y;

    /// <summary>Z coordinate of <see cref="Position"/>.</summary>
    public double Z => Position.Z;

    /// <summary>Formats position, color and intensity, independent of the current culture.</summary>
    public override string ToString()
    {
        string colorStr = Color.HasValue ? $" {Color.Value}" : "";
        string intStr = Intensity.HasValue ? string.Create(CultureInfo.InvariantCulture, $" I={Intensity.Value:F3}") : "";
        return $"PclPoint({Position}{colorStr}{intStr})";
    }
}
