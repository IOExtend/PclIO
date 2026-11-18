using System.Text;
using PclIO.Internal;
using Xunit;

namespace PclIO.Tests;

/// <summary>Checks the managed Zstandard decoder against frames from the reference compressor, against frames built
/// by hand to reach every rule of RFC 8878, and against damaged and hostile input, which must raise
/// <see cref="InvalidDataException"/> and nothing else.</summary>
public sealed class ZstdDecoderTests
{
    // ---- Content that the vectors were made from ----------------------------------------------------------------------

    private sealed class Lcg(uint seed)
    {
        private uint _state = seed;

        public uint Next()
        {
            _state = unchecked(_state * 1664525u + 1013904223u);
            return _state >> 8;
        }
    }

    private static readonly string[] Words =
        ["point", "cloud", "scan", "leica", "frame", "block", "zstd", "range", "angle", "color", "pulse", "echo", "grid", "tile", "mesh", "node"];

    private static readonly byte[] RecordFiller = [.. Enumerable.Range(0, 61).Select(k => (byte)(k * 37 + 11))];

    /// <summary>Regenerates content from a rule such as <c>text:12000:5+zeros:300</c>: parts of the form
    /// kind:length[:seed[:alphabet]] joined by '+'. The same rules produced the vectors with the reference compressor.</summary>
    private static byte[] Content(string rule)
    {
        var result = new List<byte>();
        if (rule.Length == 0) return [];

        foreach (string part in rule.Split('+'))
        {
            string[] field = part.Split(':');
            int length = int.Parse(field[1]);
            var piece = new List<byte>();
            var random = new Lcg(field.Length > 2 ? uint.Parse(field[2]) : 0);
            switch (field[0])
            {
                case "zeros":
                    piece.AddRange(new byte[length]);
                    break;
                case "random":
                    for (int i = 0; i < length; i++) piece.Add((byte)(random.Next() & 0xFF));
                    break;
                case "text":
                    while (piece.Count < length)
                    {
                        piece.AddRange(Encoding.ASCII.GetBytes(Words[random.Next() % 16] + " "));
                    }

                    break;
                case "skewed":
                    int alphabet = int.Parse(field[3]);
                    for (int i = 0; i < length; i++)
                    {
                        int value = 0;
                        while (random.Next() % 3 == 0 && value < alphabet - 1) value++;
                        piece.Add((byte)value);
                    }

                    break;
                case "nibble":
                    for (int i = 0; i < length; i++) piece.Add((byte)((random.Next() >> 12) & 0x0F));
                    break;
                case "tri":
                    for (int i = 0; i < length; i++)
                    {
                        uint first = random.Next() & 0xFF;
                        uint second = (random.Next() >> 3) & 0xFF;
                        piece.Add((byte)Math.Min(first, second));
                    }

                    break;
                case "records":
                    for (int index = 0; piece.Count < length; index++)
                    {
                        piece.Add((byte)index);
                        piece.Add((byte)(index >> 8));
                        piece.Add((byte)(random.Next() & 0x0F));
                        piece.AddRange(RecordFiller);
                    }

                    break;
                default:
                    throw new ArgumentException($"Unknown content kind '{field[0]}'.");
            }

            result.AddRange(piece.Take(length));
        }

        return [.. result];
    }

    public static TheoryData<string> VectorNames => [.. ZstdVectors.All.Select(v => v.Name)];

    private static byte[] FrameOf(string name) => Convert.FromBase64String(ZstdVectors.All.Single(v => v.Name == name).Base64);

    internal static byte[] ContentOf(string name) => Content(ZstdVectors.All.Single(v => v.Name == name).Content);

