using System.Buffers.Binary;
using System.Text;

namespace PclIO.Tests;

/// <summary>A synthetic point data file in the sectioned layout, with the offsets a test needs to damage it precisely.</summary>
internal sealed record PackedBin(byte[] Bytes, int PropertyOffset, int DataOffset, int PointsStreamOffset);

/// <summary>Builds point data files in the sectioned layout: header, section table, a placeholder section, the property table
/// and the data section with Snappy-compressed channels.</summary>
internal static class PinPointPackedFixtures
{
    private const int HeaderSize = 24;
    private const int EntrySize = 20;

    /// <summary>Creates a file. Colours are RGB (three bytes per point), intensities one byte per point.</summary>
    /// <param name="runEncodedColors">Stores the colour channel as one literal followed by copies, which needs identical colours.</param>
    public static PackedBin Create(IReadOnlyList<(float X, float Y, float Z)> points, byte[]? rgb = null, byte[]? intensities = null,
        bool runEncodedColors = false)
    {
        int n = points.Count;
        byte[] positions = new byte[n * 12];
        for (int i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(positions.AsSpan(i * 12, 4), points[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(positions.AsSpan((i * 12) + 4, 4), points[i].Y);
            BinaryPrimitives.WriteSingleLittleEndian(positions.AsSpan((i * 12) + 8, 4), points[i].Z);
        }

        var channels = new List<(string Name, byte[] Stream)>
        {
            ("RowColIndex", Literals(new byte[n * 8])),
            ("Points", Literals(positions)),
            ("FilterBits", Literals(new byte[n])),
        };
        if (rgb != null) channels.Add(("RGB8", runEncodedColors ? Runs(rgb, 3) : Literals(rgb)));
        if (intensities != null) channels.Add(("Intensities8", Literals(intensities)));

        return Assemble(channels, n);
    }

    /// <summary>Assembles a file from named, already compressed channel streams and a stored point count.</summary>
    /// <param name="propertyTable">The serialised property table; by default one listing the channel names.</param>
    /// <param name="dataHeaderValue">The first 64-bit value of the data section, which is 0 in the supported layout.</param>
    /// <param name="afterChannels">Bytes placed inside the data section after the last channel stream.</param>
    public static PackedBin Assemble(IReadOnlyList<(string Name, byte[] Stream)> channels, long storedPoints,
        byte[]? propertyTable = null, ulong dataHeaderValue = 0, byte[]? afterChannels = null)
    {
        byte[] placeholder = Str("x");
        byte[] properties = propertyTable ?? PropertyTable(channels.Select(c => c.Name));

        using var data = new MemoryStream();
        using var dataWriter = new BinaryWriter(data);
        dataWriter.Write(dataHeaderValue);
        dataWriter.Write((ulong)storedPoints);
        int pointsStreamOffset = -1;
        foreach (var (name, stream) in channels)
        {
            dataWriter.Write((ulong)stream.Length);
            if (name == "Points") pointsStreamOffset = (int)data.Position;
            dataWriter.Write(stream);
        }

        if (afterChannels != null) dataWriter.Write(afterChannels);
        dataWriter.Flush();
        byte[] dataSection = data.ToArray();

        int tableEnd = HeaderSize + (3 * EntrySize);
        using var file = new MemoryStream();
        using var writer = new BinaryWriter(file);
        writer.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        writer.Write(1UL);
        writer.Write(3UL);
        long next = tableEnd;
        foreach (var (id, length) in new (uint, int)[] { (1, placeholder.Length), (2, properties.Length), (5, dataSection.Length) })
        {
            writer.Write(id);
            writer.Write((ulong)next);
            writer.Write((ulong)length);
            next += length;
        }

        int propertyOffset = tableEnd + placeholder.Length;
        int dataOffset = propertyOffset + properties.Length;
        writer.Write(placeholder);
        writer.Write(properties);
        writer.Write(dataSection);
        writer.Flush();
        return new PackedBin(file.ToArray(), propertyOffset, dataOffset, pointsStreamOffset < 0 ? -1 : dataOffset + pointsStreamOffset);
    }

    /// <summary>A stream that stores the data as literals.</summary>
    public static byte[] Literals(byte[] data) => SnappyBuilder.OfLiterals(data);

    /// <summary>A stream that stores the first <paramref name="period"/> bytes and then copies them, for periodic data.</summary>
    private static byte[] Runs(byte[] data, int period)
    {
        for (int i = period; i < data.Length; i++)
        {
            if (data[i] != data[i - period]) throw new ArgumentException("The data is not periodic.", nameof(data));
        }

        var builder = new SnappyBuilder().Literal(data.AsSpan(0, period));
        for (int done = period; done < data.Length; done += 64) builder.Copy(period, Math.Min(64, data.Length - done), 2);
        return builder.Build();
    }

    // ---- The tagged tree of the property table: strings, arrays and maps with the length in the low five bits ----------

    public static byte[] Str(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        return [(byte)(0x60 | utf8.Length), .. utf8];
    }

    public static byte[] Array(params byte[][] items) => [(byte)(0x80 | items.Length), .. items.SelectMany(i => i)];

    public static byte[] Map(params (string Key, byte[] Value)[] entries) =>
        [(byte)(0xA0 | entries.Length), .. entries.SelectMany(e => Str(e.Key).Concat(e.Value))];

    /// <summary>A property table that lists the given channel names in order.</summary>
    public static byte[] PropertyTable(IEnumerable<string> names) =>
        Map(("PropertyTable", Array([.. names.Select(name => Map(
            ("STDPropType", Map(("type", Str("int")), ("value", Str("0")))),
            ("name", Map(("type", Str("string")), ("value", Str(name))))))])));
}
