using System.Globalization;

namespace PclIO;

/// <summary>Represents an 8-bit RGBA color.</summary>
/// <param name="R">Red channel.</param>
/// <param name="G">Green channel.</param>
/// <param name="B">Blue channel.</param>
/// <param name="A">Alpha channel; 255 (opaque) when the file has no alpha.</param>
public readonly record struct PclColor(byte R, byte G, byte B, byte A = 255)
{
    /// <summary>Formats as "RGBA(r, g, b, a)", independent of the current culture.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"RGBA({R}, {G}, {B}, {A})");
}
