using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace PclIO.Tests;

/// <summary>
/// Builds a synthetic .rcs file from the values of its header, its sections and its leaves. The bytes are written from what
/// the reader decodes; no byte of them comes from a real scan.
/// </summary>
internal sealed class RcsScanBuilder
{
    public const int HeaderSize = 512;
    public const int LeafRecordSize = 368;
    public const int PointRecordSize = 16;

    /// <summary>Section type of the leaves section.</summary>
    public const uint LeavesSection = 2;

    /// <summary>Section type of the points section.</summary>
    public const uint PointsSection = 1;

    /// <summary>Largest offset a record can hold on an axis.</summary>
    public const uint MaxOffset = (1u << 18) - 1;

    /// <summary>Point count stated in the header.</summary>
    public long PointCount { get; set; }

    /// <summary>Translation stated in the header; every position of the file is relative to it.</summary>
    public (double X, double Y, double Z) Translation { get; set; }

    /// <summary>Scale stated in the header.</summary>
    public (double X, double Y, double Z) Scale { get; set; } = (1, 1, 1);

    /// <summary>Whether the header states that the points hold a colour.</summary>
    public bool Color { get; set; } = true;

    /// <summary>Whether the header states that the points hold an intensity.</summary>
    public bool Intensity { get; set; } = true;

    /// <summary>Whether the header states that the points can hold normals.</summary>
    public bool Normals { get; set; }

    /// <summary>Local bounds stated in the header; the octree bounds of the header are the same.</summary>
    public (double X, double Y, double Z) BoundsMin { get; set; } = (-10, -10, -10);

    /// <summary>Upper corner of <see cref="BoundsMin"/>.</summary>
    public (double X, double Y, double Z) BoundsMax { get; set; } = (10, 10, 10);

    /// <summary>The sections as they are laid out in the file, by section type.</summary>
    public List<uint> SectionOrder { get; set; } = [LeavesSection, PointsSection];

    /// <summary>The payload of every section that is neither the leaves nor the points section, by section type.</summary>
    public Dictionary<uint, byte[]> OtherSections { get; } = [];

    /// <summary>Bytes that follow the leaf blocks inside the points section.</summary>
    public byte[] PointsTail { get; set; } = [];

    /// <summary>The leaves of the file, in file order.</summary>
    public List<RcsLeafBuilder> Leaves { get; } = [];

    /// <summary>Adds a leaf whose cube starts at <paramref name="min"/> and has this edge length, holding these records.</summary>
    public RcsLeafBuilder AddLeaf((double X, double Y, double Z) min, double edge, params RcsSamplePoint[] records)
    {
        var leaf = new RcsLeafBuilder(min, edge);
        leaf.Records.AddRange(records);
        Leaves.Add(leaf);
        return leaf;
    }

    /// <summary>Writes the file.</summary>
    public byte[] Build()
    {
        var payloads = new Dictionary<uint, byte[]>
        {
            [LeavesSection] = BuildLeaves(),
            [PointsSection] = BuildPoints(),
        };

        // The header, then the sections in the stated order.
        var file = new List<byte>(HeaderSize);
        file.AddRange(new byte[HeaderSize]);
        var entries = new List<(uint Type, long Offset, long Size)>();
        foreach (uint type in SectionOrder)
        {
            if (!payloads.TryGetValue(type, out byte[]? payload) && !OtherSections.TryGetValue(type, out payload))
                throw new InvalidOperationException($"Section {type} has no payload.");
            entries.Add((type, file.Count, payload.Length));
            file.AddRange(payload);
        }

        byte[] bytes = [.. file];
        WriteHeader(bytes, entries);
        return bytes;
    }

    // The leaves section: the number of leaves, four unused bytes, then one record per leaf.
    private byte[] BuildLeaves()
    {
        var section = new byte[8 + (Leaves.Count * LeafRecordSize)];
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(0, 4), (uint)Leaves.Count);
        for (int i = 0; i < Leaves.Count; i++)
        {
            Span<byte> record = section.AsSpan(8 + (i * LeafRecordSize), LeafRecordSize);
            RcsLeafBuilder leaf = Leaves[i];
            WriteTriple(record, leaf.Min);
            WriteTriple(record[24..], Add(leaf.Min, leaf.Edge));
            WriteTriple(record[48..], leaf.Min);
            WriteTriple(record[72..], Add(leaf.Min, leaf.Edge));
            BinaryPrimitives.WriteUInt32LittleEndian(record[100..104], (uint)leaf.Records.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(record[104..108], leaf.StatedBlockSize);
        }
        return section;
    }

    // The points section: the block of every leaf (its index words, then its records), then the tail.
    private byte[] BuildPoints()
    {
        var section = new List<byte>();
        foreach (RcsLeafBuilder leaf in Leaves)
        {
            int blockStart = section.Count;
            section.AddRange(new byte[leaf.BytesInFile]);
            for (int i = 0; i < leaf.Records.Count; i++)
                leaf.Records[i].WriteTo(CollectionsMarshal.AsSpan(section)[(blockStart + leaf.IndexBytesInFile + (i * PointRecordSize))..]);
        }
        section.AddRange(PointsTail);
        return [.. section];
    }

