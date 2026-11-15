using System.Buffers.Binary;
using System.Text;

namespace PclIO.Tests;

/// <summary>Writes small compound files (OLE2) for the tests. Streams are always stored in regular sectors (the small-stream
/// cutoff is zero), so no mini stream is needed. The directory occupies the first sectors, then the stream data, then the FAT.</summary>
internal sealed class Ole2Builder
{
    public const uint EndOfChain = 0xFFFFFFFE;
    public const uint FreeSector = 0xFFFFFFFF;

    internal sealed class Node
    {
        public required string Name { get; init; }
        public byte Type { get; init; }
        public int Left { get; set; } = -1;
        public int Right { get; set; } = -1;
        public int Child { get; set; } = -1;
        public byte[] Data { get; init; } = [];
    }

    private readonly List<Node> _nodes = [new Node { Name = "Root Entry", Type = 5 }];

    /// <summary>Sector shift written to the header: 9 (512 bytes) or 12 (4096 bytes).</summary>
    public int SectorShift { get; init; } = 9;

    public IReadOnlyList<Node> Nodes => _nodes;

    private int SectorSize => 1 << SectorShift;

    /// <summary>File offset of a directory record; the directory is the first chain, starting at sector 0.</summary>
    public long DirectoryEntryOffset(int index) => SectorSize + (index * 128L);

    /// <summary>Adds a node to the list without linking it anywhere; the caller sets the sibling and child links.</summary>
    public int AddUnlinked(string name, byte type, byte[]? data = null)
    {
        _nodes.Add(new Node { Name = name, Type = type, Data = data ?? [] });
        return _nodes.Count - 1;
    }

    /// <summary>Adds a storage (type 1) or stream (type 2) as the last child of a parent.</summary>
    public int Add(int parent, string name, byte type, byte[]? data = null)
    {
        int index = AddUnlinked(name, type, data);
        Node p = _nodes[parent];
        if (p.Child < 0)
        {
            p.Child = index;
            return index;
        }

        int sibling = p.Child;
        while (_nodes[sibling].Right >= 0) sibling = _nodes[sibling].Right;
        _nodes[sibling].Right = index;
        return index;
    }

    public int AddStorage(int parent, string name) => Add(parent, name, 1);

    public int AddStream(int parent, string name, byte[] data) => Add(parent, name, 2, data);

    public byte[] Build()
    {
        int sectorSize = SectorSize;
        int perSector = sectorSize / 128;
        int dirSectors = (_nodes.Count + perSector - 1) / perSector;

        // Stream data follows the directory, one chain per stream.
        var startSectors = new uint[_nodes.Count];
        int next = dirSectors;
        for (int i = 0; i < _nodes.Count; i++)
        {
            int count = (_nodes[i].Data.Length + sectorSize - 1) / sectorSize;
            startSectors[i] = count == 0 ? EndOfChain : (uint)next;
            next += count;
        }

        int contentSectors = next;
        int fatPerSector = sectorSize / 4;
        int fatSectors = 1;
        while (fatSectors * fatPerSector < contentSectors + fatSectors) fatSectors++;
        if (fatSectors > 109) throw new InvalidOperationException("The test builder does not write a DIFAT.");

        int totalSectors = contentSectors + fatSectors;
        uint[] fat = new uint[fatSectors * fatPerSector];
        Array.Fill(fat, FreeSector);
        for (int s = 0; s < dirSectors; s++) fat[s] = s == dirSectors - 1 ? EndOfChain : (uint)(s + 1);
        for (int i = 0; i < _nodes.Count; i++)
        {
            int count = (_nodes[i].Data.Length + sectorSize - 1) / sectorSize;
            for (int k = 0; k < count; k++)
                fat[startSectors[i] + k] = k == count - 1 ? EndOfChain : (uint)(startSectors[i] + k + 1);
        }
        for (int f = 0; f < fatSectors; f++) fat[contentSectors + f] = 0xFFFFFFFD;

        byte[] file = new byte[(1L + totalSectors) * sectorSize];
        Span<byte> header = file.AsSpan(0, 512);
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(0x18, 2), 0x3E);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(0x1A, 2), (ushort)(SectorShift == 9 ? 3 : 4));
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(0x1C, 2), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(0x1E, 2), (ushort)SectorShift);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(0x20, 2), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(0x2C, 4), (uint)fatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(0x30, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(0x38, 4), 0); // every stream uses regular sectors
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(0x3C, 4), EndOfChain);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(0x44, 4), EndOfChain);
        for (int i = 0; i < 109; i++)
        {
            uint id = i < fatSectors ? (uint)(contentSectors + i) : FreeSector;
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(0x4C + (i * 4), 4), id);
        }

        for (int i = 0; i < _nodes.Count; i++)
        {
            Span<byte> e = file.AsSpan((int)DirectoryEntryOffset(i), 128);
            Node n = _nodes[i];
            byte[] name = Encoding.Unicode.GetBytes(n.Name);
            name.CopyTo(e);
            BinaryPrimitives.WriteUInt16LittleEndian(e.Slice(0x40, 2), (ushort)(name.Length + 2));
            e[0x42] = n.Type;
            e[0x43] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(e.Slice(0x44, 4), unchecked((uint)n.Left));
            BinaryPrimitives.WriteUInt32LittleEndian(e.Slice(0x48, 4), unchecked((uint)n.Right));
            BinaryPrimitives.WriteUInt32LittleEndian(e.Slice(0x4C, 4), unchecked((uint)n.Child));
            BinaryPrimitives.WriteUInt32LittleEndian(e.Slice(0x74, 4), startSectors[i]);
            BinaryPrimitives.WriteUInt64LittleEndian(e.Slice(0x78, 8), (ulong)n.Data.Length);

            if (n.Data.Length > 0)
                n.Data.CopyTo(file.AsSpan((int)((1L + startSectors[i]) * sectorSize)));
        }

        for (int f = 0; f < fatSectors; f++)
        {
            for (int k = 0; k < fatPerSector; k++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    file.AsSpan((int)((1L + contentSectors + f) * sectorSize) + (k * 4), 4), fat[(f * fatPerSector) + k]);
            }
        }

        return file;
    }

    /// <summary>A directory of <paramref name="count"/> streams chained through their left-sibling links: the deepest possible tree.</summary>
    public static Ole2Builder WithLeftChain(int count, int sectorShift)
    {
        var builder = new Ole2Builder { SectorShift = sectorShift };
        for (int i = 1; i <= count; i++)
        {
            int index = builder.AddUnlinked("s" + i, 2);
            builder._nodes[index].Left = index - 1 == 0 ? -1 : index - 1;
        }
        builder._nodes[0].Child = count;
        return builder;
    }
}
