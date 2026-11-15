using System.Buffers;

namespace PclIO.Internal;

/// <summary>
/// Decodes a raw Snappy stream incrementally: a varint holding the uncompressed length, followed by literal and copy
/// elements. Only the last <see cref="WindowSize"/> decoded bytes are kept, so memory use does not depend on the size of
/// the data. The reader owns <c>source</c> and disposes it.
/// </summary>
internal sealed class SnappyReader : Stream
{
    // Copies may reach back at most this far. Encoders that split their input into 64 KiB fragments never exceed it;
    // a stream that does is reported as unsupported instead of being decoded wrongly.
    private const int WindowSize = 1 << 20;
    private const int WindowMask = WindowSize - 1;
    private const int InputSize = 64 * 1024;
    private const int MaxPreambleBytes = 5;

    private readonly Stream _source;
    private readonly byte[] _input = ArrayPool<byte>.Shared.Rent(InputSize);
    private readonly byte[] _window = ArrayPool<byte>.Shared.Rent(WindowSize);
    private long _sourceLeft;
    private int _inputPos, _inputEnd;
    private long _produced;
    private long _literalLeft;
    private int _copyLeft, _copyOffset;
    private bool _disposed;

    /// <summary>Number of bytes the stream decodes to, as declared by its preamble.</summary>
    public long UncompressedLength { get; }

    /// <param name="source">Positioned at the first byte of the stream.</param>
    /// <param name="compressedLength">Length of the stream in bytes; nothing beyond it is read.</param>
    /// <exception cref="InvalidDataException">The preamble is missing or invalid.</exception>
    public SnappyReader(Stream source, long compressedLength)
    {
        _source = source;
        try
        {
            Span<byte> head = stackalloc byte[MaxPreambleBytes];
            int want = (int)Math.Min(MaxPreambleBytes, compressedLength);
            int got = source.ReadAtLeast(head[..want], want, throwOnEndOfStream: false);
            int used = ParsePreamble(head[..got], out long length);
            UncompressedLength = length;

            // Bytes read past the preamble are the start of the first element.
            head[used..got].CopyTo(_input);
            _inputEnd = got - used;
            _sourceLeft = compressedLength - got;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Reads only the declared uncompressed length of the stream that starts at the current position of <paramref name="source"/>.</summary>
    public static long ReadUncompressedLength(Stream source, long compressedLength)
    {
        Span<byte> head = stackalloc byte[MaxPreambleBytes];
        int want = (int)Math.Min(MaxPreambleBytes, compressedLength);
        int got = source.ReadAtLeast(head[..want], want, throwOnEndOfStream: false);
        ParsePreamble(head[..got], out long length);
        return length;
    }

    private static int ParsePreamble(ReadOnlySpan<byte> head, out long length)
    {
        length = 0;
        for (int i = 0; i < head.Length; i++)
        {
            // The length is a 32-bit value: the fifth byte may only carry the top four bits.
            if (i == MaxPreambleBytes - 1 && head[i] > 0x0F) throw FormatError.Corrupted("compressed stream declares a length above 32 bits");
            length |= (long)(head[i] & 0x7F) << (7 * i);
            if (head[i] < 0x80) return i + 1;
        }

        throw FormatError.Truncated("the length at the start of a compressed stream");
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int written = 0;
        while (written < buffer.Length)
        {
            if (_literalLeft > 0)
            {
                if (_inputPos == _inputEnd) Refill();
                int n = (int)Math.Min(Math.Min(buffer.Length - written, _literalLeft), _inputEnd - _inputPos);
                var chunk = _input.AsSpan(_inputPos, n);
                chunk.CopyTo(buffer[written..]);
                StoreInWindow(chunk);
                _inputPos += n;
                _literalLeft -= n;
                _produced += n;
                written += n;
            }
            else if (_copyLeft > 0)
            {
                int n = Math.Min(buffer.Length - written, _copyLeft);
                for (int i = 0; i < n; i++)
                {
                    // A copy may overlap its own output, so it proceeds byte by byte.
                    byte value = _window[(int)((_produced - _copyOffset) & WindowMask)];
                    _window[(int)(_produced & WindowMask)] = value;
                    buffer[written + i] = value;
                    _produced++;
                }

                _copyLeft -= n;
                written += n;
            }
            else if (!NextElement())
            {
                break;
            }
        }

        return written;
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    // Reads the next element header. Returns false once the declared length has been produced.
    private bool NextElement()
    {
        long remaining = UncompressedLength - _produced;
        if (remaining == 0)
        {
            if (_inputPos != _inputEnd || _sourceLeft != 0)
                throw FormatError.Corrupted("compressed data continues after the declared length");
            return false;
        }

        int tag = NextByte();
        if ((tag & 3) == 0)
        {
            long length = tag >> 2;
            if (length >= 60)
            {
                // Tags 60..63 store the length minus one in the following 1..4 bytes.
                int extra = (int)length - 59;
                length = 0;
                for (int i = 0; i < extra; i++) length |= (long)NextByte() << (8 * i);
            }

            length++;
            if (length > remaining) throw FormatError.Corrupted("a literal runs past the declared length");
            _literalLeft = length;
            return true;
        }

        int copyLength;
        long offset;
        switch (tag & 3)
        {
            case 1:
                copyLength = 4 + ((tag >> 2) & 7);
                offset = ((long)(tag >> 5) << 8) | NextByte();
                break;
            case 2:
                copyLength = 1 + (tag >> 2);
                offset = NextByte();
                offset |= (long)NextByte() << 8;
                break;
            default:
                copyLength = 1 + (tag >> 2);
                offset = 0;
                for (int i = 0; i < 4; i++) offset |= (long)NextByte() << (8 * i);
                break;
        }

        if (offset == 0 || offset > _produced) throw FormatError.Corrupted("a copy reaches before the start of the data");
        if (offset > WindowSize) throw FormatError.Unsupported($"a compressed stream copies from more than {WindowSize} bytes back");
        if (copyLength > remaining) throw FormatError.Corrupted("a copy runs past the declared length");
        _copyLeft = copyLength;
        _copyOffset = (int)offset;
        return true;
    }

    private void StoreInWindow(ReadOnlySpan<byte> data)
    {
        int position = (int)(_produced & WindowMask);
        int first = Math.Min(data.Length, WindowSize - position);
        data[..first].CopyTo(_window.AsSpan(position));
        data[first..].CopyTo(_window);
    }

    private byte NextByte()
    {
        if (_inputPos == _inputEnd) Refill();
        return _input[_inputPos++];
    }

    private void Refill()
    {
        if (_sourceLeft == 0) throw FormatError.Truncated("more compressed data");
        int read = _source.Read(_input, 0, (int)Math.Min(InputSize, _sourceLeft));
        if (read == 0) throw FormatError.Truncated("compressed data");
        _inputPos = 0;
        _inputEnd = read;
        _sourceLeft -= read;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
            {
                _source.Dispose();
                ArrayPool<byte>.Shared.Return(_input);
                ArrayPool<byte>.Shared.Return(_window);
            }
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
