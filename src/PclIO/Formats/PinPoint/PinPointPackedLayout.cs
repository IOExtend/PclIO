using System.Text;
using PclIO.Internal;

namespace PclIO.Formats.PinPoint;

/// <summary>
/// Reads the layout of a point data file that is a list of numbered sections: a header (an 8-byte value that is not
/// interpreted, the number 1, the section count), one 20-byte entry per section (id, offset, length; the sections follow
/// each other without gaps up to the end of the file), a property table (section 2) that names the channels in order, and
/// a data section (section 5): two 64-bit values (0 and the point count) followed, for each named channel, by its stream
/// length and the stream, which is Snappy-compressed.
/// </summary>
internal static class PinPointPackedLayout
{
    private const int HeaderSize = 24;
    private const int EntrySize = 20;
    private const int MaxSections = 16;
    private const long MaxPropertyTableBytes = 1L << 20;
    private const int MaxNodeDepth = 8;
    private const uint PropertyTableSection = 2;
    private const uint DataSection = 5;
    private const int DataHeaderSize = 16;

    /// <summary>Returns the channel layout, or null when the file does not have this section structure.</summary>
    /// <exception cref="InvalidDataException">The file has the section structure but is damaged.</exception>
    /// <exception cref="NotSupportedException">The file has the section structure with a feature that is not supported.</exception>
    public static PinPointDocument.ChannelTable? TryRead(FileStream fs)
    {
        if (fs.Length < HeaderSize) return null;

        Span<byte> header = stackalloc byte[HeaderSize];
        fs.Position = 0;
        fs.ReadExactly(header);
        if (Bytes.ReadUInt64LE(header[8..]) != 1) return null;
        ulong sectionCount = Bytes.ReadUInt64LE(header[16..]);
        if (sectionCount == 0 || sectionCount > MaxSections) return null;

        int count = (int)sectionCount;
        long tableEnd = HeaderSize + ((long)count * EntrySize);
        if (fs.Length < tableEnd) throw FormatError.Truncated("the section table");

        byte[] entries = new byte[count * EntrySize];
        fs.ReadExactly(entries);

        long next = tableEnd;
        long propertyOffset = -1, propertyLength = 0, dataOffset = -1, dataLength = 0;
        for (int i = 0; i < count; i++)
        {
            var entry = entries.AsSpan(i * EntrySize, EntrySize);
            uint id = Bytes.ReadUInt32LE(entry);
            ulong offset = Bytes.ReadUInt64LE(entry[4..]);
            ulong length = Bytes.ReadUInt64LE(entry[12..]);
            if (offset != (ulong)next) throw FormatError.Corrupted("the sections of a point data file are not contiguous");
            if (length > (ulong)(fs.Length - next)) throw FormatError.Truncated("section data");

            next += (long)length;
            if (id == PropertyTableSection)
            {
                if (propertyOffset >= 0) throw FormatError.Corrupted("duplicate property table");
                propertyOffset = (long)offset;
                propertyLength = (long)length;
            }
            else if (id == DataSection)
            {
                if (dataOffset >= 0) throw FormatError.Corrupted("duplicate data section");
                dataOffset = (long)offset;
                dataLength = (long)length;
            }
        }

        if (next != fs.Length) throw FormatError.Corrupted("bytes after the last section of a point data file");
        if (propertyOffset < 0 || dataOffset < 0) throw FormatError.Corrupted("the property table or the data section is missing");
        if (propertyLength > MaxPropertyTableBytes) throw FormatError.Corrupted("the property table is unreasonably large");

        byte[] propertyBytes = new byte[propertyLength];
        fs.Position = propertyOffset;
        fs.ReadExactly(propertyBytes);
        var names = ReadChannelNames(propertyBytes);

        return ReadChannels(fs, names, dataOffset, dataLength);
    }

