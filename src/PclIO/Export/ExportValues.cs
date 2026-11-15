namespace PclIO.Export;

/// <summary>Value checks and normalisation shared by the exporters.</summary>
internal static class ExportValues
{
    /// <summary>True when all three coordinates are finite (not NaN or infinity).</summary>
    public static bool IsFinite(PclVector3 p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);

    /// <summary>Clamps a normalised intensity to 0..1; null and NaN mean "no intensity".</summary>
    public static float? Intensity01(float? value) =>
        value is { } f && !float.IsNaN(f) ? Math.Clamp(f, 0f, 1f) : null;
}
