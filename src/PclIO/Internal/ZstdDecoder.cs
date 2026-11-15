using System.Buffers;

namespace PclIO.Internal;

/// <summary>A managed Zstandard decoder written from RFC 8878. It reads concatenated frames and skips skippable frames,
/// supports every block and literals type, all sequence table modes, repeat offsets and the optional content checksum, and
/// rejects dictionaries. Malformed, truncated, unsupported or oversized input always raises <see cref="InvalidDataException"/>;
/// the output never grows past the limit the caller gives.</summary>
internal static class ZstdDecoder
{
    private const uint FrameMagic = 0xFD2FB528;
    private const uint SkippableMagic = 0x184D2A50; // 0x184D2A50..0x184D2A5F
    private const uint SkippableMask = 0xFFFFFFF0;
    private const int MaxBlockSize = 1 << 17;
    private const long MaxWindowSize = 1L << 27;

    /// <summary>Decompresses one or more concatenated frames.</summary>
    /// <param name="input">The frames. Skippable frames are ignored.</param>
    /// <param name="maxOutputBytes">The most decompressed bytes the caller accepts, over all frames together.</param>
    /// <returns>The contents of all frames, one after the other.</returns>
    /// <exception cref="InvalidDataException">The input is not valid Zstandard data this decoder supports, or it
    /// decompresses to more than <paramref name="maxOutputBytes"/>.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> input, int maxOutputBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxOutputBytes);
        if (input.IsEmpty) throw FormatError.Truncated("a Zstandard frame");