    private static PinPointDocument.ChannelTable ReadChannels(FileStream fs, List<string> names, long dataOffset, long dataLength)
    {
        if (dataLength < DataHeaderSize) throw FormatError.Truncated("the data section header");

        Span<byte> head = stackalloc byte[DataHeaderSize];
        fs.Position = dataOffset;
        fs.ReadExactly(head);
        if (Bytes.ReadUInt64LE(head) != 0) throw FormatError.Unsupported("a point data section with a non-zero header value");
        ulong declaredPoints = Bytes.ReadUInt64LE(head[8..]);
        if (declaredPoints > (ulong)(long.MaxValue / 16)) throw FormatError.Corrupted("the point count is unreasonably large");
        long points = (long)declaredPoints;

        var table = new PinPointDocument.ChannelTable { Packed = true, PackedPoints = points };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long position = DataHeaderSize;
        Span<byte> lengthBytes = stackalloc byte[8];
        foreach (string name in names)
        {
            if (dataLength - position < 8) throw FormatError.Truncated($"the stream of channel '{name}'");

            fs.Position = dataOffset + position;
            fs.ReadExactly(lengthBytes);
            ulong streamLength = Bytes.ReadUInt64LE(lengthBytes);
            position += 8;
            if (streamLength > (ulong)(dataLength - position)) throw FormatError.Truncated($"the stream of channel '{name}'");

            long streamOffset = dataOffset + position;
            position += (long)streamLength;

            // Each channel with a meaning here holds a fixed number of bytes per point; the stream says how many it decodes to.
            int elementSize = name switch { "Points" => 12, "RGB8" => 3, "Intensities8" => 1, _ => 0 };
            if (elementSize == 0) continue;
            if (!seen.Add(name)) throw FormatError.Corrupted($"channel '{name}' is listed twice");

            fs.Position = streamOffset;
            long decoded = SnappyReader.ReadUncompressedLength(fs, (long)streamLength);
            if (decoded != points * elementSize)
                throw FormatError.Corrupted($"channel '{name}' holds {decoded} bytes, not {elementSize} per point for {points} points");

            if (name == "Points") (table.PointsOffset, table.PointsLength) = (streamOffset, (long)streamLength);
            else if (name == "RGB8") (table.ColorsOffset, table.ColorsLength) = (streamOffset, (long)streamLength);
            else (table.IntensitiesOffset, table.IntensitiesLength) = (streamOffset, (long)streamLength);
        }

        if (position != dataLength) throw FormatError.Corrupted("bytes after the last channel of a point data section");
        if (table.PointsLength == 0 && points > 0) throw FormatError.Corrupted("the point data file has no Points channel");
        return table;
    }

    // The property table is a tree of strings, arrays and maps: a map with one entry, "PropertyTable", holding an array of
    // maps, each with a "name" entry that is itself a map whose "value" entry is the channel name.
    private static List<string> ReadChannelNames(ReadOnlySpan<byte> section)
    {
        int position = 0;
        var root = ReadNode(section, ref position, 0);
        if (position != section.Length) throw FormatError.Corrupted("bytes after the property table");

        if (root is not Dictionary<string, object?> map || !map.TryGetValue("PropertyTable", out object? table) || table is not List<object?> properties)
            throw FormatError.Corrupted("the property table has no list of properties");

        var names = new List<string>(properties.Count);
        foreach (object? property in properties)
        {
            if (property is not Dictionary<string, object?> p || !p.TryGetValue("name", out object? nameNode)
                || nameNode is not Dictionary<string, object?> nameMap || !nameMap.TryGetValue("value", out object? value)
                || value is not string name)
                throw FormatError.Corrupted("a property of the property table has no name");

            names.Add(name);
        }

        return names;
    }

    // Tag byte: the top three bits give the kind (0x60 string, 0x80 array, 0xA0 map), the low five bits the length or count.
    private static object? ReadNode(ReadOnlySpan<byte> section, ref int position, int depth)
    {
        if (depth > MaxNodeDepth) throw FormatError.Corrupted("the property table is nested too deeply");
        if (position >= section.Length) throw FormatError.Truncated("a property table value");

        byte tag = section[position++];
        int count = tag & 0x1F;
        switch (tag & 0xE0)
        {
            case 0x60:
                if (count > section.Length - position) throw FormatError.Truncated("a property table string");
                string text = Encoding.UTF8.GetString(section.Slice(position, count));
                position += count;
                return text;

            case 0x80:
                var list = new List<object?>(count);
                for (int i = 0; i < count; i++) list.Add(ReadNode(section, ref position, depth + 1));
                return list;

            case 0xA0:
                var map = new Dictionary<string, object?>(count, StringComparer.Ordinal);
                for (int i = 0; i < count; i++)
                {
                    if (ReadNode(section, ref position, depth + 1) is not string key)
                        throw FormatError.Corrupted("a property table key is not a string");
                    map[key] = ReadNode(section, ref position, depth + 1);
                }

                return map;

            default:
                throw FormatError.Unsupported($"property table element 0x{tag:X2}");
        }
    }
}
