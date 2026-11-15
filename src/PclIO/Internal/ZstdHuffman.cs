using System.Numerics;

namespace PclIO.Internal;

/// <summary>The Huffman decoding table for Zstandard literals (RFC 8878 section 4.2). It is read from a tree description and
/// stays valid until the next description replaces it, because a later block may reuse it.</summary>
internal sealed class ZstdHuffmanTable
{
    private const int MaxBits = 11;

    private readonly ZstdFseTable _weightTable = new(6);
    private readonly byte[] _weights = new byte[257];
    private readonly ushort[] _table = new ushort[1 << MaxBits]; // indexed by the next MaxBits bits; (symbol << 4) | codeLength
    private int _tableBits;

    /// <summary>False until a tree description has been read.</summary>
    public bool IsValid { get; private set; }

    /// <summary>Forgets the table, as a new frame must not reuse the tree of an earlier one.</summary>
    public void Invalidate() => IsValid = false;

    /// <summary>Reads a tree description from the start of <paramref name="source"/>.</summary>
    /// <returns>The number of bytes the description occupied.</returns>
    public int Read(ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty) throw FormatError.Truncated("a Zstandard Huffman tree description");
        int header = source[0];
        int weightCount;
        int consumed;
        if (header >= 128)
        {
            // Direct form: two 4-bit weights per byte, the first one in the high half.
            weightCount = header - 127;
            consumed = 1 + (weightCount + 1) / 2;
            if (consumed > source.Length) throw FormatError.Truncated("a Zstandard Huffman tree description");
            for (int i = 0; i < weightCount; i++)
            {
                byte packed = source[1 + i / 2];
                _weights[i] = (byte)((i & 1) == 0 ? packed >> 4 : packed & 0xF);
            }
        }
        else
        {
            consumed = 1 + header;
            if (header == 0 || consumed > source.Length) throw FormatError.Corrupted("Zstandard Huffman tree description has an invalid size.");
            ReadOnlySpan<byte> compressed = source.Slice(1, header);
            int tableBytes = _weightTable.Read(compressed, 6, MaxBits);
            weightCount = DecodeWeights(compressed[tableBytes..]);
        }

        Build(weightCount);
        return consumed;
    }

    /// <summary>Decodes exactly <paramref name="destination"/>.Length symbols from one backward bitstream.</summary>
    public void DecodeStream(ReadOnlySpan<byte> stream, Span<byte> destination)
    {
        var bits = new ZstdBackwardBits(stream);
        ushort[] table = _table;
        int tableBits = _tableBits;
        for (int i = 0; i < destination.Length; i++)
        {
            ushort entry = table[bits.Peek(tableBits)];
            destination[i] = (byte)(entry >> 4);
            bits.Skip(entry & 0xF);
        }

        if (!bits.IsExhausted) throw FormatError.Corrupted("Zstandard Huffman stream was not consumed exactly.");
    }

    /// <summary>Decodes the weights with two interleaved FSE states that share one table, until the stream runs dry.</summary>
    private int DecodeWeights(ReadOnlySpan<byte> stream)
    {
        var bits = new ZstdBackwardBits(stream);
        uint[] entries = _weightTable.Entries;
        int accuracyLog = _weightTable.AccuracyLog;
        uint state1 = bits.Read(accuracyLog);
        uint state2 = bits.Read(accuracyLog);
        if (bits.Overflowed) throw FormatError.Truncated("Zstandard Huffman weights");

        int count = 0;
        while (true)
        {
            // Every pass adds at most two weights, and a tree has at most 255 listed ones.
            if (count >= 255) throw FormatError.Corrupted("Zstandard Huffman tree lists too many weights.");

            uint entry = entries[state1];
            _weights[count++] = (byte)ZstdFseTable.SymbolOf(entry);
            state1 = ZstdFseTable.BaselineOf(entry) + bits.Read(ZstdFseTable.BitsOf(entry));
            if (bits.Overflowed)
            {
                _weights[count++] = (byte)ZstdFseTable.SymbolOf(entries[state2]);
                break;
            }

            entry = entries[state2];
            _weights[count++] = (byte)ZstdFseTable.SymbolOf(entry);
            state2 = ZstdFseTable.BaselineOf(entry) + bits.Read(ZstdFseTable.BitsOf(entry));
            if (bits.Overflowed)
            {
                _weights[count++] = (byte)ZstdFseTable.SymbolOf(entries[state1]);
                break;
            }
        }

        if (count > 255) throw FormatError.Corrupted("Zstandard Huffman tree lists too many weights.");
        return count;
    }

    /// <summary>Turns the listed weights plus the implied last one into a lookup table of codes (RFC 8878 section 4.2.1).</summary>
    private void Build(int listedCount)
    {
        // The listed weights add up to some sum of 2^(weight-1); the last symbol's weight completes it to a power of two.
        int sum = 0;
        for (int i = 0; i < listedCount; i++)
        {
            int weight = _weights[i];
            if (weight > MaxBits) throw FormatError.Corrupted("Zstandard Huffman weight is too large.");
            if (weight > 0) sum += 1 << (weight - 1);
        }

        if (sum == 0) throw FormatError.Corrupted("Zstandard Huffman tree has no symbols.");
        int tableBits = BitOperations.Log2((uint)sum) + 1;
        int missing = (1 << tableBits) - sum;
        if (tableBits > MaxBits || !BitOperations.IsPow2(missing))
            throw FormatError.Corrupted("Zstandard Huffman weights do not form a complete tree.");
        _weights[listedCount] = (byte)(BitOperations.Log2((uint)missing) + 1);
        int symbolCount = listedCount + 1;

        // Codes are handed out from the longest (lowest weight) to the shortest, in symbol order within a weight. A code of
        // `length` bits owns 2^(tableBits - length) = 2^(weight - 1) consecutive table slots.
        Span<int> nextSlot = stackalloc int[MaxBits + 2];
        Span<int> perWeight = stackalloc int[MaxBits + 2];
        for (int i = 0; i < symbolCount; i++) perWeight[_weights[i]]++;
        // The longest code length is derived from the weights, so some symbols must actually have it (weight 1); a tree
        // without any is non-canonical.
        if (perWeight[1] < 2) throw FormatError.Corrupted("Zstandard Huffman tree has no symbol with the longest code.");
        int slot = 0;
        for (int weight = 1; weight <= tableBits; weight++)
        {
            nextSlot[weight] = slot;
            slot += perWeight[weight] << (weight - 1);
        }

        for (int symbol = 0; symbol < symbolCount; symbol++)
        {
            int weight = _weights[symbol];
            if (weight == 0) continue;
            int slots = 1 << (weight - 1);
            _table.AsSpan(nextSlot[weight], slots).Fill((ushort)((symbol << 4) | (tableBits + 1 - weight)));
            nextSlot[weight] += slots;
        }

        _tableBits = tableBits;
        IsValid = true;
    }
}