        var session = new Session(Math.Min(maxOutputBytes, Array.MaxLength));
        try
        {
            int position = 0;
            while (position < input.Length)
            {
                ReadOnlySpan<byte> rest = input[position..];
                if (rest.Length < 4) throw FormatError.Truncated("a Zstandard frame");
                uint magic = Bytes.ReadUInt32LE(rest);
                if ((magic & SkippableMask) == SkippableMagic)
                {
                    if (rest.Length < 8) throw FormatError.Truncated("a Zstandard skippable frame header");
                    uint size = Bytes.ReadUInt32LE(rest[4..]);
                    if (size > rest.Length - 8) throw FormatError.Truncated("a Zstandard skippable frame");
                    position += 8 + (int)size;
                }
                else if (magic == FrameMagic)
                {
                    position += session.DecodeFrame(rest);
                }
                else
                {
                    throw FormatError.Corrupted("data is not a Zstandard frame.");
                }
            }

            return session.TakeOutput();
        }
        finally
        {
            session.Dispose();
        }
    }

    /// <summary>The state of one <see cref="Decompress"/> call: the growing output and the scratch space for blocks.</summary>
    private sealed class Session : IDisposable
    {
        private static readonly ZstdFseTable PredefinedLiteralLengths = ZstdFseTable.FromDistribution(
            [4, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 2, 1, 1, 1, 1, 1, -1, -1, -1, -1], 6);

        private static readonly ZstdFseTable PredefinedMatchLengths = ZstdFseTable.FromDistribution(
            [1, 4, 3, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
             1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1, -1, -1], 6);

        private static readonly ZstdFseTable PredefinedOffsets = ZstdFseTable.FromDistribution(
            [1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1], 5);

        // Extra bits per literal length code and per match length code; the baselines follow from them (RFC 8878 tables 16, 17).
        private static readonly byte[] LiteralLengthBits =
            [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 3, 4, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

        private static readonly byte[] MatchLengthBits =
            [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
             1, 1, 1, 1, 2, 2, 3, 3, 4, 4, 5, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

        private static readonly int[] LiteralLengthBase = Baselines(LiteralLengthBits, 0);
        private static readonly int[] MatchLengthBase = Baselines(MatchLengthBits, 3);

        private readonly int _limit;
        private byte[] _output = [];
        private int _length;

        private readonly byte[] _literals = ArrayPool<byte>.Shared.Rent(MaxBlockSize);
        private readonly ZstdHuffmanTable _huffman = new();
        private readonly ZstdFseTable _literalLengthTable = new(9);
        private readonly ZstdFseTable _offsetTable = new(8);
        private readonly ZstdFseTable _matchLengthTable = new(9);
        private ZstdFseTable? _literalLengths;
        private ZstdFseTable? _offsets;
        private ZstdFseTable? _matchLengths;
        private uint _repeat1, _repeat2, _repeat3;

        // Properties of the frame being decoded.
        private int _frameStart;
        private int _frameCap; // output length the frame may not pass: the global limit or the declared content size
        private long _windowSize;

        public Session(int limit)
        {
            _limit = limit;
        }

        public void Dispose() => ArrayPool<byte>.Shared.Return(_literals);

        public byte[] TakeOutput() => _length == _output.Length ? _output : _output.AsSpan(0, _length).ToArray();

        /// <summary>Decodes the frame at the start of <paramref name="frame"/>, whose magic number has been checked.</summary>
        /// <returns>The number of bytes the frame occupied.</returns>
        public int DecodeFrame(ReadOnlySpan<byte> frame)
        {
            int position = 4;
            Require(frame, position, 1, "a Zstandard frame header");
            byte descriptor = frame[position++];
            if ((descriptor & 0x08) != 0) throw FormatError.Corrupted("Zstandard frame sets a reserved bit.");
            int contentSizeFlag = descriptor >> 6;
            bool singleSegment = (descriptor & 0x20) != 0;
            bool hasChecksum = (descriptor & 0x04) != 0;

            long windowSize = 0;
            if (!singleSegment)
            {
                Require(frame, position, 1, "a Zstandard frame header");
                int window = frame[position++];
                long baseSize = 1L << (10 + (window >> 3));
                windowSize = baseSize + baseSize / 8 * (window & 7);
                if (windowSize > MaxWindowSize)
                    throw FormatError.Corrupted($"Zstandard window of {windowSize} bytes exceeds the supported {MaxWindowSize} bytes.");
            }

            int dictionaryIdSize = (descriptor & 3) switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 };
            Require(frame, position, dictionaryIdSize, "a Zstandard frame header");
            uint dictionaryId = 0;
            for (int i = 0; i < dictionaryIdSize; i++) dictionaryId |= (uint)frame[position + i] << (8 * i);
            position += dictionaryIdSize;
            if (dictionaryId != 0) throw FormatError.Corrupted("Zstandard frame needs a dictionary, which is not supported.");

            int contentSizeBytes = contentSizeFlag switch { 0 => singleSegment ? 1 : 0, 1 => 2, 2 => 4, _ => 8 };
            Require(frame, position, contentSizeBytes, "a Zstandard frame header");
            bool hasContentSize = contentSizeBytes > 0;
            ulong contentSize = 0;
            for (int i = 0; i < contentSizeBytes; i++) contentSize |= (ulong)frame[position + i] << (8 * i);
            position += contentSizeBytes;
            if (contentSizeBytes == 2) contentSize += 256;

            _frameStart = _length;
            _frameCap = _limit;
            if (hasContentSize)
            {
                if (contentSize > (ulong)(_limit - _length)) throw OutputTooLarge();
                _frameCap = _length + (int)contentSize;
            }

            if (singleSegment)
            {
                // A single-segment frame's window is its content size.
                if (contentSize > (ulong)MaxWindowSize)
                    throw FormatError.Corrupted($"Zstandard window of {contentSize} bytes exceeds the supported {MaxWindowSize} bytes.");
                windowSize = (long)contentSize;
            }

            _windowSize = windowSize;
            int maxBlock = (int)Math.Min(windowSize, MaxBlockSize);

            _huffman.Invalidate();
            _literalLengths = _offsets = _matchLengths = null;
            _repeat1 = 1;
            _repeat2 = 4;
            _repeat3 = 8;

            bool lastBlock;
            do
            {
                Require(frame, position, 3, "a Zstandard block header");
                uint header = (uint)(frame[position] | frame[position + 1] << 8 | frame[position + 2] << 16);
                position += 3;
                lastBlock = (header & 1) != 0;
                int blockType = (int)(header >> 1) & 3;
                int blockSize = (int)(header >> 3);
                if (blockType == 3) throw FormatError.Corrupted("Zstandard block has the reserved type.");
                if (blockSize > maxBlock) throw FormatError.Corrupted($"Zstandard block of {blockSize} bytes exceeds the maximum of {maxBlock}.");

                switch (blockType)
                {
                    case 0:
                        Require(frame, position, blockSize, "a Zstandard raw block");
                        Reserve(blockSize);
                        frame.Slice(position, blockSize).CopyTo(_output.AsSpan(_length));
                        _length += blockSize;
                        position += blockSize;
                        break;
                    case 1:
                        Require(frame, position, 1, "a Zstandard RLE block");
                        Reserve(blockSize);
                        _output.AsSpan(_length, blockSize).Fill(frame[position]);
                        _length += blockSize;
                        position += 1;
                        break;
                    default:
                        Require(frame, position, blockSize, "a Zstandard compressed block");
                        DecodeCompressedBlock(frame.Slice(position, blockSize), maxBlock);
                        position += blockSize;
                        break;
                }
            }
            while (!lastBlock);

            if (hasContentSize && (ulong)(_length - _frameStart) != contentSize)
                throw FormatError.Corrupted("Zstandard frame content is shorter than its header declares.");

            if (hasChecksum)
            {
                Require(frame, position, 4, "a Zstandard content checksum");
                uint expected = Bytes.ReadUInt32LE(frame[position..]);
                uint actual = (uint)XxHash64.Hash(_output.AsSpan(_frameStart, _length - _frameStart));
                if (expected != actual) throw FormatError.Corrupted("Zstandard content checksum does not match.");
                position += 4;
            }

            return position;
        }

        private static void Require(ReadOnlySpan<byte> data, int offset, int count, string what)
        {
            if (data.Length - offset < count) throw FormatError.Truncated(what);
        }

        private InvalidDataException OutputTooLarge() =>
            FormatError.Corrupted($"decompressed data exceeds the allowed {_limit} bytes.");

        /// <summary>Makes room for <paramref name="count"/> more output bytes, or fails when they would pass the frame's cap.</summary>
        private void Reserve(int count)
        {
            if (count > _frameCap - _length) throw Overflow(_frameCap);
            Grow(_length + count);
        }

        /// <summary>The error for output that passes <paramref name="end"/>, which is the end of the block or, when the frame cap
        /// comes first, the frame cap: the maximum block size, the caller's limit or the size the frame declared.</summary>
        private InvalidDataException Overflow(int end)
        {
            if (end < _frameCap) return FormatError.Corrupted("Zstandard block decompresses to more than the maximum block size.");
            return _frameCap == _limit
                ? OutputTooLarge()
                : FormatError.Corrupted("Zstandard frame content is longer than its header declares.");
        }

        /// <summary>Grows the output array, at least doubling it, to hold <paramref name="needed"/> bytes. Memory is only ever
        /// obtained for data that a block is about to produce, never for a size read from the input.</summary>
        private void Grow(int needed)
        {
            if (needed <= _output.Length) return;
            int size = (int)Math.Min(Math.Max((long)_output.Length * 2, needed), _limit);
            Array.Resize(ref _output, size);
        }

        private void DecodeCompressedBlock(ReadOnlySpan<byte> block, int maxBlock)
        {
            int literalCount = ReadLiterals(block, maxBlock, out int literalsSize);
            ReadOnlySpan<byte> sequences = block[literalsSize..];
            if (sequences.IsEmpty) throw FormatError.Truncated("a Zstandard sequences section");

            int sequenceCount;
            int position;
            int first = sequences[0];
            if (first < 128)
            {
                sequenceCount = first;
                position = 1;
            }
            else if (first < 255)
            {
                Require(sequences, 0, 2, "a Zstandard sequence count");
                sequenceCount = ((first - 128) << 8) + sequences[1];
                position = 2;
            }
            else
            {
                Require(sequences, 0, 3, "a Zstandard sequence count");
                sequenceCount = sequences[1] + (sequences[2] << 8) + 0x7F00;
                position = 3;
            }

            // The block may produce at most maxBlock bytes, and never more than the frame cap allows.
            int room = Math.Min(maxBlock, _frameCap - _length);
            Grow(_length + room);
            int end = _length + room;

            if (sequenceCount == 0)
            {
                if (position != sequences.Length) throw FormatError.Corrupted("Zstandard block has data after an empty sequences section.");
                if (literalCount > room) throw Overflow(end);
                _literals.AsSpan(0, literalCount).CopyTo(_output.AsSpan(_length));
                _length += literalCount;
                return;
            }

            Require(sequences, position, 1, "Zstandard symbol compression modes");
            int modes = sequences[position++];
            if ((modes & 3) != 0) throw FormatError.Corrupted("Zstandard sequences section sets reserved bits.");
            position += SetUpTable(ref _literalLengths, _literalLengthTable, PredefinedLiteralLengths, modes >> 6, sequences[position..], 9, 35);
            position += SetUpTable(ref _offsets, _offsetTable, PredefinedOffsets, (modes >> 4) & 3, sequences[position..], 8, 31);
            position += SetUpTable(ref _matchLengths, _matchLengthTable, PredefinedMatchLengths, (modes >> 2) & 3, sequences[position..], 9, 52);

            ExecuteSequences(sequences[position..], sequenceCount, literalCount, end);
        }

        /// <summary>Picks the decoding table for one symbol type according to its compression mode.</summary>
        /// <returns>The number of bytes of table description consumed from <paramref name="source"/>.</returns>
        private static int SetUpTable(ref ZstdFseTable? current, ZstdFseTable own, ZstdFseTable predefined, int mode,
            ReadOnlySpan<byte> source, int maxAccuracyLog, int maxSymbol)
        {
            switch (mode)
            {
                case 0:
                    current = predefined;
                    return 0;
                case 1:
                    if (source.IsEmpty) throw FormatError.Truncated("a Zstandard RLE table");
                    if (source[0] > maxSymbol) throw FormatError.Corrupted("Zstandard RLE table symbol is out of range.");
                    own.SetSingleSymbol(source[0]);
                    current = own;
                    return 1;
                case 2:
                    int consumed = own.Read(source, maxAccuracyLog, maxSymbol);
                    current = own;
                    return consumed;
                default:
                    if (current is null) throw FormatError.Corrupted("Zstandard block repeats a table that does not exist.");
                    return 0;
            }
        }

        /// <summary>Decodes the literals section into <see cref="_literals"/>.</summary>
        /// <returns>The number of literals.</returns>
        private int ReadLiterals(ReadOnlySpan<byte> block, int maxBlock, out int sectionSize)
        {
            if (block.IsEmpty) throw FormatError.Truncated("a Zstandard literals section");
            int first = block[0];
            int type = first & 3;
            int sizeFormat = (first >> 2) & 3;

            if (type < 2)
            {
                // Raw and RLE literals: a size of 5, 12 or 20 bits, in a header of 1, 2 or 3 bytes.
                int headerSize = sizeFormat is 0 or 2 ? 1 : sizeFormat == 1 ? 2 : 3;
                Require(block, 0, headerSize, "a Zstandard literals header");
                int size = headerSize switch
                {
                    1 => first >> 3,
                    2 => (first >> 4) | (block[1] << 4),
                    _ => (first >> 4) | (block[1] << 4) | (block[2] << 12),
                };
                if (size > maxBlock) throw FormatError.Corrupted("Zstandard literals exceed the maximum block size.");

                if (type == 0)
                {
                    Require(block, headerSize, size, "Zstandard raw literals");
                    block.Slice(headerSize, size).CopyTo(_literals);
                    sectionSize = headerSize + size;
                }
                else
                {
                    Require(block, headerSize, 1, "Zstandard RLE literals");
                    _literals.AsSpan(0, size).Fill(block[headerSize]);
                    sectionSize = headerSize + 1;
                }

                return size;
            }

            // Huffman literals: regenerated and compressed sizes of 10, 10, 14 or 18 bits each, in a header of 3, 3, 4 or 5 bytes.
            int streams = sizeFormat == 0 ? 1 : 4;
            int sizeBits = sizeFormat switch { 0 or 1 => 10, 2 => 14, _ => 18 };
            int headerBytes = sizeFormat switch { 0 or 1 => 3, 2 => 4, _ => 5 };
            Require(block, 0, headerBytes, "a Zstandard literals header");
            ulong header = 0;
            for (int i = 0; i < headerBytes; i++) header |= (ulong)block[i] << (8 * i);
            int mask = (1 << sizeBits) - 1;
            int regenerated = (int)(header >> 4) & mask;
            int compressed = (int)(header >> (4 + sizeBits)) & mask;
            if (regenerated > maxBlock) throw FormatError.Corrupted("Zstandard literals exceed the maximum block size.");
            Require(block, headerBytes, compressed, "Zstandard Huffman literals");
            ReadOnlySpan<byte> content = block.Slice(headerBytes, compressed);
            sectionSize = headerBytes + compressed;

            if (type == 2)
            {
                content = content[_huffman.Read(content)..];
            }
            else if (!_huffman.IsValid)
            {
                throw FormatError.Corrupted("Zstandard block reuses a Huffman tree that does not exist.");
            }

            Span<byte> literals = _literals.AsSpan(0, regenerated);
            if (streams == 1)
            {
                _huffman.DecodeStream(content, literals);
                return regenerated;
            }

            // Four streams: the first three decode a quarter of the literals each (rounded up), the fourth the rest.
            Require(content, 0, 6, "a Zstandard Huffman jump table");
            int size1 = Bytes.ReadUInt16LE(content);
            int size2 = Bytes.ReadUInt16LE(content[2..]);
            int size3 = Bytes.ReadUInt16LE(content[4..]);
            int size4 = content.Length - 6 - size1 - size2 - size3;
            int quarter = (regenerated + 3) / 4;
            if (size4 < 0 || regenerated - 3 * quarter < 0) throw FormatError.Corrupted("Zstandard Huffman stream sizes are inconsistent.");

            ReadOnlySpan<byte> body = content[6..];
            _huffman.DecodeStream(body[..size1], literals[..quarter]);
            _huffman.DecodeStream(body.Slice(size1, size2), literals.Slice(quarter, quarter));
            _huffman.DecodeStream(body.Slice(size1 + size2, size3), literals.Slice(2 * quarter, quarter));
            _huffman.DecodeStream(body[(size1 + size2 + size3)..], literals[(3 * quarter)..]);
            return regenerated;
        }

        /// <summary>Decodes the sequences bitstream and runs the sequences against the literals, appending to the output.</summary>
        /// <param name="stream">The bitstream that follows the table descriptions.</param>
        /// <param name="sequenceCount">How many sequences it holds.</param>
        /// <param name="literalCount">How many literals the block's literals section produced.</param>
        /// <param name="end">The output length this block may not pass.</param>
        private void ExecuteSequences(ReadOnlySpan<byte> stream, int sequenceCount, int literalCount, int end)
        {
            var bits = new ZstdBackwardBits(stream);
            uint[] literalLengthEntries = _literalLengths!.Entries;
            uint[] offsetEntries = _offsets!.Entries;
            uint[] matchLengthEntries = _matchLengths!.Entries;
            uint literalLengthState = bits.Read(_literalLengths.AccuracyLog);
            uint offsetState = bits.Read(_offsets.AccuracyLog);
            uint matchLengthState = bits.Read(_matchLengths.AccuracyLog);

            Span<byte> output = _output;
            ReadOnlySpan<byte> literals = _literals.AsSpan(0, literalCount);
            int position = _length;
            int literalPosition = 0;
            uint repeat1 = _repeat1, repeat2 = _repeat2, repeat3 = _repeat3;

            for (int i = 0; i < sequenceCount; i++)
            {
                uint literalLengthEntry = literalLengthEntries[literalLengthState];
                uint offsetEntry = offsetEntries[offsetState];
                uint matchLengthEntry = matchLengthEntries[matchLengthState];
                int literalLengthCode = ZstdFseTable.SymbolOf(literalLengthEntry);
                int offsetCode = ZstdFseTable.SymbolOf(offsetEntry);
                int matchLengthCode = ZstdFseTable.SymbolOf(matchLengthEntry);

                // The extra bits come in the order offset, match length, literal length; the states then advance in the
                // order literal length, match length, offset, except after the last sequence.
                uint offsetValue = (1u << offsetCode) + bits.Read(offsetCode);
                int matchLength = MatchLengthBase[matchLengthCode] + (int)bits.Read(MatchLengthBits[matchLengthCode]);
                int literalLength = LiteralLengthBase[literalLengthCode] + (int)bits.Read(LiteralLengthBits[literalLengthCode]);
                if (i + 1 < sequenceCount)
                {
                    literalLengthState = ZstdFseTable.BaselineOf(literalLengthEntry) + bits.Read(ZstdFseTable.BitsOf(literalLengthEntry));
                    matchLengthState = ZstdFseTable.BaselineOf(matchLengthEntry) + bits.Read(ZstdFseTable.BitsOf(matchLengthEntry));
                    offsetState = ZstdFseTable.BaselineOf(offsetEntry) + bits.Read(ZstdFseTable.BitsOf(offsetEntry));
                }

                if (bits.Overflowed) throw FormatError.Truncated("Zstandard sequences");

                if (literalLength > literalCount - literalPosition) throw FormatError.Corrupted("Zstandard sequence uses more literals than the block has.");
                if (literalLength + matchLength > end - position) throw Overflow(end);
                literals.Slice(literalPosition, literalLength).CopyTo(output[position..]);
                position += literalLength;
                literalPosition += literalLength;

                uint offset;
                if (offsetValue > 3)
                {
                    offset = offsetValue - 3;
                    repeat3 = repeat2;
                    repeat2 = repeat1;
                    repeat1 = offset;
                }
                else
                {
                    // Values 1..3 name a recent offset; with no literals before the match they are shifted by one, and the
                    // fourth choice is the most recent offset minus one.
                    uint choice = offsetValue + (literalLength == 0 ? 1u : 0u);
                    if (choice == 1)
                    {
                        offset = repeat1;
                    }
                    else if (choice == 2)
                    {
                        offset = repeat2;
                        repeat2 = repeat1;
                        repeat1 = offset;
                    }
                    else
                    {
                        offset = choice == 3 ? repeat3 : repeat1 - 1;
                        repeat3 = repeat2;
                        repeat2 = repeat1;
                        repeat1 = offset;
                    }
                }

                if (offset == 0 || offset > position - _frameStart || offset > _windowSize)
                    throw FormatError.Corrupted("Zstandard match offset reaches outside the window.");

                CopyMatch(output, position, (int)offset, matchLength);
                position += matchLength;
            }

            if (!bits.IsExhausted) throw FormatError.Corrupted("Zstandard sequences bitstream was not consumed exactly.");

            int remaining = literalCount - literalPosition;
            if (remaining > end - position) throw Overflow(end);
            literals[literalPosition..].CopyTo(output[position..]);
            _length = position + remaining;
            _repeat1 = repeat1;
            _repeat2 = repeat2;
            _repeat3 = repeat3;
        }

        /// <summary>Copies a match that may overlap its own output: a distance shorter than the length repeats the pattern.</summary>
        private static void CopyMatch(Span<byte> output, int position, int offset, int length)
        {
            int source = position - offset;
            if (offset >= length)
            {
                output.Slice(source, length).CopyTo(output[position..]);
            }
            else if (offset == 1)
            {
                output.Slice(position, length).Fill(output[source]);
            }
            else
            {
                // Each pass copies everything written since the match began, so the pattern doubles until it is long enough.
                int copied = 0;
                int chunk = offset;
                while (copied < length)
                {
                    int count = Math.Min(chunk, length - copied);
                    output.Slice(source, count).CopyTo(output[(position + copied)..]);
                    copied += count;
                    chunk += count;
                }
            }
        }

        private static int[] Baselines(byte[] bits, int first)
        {
            var baselines = new int[bits.Length];
            int value = first;
            for (int code = 0; code < bits.Length; code++)
            {
                baselines[code] = value;
                value += 1 << bits[code];
            }

            return baselines;
        }
    }
}
