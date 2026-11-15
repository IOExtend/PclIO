using System.Buffers.Binary;

namespace PclIO.Export;

/// <summary>
/// Exports point clouds to the ASPRS LAS 1.2 binary format (point format 2: XYZ, intensity, classification, RGB).
/// </summary>
/// <remarks>
/// The header needs the point count and bounds before the first record, so the source is enumerated twice
/// (<see cref="PclDocument.ReadAllPoints"/> is re-enumerable): pass one computes count and bounds, pass two writes
/// the records. Memory use does not depend on the number of points. The output stream does not need to be seekable.
/// Coordinates are stored with a fixed 1 mm scale, so the extent of each axis must not exceed about 2147 km.
/// Points with non-finite coordinates are skipped. Intensity is clamped to 0..1 and stored as 16 bit;
/// a point without intensity is written with full intensity, a point without colour is written white
/// (the format has no "missing" value). Return number is always 1 of 1; normals are not exported.
/// </remarks>
public static class PclLasExporter
{
    private const int HeaderSize = 227;
    private const int RecordSize = 26;
    private const double Scale = 0.001; // 1 millimetre

    /// <summary>Writes the document's points to <paramref name="output"/> and returns the number of points written.</summary>
    /// <param name="doc">Source document.</param>
    /// <param name="output">Destination stream.</param>
    /// <param name="options">Read options (limit, subsampling, transform); null for defaults.</param>
    /// <returns>Number of point records written.</returns>
    /// <exception cref="NotSupportedException">The point count or the coordinate extent does not fit the LAS 1.2 limits.</exception>
    /// <exception cref="InvalidOperationException">The source returned different points in the two passes.</exception>
    public static long Export(PclDocument doc, Stream output, PclReadOptions? options = null) =>
        Export(doc, output, options, out _);

    /// <summary>Writes the document's points and also reports how many points were skipped.</summary>
    /// <param name="doc">Source document.</param>
    /// <param name="output">Destination stream.</param>
    /// <param name="options">Read options (limit, subsampling, transform); null for defaults.</param>
    /// <param name="skippedPoints">Number of points skipped because a coordinate was NaN or infinite.</param>
    /// <returns>Number of point records written.</returns>
    /// <exception cref="NotSupportedException">The point count or the coordinate extent does not fit the LAS 1.2 limits.</exception>
    /// <exception cref="InvalidOperationException">The source returned different points in the two passes.</exception>
    public static long Export(PclDocument doc, Stream output, PclReadOptions? options, out long skippedPoints)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(output);
        options ??= PclReadOptions.Default;

        // Pass 1: count and bounds.
        long count = 0;
        skippedPoints = 0;
        PclBounds bounds = PclBounds.Empty;
        foreach (var pt in doc.ReadAllPoints(options))
        {
            if (!ExportValues.IsFinite(pt.Position))
            {
                skippedPoints++;
                continue;
            }
            if (++count > uint.MaxValue)
                throw new NotSupportedException("LAS 1.2 cannot hold more than 4294967295 points.");
            bounds = bounds.Encapsulate(pt.Position);
        }

        if (bounds.IsEmpty) bounds = new PclBounds(PclVector3.Zero, PclVector3.Zero);

        // Record coordinates are int32 multiples of the scale.
        if ((bounds.Max.X - bounds.Min.X) / Scale > int.MaxValue ||
            (bounds.Max.Y - bounds.Min.Y) / Scale > int.MaxValue ||
            (bounds.Max.Z - bounds.Min.Z) / Scale > int.MaxValue)
            throw new NotSupportedException("The coordinate extent is too large for LAS 1.2 with a 1 mm scale.");

        double offsetX = bounds.Min.X;
        double offsetY = bounds.Min.Y;
        double offsetZ = bounds.Min.Z;

        byte[] header = new byte[HeaderSize];
        "LASF"u8.CopyTo(header.AsSpan(0, 4));
        header[24] = 1; // version 1.2
        header[25] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(94, 2), HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(96, 4), HeaderSize); // offset to point data
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(100, 4), 0); // no variable length records
        header[104] = 2; // point data format 2
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(105, 2), RecordSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(107, 4), (uint)count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(111, 4), (uint)count); // points of return 1

        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(131, 8), Scale);
        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(139, 8), Scale);
        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(147, 8), Scale);

        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(155, 8), offsetX);
        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(163, 8), offsetY);
        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(171, 8), offsetZ);

        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(179, 8), bounds.Max.X);
        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(187, 8), bounds.Min.X);
        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(195, 8), bounds.Max.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(203, 8), bounds.Min.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(211, 8), bounds.Max.Z);
        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(219, 8), bounds.Min.Z);

        output.Write(header, 0, header.Length);

        // Pass 2: records, written in chunks to avoid one small write per point.
        byte[] buf = new byte[RecordSize * 2048];
        int used = 0;
        long written = 0;
        foreach (var pt in doc.ReadAllPoints(options))
        {
            if (!ExportValues.IsFinite(pt.Position)) continue;
            if (++written > count)
                throw new InvalidOperationException("The point source changed between the two export passes.");

            var rec = buf.AsSpan(used, RecordSize);
            BinaryPrimitives.WriteInt32LittleEndian(rec[..4], checked((int)Math.Round((pt.Position.X - offsetX) / Scale)));
            BinaryPrimitives.WriteInt32LittleEndian(rec.Slice(4, 4), checked((int)Math.Round((pt.Position.Y - offsetY) / Scale)));
            BinaryPrimitives.WriteInt32LittleEndian(rec.Slice(8, 4), checked((int)Math.Round((pt.Position.Z - offsetZ) / Scale)));

            float intensity = ExportValues.Intensity01(pt.Intensity) ?? 1.0f;
            BinaryPrimitives.WriteUInt16LittleEndian(rec.Slice(12, 2), (ushort)MathF.Round(intensity * 65535f));

            rec[14] = 0x09; // return number 1 of 1
            rec[15] = (byte)((pt.Classification ?? 0) & 0x1F); // upper bits are flags in LAS 1.2
            rec[16] = 0; // scan angle
            rec[17] = 0; // user data
            BinaryPrimitives.WriteUInt16LittleEndian(rec.Slice(18, 2), 0); // point source id

            // Scale 8-bit channels to the full 16-bit range (255 -> 65535).
            BinaryPrimitives.WriteUInt16LittleEndian(rec.Slice(20, 2), (ushort)((pt.Color?.R ?? 255) * 257));
            BinaryPrimitives.WriteUInt16LittleEndian(rec.Slice(22, 2), (ushort)((pt.Color?.G ?? 255) * 257));
            BinaryPrimitives.WriteUInt16LittleEndian(rec.Slice(24, 2), (ushort)((pt.Color?.B ?? 255) * 257));

            used += RecordSize;
            if (used == buf.Length)
            {
                output.Write(buf, 0, used);
                used = 0;
            }
        }

        if (used > 0) output.Write(buf, 0, used);

        if (written != count)
            throw new InvalidOperationException("The point source changed between the two export passes.");

        return written;
    }
}
