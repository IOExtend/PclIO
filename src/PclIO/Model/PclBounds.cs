namespace PclIO;

/// <summary>Represents an axis-aligned 3D bounding box.</summary>
/// <param name="Min">Corner with the smallest coordinates.</param>
/// <param name="Max">Corner with the largest coordinates.</param>
public readonly record struct PclBounds(PclVector3 Min, PclVector3 Max)
{
    /// <summary>An empty box (Min is +infinity, Max is -infinity) that is neutral for <see cref="Union"/> and <see cref="Encapsulate"/>.</summary>
    public static PclBounds Empty { get; } = new(
        new(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity),
        new(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity));

    /// <summary>True when Min exceeds Max on any axis.</summary>
    public bool IsEmpty => Min.X > Max.X || Min.Y > Max.Y || Min.Z > Max.Z;

    /// <summary>Midpoint of the box; meaningful only when the box is not empty.</summary>
    public PclVector3 Center => new((Min.X + Max.X) * 0.5, (Min.Y + Max.Y) * 0.5, (Min.Z + Max.Z) * 0.5);

    /// <summary>Extent on each axis, clamped to zero.</summary>
    public PclVector3 Size => new(Math.Max(0, Max.X - Min.X), Math.Max(0, Max.Y - Min.Y), Math.Max(0, Max.Z - Min.Z));

    /// <summary>True when the box is not empty and contains the point (edges included).</summary>
    public bool Contains(PclVector3 pt) =>
        !IsEmpty &&
        pt.X >= Min.X && pt.X <= Max.X &&
        pt.Y >= Min.Y && pt.Y <= Max.Y &&
        pt.Z >= Min.Z && pt.Z <= Max.Z;

    /// <summary>True when this bounding box overlaps with another bounding box (edges included).</summary>
    public bool Intersects(PclBounds other) =>
        !IsEmpty && !other.IsEmpty &&
        Min.X <= other.Max.X && Max.X >= other.Min.X &&
        Min.Y <= other.Max.Y && Max.Y >= other.Min.Y &&
        Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;

    /// <summary>Returns the smallest box that contains this box and the point.</summary>
    public PclBounds Encapsulate(PclVector3 pt)
    {
        if (IsEmpty) return new PclBounds(pt, pt);
        return new PclBounds(
            new PclVector3(Math.Min(Min.X, pt.X), Math.Min(Min.Y, pt.Y), Math.Min(Min.Z, pt.Z)),
            new PclVector3(Math.Max(Max.X, pt.X), Math.Max(Max.Y, pt.Y), Math.Max(Max.Z, pt.Z)));
    }

    /// <summary>Returns the smallest box that contains both boxes; an empty operand is ignored.</summary>
    public PclBounds Union(PclBounds other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        return new PclBounds(
            new PclVector3(Math.Min(Min.X, other.Min.X), Math.Min(Min.Y, other.Min.Y), Math.Min(Min.Z, other.Min.Z)),
            new PclVector3(Math.Max(Max.X, other.Max.X), Math.Max(Max.Y, other.Max.Y), Math.Max(Max.Z, other.Max.Z)));
    }

    /// <summary>Formats as "[min .. max]", independent of the current culture.</summary>
    public override string ToString() => $"[{Min} .. {Max}]";
}
