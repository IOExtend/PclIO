using System.Buffers.Binary;
using System.Text;

namespace PclIO.Export;

/// <summary>
/// Exports point clouds to the ASPRS LAS binary format: version 1.2 with point format 2 (XYZ, intensity,
/// classification, RGB), or version 1.4 with point formats 6 to 8, selected by <see cref="PclLasOptions"/>.
/// </summary>
/// <remarks>
/// The header needs the point count and bounds before the first record, so the source is enumerated twice
/// (<see cref="PclDocument.ReadAllPoints"/> is re-enumerable): pass one computes count and bounds, pass two writes
/// the records. Memory use does not depend on the number of points. The output stream does not need to be seekable.
/// Points with non-finite coordinates are skipped. Intensity is clamped to 0..1 and stored as 16 bit;
/// a point without intensity is written with full intensity, a point without colour is written white
/// (the format has no "missing" value). Return number is always 1 of 1; normals are not exported.
/// </remarks>
public static class PclLasExporter
{
    private const int HeaderSize12 = 227;
    private const int HeaderSize14 = 375;

    /// <summary>Writes the document's points to <paramref name="output"/> and returns the number of points written.</summary>
    /// <param name="doc">Source document.</param>
    /// <param name="output">Destination stream.</param>
    /// <param name="options">Read options (limit, subsampling, transform); null for defaults.</param>
    /// <returns>Number of point records written.</returns>
    /// <exception cref="NotSupportedException">The point count or the coordinate extent does not fit the LAS limits.</exception>
    /// <exception cref="InvalidOperationException">The source returned different points in the two passes.</exception>
    public static long Export(PclDocument doc, Stream output, PclReadOptions? options = null) =>
        Export(doc, output, options, out _);

    /// <summary>Writes the document's points and also reports how many points were skipped.</summary>
    /// <param name="doc">Source document.</param>
    /// <param name="output">Destination stream.</param>
    /// <param name="options">Read options (limit, subsampling, transform); null for defaults.</param>
    /// <param name="skippedPoints">Number of points skipped because a coordinate was NaN or infinite.</param>
    /// <returns>Number of point records written.</returns>
    /// <exception cref="NotSupportedException">The point count or the coordinate extent does not fit the LAS limits.</exception>
    /// <exception cref="InvalidOperationException">The source returned different points in the two passes.</exception>
    public static long Export(PclDocument doc, Stream output, PclReadOptions? options, out long skippedPoints) =>
        Export(doc, output, options, null, out skippedPoints);

    /// <summary>Writes the document's points with the given LAS options and returns the number of points written.</summary>
    /// <param name="doc">Source document.</param>
    /// <param name="output">Destination stream.</param>
    /// <param name="options">Read options (limit, subsampling, transform); null for defaults.</param>
    /// <param name="lasOptions">LAS version, point format, scale, offset and coordinate system; null for defaults.</param>
    /// <returns>Number of point records written.</returns>
    /// <exception cref="NotSupportedException">The version and point format do not match, or the point count or the
    /// coordinate extent does not fit the LAS limits.</exception>
    /// <exception cref="InvalidOperationException">The source returned different points in the two passes.</exception>
    public static long Export(PclDocument doc, Stream output, PclReadOptions? options, PclLasOptions? lasOptions) =>
        Export(doc, output, options, lasOptions, out _);

    /// <summary>Writes the document's points with the given LAS options and also reports how many points were skipped.</summary>
    /// <param name="doc">Source document.</param>
    /// <param name="output">Destination stream.</param>
    /// <param name="options">Read options (limit, subsampling, transform); null for defaults.</param>
    /// <param name="lasOptions">LAS version, point format, scale, offset and coordinate system; null for defaults.</param>
    /// <param name="skippedPoints">Number of points skipped because a coordinate was NaN or infinite.</param>
    /// <returns>Number of point records written.</returns>
    /// <exception cref="NotSupportedException">The version and point format do not match, or the point count or the
    /// coordinate extent does not fit the LAS limits.</exception>
    /// <exception cref="InvalidOperationException">The source returned different points in the two passes.</exception>
    public static long Export(PclDocument doc, Stream output, PclReadOptions? options, PclLasOptions? lasOptions, out long skippedPoints)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(output);
        options ??= PclReadOptions.Default;
        lasOptions ??= PclLasOptions.Default;

        bool is14 = lasOptions.Version == LasVersion.V14;
        int format = lasOptions.PointFormat;
        if (format == 2 && is14 || format is 6 or 7 or 8 && !is14)
            throw new NotSupportedException($"LAS point format {format} cannot be written with LAS {(is14 ? "1.4" : "1.2")}.");
        if (lasOptions.Version is not (LasVersion.V12 or LasVersion.V14))
            throw new NotSupportedException($"LAS version {lasOptions.Version:d} is not supported.");