    private void WriteHeader(Span<byte> header, List<(uint Type, long Offset, long Size)> entries)
    {
        // Container signature, then version 3.2.
        "ADOCT\0\0\0"u8.CopyTo(header[..8]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..16], 2);

        WriteTriple(header[0x10..], Translation);
        WriteTriple(header[0x40..], Scale);

        WriteTriple(header[0x70..], BoundsMin);
        WriteTriple(header[(0x70 + 24)..], BoundsMax);
        WriteTriple(header[0xA0..], BoundsMin);
        WriteTriple(header[(0xA0 + 24)..], BoundsMax);

        BinaryPrimitives.WriteUInt64LittleEndian(header[0xD8..0xE0], (ulong)PointCount);
        header[0xE0] = (byte)(Color ? 1 : 0);
        header[0xE1] = (byte)(Normals ? 1 : 0);
        header[0xE2] = (byte)(Intensity ? 1 : 0);
        "00000000-0000-0000-0000-000000000001"u8.CopyTo(header[0xE3..]);
        "LOCAL_GRID_SYSTEM"u8.CopyTo(header[0x1B9..]);

        BinaryPrimitives.WriteUInt32LittleEndian(header[0x129..0x12D], (uint)entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            Span<byte> entry = header[(0x12D + (i * 24))..];
            BinaryPrimitives.WriteUInt64LittleEndian(entry[..8], entries[i].Type);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[8..16], (ulong)entries[i].Offset);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[16..24], (ulong)entries[i].Size);
        }
    }

    private static void WriteTriple(Span<byte> target, (double X, double Y, double Z) value)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(target[..8], value.X);
        BinaryPrimitives.WriteDoubleLittleEndian(target[8..16], value.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(target[16..24], value.Z);
    }

    private static (double X, double Y, double Z) Add((double X, double Y, double Z) value, double offset) =>
        (value.X + offset, value.Y + offset, value.Z + offset);
}

/// <summary>One leaf of a synthetic .rcs file: its cube, its records, and what its record states about its block.</summary>
internal sealed class RcsLeafBuilder((double X, double Y, double Z) min, double edge)
{
    /// <summary>Lower corner of the cube of the leaf.</summary>
    public (double X, double Y, double Z) Min { get; } = min;

    /// <summary>Edge length of the cube of the leaf.</summary>
    public double Edge { get; } = edge;

    /// <summary>The point records of the leaf, in file order.</summary>
    public List<RcsSamplePoint> Records { get; } = [];

    /// <summary>Bytes of index words before the records; null for the four bytes every record has.</summary>
    public int? IndexBytes { get; set; }

    /// <summary>Block size the leaf record states; null for the size the block is written with.</summary>
    public uint? DeclaredBlockSize { get; set; }

    /// <summary>The index words the block is written with.</summary>
    public int IndexBytesInFile => IndexBytes ?? 4 * Records.Count;

    /// <summary>The bytes the block is written with: its index words, then its records.</summary>
    public int WrittenBlockSize => IndexBytesInFile + (Records.Count * RcsScanBuilder.PointRecordSize);

    /// <summary>The block size the leaf record states: the declared one, or the one the block is written with.</summary>
    public uint StatedBlockSize => DeclaredBlockSize ?? (uint)WrittenBlockSize;

    /// <summary>The bytes the block takes in the file: what it is written with, or more when it states a larger size.</summary>
    public int BytesInFile => (int)Math.Max(StatedBlockSize, WrittenBlockSize);
}

/// <summary>
/// One point record of a synthetic .rcs leaf, as the file holds it: the millimetre offsets from the corner of the leaf cube
/// on three 18-bit fields, the top byte of the same 64-bit word, and the bytes of the channels.
/// </summary>
public readonly record struct RcsSamplePoint(uint X, uint Y, uint Z, byte Intensity = 0, byte R = 0, byte G = 0, byte B = 0, ushort Normal = 0, byte Top = 0)
{
    /// <summary>Writes the 16 bytes of the record.</summary>
    public void WriteTo(Span<byte> record)
    {
        if (X > RcsScanBuilder.MaxOffset || Y > RcsScanBuilder.MaxOffset || Z > RcsScanBuilder.MaxOffset)
            throw new ArgumentOutOfRangeException(nameof(X), "An offset does not fit in its 18 bits.");

        BinaryPrimitives.WriteUInt64LittleEndian(record, X | ((ulong)Y << 18) | ((ulong)Z << 36) | ((ulong)Top << 56));
        record[8] = Intensity;
        record[9] = B;
        record[10] = G;
        record[11] = R;
        BinaryPrimitives.WriteUInt16LittleEndian(record[12..], Normal);
    }
}
