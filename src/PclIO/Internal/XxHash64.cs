using System.Buffers.Binary;
using System.Numerics;

namespace PclIO.Internal;

/// <summary>XXH64 (seed 0 by default), the 64-bit hash that Zstandard uses for its optional content checksum.</summary>
internal static class XxHash64
{
    private const ulong Prime1 = 11400714785074694791UL;
    private const ulong Prime2 = 14029467366897019727UL;
    private const ulong Prime3 = 1609587929392839161UL;
    private const ulong Prime4 = 9650029242287828579UL;
    private const ulong Prime5 = 2870177450012600261UL;

    public static ulong Hash(ReadOnlySpan<byte> data, ulong seed = 0)
    {
        int length = data.Length;
        ulong h;
        if (length >= 32)
        {
            ulong v1 = seed + Prime1 + Prime2;
            ulong v2 = seed + Prime2;
            ulong v3 = seed;
            ulong v4 = seed - Prime1;
            do
            {
                v1 = Round(v1, BinaryPrimitives.ReadUInt64LittleEndian(data));
                v2 = Round(v2, BinaryPrimitives.ReadUInt64LittleEndian(data[8..]));
                v3 = Round(v3, BinaryPrimitives.ReadUInt64LittleEndian(data[16..]));
                v4 = Round(v4, BinaryPrimitives.ReadUInt64LittleEndian(data[24..]));
                data = data[32..];
            }
            while (data.Length >= 32);

            h = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7)
                + BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);
            h = MergeRound(h, v1);
            h = MergeRound(h, v2);
            h = MergeRound(h, v3);
            h = MergeRound(h, v4);
        }
        else
        {
            h = seed + Prime5;
        }

        h += (ulong)length;

        while (data.Length >= 8)
        {
            h ^= Round(0, BinaryPrimitives.ReadUInt64LittleEndian(data));
            h = BitOperations.RotateLeft(h, 27) * Prime1 + Prime4;
            data = data[8..];
        }

        if (data.Length >= 4)
        {
            h ^= BinaryPrimitives.ReadUInt32LittleEndian(data) * Prime1;
            h = BitOperations.RotateLeft(h, 23) * Prime2 + Prime3;
            data = data[4..];
        }

        foreach (byte b in data)
        {
            h ^= b * Prime5;
            h = BitOperations.RotateLeft(h, 11) * Prime1;
        }

        h ^= h >> 33;
        h *= Prime2;
        h ^= h >> 29;
        h *= Prime3;
        h ^= h >> 32;
        return h;
    }

    private static ulong Round(ulong acc, ulong input) => BitOperations.RotateLeft(acc + input * Prime2, 31) * Prime1;

    private static ulong MergeRound(ulong acc, ulong value) => (acc ^ Round(0, value)) * Prime1 + Prime4;
}
