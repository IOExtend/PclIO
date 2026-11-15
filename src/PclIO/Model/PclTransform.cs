namespace PclIO;

/// <summary>
/// Transform data read from a file and the part of it the library applies: scale then translation,
/// or a 4x4 matrix when one is given. <see cref="Rotation"/> is stored for information only and is not applied.
/// </summary>
public sealed record PclTransform
{
    /// <summary>The identity transform (no translation, unit scale, no matrix).</summary>
    public static PclTransform Identity { get; } = new();

    /// <summary>True when translation and rotation are zero, scale is one and no matrix is set.</summary>
    public bool IsIdentity => Translation.IsZero && Rotation.IsZero && Scale == PclVector3.One && Matrix4x4 == null;

    /// <summary>Translation added after scaling; applied by <see cref="TransformPoint"/> when no matrix is set.</summary>
    public PclVector3 Translation { get; init; } = PclVector3.Zero;

    /// <summary>Rotation values as parsed from the file (their meaning is format specific). Not applied by this class.</summary>
    public PclVector3 Rotation { get; init; } = PclVector3.Zero;

    /// <summary>Per-axis scale applied before the translation when no matrix is set.</summary>
    public PclVector3 Scale { get; init; } = PclVector3.One;

    /// <summary>
    /// Optional 4x4 row-major matrix (16 elements, translation in elements 3, 7 and 11), if the format stores one.
    /// When it has exactly 16 elements it replaces <see cref="Scale"/> and <see cref="Translation"/>.
    /// </summary>
    public double[]? Matrix4x4 { get; init; }

    /// <summary>Transforms a local point: by the matrix when present, otherwise scale then translation. Rotation is not applied.</summary>
    /// <param name="local">Point in the scan's local space.</param>
    public PclVector3 TransformPoint(PclVector3 local)
    {
        if (Matrix4x4 is { Length: 16 } m)
        {
            double x = local.X * m[0] + local.Y * m[1] + local.Z * m[2] + m[3];
            double y = local.X * m[4] + local.Y * m[5] + local.Z * m[6] + m[7];
            double z = local.X * m[8] + local.Y * m[9] + local.Z * m[10] + m[11];
            return new PclVector3(x, y, z);
        }

        double sx = local.X * Scale.X + Translation.X;
        double sy = local.Y * Scale.Y + Translation.Y;
        double sz = local.Z * Scale.Z + Translation.Z;
        return new PclVector3(sx, sy, sz);
    }

    /// <summary>
    /// Transforms a direction or normal without translation (matrix 3x3 part, otherwise per-axis scale; rotation is not applied)
    /// and normalizes the result to unit length. A zero-length result returns the input unchanged.
    /// </summary>
    /// <param name="vector">Direction in the scan's local space.</param>
    public PclVector3 TransformVector(PclVector3 vector)
    {
        if (Matrix4x4 is { Length: 16 } m)
        {
            double x = vector.X * m[0] + vector.Y * m[1] + vector.Z * m[2];
            double y = vector.X * m[4] + vector.Y * m[5] + vector.Z * m[6];
            double z = vector.X * m[8] + vector.Y * m[9] + vector.Z * m[10];
            double len = Math.Sqrt(x * x + y * y + z * z);
            return len > 0 ? new PclVector3(x / len, y / len, z / len) : vector;
        }

        double sx = vector.X * Scale.X;
        double sy = vector.Y * Scale.Y;
        double sz = vector.Z * Scale.Z;
        double slen = Math.Sqrt(sx * sx + sy * sy + sz * sz);
        return slen > 0 ? new PclVector3(sx / slen, sy / slen, sz / slen) : vector;
    }
}
