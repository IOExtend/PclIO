using System.Numerics;

namespace PclIO.Internal;

/// <summary>A Finite State Entropy decoding table (RFC 8878 section 4.1). Each state is one packed entry: the baseline of
/// the next state in bits 0-15, the number of bits to add to it in bits 16-23 and the symbol the state stands for in bits 24-31.</summary>
internal sealed class ZstdFseTable
{
    private readonly short[] _counts = new short[256];
    private readonly ushort[] _next = new ushort[256];

    public ZstdFseTable(int maxAccuracyLog)
    {
        Entries = new uint[1 << maxAccuracyLog];
    }

    public uint[] Entries { get; }

    /// <summary>log2 of the number of states in use.</summary>
    public int AccuracyLog { get; private set; }

    public static uint BaselineOf(uint entry) => entry & 0xFFFF;

    public static int BitsOf(uint entry) => (int)((entry >> 16) & 0xFF);

    public static int SymbolOf(uint entry) => (int)(entry >> 24);

    /// <summary>Builds the table from a predefined distribution of the specification.</summary>
    public static ZstdFseTable FromDistribution(ReadOnlySpan<short> distribution, int accuracyLog)
    {
        var table = new ZstdFseTable(accuracyLog);
        distribution.CopyTo(table._counts);
        table.Build(distribution.Length, accuracyLog);
        return table;
    }

    /// <summary>Makes a table whose every state yields <paramref name="symbol"/> without reading any bit (RLE_Mode).</summary>
    public void SetSingleSymbol(int symbol)
    {
        Entries[0] = (uint)symbol << 24;
        AccuracyLog = 0;
    }

    /// <summary>Reads a table description from the start of <paramref name="source"/> and builds the table.</summary>
    /// <param name="source">The bytes that start with the description.</param>
    /// <param name="maxAccuracyLog">The largest accuracy log the context allows.</param>
    /// <param name="maxSymbol">The largest symbol value the context allows.</param>
    /// <returns>The number of bytes the description occupied.</returns>
    public int Read(ReadOnlySpan<byte> source, int maxAccuracyLog, int maxSymbol)
    {
        var bits = new ZstdForwardBits(source);
        int accuracyLog = (int)bits.Read(4) + 5;
        if (accuracyLog > maxAccuracyLog)
            throw FormatError.Corrupted($"Zstandard FSE accuracy log {accuracyLog} exceeds the maximum of {maxAccuracyLog}.");

        int remaining = 1 << accuracyLog; // probability points not yet handed out
        int symbol = 0;
        while (remaining > 0)
        {
            if (symbol > maxSymbol) throw FormatError.Corrupted("Zstandard FSE table describes too many symbols.");

            // The value is in 0..remaining+1; values below `threshold` are stored in one bit less than the others.
            int largest = remaining + 1;
            int width = BitOperations.Log2((uint)largest) + 1;
            int threshold = (1 << width) - 1 - largest;
            uint peeked = bits.Peek(width);
            int low = (int)(peeked & ((1u << (width - 1)) - 1));
            int value;
            if (low < threshold)
            {
                value = low;
                bits.Skip(width - 1);
            }
            else
            {
                value = (int)peeked;
                if (value >= 1 << (width - 1)) value -= threshold;
                bits.Skip(width);
            }

            int probability = value - 1; // -1 means "less than one" and takes one point
            _counts[symbol++] = (short)probability;
            remaining -= probability < 0 ? 1 : probability;

            if (probability == 0)
            {
                // A zero is followed by a count of further zero symbols, 2 bits at a time, 3 meaning "read another count".
                int repeat;
                do
                {
                    repeat = (int)bits.Read(2);
                    if (symbol + repeat > maxSymbol + 1) throw FormatError.Corrupted("Zstandard FSE table describes too many symbols.");
                    for (int i = 0; i < repeat; i++) _counts[symbol++] = 0;
                }
                while (repeat == 3);
            }
        }

        Build(symbol, accuracyLog);
        return bits.BytesConsumed;
    }

    private void Build(int symbolCount, int accuracyLog)
    {
        int size = 1 << accuracyLog;
        int mask = size - 1;
        uint[] entries = Entries;
        AccuracyLog = accuracyLog;

        // Symbols with probability "less than one" take one state each, from the end of the table backwards.
        int highest = size - 1;
        for (int s = 0; s < symbolCount; s++)
        {
            if (_counts[s] == -1)
            {
                entries[highest--] = (uint)s << 24;
                _next[s] = 1;
            }
            else
            {
                _next[s] = (ushort)_counts[s];
            }
        }

        // The other symbols are spread over the remaining states with a step that visits every state once.
        int step = (size >> 1) + (size >> 3) + 3;
        int position = 0;
        for (int s = 0; s < symbolCount; s++)
        {
            for (int i = 0; i < _counts[s]; i++)
            {
                entries[position] = (uint)s << 24;
                do position = (position + step) & mask;
                while (position > highest);
            }
        }

        // The states of one symbol, in table order, get the numbers count..2*count-1; the lower numbers need one more bit.
        for (int state = 0; state < size; state++)
        {
            uint symbol = entries[state] >> 24;
            int number = _next[symbol]++;
            int bits = accuracyLog - BitOperations.Log2((uint)number);
            entries[state] = (uint)((number << bits) - size) | (uint)bits << 16 | symbol << 24;
        }
    }
}
