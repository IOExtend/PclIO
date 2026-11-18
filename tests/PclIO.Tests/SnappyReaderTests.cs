using System.Security.Cryptography;
using PclIO.Internal;
using Xunit;

namespace PclIO.Tests;

public sealed class SnappyReaderTests
{
    private static byte[] DecodeAll(byte[] stream, int chunk = 1 << 16)
    {
        using var reader = new SnappyReader(new MemoryStream(stream), stream.Length);
        var result = new MemoryStream();
        byte[] buffer = new byte[chunk];
        int n;
        while ((n = reader.Read(buffer, 0, buffer.Length)) > 0) result.Write(buffer, 0, n);
        return result.ToArray();
    }

    private static byte[] Random(int length, int seed)
    {
        byte[] data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    // What a copy of the given offset and length appends, computed byte by byte.
    private static void ExpandCopy(List<byte> output, int offset, int length)
    {
        for (int i = 0; i < length; i++) output.Add(output[^offset]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(59)]
    [InlineData(60)]
    [InlineData(61)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(65535)]
    [InlineData(65536)]
    [InlineData(65537)]
    [InlineData(300_000)]
    public void Literals_Are_Decoded_At_Every_Length_Encoding_Boundary(int length)
    {
        byte[] data = Random(length, length);

        Assert.Equal(data, DecodeAll(new SnappyBuilder().Literal(data).Build()));
    }

    [Fact]
    public void A_Literal_Of_Sixteen_Million_Bytes_Uses_The_Four_Byte_Length()
    {
        byte[] data = new byte[(1 << 24) + 5];
        data[^1] = 7;

        byte[] decoded = DecodeAll(new SnappyBuilder().Literal(data).Build());

        Assert.Equal(data.Length, decoded.Length);
        Assert.Equal(7, decoded[^1]);
    }

    [Fact]
    public void Copies_Of_Every_Tag_Kind_Are_Expanded_Including_Overlapping_Ones()
    {
        byte[] start = Random(3000, 8);
        var expected = new List<byte>(start);
        var builder = new SnappyBuilder().Literal(start);

        void Copy(int offset, int length, int tagBytes)
        {
            builder.Copy(offset, length, tagBytes);
            ExpandCopy(expected, offset, length);
        }

        Copy(10, 4, 1);
        Copy(3, 11, 1);
        Copy(2047, 4, 1);
        Copy(7, 40, 2);
        Copy(1, 64, 2);
        Copy(2, 33, 2);
        Copy(60, 64, 2);
        Copy(5, 9, 4);
        Copy(100, 64, 4);

        Assert.Equal(expected, DecodeAll(builder.Build()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(100)]
    public void The_Result_Does_Not_Depend_On_How_Many_Bytes_Each_Read_Asks_For(int chunk)
    {
        byte[] literal = Random(500, 1);
        var expected = new List<byte>(literal);
        var builder = new SnappyBuilder().Literal(literal);
        foreach (var (offset, length) in new[] { (1, 64), (300, 50), (2, 33), (499, 64) })
        {
            builder.Copy(offset, length, 2);
            ExpandCopy(expected, offset, length);
        }

        Assert.Equal(expected, DecodeAll(builder.Build(), chunk));
    }

    [Fact]
    public void A_Copy_Can_Reach_Back_Several_Mebibytes()
    {
        byte[] data = Random((2 << 20) + 1000, 2);
        var expected = new List<byte>(data);
        var builder = new SnappyBuilder().Literal(data);
        builder.Copy(2 << 20, 64, 4);
        ExpandCopy(expected, 2 << 20, 64);
        builder.Copy(2 << 20, 64, 4);
        ExpandCopy(expected, 2 << 20, 64);

        Assert.Equal(expected, DecodeAll(builder.Build()));
    }

    [Fact]
    public void Copies_Stay_Correct_When_The_Window_Wraps()
    {
        // The stream declares more than the capped window, so the window wraps while an old copy is still in reach.
        byte[] data = new byte[8 << 20];
        var expected = new List<byte>(data);
        var builder = new SnappyBuilder().Literal(data);
        builder.Copy(8 << 20, 64, 4);
        ExpandCopy(expected, 8 << 20, 64);

        Assert.Equal(expected, DecodeAll(builder.Build()));
    }

    [Fact]
    public void A_Copy_Reaching_Further_Back_Than_The_Window_Is_Reported_As_Unsupported()
    {
        byte[] data = Random((8 << 20) + 1000, 3);
        byte[] stream = new SnappyBuilder().Literal(data).Copy((8 << 20) + 1, 64, 4).Build();

        Assert.Throws<NotSupportedException>(() => DecodeAll(stream));
    }

    [Fact]
    public void An_Empty_Stream_Is_Decoded_To_Nothing()
    {
        Assert.Empty(DecodeAll([0]));
    }

    [Fact]
    public void The_Declared_Length_Is_Available_Without_Decoding()
    {
        byte[] stream = new SnappyBuilder().Literal(new byte[300]).Build();
        using var source = new MemoryStream(stream);

        Assert.Equal(300, SnappyReader.ReadUncompressedLength(source, stream.Length));
        using var reader = new SnappyReader(new MemoryStream(stream), stream.Length);
        Assert.Equal(300, reader.UncompressedLength);
    }

    [Fact]
    public void Streams_From_Another_Encoder_Decode_To_The_Original_Data()
    {
        Assert.NotEmpty(SnappyVectors.All);
        foreach (var vector in SnappyVectors.All)
        {
            byte[] decoded = DecodeAll(vector.Compressed);

            Assert.Equal(vector.Length, decoded.Length);
            Assert.Equal(vector.Sha256, Convert.ToHexString(SHA256.HashData(decoded)).ToLowerInvariant());
        }
    }

    [Fact]
    public void Only_The_Declared_Compressed_Length_Is_Read_From_The_Source()
    {
        byte[] stream = new SnappyBuilder().Literal("hello"u8).Build();
        byte[] followedByOtherData = [.. stream, 0xFF, 0xFF, 0xFF];
        var source = new MemoryStream(followedByOtherData);

        using var reader = new SnappyReader(source, stream.Length);
        byte[] buffer = new byte[16];

        Assert.Equal(5, reader.Read(buffer, 0, buffer.Length));
        Assert.Equal(0, reader.Read(buffer, 0, buffer.Length));
    }

    // ---- Damaged streams -------------------------------------------------------------------------------------------

    public static TheoryData<string, byte[]> DamagedStreams =>
        new()
        {
            { "no bytes at all", [] },
            { "preamble cut short", [0x80] },
            { "preamble longer than 32 bits", [0x80, 0x80, 0x80, 0x80, 0x10] },
            { "no elements for a declared length", new SnappyBuilder().Build(4) },
            { "elements produce less than declared", new SnappyBuilder().Literal("abc"u8).Build(5) },
            { "literal longer than declared", new SnappyBuilder().Literal("abcd"u8).Build(3) },
            { "literal data cut short", new SnappyBuilder().Literal("abcdef"u8).Build()[..5] },
            { "literal length bytes cut short", [0x0A, 0xF4, 0x05] },
            { "copy with offset zero", new SnappyBuilder().Literal("a"u8).Raw(0x01, 0x00).Build(5) },
            { "copy before the start of the data", new SnappyBuilder().Literal("ab"u8).Copy(3, 4, 1).Build() },
            { "copy past the declared length", new SnappyBuilder().Literal("ab"u8).Copy(1, 5, 1).Build(4) },
            { "copy offset bytes cut short", new SnappyBuilder().Literal("ab"u8).Copy(1, 5, 2).Build()[..^1] },
            { "bytes after the last element", new SnappyBuilder().Literal("a"u8).Raw(0x00).Build() },
        };

    [Theory]
    [MemberData(nameof(DamagedStreams))]
    public void A_Damaged_Stream_Is_Rejected_As_Malformed(string description, byte[] stream)
    {
        Assert.True(description.Length > 0);

        Assert.Throws<InvalidDataException>(() => DecodeAll(stream));
    }

    [Fact]
    public void A_Stream_Shorter_Than_Its_Declared_Compressed_Length_Is_Rejected_As_Truncated()
    {
        byte[] stream = new SnappyBuilder().Literal(Random(100, 4)).Build();
        using var reader = new SnappyReader(new MemoryStream(stream[..50]), stream.Length);
        byte[] buffer = new byte[200];

        Assert.Throws<InvalidDataException>(() => reader.ReadExactly(buffer));
    }

    [Fact]
    public void Every_Prefix_Of_A_Stream_Is_Rejected()
    {
        byte[] data = Random(400, 5);
        byte[] stream = new SnappyBuilder().Literal(data).Copy(100, 60, 2).Copy(3, 9, 1).Copy(1, 20, 4).Build();

        for (int length = 0; length < stream.Length; length++)
        {
            byte[] cut = stream[..length];
            try
            {
                using var reader = new SnappyReader(new MemoryStream(cut), cut.Length);
                byte[] buffer = new byte[1024];
                while (reader.Read(buffer, 0, buffer.Length) > 0)
                {
                }

                Assert.Fail($"A stream cut to {length} of {stream.Length} bytes was accepted.");
            }
            catch (InvalidDataException)
            {
                // The expected outcome.
            }
        }
    }

    [Fact]
    public void Every_Single_Byte_Change_Is_Rejected_Or_Decoded_Without_Another_Exception()
    {
        byte[] stream = new SnappyBuilder().Literal(Random(300, 6)).Copy(100, 60, 2).Copy(3, 9, 1).Copy(1, 20, 4).Build();

        for (int i = 0; i < stream.Length; i++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                byte[] changed = [.. stream];
                changed[i] ^= (byte)(1 << bit);
                try
                {
                    DecodeAll(changed);
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
                {
                    // Rejected, which is the usual outcome; some changes keep the stream valid.
                }
            }
        }
    }

    [Fact]
    public void Reading_After_Dispose_Is_Refused()
    {
        var reader = new SnappyReader(new MemoryStream(new SnappyBuilder().Literal("abc"u8).Build()), 4);
        reader.Dispose();

        Assert.Throws<ObjectDisposedException>(() => reader.Read(new byte[3], 0, 3));
    }

    [Fact]
    public void Disposing_The_Reader_Disposes_The_Source()
    {
        var source = new MemoryStream(new SnappyBuilder().Literal("a"u8).Build());
        var reader = new SnappyReader(source, source.Length);

        reader.Dispose();

        Assert.False(source.CanRead);
    }

    [Fact]
    public void A_Preamble_That_Is_Invalid_Disposes_The_Source()
    {
        var source = new MemoryStream([0x80]);

        Assert.Throws<InvalidDataException>(() => new SnappyReader(source, source.Length));

        Assert.False(source.CanRead);
    }
}
