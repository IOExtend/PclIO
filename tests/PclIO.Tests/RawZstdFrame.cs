using System.Buffers.Binary;

namespace PclIO.Tests;

/// <summary>Writes Zstandard frames without a compressor: a block of one repeated byte becomes an RLE block (so crafted
/// oversized planes stay small), any other block is stored raw. The fixtures use them to give the Leica reader valid frames;
/// compressed block types are covered by the decoder's own tests.</summary>
internal static class RawZstdFrame
{
    private const int MaxBlock = 128 * 1024;

    /// <summary>A frame with a window of 128 KiB, the content size in its header and one block per 128 KiB of data.</summary>
    public static byte[] Wrap(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        ms.Write([0x28, 0xB5, 0x2F, 0xFD]);

        // Descriptor: 8-byte content size, no checksum, no dictionary, window descriptor present. Window 2^17 = 128 KiB.
        ms.WriteByte(0xC0);
        ms.WriteByte(0x38);
        Span<byte> size = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(size, (ulong)data.Length);
        ms.Write(size);

        int offset = 0;
        do
        {
            int length = Math.Min(MaxBlock, data.Length - offset);
            bool last = offset + length == data.Length;

            // Block header: last-block flag, type (0 raw, 1 RLE), 21-bit size. An RLE block stores its byte once.
            ReadOnlySpan<byte> block = data.Slice(offset, length);
            bool rle = length > 0 && block.IndexOfAnyExcept(block[0]) < 0;
            uint header = (last ? 1u : 0u) | (rle ? 1u << 1 : 0u) | ((uint)length << 3);
            ms.Write([(byte)header, (byte)(header >> 8), (byte)(header >> 16)]);
            ms.Write(rle ? block[..1] : block);
            offset += length;
        }
        while (offset < data.Length);

        return ms.ToArray();
    }
}
