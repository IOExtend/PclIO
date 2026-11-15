using System.Globalization;

namespace PclIO.Export;

/// <summary>Exports point clouds to a simple space-delimited text format, one point per line.</summary>
/// <remarks>
/// Columns depend on the channels a point has: <c>X Y Z R G B</c> when it has a colour (intensity is then
/// not written), <c>X Y Z I</c> when it has only an intensity (clamped to 0..1, 3 decimals), otherwise <c>X Y Z</c>.
/// Coordinates have 4 decimals. Output is culture independent (invariant, '.' decimal separator) and every
/// line ends with '\n'. The cloud is streamed point by point, so memory use does not depend on its size.
/// Points with non-finite coordinates are skipped. Normals are not exported.
/// </remarks>
public static class PclXyzExporter
{
    /// <summary>Writes the document's points to <paramref name="writer"/> and returns the number of points written.</summary>
    /// <param name="doc">Source document.</param>
    /// <param name="writer">Destination text writer; the caller owns it and must flush or dispose it.</param>
    /// <param name="options">Read options (limit, subsampling, transform); null for defaults.</param>
    /// <returns>Number of point lines written.</returns>
    public static long Export(PclDocument doc, TextWriter writer, PclReadOptions? options = null) =>
        Export(doc, writer, options, out _);

    /// <summary>Writes the document's points and also reports how many points were skipped.</summary>
    /// <param name="doc">Source document.</param>
    /// <param name="writer">Destination text writer; the caller owns it and must flush or dispose it.</param>
    /// <param name="options">Read options (limit, subsampling, transform); null for defaults.</param>
    /// <param name="skippedPoints">Number of points skipped because a coordinate was NaN or infinite.</param>
    /// <returns>Number of point lines written.</returns>
    public static long Export(PclDocument doc, TextWriter writer, PclReadOptions? options, out long skippedPoints)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(writer);
        options ??= PclReadOptions.Default;

        long written = 0;
        skippedPoints = 0;
        foreach (var pt in doc.ReadAllPoints(options))
        {
            if (!ExportValues.IsFinite(pt.Position))
            {
                skippedPoints++;
                continue;
            }

            var (x, y, z) = (pt.Position.X, pt.Position.Y, pt.Position.Z);
            string line;
            if (pt.Color is { } c)
                line = string.Create(CultureInfo.InvariantCulture, $"{x:F4} {y:F4} {z:F4} {c.R} {c.G} {c.B}\n");
            else if (ExportValues.Intensity01(pt.Intensity) is { } intensity)
                line = string.Create(CultureInfo.InvariantCulture, $"{x:F4} {y:F4} {z:F4} {intensity:F3}\n");
            else
                line = string.Create(CultureInfo.InvariantCulture, $"{x:F4} {y:F4} {z:F4}\n");

            writer.Write(line);
            written++;
        }

        return written;
    }
}
