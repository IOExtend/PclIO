using System.Text;
using System.Text.Json;

namespace PclIO.Export;

/// <summary>Exports point cloud metadata and, optionally, a bounded list of sample points as JSON.</summary>
/// <remarks>
/// Root properties: <c>format</c>, <c>fileName</c> (file or folder name only, never the full path),
/// <c>fileSize</c>, <c>totalPointCount</c>, <c>coordinateSystem</c> (only when known), <c>overallBounds</c>
/// (<c>min</c>/<c>max</c> arrays of three numbers, or null when unknown), <c>metadata</c> (keys sorted ordinally),
/// <c>scans</c> (id, name, pointCount, channel flags and a transform whose vectors are arrays of three numbers),
/// and <c>samplePoints</c>, which is written only when <c>maxPoints</c> is greater than zero. Sample points use
/// <c>x y z</c> (4 decimals) plus <c>intensity</c> (clamped to 0..1) and <c>r g b</c> when present; normals are
/// not exported. Sample points are read with default read options and at most <c>maxPoints</c> are written.
/// Non-finite coordinates cannot be represented in JSON: such sample points are skipped, and non-finite numbers in
/// bounds or transforms are written as null. Output is culture independent and is flushed to the writer in
/// blocks while the points are streamed.
/// </remarks>
public static class PclJsonExporter
{
    private const int FlushInterval = 4096;

    /// <summary>Writes the document as JSON and returns the number of sample points written.</summary>
    /// <param name="doc">Source document.</param>
    /// <param name="writer">Destination text writer; the caller owns it and must flush or dispose it.</param>
    /// <param name="indented">Whether to indent the JSON.</param>
    /// <param name="maxPoints">Maximum number of sample points to include; null or zero writes none.</param>
    /// <returns>Number of sample points written.</returns>
    public static long Export(PclDocument doc, TextWriter writer, bool indented = true, int? maxPoints = null) =>
        Export(doc, writer, indented, maxPoints, out _);

    /// <summary>Writes the document as JSON and also reports how many sample points were skipped.</summary>
    /// <param name="doc">Source document.</param>
    /// <param name="writer">Destination text writer; the caller owns it and must flush or dispose it.</param>
    /// <param name="indented">Whether to indent the JSON.</param>
    /// <param name="maxPoints">Maximum number of sample points to include; null or zero writes none.</param>
    /// <param name="skippedPoints">Number of sample points skipped because a coordinate was NaN or infinite.</param>
    /// <returns>Number of sample points written.</returns>
    public static long Export(PclDocument doc, TextWriter writer, bool indented, int? maxPoints, out long skippedPoints)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(writer);

        skippedPoints = 0;
        var options = new JsonWriterOptions { Indented = indented };
        using var jw = new Utf8JsonWriter(new TextWriterStream(writer), options);

        jw.WriteStartObject();
        jw.WriteString("format", doc.Kind.ToString());
        // Name only: the absolute input path is private to the machine that produced the export.
        jw.WriteString("fileName", Path.GetFileName(Path.TrimEndingDirectorySeparator(doc.FilePath)));
        jw.WriteNumber("fileSize", doc.FileSize);
        jw.WriteNumber("totalPointCount", doc.TotalPointCount);

        if (doc.CoordinateSystem != null)
            jw.WriteString("coordinateSystem", doc.CoordinateSystem);

        var bounds = doc.OverallBounds;
        if (bounds.IsEmpty)
        {
            jw.WriteNull("overallBounds");
        }
        else
        {
            jw.WriteStartObject("overallBounds");
            WriteVector(jw, "min", bounds.Min);
            WriteVector(jw, "max", bounds.Max);
            jw.WriteEndObject();
        }

        jw.WriteStartObject("metadata");
        foreach (var (k, v) in doc.Metadata.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            jw.WriteString(k, v);
        }
        jw.WriteEndObject();

        jw.WriteStartArray("scans");
        foreach (var scan in doc.Scans)
        {
            jw.WriteStartObject();
            jw.WriteString("id", scan.Id);
            jw.WriteString("name", scan.Name);
            jw.WriteNumber("pointCount", scan.PointCount);
            jw.WriteBoolean("hasColors", scan.HasColors);
            jw.WriteBoolean("hasNormals", scan.HasNormals);
            jw.WriteBoolean("hasIntensities", scan.HasIntensities);

            jw.WriteStartObject("transform");
            WriteVector(jw, "translation", scan.Transform.Translation);
            WriteVector(jw, "rotation", scan.Transform.Rotation);
            WriteVector(jw, "scale", scan.Transform.Scale);
            jw.WriteEndObject();

            jw.WriteEndObject();
        }
        jw.WriteEndArray();

        long pointsWritten = 0;
        if (maxPoints.HasValue && maxPoints.Value > 0)
        {
            jw.WriteStartArray("samplePoints");
            var readOpts = new PclReadOptions { MaxPoints = maxPoints.Value };
            foreach (var pt in doc.ReadAllPoints(readOpts))
            {
                if (!ExportValues.IsFinite(pt.Position))
                {
                    skippedPoints++;
                    continue;
                }

                jw.WriteStartObject();
                jw.WriteNumber("x", Math.Round(pt.Position.X, 4));
                jw.WriteNumber("y", Math.Round(pt.Position.Y, 4));
                jw.WriteNumber("z", Math.Round(pt.Position.Z, 4));
                if (ExportValues.Intensity01(pt.Intensity) is { } intensity)
                    jw.WriteNumber("intensity", Math.Round(intensity, 3));
                if (pt.Color.HasValue)
                {
                    jw.WriteNumber("r", pt.Color.Value.R);
                    jw.WriteNumber("g", pt.Color.Value.G);
                    jw.WriteNumber("b", pt.Color.Value.B);
                }
                jw.WriteEndObject();
                pointsWritten++;

                // Keep the writer's internal buffer small while streaming.
                if (pointsWritten % FlushInterval == 0) jw.Flush();
            }
            jw.WriteEndArray();
        }

        jw.WriteEndObject();
        jw.Flush();
        return pointsWritten;
    }

    private static void WriteVector(Utf8JsonWriter jw, string name, PclVector3 v)
    {
        jw.WriteStartArray(name);
        WriteFinite(jw, v.X);
        WriteFinite(jw, v.Y);
        WriteFinite(jw, v.Z);
        jw.WriteEndArray();
    }

    // JSON has no NaN/Infinity; null marks a value that cannot be represented.
    private static void WriteFinite(Utf8JsonWriter jw, double value)
    {
        if (double.IsFinite(value)) jw.WriteNumberValue(value);
        else jw.WriteNullValue();
    }

    /// <summary>Adapts a <see cref="TextWriter"/> to the byte stream that <see cref="Utf8JsonWriter"/> needs.</summary>
    private sealed class TextWriterStream : Stream
    {
        private readonly TextWriter _writer;
        // Stateful decoder keeps a multi-byte character that is split across two writes intact.
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

        public TextWriterStream(TextWriter writer) => _writer = writer;

        public override void Write(byte[] buffer, int offset, int count)
        {
            char[] chars = new char[_decoder.GetCharCount(buffer, offset, count)];
            int n = _decoder.GetChars(buffer, offset, count, chars, 0);
            _writer.Write(chars, 0, n);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _writer.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