    // ---- Real frames --------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void A_Frame_From_The_Reference_Compressor_Decompresses_To_Its_Content(string name)
    {
        byte[] expected = ContentOf(name);

        byte[] actual = ZstdDecoder.Decompress(FrameOf(name), expected.Length);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void Output_One_Byte_Over_The_Limit_Is_Rejected(string name)
    {
        byte[] expected = ContentOf(name);
        if (expected.Length == 0) return;

        Assert.Throws<InvalidDataException>(() => ZstdDecoder.Decompress(FrameOf(name), expected.Length - 1));
    }

    [Fact]
    public void An_Empty_Frame_Decompresses_With_A_Limit_Of_Zero()
    {
        Assert.Empty(ZstdDecoder.Decompress(FrameOf("Empty"), 0));
        Assert.Empty(ZstdDecoder.Decompress(FrameOf("EmptyChecksum"), 0));
    }

    [Fact]
    public void The_Same_Frames_Decode_Again_And_Again_From_One_Process()
    {
        byte[] frame = FrameOf("MultiBlock");
        byte[] expected = ContentOf("MultiBlock");

        for (int i = 0; i < 3; i++) Assert.Equal(expected, ZstdDecoder.Decompress(frame, expected.Length));
    }

    [Fact]
    public void The_Checksum_Of_A_Frame_Is_The_Low_Half_Of_XxHash64_Of_Its_Content()
    {
        byte[] frame = FrameOf("TextLevel3Checksum");
        byte[] content = ContentOf("TextLevel3Checksum");

        Assert.Equal((uint)XxHash64.Hash(content), Bytes.ReadUInt32LE(frame.AsSpan(frame.Length - 4)));
    }

    [Fact]
    public void XxHash64_Of_Nothing_Is_The_Published_Value()
    {
        Assert.Equal(0xEF46DB3751D8E999UL, XxHash64.Hash([]));
    }

    // ---- Frames built by hand -----------------------------------------------------------------------------------------

    private static readonly byte[] Magic = [0x28, 0xB5, 0x2F, 0xFD];

    private static byte[] Join(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    private static byte[] BlockHeader(bool last, int type, int size)
    {
        int header = (size << 3) | (type << 1) | (last ? 1 : 0);
        return [(byte)header, (byte)(header >> 8), (byte)(header >> 16)];
    }

    private static byte[] RawBlock(byte[] data, bool last = true) => Join(BlockHeader(last, 0, data.Length), data);

    private static byte[] RleBlock(byte value, int size, bool last = true) => Join(BlockHeader(last, 1, size), [value]);

    /// <summary>A frame without content size whose window is 2^(10+exponent) bytes.</summary>
    private static byte[] WindowFrame(int exponent, params byte[][] blocks) =>
        Join(Magic, [0x00, (byte)(exponent << 3)], Join(blocks));

    /// <summary>Bytes 0, 7, 14 ... modulo 251: content that is neither constant nor periodic over short spans.</summary>
    private static byte[] Pattern(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(i * 7 % 251))];

    private static byte[] RawLiterals(byte[] literals)
    {
        int size = literals.Length;
        byte[] header = size < 32 ? [(byte)(size << 3)]
            : size < 4096 ? [(byte)(((size & 15) << 4) | 4), (byte)(size >> 4)]
            : [(byte)(((size & 15) << 4) | 12), (byte)((size >> 4) & 0xFF), (byte)(size >> 12)];
        return Join(header, literals);
    }

    private static byte[] RleLiterals(byte value, int size)
    {
        byte[] header = size < 32 ? [(byte)((size << 3) | 1)]
            : size < 4096 ? [(byte)(((size & 15) << 4) | 5), (byte)(size >> 4)]
            : [(byte)(((size & 15) << 4) | 13), (byte)((size >> 4) & 0xFF), (byte)(size >> 12)];
        return Join(header, [value]);
    }

    /// <summary>A compressed block with the given literals section and no sequences.</summary>
    private static byte[] LiteralsOnlyBlock(byte[] literalsSection, bool last = true) =>
        Join(BlockHeader(last, 2, literalsSection.Length + 1), literalsSection, [0x00]);

    /// <summary>A compressed block with raw literals and sequences that all use one literal length code, one offset code and
    /// one match length code (the RLE table mode), so that each sequence carries only its extra bits. The extra bit counts
    /// are those of the codes: offset code n has n extra bits; literal length codes 16..19 and match length codes 32..35 have 1.</summary>
    private static byte[] SequenceBlock(bool last, byte[] literals, int literalLengthCode, int offsetCode, int matchLengthCode,
        params (int Literal, int Match, int Offset)[] extraBits)
    {
        int count = extraBits.Length;
        byte[] countField = count < 128 ? [(byte)count]
            : count < 0x7F00 ? [(byte)((count >> 8) + 128), (byte)count]
            : [255, (byte)(count - 0x7F00), (byte)((count - 0x7F00) >> 8)];

        // The decoder reads the stream from its end, so the last sequence is written first; within a sequence the offset
        // bits are read first and therefore written last. A 1 bit marks the end, followed by zero bits up to a byte.
        var bits = new List<bool>();
        void Put(int value, int width)
        {
            for (int i = 0; i < width; i++) bits.Add(((value >> i) & 1) != 0);
        }

        for (int i = count - 1; i >= 0; i--)
        {
            Put(extraBits[i].Literal, literalLengthCode < 16 ? 0 : 1);
            Put(extraBits[i].Match, matchLengthCode < 32 ? 0 : 1);
            Put(extraBits[i].Offset, offsetCode);
        }

        bits.Add(true);
        while (bits.Count % 8 != 0) bits.Add(false);
        var stream = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++)
        {
            if (bits[i]) stream[i / 8] |= (byte)(1 << (i % 8));
        }

        byte[] body = Join(RawLiterals(literals), countField, [0x54, (byte)literalLengthCode, (byte)offsetCode, (byte)matchLengthCode], stream);
        return Join(BlockHeader(last, 2, body.Length), body);
    }

