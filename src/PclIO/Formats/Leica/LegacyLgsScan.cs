using System.Globalization;
using System.Numerics;
using System.Text;
using PclIO.Internal;

namespace PclIO.Formats.Leica;

internal sealed class LegacyLgsScan : PclScan
{
    private static ReadOnlySpan<byte> Signature => "UDS4\u0007\0\0\0"u8;
    private const int MaskSize = 1024;
    private const int MaskSearchBytes = 16 * 1024 * 1024;
    private const int MaxMetadataBytes = 64 * 1024;
    private const int MaxBlocks = 1_000_000;
    private const int MaxGeometryBytes = 512 * 1024 * 1024;
    private const int AttributePagePoints = 4096;

    private readonly string _path;
    private readonly long _offset;
    private readonly long _length;
    private readonly byte[] _mask;
    private readonly Header _header;

    private sealed record Header(int Count, int Depth, PclVector3 Origin, PclVector3 Size,
        PclBounds Bounds, int[] Widths, int Color, int Intensity, int Blocks, int Groups, int DirectoryEnd);
    private readonly record struct Block(int FirstRecord, int Records, long GeometryOffset, int GeometryLength,
        long ColorOffset, long IntensityOffset, uint Children);

    private LegacyLgsScan(string path, long offset, long length, byte[] mask, Header header)
    {
        (_path, _offset, _length, _mask, _header) = (path, offset, length, mask, header);
        Name = Path.GetFileNameWithoutExtension(path);
        Bounds = header.Bounds;
    }

    public override string Id => "unified-cloud";
    public override string Name { get; }
    public override long PointCount => _header.Count;
    public override bool HasColors => _header.Color >= 0;
    public override bool HasIntensities => _header.Intensity >= 0;

    internal static LegacyLgsScan? TryOpen(FileStream stream, LgsxEntry entry)
    {
        long offset = (long)entry.Offset;
        long length = (long)entry.Size;
        int prefixLength = (int)Math.Min(length, MaskSearchBytes);
        if (prefixLength < 156) return null;
        var prefix = new byte[prefixLength];
        stream.Position = offset;
        stream.ReadExactly(prefix);
        byte[]? mask = FindMask(prefix);
        if (mask == null) return null;

        Xor(prefix.AsSpan(0, 156), mask, 0);
        uint metadataSize = Bytes.ReadUInt32LE(prefix.AsSpan(8));
        if (metadataSize is < 148 or > MaxMetadataBytes || metadataSize + 8 > prefix.Length - 32)
            throw FormatError.Corrupted("invalid point metadata size.");
        int directory = (int)metadataSize + 8;
        Xor(prefix.AsSpan(156, directory + 32 - 156), mask, 156);
        Header header = ParseHeader(prefix, directory, length);
        return new LegacyLgsScan(stream.Name, offset, length, mask, header);
    }

