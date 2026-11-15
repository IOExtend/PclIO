using System.Buffers.Binary;
using System.Text;

namespace PclIO.Internal;

internal static class Bytes
{
    public static short ReadInt16LE(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadInt16LittleEndian(span);

    public static ushort ReadUInt16LE(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadUInt16LittleEndian(span);

    public static uint ReadUInt32LE(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadUInt32LittleEndian(span);

    public static ulong ReadUInt64LE(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadUInt64LittleEndian(span);

    public static int ReadInt32LE(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadInt32LittleEndian(span);

    public static float ReadSingleLE(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadSingleLittleEndian(span);

    public static double ReadDoubleLE(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadDoubleLittleEndian(span);

    public static string ReadAsciiNullTerminated(ReadOnlySpan<byte> span)
    {
        int nullIdx = span.IndexOf((byte)0);
        if (nullIdx >= 0) span = span[..nullIdx];
        return Encoding.ASCII.GetString(span);
    }
}
