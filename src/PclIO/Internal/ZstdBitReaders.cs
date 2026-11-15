using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace PclIO.Internal;

/// <summary>Reads a bitstream from its first byte to its last, least significant bit first. Zstandard uses this order for
/// the description of an FSE table. Peeking past the end yields zero bits; consuming past the end is an error.</summary>
internal ref struct ZstdForwardBits
{
    private readonly ReadOnlySpan<byte> _data;
    private int _bitPosition;

    public ZstdForwardBits(ReadOnlySpan<byte> data)
    {
        _data = data;
        _bitPosition = 0;
    }

    /// <summary>Number of whole bytes touched so far; a partly used last byte counts as used.</summary>
    public readonly int BytesConsumed => (_bitPosition + 7) >> 3;

    /// <summary>Returns the next <paramref name="bits"/> bits (at most 24) without consuming them.</summary>
    public readonly uint Peek(int bits)
    {
        int index = _bitPosition >> 3;
        ulong window = 0;
        for (int i = 0; i < 5 && index + i < _data.Length; i++)
            window |= (ulong)_data[index + i] << (8 * i);
        return (uint)((window >> (_bitPosition & 7)) & ((1UL << bits) - 1));
    }

    public void Skip(int bits)
    {
        _bitPosition += bits;
        if (_bitPosition > _data.Length * 8)
            throw FormatError.Truncated("the end of a Zstandard FSE table description");
    }

    public uint Read(int bits)
    {
        uint value = Peek(bits);
        Skip(bits);
        return value;
    }
}

/// <summary>Reads a bitstream backwards, from the last bit written to the first. Zstandard writes sequences, Huffman
/// literals and FSE-coded Huffman weights this way: the last byte holds a 1 bit that marks the end, with zero bits above it
/// that are padding, and the stream is consumed from just below that marker down to bit 0 of byte 0. A multi-bit field is
/// stored least significant bit first, so reading it takes the next lower bits as one number.</summary>
/// <remarks>Asking for more bits than remain does not throw. The missing bits read as zero and <see cref="Overflowed"/>
/// becomes true, which is how the weights stream announces its end and how a damaged stream is detected.</remarks>
internal ref struct ZstdBackwardBits
{
    private readonly ReadOnlySpan<byte> _data;
    private int _bytePosition; // bytes [0, _bytePosition) are not loaded into the container yet
    private ulong _container; // the low _count bits are the next bits to read; the stream's next bit is the top one
    private int _count;

    public ZstdBackwardBits(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) throw FormatError.Truncated("a non-empty Zstandard bitstream");
        byte last = data[^1];
        if (last == 0) throw FormatError.Corrupted("Zstandard bitstream has no end marker.");
        int usable = BitOperations.Log2(last);
        _data = data;
        _bytePosition = data.Length - 1;
        _container = (ulong)last & ((1UL << usable) - 1);
        _count = usable;
    }

    /// <summary>True when a read needed more bits than the stream had.</summary>
    public readonly bool Overflowed => _count < 0;

    /// <summary>True when every bit has been consumed, no more and no fewer.</summary>
    public readonly bool IsExhausted => _count == 0 && _bytePosition == 0;

    /// <summary>Returns the next <paramref name="bits"/> bits (at most 32) without consuming them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Peek(int bits)
    {
        if (_count < bits) Refill();
        int shift = _count - bits;
        ulong value = shift >= 0 ? _container >> shift : _container << -shift;
        return (uint)(value & ((1UL << bits) - 1));
    }

    /// <summary>Consumes bits that a preceding <see cref="Peek"/> returned.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Skip(int bits) => _count -= bits;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Read(int bits)
    {
        uint value = Peek(bits);
        _count -= bits;
        return value;
    }

    private void Refill()
    {
        if (_bytePosition >= 8)
        {
            // Take as many whole bytes as fit below the 64-bit limit; the bytes just below the loaded ones become the low bits.
            ulong word = BinaryPrimitives.ReadUInt64LittleEndian(_data[(_bytePosition - 8)..]);
            int bytes = (63 - _count) >> 3;
            _container = (_container << (8 * bytes)) | (word >> (64 - 8 * bytes));
            _bytePosition -= bytes;
            _count += 8 * bytes;
        }
        else
        {
            while (_bytePosition > 0 && _count <= 55)
            {
                _container = (_container << 8) | _data[--_bytePosition];
                _count += 8;
            }
        }
    }
}
