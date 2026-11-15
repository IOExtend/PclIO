using System.Globalization;

namespace PclIO;

/// <summary>Represents a 3D point or direction vector with double-precision coordinates.</summary>
/// <param name="X">X coordinate.</param>
/// <param name="Y">Y coordinate.</param>
/// <param name="Z">Z coordinate.</param>
public readonly record struct PclVector3(double X, double Y, double Z)
{
    /// <summary>The vector (0, 0, 0).</summary>
    public static PclVector3 Zero { get; } = new(0, 0, 0);

    /// <summary>The vector (1, 1, 1).</summary>
    public static PclVector3 One { get; } = new(1, 1, 1);

    /// <summary>True when all three components equal zero.</summary>
    public bool IsZero => X == 0.0 && Y == 0.0 && Z == 0.0;

    /// <summary>Component-wise sum.</summary>
    public static PclVector3 operator +(PclVector3 a, PclVector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    /// <summary>Component-wise difference.</summary>
    public static PclVector3 operator -(PclVector3 a, PclVector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    /// <summary>Multiplies every component by a scalar.</summary>
    public static PclVector3 operator *(PclVector3 a, double scalar) => new(a.X * scalar, a.Y * scalar, a.Z * scalar);

    /// <summary>Formats as "(x, y, z)" with four decimals, independent of the current culture.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({X:F4}, {Y:F4}, {Z:F4})");
}