    [Fact]
    public void A_Match_Longer_Than_Its_Distance_Repeats_The_Pattern()
    {
        // "abc", then 10 bytes copied from 3 back (offset code 2 with extra bits 2 gives offset value 6, offset 3).
        byte[] frame = WindowFrame(0, SequenceBlock(true, "abc"u8.ToArray(), 3, 2, 7, (0, 0, 2)));

        Assert.Equal("abcabcabcabca"u8.ToArray(), ZstdDecoder.Decompress(frame, 100));
    }

    [Fact]
    public void A_Match_At_Distance_One_Repeats_The_Last_Byte()
    {
        byte[] frame = WindowFrame(0, SequenceBlock(true, "x"u8.ToArray(), 1, 2, 2, (0, 0, 0)));

        Assert.Equal("xxxxxx"u8.ToArray(), ZstdDecoder.Decompress(frame, 100));
    }

    [Fact]
    public void Repeat_Offsets_Carry_Over_Between_Blocks_And_Shift_When_No_Literals_Precede_The_Match()
    {
        // Each block has one sequence with a match of 3. Offset values: 8 (new offset 5); 1 (repeat 1); 2 with no literals
        // (the third recent offset); 3 with no literals (the most recent offset minus one).
        byte[] frame = WindowFrame(0,
            SequenceBlock(false, "abcdefgh"u8.ToArray(), 8, 3, 0, (0, 0, 0)),
            SequenceBlock(false, "Z"u8.ToArray(), 1, 0, 0, (0, 0, 0)),
            SequenceBlock(false, [], 0, 1, 0, (0, 0, 0)),
            SequenceBlock(true, [], 0, 1, 0, (0, 0, 1)));

        Assert.Equal("abcdefghdefZhdeZhdZhd"u8.ToArray(), ZstdDecoder.Decompress(frame, 100));
    }

