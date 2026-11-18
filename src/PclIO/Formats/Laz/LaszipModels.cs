namespace PclIO.Formats.Laz;

/// <summary>Adaptive bit model for LASzip arithmetic coding.</summary>
internal sealed class LaszipBitModel
{
    private const int LengthShift = 13;
    private const int MaxCount = 1 << 13;

    public uint Bit0Count { get; private set; } = 1;
    public uint BitCount { get; private set; } = 2;
    public uint Bit0Prob { get; private set; } = 1u << (LengthShift - 1);
    public uint UpdateCycle { get; private set; } = 4;
    public uint BitsUntilUpdate { get; set; } = 4;

    public void Init()
    {
        Bit0Count = 1;
        BitCount = 2;
        Bit0Prob = 1u << (LengthShift - 1);
        UpdateCycle = 4;
        BitsUntilUpdate = 4;
    }

    public void IncrementBit0() => Bit0Count++;

    public void Update()
    {
        BitCount += UpdateCycle;
        if (BitCount > MaxCount)
        {
            BitCount = (BitCount + 1) >> 1;
            Bit0Count = (Bit0Count + 1) >> 1;
            if (Bit0Count == BitCount) BitCount++;
        }

        uint scale = 0x80000000u / BitCount;
        Bit0Prob = (uint)(((ulong)Bit0Count * scale) >> (31 - LengthShift));

        UpdateCycle = (5 * UpdateCycle) >> 2;
        if (UpdateCycle > 64) UpdateCycle = 64;
        BitsUntilUpdate = UpdateCycle;
    }
}

/// <summary>Adaptive data model for multi-symbol LASzip arithmetic coding.</summary>
internal sealed class LaszipDataModel
{
    private const int LengthShift = 15;
    private const int MaxCount = 1 << 15;

    public int Symbols { get; }
    public int LastSymbol { get; }
    public uint[] Distribution { get; }
    public uint[] SymbolCount { get; }
    public uint[]? DecoderTable { get; }
    public int TableSize { get; }
    public int TableShift { get; }
    public uint TotalCount { get; private set; }
    public uint UpdateCycle { get; private set; }
    public uint SymbolsUntilUpdate { get; set; }

    public LaszipDataModel(int symbols)
    {
        if (symbols is < 2 or > (1 << 11))
            throw new ArgumentOutOfRangeException(nameof(symbols));

        Symbols = symbols;
        LastSymbol = symbols - 1;

        if (symbols > 16)
        {
            int tableBits = 3;
            while (symbols > (1 << (tableBits + 2))) tableBits++;
            TableSize = 1 << tableBits;
            TableShift = LengthShift - tableBits;
            DecoderTable = new uint[TableSize + 2];
        }

        Distribution = new uint[symbols + 1];
        SymbolCount = new uint[symbols];
        for (int i = 0; i < symbols; i++) SymbolCount[i] = 1;

        UpdateCycle = (uint)symbols;
        Update();
        SymbolsUntilUpdate = UpdateCycle = (uint)(symbols + 6) >> 1;
    }

    public void Update()
    {
        TotalCount += UpdateCycle;
        if (TotalCount > MaxCount)
        {
            TotalCount = 0;
            for (int i = 0; i < Symbols; i++)
            {
                SymbolCount[i] = (SymbolCount[i] + 1) >> 1;
                TotalCount += SymbolCount[i];
            }
        }

        uint scale = 0x80000000u / TotalCount;
        uint sum = 0;
        int s = 0;

        if (DecoderTable == null)
        {
            for (int k = 0; k < Symbols; k++)
            {
                Distribution[k] = (uint)(((ulong)sum * scale) >> (31 - LengthShift));
                sum += SymbolCount[k];
            }
        }
        else
        {
            for (int k = 0; k < Symbols; k++)
            {
                Distribution[k] = (uint)(((ulong)sum * scale) >> (31 - LengthShift));
                sum += SymbolCount[k];
                int w = (int)(Distribution[k] >> TableShift);
                while (s < w) DecoderTable[++s] = (uint)(k - 1);
            }
            DecoderTable[0] = 0;
            while (s <= TableSize) DecoderTable[++s] = (uint)(Symbols - 1);
        }

        Distribution[Symbols] = MaxCount;

        UpdateCycle = (5 * UpdateCycle) >> 2;
        uint maxCycle = (uint)(Symbols + 6) << 3;
        if (UpdateCycle > maxCycle) UpdateCycle = maxCycle;
        SymbolsUntilUpdate = UpdateCycle;
    }
}

/// <summary>Sorted 5-element running median filter for LASzip coordinate prediction.</summary>
internal struct StreamingMedian5
{
    public int V0, V1, V2, V3, V4;
    public bool High;

    public void Init()
    {
        V0 = V1 = V2 = V3 = V4 = 0;
        High = true;
    }

    public readonly int Get() => V2;

    public void Add(int v)
    {
        if (High)
        {
            if (v < V2)
            {
                V4 = V3;
                V3 = V2;
                if (v < V0) { V2 = V1; V1 = V0; V0 = v; }
                else if (v < V1) { V2 = V1; V1 = v; }
                else V2 = v;
            }
            else
            {
                if (v < V3) { V4 = V3; V3 = v; }
                else V4 = v;
                High = false;
            }
        }
        else
        {
            if (V2 < v)
            {
                V0 = V1;
                V1 = V2;
                if (V4 < v) { V2 = V3; V3 = V4; V4 = v; }
                else if (V3 < v) { V2 = V3; V3 = v; }
                else V2 = v;
            }
            else
            {
                if (V1 < v) { V0 = V1; V1 = v; }
                else V0 = v;
                High = true;
            }
        }
    }
}

/// <summary>Lookup tables for return mapping in LASzip v2 decompression.</summary>
internal static class LaszipLookupTables
{
    public static readonly byte[,] ReturnMap =
    {
        { 15, 14, 13, 12, 11, 10,  9,  8 },
        { 14,  0,  1,  3,  6, 10, 10,  9 },
        { 13,  1,  2,  4,  7, 11, 11, 10 },
        { 12,  3,  4,  5,  8, 12, 12, 11 },
        { 11,  6,  7,  8,  9, 13, 13, 12 },
        { 10, 10, 11, 12, 13, 14, 14, 13 },
        {  9, 10, 11, 12, 13, 14, 14, 13 },
        {  8,  9, 10, 11, 12, 13, 13, 15 }
    };

    public static readonly byte[,] ReturnLevel =
    {
        { 0, 1, 2, 3, 4, 5, 6, 7 },
        { 1, 0, 1, 2, 3, 4, 5, 6 },
        { 2, 1, 0, 1, 2, 3, 4, 5 },
        { 3, 2, 1, 0, 1, 2, 3, 4 },
        { 4, 3, 2, 1, 0, 1, 2, 3 },
        { 5, 4, 3, 2, 1, 0, 1, 2 },
        { 6, 5, 4, 3, 2, 1, 0, 1 },
        { 7, 6, 5, 4, 3, 2, 1, 0 }
    };
}
