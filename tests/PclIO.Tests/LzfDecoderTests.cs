using PclIO.Internal;
using Xunit;

namespace PclIO.Tests;

/// <summary>Hand-built LZF streams: every vector is computed from the token rules, not by the test encoder.</summary>
public sealed class LzfDecoderTests
{
    private static byte[] Decode(byte[] stream, int expected) => LzfDecoder.Decompress(stream, expected);

    [Fact]
    public void Empty_Stream_Decodes_To_Empty() =>
        Assert.Empty(Decode([], 0));

    [Fact]
    public void A_Literal_Run_Copies_Its_Bytes() =>
        Assert.Equal("abc"u8.ToArray(), Decode([0x02, (byte)'a', (byte)'b', (byte)'c'], 3));

    [Fact]
    public void A_Literal_Run_Holds_At_Most_32_Bytes()
    {
        byte[] payload = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        Assert.Equal(payload, Decode([0x1F, .. payload], 32));
    }

    [Fact]
    public void A_Match_Copies_From_The_Output()
    {
        // 'a', then a match of offset 1 and 9 bytes: control (7 << 5) | 0, extra 0, low 0.
        Assert.Equal("aaaaaaaaaa"u8.ToArray(), Decode([0x00, (byte)'a', 0xE0, 0x00, 0x00], 10));
    }

    [Fact]
    public void A_Match_May_Overlap_Its_Own_Output()
    {
        // "ab", then a match of offset 2 and 18 bytes: control (7 << 5) | 0, extra 9, low 1.
        byte[] expected = "abababababababababab"u8.ToArray();
        Assert.Equal(expected, Decode([0x01, (byte)'a', (byte)'b', 0xE0, 0x09, 0x01], 20));
    }

    [Fact]
    public void A_Match_Without_Extra_Length_Copies_Up_To_Nine_Bytes()
    {
        // "abcdef", then a match of offset 6 and 5 bytes: control (3 << 5) | 0, low 5.
        byte[] expected = "abcdefabcde"u8.ToArray();
        Assert.Equal(expected, Decode([0x05, .. "abcdef"u8.ToArray(), 0x60, 0x05], 11));
    }

    [Fact]
    public void A_Match_Can_Reach_8192_Bytes_Back()
    {
        byte[] prefix = Enumerable.Range(0, 8192).Select(i => (byte)(i & 0xFF)).ToArray();
        var stream = new List<byte>();
        for (int at = 0; at < prefix.Length; at += 32)
        {
            stream.Add(0x1F);
            stream.AddRange(prefix.AsSpan(at, 32).ToArray());
        }

        // Offset 8192 is (31 << 8) + 255 + 1: control (1 << 5) | 31, low 255; copies 3 bytes.
        stream.Add(0x3F);
        stream.Add(0xFF);
        byte[] expected = [.. prefix, prefix[0], prefix[1], prefix[2]];
        Assert.Equal(expected, Decode([.. stream], expected.Length));
    }

    [Fact]
    public void A_Round_Trip_Through_The_Test_Encoder_Restores_Random_Data()
    {
        var random = new Random(42);
        foreach (int length in new[] { 0, 1, 31, 32, 33, 300, 9000 })
        {
            byte[] data = new byte[length];
            random.NextBytes(data);
            // Repetitions make matches likely at every size.
            for (int i = 100; i + 10 < length; i += 100) Array.Copy(data, 0, data, i, 10);

            byte[] compressed = SyntheticFixtures.LzfCompress(data);
            Assert.Equal(data, Decode(compressed, data.Length));
        }
    }

    [Theory]
    [InlineData(new byte[] { 0x05, (byte)'a' })] // literal run ends early
    [InlineData(new byte[] { 0x00, (byte)'a', 0x20, 0x05 })] // match of offset 6 with one byte out
    [InlineData(new byte[] { 0x00, (byte)'a' })] // literal ends early with output missing
    public void A_Truncated_Stream_Is_Rejected(byte[] stream) =>
        Assert.Throws<InvalidDataException>(() => Decode(stream, 4));

    [Fact]
    public void A_Output_Overrun_Is_Rejected() =>
        Assert.Throws<InvalidDataException>(() => Decode([0x1F, .. new byte[32]], 10));

    [Fact]
    public void Trailing_Bytes_Are_Rejected() =>
        Assert.Throws<InvalidDataException>(() => Decode([0x00, (byte)'a', 0x00, (byte)'b'], 1));
}