    private static byte[]? FindMask(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(Signature)) return new byte[MaskSize];
        Span<byte> tag = stackalloc byte[8];
        for (int i = 0; i < tag.Length; i++) tag[i] = (byte)(data[i] ^ Signature[i]);
        ReadOnlySpan<byte> match = [];
        // Two identical aligned padding sectors disambiguate the mask from a partial zero run.
        for (int offset = MaskSize; offset <= data.Length - 2 * MaskSize; offset += MaskSize)
        {
            var sector = data.Slice(offset, MaskSize);
            if (!sector.StartsWith(tag) || !sector.SequenceEqual(data.Slice(offset + MaskSize, MaskSize))) continue;
            if (!match.IsEmpty && !sector.SequenceEqual(match)) return null;
            match = sector;
        }
        return match.IsEmpty ? null : match.ToArray();
    }

    private static Header ParseHeader(ReadOnlySpan<byte> data, int directory, long length)
    {
        double edge = Bytes.ReadDoubleLE(data[12..]);
        uint levels = Bytes.ReadUInt32LE(data[28..]);
        PclVector3 scale = ReadVector(data[32..]), origin = ReadVector(data[56..]);
        var size = scale * edge;
        if (!double.IsFinite(edge) || edge <= 0 || levels is < 2 or > 30 ||
            !Finite(origin) || !Finite(size) || !Finite(origin + size) ||
            size.X <= 0 || size.Y <= 0 || size.Z <= 0)
            throw FormatError.Corrupted("invalid point grid transform.");

        uint pairs = Bytes.ReadUInt32LE(data[152..]);
        if (pairs > 256) throw FormatError.Corrupted("too many point metadata fields.");
        var metadata = data[156..directory];
        int count = 0;
        string? modelAttributes = null;
        for (int i = 0; i < pairs; i++)
        {
            string key = ReadString(ref metadata);
            string value = ReadString(ref metadata);
            if (key == "UniquePointCount")
                _ = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out count);
            else if (key == "ModelAttributes")
                modelAttributes = value;
        }
        if (!metadata.IsEmpty || count <= 0 || modelAttributes == null)
            throw FormatError.Corrupted("missing point count or attribute metadata.");

        string[] attributes = modelAttributes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var table = data[directory..];
        uint blocks = Bytes.ReadUInt32LE(table[24..]), groups = Bytes.ReadUInt32LE(table[28..]);
        if (Bytes.ReadUInt32LE(table) != 2 || Bytes.ReadUInt32LE(table[4..]) != 256 ||
            attributes.Length is < 1 or > 64 || Bytes.ReadUInt32LE(table[12..]) != attributes.Length)
            throw FormatError.Unsupported("unrecognized Leica LGS point directory.");
        if (groups == 0 || groups > blocks || blocks > MaxBlocks || blocks > count)
            throw FormatError.Corrupted("invalid point block count.");

        var widths = new int[attributes.Length];
        for (int i = 0; i < widths.Length; i++)
        {
            widths[i] = attributes[i][(attributes[i].LastIndexOf(':') + 1)..] switch
            {
                "u8" => 1,
                "u16" => 2,
                "u32" => 4,
                _ => throw FormatError.Unsupported("unrecognized Leica LGS point attribute type."),
            };
        }
        if ((long)count * widths.Sum() > length)
            throw FormatError.Corrupted("the declared points exceed the point section.");
        int color = Array.IndexOf(attributes, "udRGB:u32");
        int intensity = Array.IndexOf(attributes, "udIntensity:u16");
        int directoryEnd = directory + 32 + 8 * widths.Length;
        var center = ReadVector(data[104..]);
        var half = ReadVector(data[128..]);
        var min = ToWorld(center - half, origin, size);
        var max = ToWorld(center + half, origin, size);
        PclBounds bounds = Finite(min) && Finite(max) ? new(min, max) : PclBounds.Empty;
        return new(count, (int)levels - 1, origin, size, bounds, widths, color, intensity, (int)blocks, (int)groups, directoryEnd);
    }

    private static string ReadString(ref ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2) throw FormatError.Truncated("point metadata");
        int size = Bytes.ReadUInt16LE(bytes);
        if (size == 0 || size > bytes.Length - 2 || bytes[size + 1] != 0)
            throw FormatError.Corrupted("invalid point metadata string.");
        string value = Encoding.ASCII.GetString(bytes.Slice(2, size - 1));
        bytes = bytes[(2 + size)..];
        return value;
    }

    private static PclVector3 ReadVector(ReadOnlySpan<byte> data) =>
        new(Bytes.ReadDoubleLE(data), Bytes.ReadDoubleLE(data[8..]), Bytes.ReadDoubleLE(data[16..]));

    private static bool Finite(PclVector3 p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);

    private static PclVector3 ToWorld(PclVector3 position, PclVector3 origin, PclVector3 size) =>
        new(origin.X + size.X * position.X, origin.Y + size.Y * position.Y, origin.Z + size.Z * position.Z);

    private static void Xor(Span<byte> data, byte[] mask, long offset)
    {
        int phase = (int)(offset & (MaskSize - 1));
        int first = Math.Min(data.Length, MaskSize - phase);
        for (int i = 0; i < first; i++) data[i] ^= mask[phase + i];
        for (int start = first; start < data.Length;)
        {
            int n = Math.Min(MaskSize, data.Length - start);
            int i = 0;
            for (; i + Vector<byte>.Count <= n; i += Vector<byte>.Count)
            {
                var decoded = new Vector<byte>(data[(start + i)..]) ^ new Vector<byte>(mask.AsSpan(i));
                decoded.CopyTo(data[(start + i)..]);
            }
            for (; i < n; i++) data[start + i] ^= mask[i];
            start += n;
        }
    }

    private void Read(FileStream stream, long offset, Span<byte> data)
    {
        if (offset < 0 || offset > _length - data.Length)
            throw FormatError.Truncated("legacy point section");
        stream.Position = _offset + offset;
        stream.ReadExactly(data);
        Xor(data, _mask, offset);
    }

    private Block[] ReadBlocks(FileStream stream, out int geometryBytes)
    {
        int descriptorSize = (2 + _header.Widths.Length) * sizeof(uint);
        var descriptions = new byte[checked(_header.Blocks * descriptorSize)];
        var known = new bool[_header.Blocks];
        var blockOffsets = new long[_header.Blocks];
        var blocks = new Block[_header.Blocks];
        long position = _header.DirectoryEnd;
        Read(stream, position, descriptions.AsSpan(0, descriptorSize));
        position += descriptorSize;
        known[0] = true;
        long blocksStart = position;
        int firstRecord = 0, groups = 0;
        geometryBytes = 0;
        Span<byte> groupHeader = stackalloc byte[20];
        Span<byte> descriptorBuffer = stackalloc byte[264];
        var newDescriptor = descriptorBuffer[..descriptorSize];

        for (int i = 0; i < blocks.Length; i++)
        {
            if (!known[i] || (blockOffsets[i] != 0 && blockOffsets[i] != position))
                throw FormatError.Corrupted("missing or inconsistent point block entry.");
            var description = descriptions.AsSpan(i * descriptorSize, descriptorSize);
            long end = position + Bytes.ReadUInt32LE(description);
            int records = CountRecords(description);
            long attributes = (long)records * _header.Widths.Sum();
            long geometryEnd = end - attributes;
            if (records > _header.Count - firstRecord || geometryEnd < position || end > _length)
                throw FormatError.Corrupted("invalid point block size.");

            uint children = 0;
            if (geometryEnd != position)
            {
                if (geometryEnd - position < groupHeader.Length) throw FormatError.Truncated("point geometry header");
                Read(stream, position, groupHeader);
                ulong nextOffset = Bytes.ReadUInt64LE(groupHeader);
                uint next = Bytes.ReadUInt32LE(groupHeader[8..]);
                children = Bytes.ReadUInt32LE(groupHeader[12..]);
                uint flags = Bytes.ReadUInt32LE(groupHeader[16..]);
                int n = (int)(flags & 0xffff);
                position += groupHeader.Length;
                if (n == 0 || next > blocks.Length - n || nextOffset > (ulong)(_length - blocksStart) ||
                    (long)n * descriptorSize > geometryEnd - position)
                    throw FormatError.Corrupted("invalid child block range.");
                long childOffset = blocksStart + (long)nextOffset;
                long childRecords = 0;
                for (int j = (int)next; j < (int)next + n; j++)
                {
                    Read(stream, position, newDescriptor);
                    var target = descriptions.AsSpan(j * descriptorSize, descriptorSize);
                    if ((known[j] && !newDescriptor.SequenceEqual(target)) ||
                        (blockOffsets[j] != 0 && blockOffsets[j] != childOffset))
                        throw FormatError.Corrupted("contradictory point block entries.");
                    newDescriptor.CopyTo(target);
                    known[j] = true;
                    blockOffsets[j] = childOffset;
                    childOffset += Bytes.ReadUInt32LE(newDescriptor);
                    childRecords += CountRecords(newDescriptor);
                    position += descriptorSize;
                }
                if (childRecords != children - (geometryEnd - position) + ((flags & 0x10000) != 0 ? 1 : 0))
                    throw FormatError.Corrupted("point attributes do not match their geometry group.");
                groups++;
            }

            long geometryLength = geometryEnd - position;
            if (geometryLength > MaxGeometryBytes - geometryBytes)
                throw FormatError.Unsupported("the Leica LGS point grid exceeds the supported size.");
            geometryBytes += (int)geometryLength;
            blocks[i] = new(firstRecord, records, position, (int)geometryLength,
                AttributeOffset(geometryEnd, records, _header.Color),
                AttributeOffset(geometryEnd, records, _header.Intensity), children);
            firstRecord += records;
            position = end;
        }
        if (groups != _header.Groups || firstRecord != _header.Count)
            throw FormatError.Corrupted("the point directory disagrees with its declared counts.");
        return blocks;
    }

    private int CountRecords(ReadOnlySpan<byte> descriptor)
    {
        uint bytes = Bytes.ReadUInt32LE(descriptor[8..]);
        int width = _header.Widths[0];
        if (bytes % width != 0 || bytes / width > int.MaxValue)
            throw FormatError.Corrupted("invalid point attribute length.");
        int records = (int)(bytes / width);
        for (int i = 1; i < _header.Widths.Length; i++)
            if (Bytes.ReadUInt32LE(descriptor[(8 + 4 * i)..]) != (long)records * _header.Widths[i])
                throw FormatError.Corrupted("inconsistent point attribute lengths.");
        return records;
    }

    private long AttributeOffset(long start, int records, int channel)
    {
        if (channel < 0) return -1;
        for (int i = 0; i < channel; i++) start += (long)records * _header.Widths[i];
        return start;
    }

    private (int[] Start, int[] Count) ReadLevels(FileStream stream, Block[] blocks, byte[] masks)
    {
        int offset = 0;
        foreach (var block in blocks)
        {
            if (block.GeometryLength == 0) continue;
            var bytes = masks.AsSpan(offset, block.GeometryLength);
            Read(stream, block.GeometryOffset, bytes);
            long children = 0;
            foreach (byte value in bytes) children += BitOperations.PopCount(value);
            if (children != block.Children)
                throw FormatError.Corrupted("point occupancy disagrees with a geometry group.");
            offset += bytes.Length;
        }
        var starts = new int[_header.Depth];
        var counts = new int[_header.Depth];
        offset = 0;
        int count = 1;
        for (int depth = 0; depth < starts.Length; depth++)
        {
            if (count == 0 || count > masks.Length - offset)
                throw FormatError.Corrupted("the point grid is missing an occupancy level.");
            starts[depth] = offset;
            counts[depth] = count;
            long children = 0;
            foreach (byte value in masks.AsSpan(offset, count))
            {
                if (value == 0) throw FormatError.Corrupted("an internal voxel has no children.");
                children += BitOperations.PopCount(value);
            }
            if (children > _header.Count)
                throw FormatError.Corrupted("an occupancy level exceeds the declared point count.");
            offset += count;
            count = (int)children;
        }
        if (offset != masks.Length || count != _header.Count)
            throw FormatError.Corrupted("the point grid disagrees with its declared leaf count.");
        return (starts, counts);
    }

    public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null)
    {
        options ??= PclReadOptions.Default;
        if (options.MaxPoints == 0) yield break;
        using var stream = File.OpenRead(_path);
        var blocks = ReadBlocks(stream, out int geometryBytes);
        var masks = new byte[geometryBytes];
        var (positions, nextRecords) = ReadLevels(stream, blocks, masks);
        var nodes = new Node[_header.Depth];
        var pages = new AttributePage[_header.Depth + 1];
        int depth = 0, index = 0, skip = 0;
        ulong yielded = 0;
        double resolution = 1.0 / (1 << _header.Depth);
        nodes[0].Remaining = masks[positions[0]++];
        nodes[0].First = true;

        // Visiting each level in the same order as its masks avoids holding an entire voxel frontier.
        while (depth >= 0)
        {
            Node node = nodes[depth];
            if (node.Remaining == 0)
            {
                depth--;
                continue;
            }
            int slot = BitOperations.TrailingZeroCount(node.Remaining);
            nodes[depth].Remaining &= node.Remaining - 1;
            nodes[depth].First = false;
            int x = (node.X << 1) | (slot & 1);
            int y = (node.Y << 1) | ((slot >> 1) & 1);
            int z = (node.Z << 1) | (slot >> 2);
            // The first occupied child retains the parent attributes.
            int record = node.First ? node.Record : nextRecords[depth]++;
            int recordDepth = node.First ? node.RecordDepth : depth + 1;

            if (depth + 1 < nodes.Length)
            {
                depth++;
                nodes[depth] = new Node(x, y, z, masks[positions[depth]++], true, record, recordDepth);
                continue;
            }
            index++;
            if (skip-- > 0) continue;
            skip = options.SubsampleStep - 1;
            var position = ToWorld(new((x + .5) * resolution, (y + .5) * resolution, (z + .5) * resolution),
                _header.Origin, _header.Size);
            var (color, intensity) = ReadAttributes(stream, blocks, ref pages[recordDepth], record);
            yield return new PclPoint(position, color, intensity, Index: yielded++);
            if (yielded >= (ulong)(options.MaxPoints ?? long.MaxValue)) yield break;
        }
        if (index != _header.Count) throw FormatError.Corrupted("point traversal ended early.");
    }

    private record struct Node(int X, int Y, int Z, int Remaining, bool First, int Record, int RecordDepth);

    private struct AttributePage
    {
        internal int Start, End, Block;
        internal byte[]? Color, Intensity;
    }

    private (PclColor?, float?) ReadAttributes(FileStream stream, Block[] blocks, ref AttributePage page, int record)
    {
        if (!HasColors && !HasIntensities) return (null, null);
        if (record < page.Start || record >= page.End)
        {
            int low = page.Block, high = blocks.Length - 1;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (record < blocks[mid].FirstRecord + blocks[mid].Records) high = mid;
                else low = mid + 1;
            }
            var block = blocks[low];
            int within = record - block.FirstRecord;
            int begin = within / AttributePagePoints * AttributePagePoints;
            int count = Math.Min(AttributePagePoints, block.Records - begin);
            if (HasColors)
            {
                page.Color ??= new byte[4 * AttributePagePoints];
                Read(stream, block.ColorOffset + 4L * begin, page.Color.AsSpan(0, 4 * count));
            }
            if (HasIntensities)
            {
                page.Intensity ??= new byte[2 * AttributePagePoints];
                Read(stream, block.IntensityOffset + 2L * begin, page.Intensity.AsSpan(0, 2 * count));
            }
            (page.Start, page.End, page.Block) = (block.FirstRecord + begin, block.FirstRecord + begin + count, low);
        }
        int index = record - page.Start;
        PclColor? color = page.Color is { } bgra ? new PclColor(bgra[4 * index + 2], bgra[4 * index + 1], bgra[4 * index]) : null;
        float? intensity = page.Intensity is { } shorts ? Bytes.ReadUInt16LE(shorts.AsSpan(2 * index)) / 65535.0f : null;
        return (color, intensity);
    }
}