    [Fact]
    public void A_Block_Can_Hold_More_Than_32511_Sequences()
    {
        // 32512 sequences need the three-byte count. Each has one literal and a match of 3 at distance 1.
        byte[] literals = Pattern(32512);
        var sequences = new (int, int, int)[32512];
        byte[] frame = WindowFrame(7, SequenceBlock(true, literals, 1, 0, 0, sequences));

        byte[] expected = [.. literals.SelectMany(b => new[] { b, b, b, b })];
        Assert.Equal(expected, ZstdDecoder.Decompress(frame, expected.Length));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(100000)]
    public void Raw_And_Run_Length_Literals_Are_Read_With_Every_Header_Size(int size)
    {
        byte[] raw = WindowFrame(7, LiteralsOnlyBlock(RawLiterals(Pattern(size))));
        byte[] rle = WindowFrame(7, LiteralsOnlyBlock(RleLiterals(0x5A, size)));

        Assert.Equal(Pattern(size), ZstdDecoder.Decompress(raw, size));
        Assert.Equal(Enumerable.Repeat((byte)0x5A, size).ToArray(), ZstdDecoder.Decompress(rle, size));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 300)]
    [InlineData(2, 70000)]
    [InlineData(3, 5)]
    public void The_Content_Size_Field_Is_Read_In_Each_Of_Its_Widths(int flag, int size)
    {
        // Single-segment frames: the field is 1, 2 (stored minus 256), 4 or 8 bytes.
        byte[] field = flag switch
        {
            0 => [(byte)size],
            1 => [(byte)(size - 256), (byte)((size - 256) >> 8)],
            2 => BitConverter.GetBytes(size),
            _ => BitConverter.GetBytes((long)size),
        };
        byte[] frame = Join(Magic, [(byte)(0x20 | (flag << 6))], field, RawBlock(Pattern(size)));

        Assert.Equal(Pattern(size), ZstdDecoder.Decompress(frame, size));
    }

    [Fact]
    public void A_Frame_With_A_Window_Descriptor_And_A_Content_Size_Is_Read()
    {
        // Content_Size_Flag 2 (4 bytes) without Single_Segment.
        byte[] frame = Join(Magic, [0x80, 0x00], BitConverter.GetBytes(40), RawBlock(Pattern(40)));

        Assert.Equal(Pattern(40), ZstdDecoder.Decompress(frame, 40));
    }

    [Fact]
    public void A_Window_Of_Exactly_128_MiB_Is_Accepted()
    {
        Assert.Empty(ZstdDecoder.Decompress(WindowFrame(17, RawBlock([])), 0));
    }

    // ---- Hostile and damaged input ------------------------------------------------------------------------------------

    private static void AssertRejected(byte[] input, int limit = 1 << 20) =>
        Assert.Throws<InvalidDataException>(() => ZstdDecoder.Decompress(input, limit));

    [Fact]
    public void Nothing_At_All_Is_Rejected()
    {
        AssertRejected([]);
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })]
    [InlineData(new byte[] { 0x27, 0xB5, 0x2F, 0xFD, 0x00, 0x00 })] // the magic number of a legacy format
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F })]
    public void A_Wrong_Magic_Number_Is_Rejected(byte[] input)
    {
        AssertRejected(input);
    }

    [Fact]
    public void Bytes_After_The_Last_Frame_Are_Rejected()
    {
        AssertRejected([.. FrameOf("TextLevel3"), 0x00]);
        AssertRejected([.. FrameOf("TextLevel3"), .. FrameOf("TextLevel3")[..3]]);
        AssertRejected([.. FrameOf("TextLevel3"), 1, 2, 3, 4]);
    }

    [Theory]
    [InlineData("OneByte")]
    [InlineData("TextLevel3Checksum")]
    [InlineData("RepeatOffsets")]
    public void A_Frame_Cut_Short_At_Any_Length_Is_Rejected(string name)
    {
        byte[] frame = FrameOf(name);

        for (int length = 0; length < frame.Length; length++)
        {
            byte[] cut = frame[..length];
            try
            {
                ZstdDecoder.Decompress(cut, 1 << 20);
                Assert.Fail($"{name} cut to {length} bytes was accepted.");
            }
            catch (InvalidDataException)
            {
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                Assert.Fail($"{name} cut to {length} bytes threw {ex.GetType().Name} instead of InvalidDataException: {ex.Message}");
            }
        }
    }

    [Fact]
    public void A_Skippable_Frame_That_Claims_More_Bytes_Than_Follow_Is_Rejected()
    {
        AssertRejected([0x5B, 0x2A, 0x4D, 0x18, 0x10, 0x00, 0x00, 0x00, 1, 2, 3]);
        AssertRejected([0x5B, 0x2A, 0x4D, 0x18, 0xFF, 0xFF, 0xFF, 0xFF, 1, 2, 3]);
        AssertRejected([0x5B, 0x2A, 0x4D, 0x18, 0x10, 0x00]);
    }

    [Fact]
    public void A_Wrong_Checksum_Is_Rejected()
    {
        byte[] frame = FrameOf("TextLevel3Checksum");
        for (int i = 1; i <= 4; i++)
        {
            byte[] damaged = (byte[])frame.Clone();
            damaged[^i] ^= 0x01;
            AssertRejected(damaged);
        }
    }

    [Fact]
    public void A_Changed_Byte_In_A_Checksummed_Frame_Is_Rejected()
    {
        // In a frame that stored its content uncompressed, any change to the content must show up in the checksum.
        byte[] frame = FrameOf("OneByte");
        byte[] damaged = (byte[])frame.Clone();
        damaged[^5] ^= 0x01; // the single content byte, just before the 4-byte checksum

        AssertRejected(damaged);
    }

    [Fact]
    public void A_Declared_Content_Size_Above_The_Limit_Is_Rejected()
    {
        byte[] frame = FrameOf("TextLevel3"); // declares 12000 bytes

        AssertRejected(frame, 11999);
        Assert.Equal(12000, ZstdDecoder.Decompress(frame, 12000).Length);
    }

    [Theory]
    [InlineData(1UL << 40)]
    [InlineData(ulong.MaxValue)]
    [InlineData(1UL << 31)]
    public void An_Enormous_Declared_Content_Size_Is_Rejected_Without_Reserving_Memory(ulong declared)
    {
        byte[] frame = Join(Magic, [0xE0], BitConverter.GetBytes(declared), RawBlock([]));

        AssertRejected(frame, 1 << 20);
        AssertRejected(frame, int.MaxValue - 1);
    }

    [Fact]
    public void A_Content_Size_That_Differs_From_The_Content_Is_Rejected()
    {
        AssertRejected(Join(Magic, [0x20, 5], RawBlock(Pattern(4))));
        AssertRejected(Join(Magic, [0x20, 3], RawBlock(Pattern(4))));
        AssertRejected(Join(Magic, [0x20, 5], RawBlock(Pattern(3), last: false), RleBlock(7, 3)));
    }

    [Fact]
    public void A_Dictionary_Id_Is_Rejected()
    {
        AssertRejected(Join(Magic, [0x21, 0x01, 0x00], RawBlock([])));
        AssertRejected(Join(Magic, [0x22, 0x00, 0x01, 0x00], RawBlock([])));
        AssertRejected(Join(Magic, [0x23, 0x00, 0x00, 0x00, 0x80, 0x00], RawBlock([])));
    }

    [Fact]
    public void A_Reserved_Bit_In_The_Frame_Header_Is_Rejected()
    {
        AssertRejected(Join(Magic, [0x28, 0x00], RawBlock([])));
        AssertRejected(Join(Magic, [0x08, 0x00], RawBlock([]))); // single-segment bit clear, window byte present
    }

    [Theory]
    [InlineData(0x90)] // 2^28
    [InlineData(0x89)] // 2^27 + 2^24
    [InlineData(0xF8)] // 2^41
    [InlineData(0xFF)]
    public void A_Window_Above_128_MiB_Is_Rejected(byte descriptor)
    {
        AssertRejected(Join(Magic, [0x00, descriptor], RawBlock([])));
    }

    [Fact]
    public void A_Block_Above_128_KiB_Is_Rejected_Even_When_All_Its_Bytes_Are_Present()
    {
        AssertRejected(WindowFrame(10, RawBlock(new byte[131073])), 1 << 24);
        AssertRejected(WindowFrame(10, RleBlock(0, 131073)), 1 << 24);
        Assert.Equal(131072, ZstdDecoder.Decompress(WindowFrame(10, RawBlock(new byte[131072])), 1 << 24).Length);
    }

    [Fact]
    public void A_Block_Larger_Than_The_Window_Is_Rejected()
    {
        AssertRejected(WindowFrame(0, RawBlock(new byte[1025])));
        Assert.Equal(1024, ZstdDecoder.Decompress(WindowFrame(0, RawBlock(new byte[1024])), 4096).Length);
    }

    [Fact]
    public void The_Reserved_Block_Type_Is_Rejected()
    {
        AssertRejected(WindowFrame(0, BlockHeader(true, 3, 0)));
    }

    [Fact]
    public void A_Run_Length_Bomb_Is_Stopped_At_The_Limit()
    {
        // 4 bytes of input per 128 KiB of output: a few kilobytes of input stand for gigabytes.
        byte[][] blocks = [.. Enumerable.Range(0, 20000).Select(i => RleBlock(0xAA, 131072, last: i == 19999))];
        byte[] frame = WindowFrame(7, blocks);

        AssertRejected(frame, 1 << 20);
        AssertRejected(frame, 131072 * 8 + 1);
    }

    [Fact]
    public void A_Run_Length_Bomb_Across_Frames_Is_Stopped_At_The_Limit()
    {
        byte[] oneFrame = WindowFrame(7, RleBlock(0xAA, 131072));

        AssertRejected(Join(Enumerable.Repeat(oneFrame, 100).ToArray()), 131072 * 50);
        Assert.Equal(131072 * 50, ZstdDecoder.Decompress(Join(Enumerable.Repeat(oneFrame, 50).ToArray()), 131072 * 50).Length);
    }

    [Fact]
    public void A_Frame_That_Never_Ends_Is_Rejected_As_Truncated()
    {
        AssertRejected(WindowFrame(7, RleBlock(1, 10, last: false)));
        AssertRejected(WindowFrame(7, RawBlock([1, 2, 3], last: false)));
    }

    [Fact]
    public void Truncated_Block_Content_Is_Rejected()
    {
        AssertRejected(Join(Magic, [0x00, 0x00], BlockHeader(true, 0, 10), [1, 2, 3]));
        AssertRejected(Join(Magic, [0x00, 0x00], BlockHeader(true, 1, 10)));
        AssertRejected(Join(Magic, [0x00, 0x00], BlockHeader(true, 2, 10), [1, 2, 3]));
        AssertRejected(Join(Magic, [0x00, 0x00], [0x01, 0x00]));
    }

    [Fact]
    public void A_Match_That_Reaches_Before_The_Start_Of_The_Frame_Is_Rejected()
    {
        // Three bytes of history; offset code 2 with extra bits 3 gives offset value 7, offset 4.
        AssertRejected(WindowFrame(0, SequenceBlock(true, "abc"u8.ToArray(), 3, 2, 0, (0, 0, 3))));
    }

    [Fact]
    public void A_Match_Cannot_Reach_Into_An_Earlier_Frame()
    {
        byte[] first = WindowFrame(0, RawBlock("abcdef"u8.ToArray()));
        byte[] second = WindowFrame(0, SequenceBlock(true, "ab"u8.ToArray(), 2, 2, 0, (0, 0, 2)));

        AssertRejected(Join(first, second));
        Assert.Equal("abcdef"u8.ToArray(), ZstdDecoder.Decompress(first, 100));
    }

    [Fact]
    public void A_Match_Beyond_The_Window_Is_Rejected_But_One_At_The_Window_Is_Accepted()
    {
        // Two raw blocks of 1000 bytes fill a 1 KiB window frame; offset code 10 with extra bits e gives offset 1021 + e.
        byte[] history = Join(RawBlock(Pattern(1000), last: false), RawBlock(Pattern(1000), last: false));

        byte[] atWindow = WindowFrame(0, history, SequenceBlock(true, [], 0, 10, 0, (0, 0, 3)));
        byte[] pastWindow = WindowFrame(0, history, SequenceBlock(true, [], 0, 10, 0, (0, 0, 4)));

        Assert.Equal(2003, ZstdDecoder.Decompress(atWindow, 4096).Length);
        AssertRejected(pastWindow, 4096);
    }

    [Fact]
    public void A_Sequence_That_Needs_More_Literals_Than_The_Block_Has_Is_Rejected()
    {
        AssertRejected(WindowFrame(0, SequenceBlock(true, "ab"u8.ToArray(), 3, 2, 0, (0, 0, 0))));
    }

    [Fact]
    public void An_Offset_Of_Zero_From_The_Most_Recent_Offset_Minus_One_Is_Rejected()
    {
        // The first block leaves the most recent offset at 1; offset value 3 without literals then means offset 0.
        byte[] frame = WindowFrame(0,
            SequenceBlock(false, "ab"u8.ToArray(), 2, 2, 0, (0, 0, 0)),
            SequenceBlock(true, [], 0, 1, 0, (0, 0, 1)));

        AssertRejected(frame);
    }

    [Fact]
    public void Tables_Do_Not_Carry_Over_To_The_Next_Frame()
    {
        byte[] first = WindowFrame(0, SequenceBlock(true, "ab"u8.ToArray(), 2, 0, 0, (0, 0, 0)));
        byte[] repeating = Join(RawLiterals("ab"u8.ToArray()), [0x01, 0xFC, 0x01]);
        byte[] second = WindowFrame(0, Join(BlockHeader(true, 2, repeating.Length), repeating));

        AssertRejected(Join(first, second));
    }

    [Fact]
    public void A_Repeated_Table_Without_An_Earlier_One_Is_Rejected()
    {
        // Compression modes 0xFC: all three tables "repeat", but this is the first block with sequences.
        byte[] body = Join(RawLiterals("abc"u8.ToArray()), [0x01, 0xFC, 0x01]);

        AssertRejected(WindowFrame(0, Join(BlockHeader(true, 2, body.Length), body)));
    }

    [Fact]
    public void Reserved_Bits_In_The_Symbol_Compression_Modes_Are_Rejected()
    {
        // Identical to a valid block (literal length 2, offset 1, match length 3) except for one reserved mode bit.
        byte[] valid = Join(RawLiterals("ab"u8.ToArray()), [0x01, 0x54, 0x02, 0x00, 0x00, 0x01]);
        byte[] reserved = Join(RawLiterals("ab"u8.ToArray()), [0x01, 0x55, 0x02, 0x00, 0x00, 0x01]);

        Assert.Equal("abbbb"u8.ToArray(), ZstdDecoder.Decompress(WindowFrame(0, Join(BlockHeader(true, 2, valid.Length), valid)), 100));
        AssertRejected(WindowFrame(0, Join(BlockHeader(true, 2, reserved.Length), reserved)));
    }

    [Fact]
    public void A_Sequence_Section_With_Bytes_After_A_Count_Of_Zero_Is_Rejected()
    {
        byte[] body = Join(RawLiterals("abc"u8.ToArray()), [0x00, 0x00]);

        AssertRejected(WindowFrame(0, Join(BlockHeader(true, 2, body.Length), body)));
    }

    [Fact]
    public void A_Sequence_Bitstream_With_Unused_Bits_Is_Rejected()
    {
        // The stream of the first sequence carries 4 spare zero bits below its end marker.
        byte[] valid = WindowFrame(0, SequenceBlock(true, "ab"u8.ToArray(), 2, 0, 0, (0, 0, 0)));
        Assert.Equal("abbbb"u8.ToArray(), ZstdDecoder.Decompress(valid, 100));

        byte[] body = Join(RawLiterals("ab"u8.ToArray()), [0x01, 0x54, 0x02, 0x00, 0x00, 0x10]);
        AssertRejected(WindowFrame(0, Join(BlockHeader(true, 2, body.Length), body)));
    }

    [Fact]
    public void A_Bitstream_Without_An_End_Marker_Is_Rejected()
    {
        byte[] body = Join(RawLiterals("ab"u8.ToArray()), [0x01, 0x54, 0x02, 0x00, 0x00, 0x00]);

        AssertRejected(WindowFrame(0, Join(BlockHeader(true, 2, body.Length), body)));
    }

    // A Huffman tree of two symbols, 0 and 1, each with weight 1 and so a one-bit code: symbol 0 is "0", symbol 1 is "1".
    private static readonly byte[] TwoSymbolTree = [0x80, 0x10];

    /// <summary>Literals section header for Huffman literals: type 2 (tree included) or 3 (treeless), regenerated and
    /// compressed sizes below 1024, one stream (size format 0) or four (size format 1).</summary>
    private static byte[] HuffmanLiteralsHeader(int type, bool fourStreams, int regenerated, int compressed)
    {
        int header = type | ((fourStreams ? 1 : 0) << 2) | (regenerated << 4) | (compressed << 14);
        return [(byte)header, (byte)(header >> 8), (byte)(header >> 16)];
    }

    [Fact]
    public void Huffman_Literals_Are_Decoded_From_One_Stream_And_From_Four()
    {
        // Single stream: the bits 0,1,1,0 below an end marker are 0b10110.
        byte[] one = Join(HuffmanLiteralsHeader(2, false, 4, 3), TwoSymbolTree, [0x16]);
        // Four streams of one symbol each, behind a jump table of three 2-byte sizes: 0b10 plus the symbol.
        byte[] four = Join(HuffmanLiteralsHeader(2, true, 4, 12), TwoSymbolTree, [1, 0, 1, 0, 1, 0], [0x02, 0x03, 0x03, 0x02]);

        Assert.Equal(new byte[] { 0, 1, 1, 0 }, ZstdDecoder.Decompress(WindowFrame(0, LiteralsOnlyBlock(one)), 100));
        Assert.Equal(new byte[] { 0, 1, 1, 0 }, ZstdDecoder.Decompress(WindowFrame(0, LiteralsOnlyBlock(four)), 100));
    }

    [Fact]
    public void Treeless_Literals_Reuse_The_Tree_Of_The_Block_Before()
    {
        byte[] first = Join(HuffmanLiteralsHeader(2, false, 4, 3), TwoSymbolTree, [0x16]);
        byte[] second = Join(HuffmanLiteralsHeader(3, false, 4, 1), [0x1A]); // bits 1,0,1,0

        byte[] frame = WindowFrame(0, LiteralsOnlyBlock(first, last: false), LiteralsOnlyBlock(second));

        Assert.Equal(new byte[] { 0, 1, 1, 0, 1, 0, 1, 0 }, ZstdDecoder.Decompress(frame, 100));
    }

    [Fact]
    public void Treeless_Literals_Without_An_Earlier_Tree_Are_Rejected()
    {
        // Their stream holds no bits, so only the missing tree can explain a refusal of 4 symbols.
        byte[] section = Join(HuffmanLiteralsHeader(3, false, 4, 1), [0x01]);

        AssertRejected(WindowFrame(0, LiteralsOnlyBlock(section)));
    }

    [Fact]
    public void A_Tree_Does_Not_Carry_Over_To_The_Next_Frame()
    {
        byte[] first = WindowFrame(0, LiteralsOnlyBlock(Join(HuffmanLiteralsHeader(2, false, 4, 3), TwoSymbolTree, [0x16])));
        byte[] second = WindowFrame(0, LiteralsOnlyBlock(Join(HuffmanLiteralsHeader(3, false, 4, 1), [0x1A])));

        AssertRejected(Join(first, second));
    }

    [Fact]
    public void A_Tree_Whose_Weight_Stream_Is_Too_Short_For_Its_Start_States_Is_Rejected()
    {
        // FSE-coded weights: a 5-bit table of two equally likely symbols (1 and 2), then a stream that holds no bits.
        byte[] tree = [0x04, 0x10, 0x88, 0x1F, 0x01];
        byte[] section = Join(HuffmanLiteralsHeader(2, false, 4, tree.Length + 1), tree, [0x16]);

        AssertRejected(WindowFrame(0, LiteralsOnlyBlock(section)));
    }

    [Fact]
    public void A_Huffman_Stream_With_Bits_Left_Over_Or_Missing_Is_Rejected()
    {
        byte[] leftOver = Join(HuffmanLiteralsHeader(2, false, 4, 3), TwoSymbolTree, [0x36]); // 5 bits for 4 symbols
        byte[] missing = Join(HuffmanLiteralsHeader(2, false, 4, 3), TwoSymbolTree, [0x06]); // 2 bits for 4 symbols

        AssertRejected(WindowFrame(0, LiteralsOnlyBlock(leftOver)));
        AssertRejected(WindowFrame(0, LiteralsOnlyBlock(missing)));
    }

    [Fact]
    public void A_Huffman_Tree_Without_A_Symbol_Of_Weight_1_Is_Rejected()
    {
        // Direct weights 3 and 2 plus the implied last weight 2 add up to 8 slots, a complete tree, yet no code has the
        // longest length that the weights announce.
        byte[] tree = [0x81, 0x32];
        byte[] section = Join(HuffmanLiteralsHeader(2, false, 3, tree.Length + 1), tree, [0x01]);

        var error = Assert.Throws<InvalidDataException>(() => ZstdDecoder.Decompress(WindowFrame(0, LiteralsOnlyBlock(section)), 100));
        Assert.Contains("longest code", error.Message);
    }

    [Theory]
    [InlineData(1 << 27, false)] // 128 MiB is the largest supported window
    [InlineData((1 << 27) + 1, true)]
    [InlineData(200 << 20, true)]
    public void A_Single_Segment_Frame_Is_Limited_To_The_Largest_Window_By_Its_Content_Size(int contentSize, bool rejectedAtTheHeader)
    {
        // Descriptor 0xE0: 8-byte content size, single segment. The frame is cut short after its header.
        byte[] frame = Join(Magic, [0xE0], BitConverter.GetBytes((long)contentSize));

        var error = Assert.Throws<InvalidDataException>(() => ZstdDecoder.Decompress(frame, 256 << 20));
        Assert.Equal(rejectedAtTheHeader, error.Message.Contains("window"));
    }

    [Fact]
    public void Huffman_Stream_Sizes_That_Do_Not_Fit_The_Literals_Are_Rejected()
    {
        // Jump table sizes 1, 1, 9 leave a negative size for the fourth stream.
        byte[] section = Join(HuffmanLiteralsHeader(2, true, 4, 12), TwoSymbolTree, [1, 0, 1, 0, 9, 0], [0x02, 0x03, 0x03, 0x02]);

        AssertRejected(WindowFrame(0, LiteralsOnlyBlock(section)));
    }

    [Theory]
    [InlineData(0, 0, "abcdefghijjjj")] // value 1: the most recent offset, 1 at the start
    [InlineData(1, 0, "abcdefghijghi")] // value 2: the second recent offset, 4 at the start
    [InlineData(1, 1, "abcdefghijcde")] // value 3: the third recent offset, 8 at the start
    public void The_Initial_Recent_Offsets_Are_1_4_And_8(int offsetCode, int extraBits, string expected)
    {
        byte[] frame = WindowFrame(0, SequenceBlock(true, "abcdefghij"u8.ToArray(), 10, offsetCode, 0, (0, 0, extraBits)));

        Assert.Equal(Encoding.ASCII.GetBytes(expected), ZstdDecoder.Decompress(frame, 100));
    }

    [Theory]
    [InlineData(0, 0, "abcdefghijghi")] // value 1 without literals: the second recent offset, 4
    [InlineData(1, 0, "abcdefghijcde")] // value 2 without literals: the third recent offset, 8
    public void Without_Literals_The_Initial_Recent_Offsets_Shift_By_One(int offsetCode, int extraBits, string expected)
    {
        byte[] frame = WindowFrame(0, RawBlock("abcdefghij"u8.ToArray(), last: false), SequenceBlock(true, [], 0, offsetCode, 0, (0, 0, extraBits)));

        Assert.Equal(Encoding.ASCII.GetBytes(expected), ZstdDecoder.Decompress(frame, 100));
    }

    [Theory]
    [InlineData("TextLevel3")]
    [InlineData("RepeatOffsets")]
    [InlineData("HuffmanFseWeights")]
    [InlineData("HuffmanDirectWeights")]
    [InlineData("MixedContent")]
    public void A_Frame_With_Any_Single_Bit_Flipped_Is_Rejected_Or_Decoded_Without_Another_Exception(string name)
    {
        byte[] frame = FrameOf(name);
        int limit = ContentOf(name).Length + 4096;

        for (int position = 0; position < frame.Length; position++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                byte[] damaged = (byte[])frame.Clone();
                damaged[position] ^= (byte)(1 << bit);
                try
                {
                    Assert.True(ZstdDecoder.Decompress(damaged, limit).Length <= limit);
                }
                catch (InvalidDataException)
                {
                }
                catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
                {
                    Assert.Fail($"{name}: bit {bit} of byte {position} flipped threw {ex.GetType().Name} instead of InvalidDataException: {ex.Message}");
                }
            }
        }
    }

    [Fact]
    public void Random_Bytes_Behind_A_Valid_Header_Are_Rejected_Or_Decoded_Without_Another_Exception()
    {
        var random = new Lcg(12345);
        for (int round = 0; round < 3000; round++)
        {
            int length = 1 + (int)(random.Next() % 200);
            byte[] body = new byte[length];
            for (int i = 0; i < length; i++) body[i] = (byte)(random.Next() & 0xFF);
            byte[] frame = WindowFrame(7, Join(BlockHeader(true, 2, length), body));
            try
            {
                Assert.True(ZstdDecoder.Decompress(frame, 1 << 20).Length <= 1 << 20);
            }
            catch (InvalidDataException)
            {
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                Assert.Fail($"Round {round} threw {ex.GetType().Name} instead of InvalidDataException: {ex.Message}");
            }
        }
    }
}
