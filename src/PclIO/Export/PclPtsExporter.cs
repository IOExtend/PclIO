using System.Globalization;

namespace PclIO.Export;

/// <summary>
/// Exports point clouds to the ASCII PTS format: a first line with the point count, then one
/// <c>X Y Z Intensity R G B</c> line per point.
/// </summary>
/// <remarks>
/// Output is culture independent (invariant, '.' decimal separator) and every line ends with '\n'.
/// The header must state the exact number of lines that follow, so the source is enumerated twice
/// (<see cref="PclDocument.ReadAllPoints"/> is re-enumerable): pass one counts, pass two writes.
/// Memory use does not depend on the number of points. Coordinates have 4 decimals. Points with
/// non-finite coordinates are skipped and are not counted in the header. Intensity is clamped to 0..1
/// and written as an integer 0..2047; a point without intensity gets 2047, a point without colour gets
/// 255 255 255 (every line has all seven columns). Normals are not exported.
/// </remarks>
public static class PclPtsExporter
{
    /// <summary>Writes the document's points to <paramref name="writer"/> and returns the number of points written.</summary>
    /// <param name="doc">Source document.</param>
    /// <param name="writer">Destination text writer; the caller owns it and must flush or dispose it.</param>
    /// <param name="options">Read options (limit, subsampling, transform); null for defaults.</param>
    /// <returns>Number of point lines written.</returns>
    /// <exception cref="InvalidOperationException">The source returned different points in the two passes.</exception>
    public static long Export(PclDocument doc, TextWriter writer, PclReadOptions? options = null) =>
        Export(doc, writer, options, out _);

    /// <summary>Writes the document's points and also reports how many points were skipped.</summary>
    /// <param name="doc">Source document.</param>
    /// <param name="writer">Destination text writer; the caller owns it and must flush or dispose it.</param>
    /// <param name="options">Read options (limit, subsampling, transform); null for defaults.</param>
    /// <param name="skippedPoints">Number of points skipped because a coordinate was NaN or infinite.</param>
    /// <returns>Number of point lines written.</returns>
    /// <exception cref="InvalidOperationException">The source returned different points in the two passes.</exception>
    public static long Export(PclDocument doc, TextWriter writer, PclReadOptions? options, out long skippedPoints)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(writer);
        options ??= PclReadOptions.Default;

        // Pass 1: the header must hold the real number of points.
        long count = 0;
        skippedPoints = 0;
        foreach (var pt in doc.ReadAllPoints(options))
        {
            if (ExportValues.IsFinite(pt.Position)) count++;
            else skippedPoints++;
        }

        writer.Write(count.ToString(CultureInfo.InvariantCulture));
        writer.Write('\n');

        // Pass 2: points.
        long written = 0;
        foreach (var pt in doc.ReadAllPoints(options))
        {
            if (!ExportValues.IsFinite(pt.Position)) continue;
            if (++written > count)
                throw new InvalidOperationException("The point source changed between the two export passes.");

            int intensity = (int)((ExportValues.Intensity01(pt.Intensity) ?? 1.0f) * 2047.0f);
            byte r = pt.Color?.R ?? 255;
            byte g = pt.Color?.G ?? 255;
            byte b = pt.Color?.B ?? 255;

            writer.Write(string.Create(CultureInfo.InvariantCulture,
                $"{pt.Position.X:F4} {pt.Position.Y:F4} {pt.Position.Z:F4} {intensity} {r} {g} {b}\n"));
        }

        if (written != count)
            throw new InvalidOperationException("The point source changed between the two export passes.");

        return written;
    }
}