        double scale = lasOptions.Scale;
        int headerSize = is14 ? HeaderSize14 : HeaderSize12;
        int recordSize = format switch { 2 => 26, 6 => 30, 7 => 36, _ => 38 };

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
            if (!is14 && count >= uint.MaxValue)
                throw new NotSupportedException("LAS 1.2 cannot hold more than 4294967295 points.");
            count++;
            bounds = bounds.Encapsulate(pt.Position);
        }

        if (bounds.IsEmpty) bounds = new PclBounds(PclVector3.Zero, PclVector3.Zero);

        double offsetX = lasOptions.Offset?.X ?? bounds.Min.X;
        double offsetY = lasOptions.Offset?.Y ?? bounds.Min.Y;
        double offsetZ = lasOptions.Offset?.Z ?? bounds.Min.Z;

        // Record coordinates are int32 multiples of the scale; with an explicit offset both ends must fit.
        if ((bounds.Max.X - offsetX) / scale > int.MaxValue || (bounds.Min.X - offsetX) / scale < int.MinValue ||
            (bounds.Max.Y - offsetY) / scale > int.MaxValue || (bounds.Min.Y - offsetY) / scale < int.MinValue ||
            (bounds.Max.Z - offsetZ) / scale > int.MaxValue || (bounds.Min.Z - offsetZ) / scale < int.MinValue)
            throw new NotSupportedException($"The coordinates do not fit int32 with LAS {(is14 ? "1.4" : "1.2")} and the configured scale and offset.");

        // The GeoTIFF record is the coordinate system carrier of LAS 1.2. LAS 1.4 point formats 6 and above require
        // WKT, which the exporter does not produce, so a 1.4 file is written without a coordinate system record.
        byte[] vlrs = (!is14 && lasOptions.WriteCoordinateSystem) ? CoordinateSystemVlr(doc.CoordinateSystem) : [];
        uint pointDataOffset = checked((uint)headerSize + (uint)vlrs.Length);

        byte[] header = new byte[headerSize];
        "LASF"u8.CopyTo(header.AsSpan(0, 4));
        header[24] = 1; // version 1.x
        header[25] = (byte)lasOptions.Version;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(94, 2), (ushort)headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(96, 4), pointDataOffset); // offset to point data
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(100, 4), (uint)(vlrs.Length / VlrSize)); // variable length records
        header[104] = (byte)format; // point data format
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(105, 2), (ushort)recordSize);

        // LAS 1.4 keeps the legacy counts at zero when the point format is 6 or above, or when the count does not
        // fit 32 bits; the extended counts at the end of the header carry the number instead.
        uint legacyCount = (is14 && (format >= 6 || count > uint.MaxValue)) ? 0 : (uint)count;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(107, 4), legacyCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(111, 4), legacyCount); // points of return 1

        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(131, 8), scale);
        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(139, 8), scale);
        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(147, 8), scale);

        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(155, 8), offsetX);
        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(163, 8), offsetY);
        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(171, 8), offsetZ);

        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(179, 8), bounds.Max.X);
        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(187, 8), bounds.Min.X);
        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(195, 8), bounds.Max.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(203, 8), bounds.Min.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(211, 8), bounds.Max.Z);
        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(219, 8), bounds.Min.Z);

        if (is14)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(247, 8), (ulong)count);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(255, 8), (ulong)count); // points of return 1
        }

        output.Write(header, 0, header.Length);
        if (vlrs.Length > 0) output.Write(vlrs, 0, vlrs.Length);

        // Pass 2: records, written in chunks to avoid one small write per point.
        byte[] buf = new byte[recordSize * 2048];
        int used = 0;
        long written = 0;
        foreach (var pt in doc.ReadAllPoints(options))
        {
            if (!ExportValues.IsFinite(pt.Position)) continue;
            if (++written > count)
                throw new InvalidOperationException("The point source changed between the two export passes.");

            var rec = buf.AsSpan(used, recordSize);
            BinaryPrimitives.WriteInt32LittleEndian(rec[..4], checked((int)Math.Round((pt.Position.X - offsetX) / scale)));
            BinaryPrimitives.WriteInt32LittleEndian(rec.Slice(4, 4), checked((int)Math.Round((pt.Position.Y - offsetY) / scale)));
            BinaryPrimitives.WriteInt32LittleEndian(rec.Slice(8, 4), checked((int)Math.Round((pt.Position.Z - offsetZ) / scale)));

            float intensity = ExportValues.Intensity01(pt.Intensity) ?? 1.0f;
            BinaryPrimitives.WriteUInt16LittleEndian(rec.Slice(12, 2), (ushort)MathF.Round(intensity * 65535f));

            if (format == 2)
            {
                rec[14] = 0x09; // return number 1 of 1
                rec[15] = (byte)((pt.Classification ?? 0) & 0x1F); // upper bits are flags in LAS 1.2
                rec[16] = 0; // scan angle
                rec[17] = 0; // user data
                BinaryPrimitives.WriteUInt16LittleEndian(rec.Slice(18, 2), 0); // point source id

                // Scale 8-bit channels to the full 16-bit range (255 -> 65535).
                BinaryPrimitives.WriteUInt16LittleEndian(rec.Slice(20, 2), (ushort)((pt.Color?.R ?? 255) * 257));
                BinaryPrimitives.WriteUInt16LittleEndian(rec.Slice(22, 2), (ushort)((pt.Color?.G ?? 255) * 257));
                BinaryPrimitives.WriteUInt16LittleEndian(rec.Slice(24, 2), (ushort)((pt.Color?.B ?? 255) * 257));
            }
            else
            {
                rec[14] = 0x11; // return number 1 of 1
                rec[15] = 0; // classification flags
                rec[16] = pt.Classification ?? 0;
                rec[17] = 0; // user data
                BinaryPrimitives.WriteInt16LittleEndian(rec.Slice(18, 2), 0); // scan angle
                BinaryPrimitives.WriteUInt16LittleEndian(rec.Slice(20, 2), 0); // point source id
                BinaryPrimitives.WriteDoubleLittleEndian(rec.Slice(22, 8), 0); // GPS time

                if (format >= 7)
                {
                    // Scale 8-bit channels to the full 16-bit range (255 -> 65535).
                    BinaryPrimitives.WriteUInt16LittleEndian(rec.Slice(30, 2), (ushort)((pt.Color?.R ?? 255) * 257));
                    BinaryPrimitives.WriteUInt16LittleEndian(rec.Slice(32, 2), (ushort)((pt.Color?.G ?? 255) * 257));
                    BinaryPrimitives.WriteUInt16LittleEndian(rec.Slice(34, 2), (ushort)((pt.Color?.B ?? 255) * 257));

                    if (format == 8)
                        BinaryPrimitives.WriteUInt16LittleEndian(rec.Slice(36, 2), 0); // near infrared
                }
            }

            used += recordSize;
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

    // A variable length record holds the whole GeoTIFF key directory, so its size is fixed.
    private const int VlrSize = 54 + 24;

    /// <summary>
    /// A GeoTIFF variable length record naming the projected EPSG code of the source, or empty when the source has
    /// none that fits in 16 bits. Only the <c>EPSG:NNNN</c> form is recognised; geographic codes and anything else
    /// are skipped without a record.
    /// </summary>
    private static byte[] CoordinateSystemVlr(string? coordinateSystem)
    {
        if (!TryParseEpsg(coordinateSystem, out ushort code)) return [];

        byte[] vlr = new byte[VlrSize];
        BinaryPrimitives.WriteUInt16LittleEndian(vlr.AsSpan(0, 2), 0); // reserved
        Encoding.ASCII.GetBytes("LASF_Projection").CopyTo(vlr, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(vlr.AsSpan(18, 2), 34735); // GeoTIFF key directory
        BinaryPrimitives.WriteUInt32LittleEndian(vlr.AsSpan(20, 4), (uint)(VlrSize - 54)); // record length after header
        Encoding.ASCII.GetBytes("GeoTIFF").CopyTo(vlr, 24);

        Span<byte> keys = vlr.AsSpan(54);
        BinaryPrimitives.WriteUInt16LittleEndian(keys[..2], 1); // key directory version
        BinaryPrimitives.WriteUInt16LittleEndian(keys.Slice(2, 2), 1); // key revision
        BinaryPrimitives.WriteUInt16LittleEndian(keys.Slice(4, 2), 0); // minor revision
        BinaryPrimitives.WriteUInt16LittleEndian(keys.Slice(6, 2), 2); // number of keys

        // GTModelTypeGeoKey = ModelTypeProjected.
        BinaryPrimitives.WriteUInt16LittleEndian(keys.Slice(8, 2), 1024);
        BinaryPrimitives.WriteUInt16LittleEndian(keys.Slice(10, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(keys.Slice(12, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(keys.Slice(14, 2), 1);

        // ProjectedCSTypeGeoKey = the EPSG code.
        BinaryPrimitives.WriteUInt16LittleEndian(keys.Slice(16, 2), 3072);
        BinaryPrimitives.WriteUInt16LittleEndian(keys.Slice(18, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(keys.Slice(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(keys.Slice(22, 2), code);

        return vlr;
    }

    // Recognises a projected EPSG code. The record writes a projected reference, so a geographic code (the
    // 4000-4999 block) would name a CRS of the wrong kind and is skipped, as is the user-defined sentinel 32767.
    private static bool TryParseEpsg(string? coordinateSystem, out ushort code)
    {
        code = 0;
        if (coordinateSystem == null) return false;

        string text = coordinateSystem.Trim();
        const string prefix = "EPSG:";
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (!ulong.TryParse(text.AsSpan(prefix.Length), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out ulong value))
            return false;

        if (value is < 1 or > ushort.MaxValue) return false;
        if (value is >= 4000 and <= 4999) return false;
        if (value == 32767) return false;
        code = (ushort)value;
        return true;
    }
}
