using System.Text;
using PclIO.Formats.Leica;
using Xunit;

namespace PclIO.Tests;

public sealed class LegacyLgsTests
{
    [Fact]
    public void Opens_Masked_Grid_And_Assigns_Progressive_Colors_To_Their_Voxels()
    {
        using var dir = new TempDirectory();
        using var doc = Assert.IsType<LgsxDocument>(PclFile.Open(dir.Write("old.lgs", CreateContainer())));
        Assert.Equal(PclFileKind.LeicaLgs, doc.Kind);
        Assert.Equal(4, doc.GetEncodedPointCount());
        var scan = Assert.Single(doc.Scans);
        Assert.True(scan.HasColors);
        Assert.True(scan.HasIntensities);
        var points = scan.ReadPoints().ToArray();
        Assert.Equal(
            new[] { new PclVector3(-2.5, 2.5, .5), new(-2.5, 2.5, 1.5), new(-1.5, 5.5, 2.5), new(-.5, 5.5, 2.5) },
            points.Select(p => p.Position));
        Assert.Equal(new byte[] { 10, 30, 20, 40 }, points.Select(p => p.Color!.Value.R));
        Assert.Equal(new int[] { 1000, 3000, 2000, 4000 }.Select(v => (float?)(v / 65535.0f)), points.Select(p => p.Intensity));
        Assert.Equal(new[] { points[0], points[2] with { Index = 1 } },
            scan.ReadPoints(new PclReadOptions { SubsampleStep = 2, MaxPoints = 2 }).ToArray());
    }

    [Fact]
    public void Rejects_Occupancy_That_Disagrees_With_The_Directory()
    {
        using var dir = new TempDirectory();
        using var doc = PclFile.Open(dir.Write("invalid.lgs", CreateContainer(invalid: true)));
        Assert.Throws<InvalidDataException>(() => Assert.Single(doc.Scans).ReadPoints().First());
    }

    private static byte[] CreateContainer(bool invalid = false)
    {
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);
        writer.Write("wild::geosystems"u8);
        long payloadStart = memory.Position;
        writer.Write("UDS4\u0007\0\0\0"u8);
        writer.Write(0u);
        writer.Write(4.0);
        writer.Write(1.0);
        writer.Write(3u);
        foreach (double value in new double[] { 1, 1, 1, -4, 2, 0, .5, .5, .5, .5, .5, .5, .5, .5, .5 })
            writer.Write(value);
        writer.Write(2u);
        WriteField("UniquePointCount");
        WriteField("4");
        WriteField("ModelAttributes");
        WriteField("udIntensity:u16 udRGB:u32");
        long metadataEnd = memory.Position;
        memory.Position = payloadStart + 8;
        writer.Write((uint)(metadataEnd - payloadStart - 8));
        memory.Position = metadataEnd;
        foreach (uint value in new uint[] { 2, 256, 0, 2, 3, 4, 1, 1 }) writer.Write(value);
        writer.Write(new byte[16]);

        const int descriptorBytes = 16;
        const int blockBytes = 20 + descriptorBytes + 3 + 4 * 6;
        WriteDescriptor();
        writer.Write(0UL);
        writer.Write(0u);
        writer.Write(6u);
        writer.Write(0x10001u);
        WriteDescriptor();
        writer.Write(new byte[] { 0x81, 0x22, invalid ? (byte)0x1c : (byte)0x0c });
        for (int i = 1; i <= 4; i++) writer.Write((ushort)(i * 1000));
        for (int i = 1; i <= 4; i++) writer.Write(new byte[] { 55, 70, (byte)(i * 10), 255 });
        memory.SetLength(payloadStart + 3 * 1024);
        memory.Position = memory.Length;
        long descriptor = memory.Position;
        writer.Write(new byte[10]);
        writer.Write((ushort)11);
        writer.Write("PointCloud\0"u8);
        writer.Write((byte)3);
        writer.Write((ulong)payloadStart);
        writer.Write((ulong)(descriptor - payloadStart));
        writer.Write(new byte[96]);
        long toc = memory.Position;
        writer.Write(1u);
        writer.Write((ulong)descriptor);
        writer.Write("Leica Project File *"u8);
        writer.Write(0u);
        writer.Write((ulong)toc);
        byte[] result = memory.ToArray();
        var key = new byte[1024];
        new Random(42).NextBytes(key);
        for (int i = (int)payloadStart; i < descriptor; i++) result[i] ^= key[(i - (int)payloadStart) % key.Length];
        return result;

        void WriteField(string value)
        {
            writer.Write((ushort)(value.Length + 1));
            writer.Write(Encoding.ASCII.GetBytes(value));
            writer.Write((byte)0);
        }

        void WriteDescriptor()
        {
            writer.Write(blockBytes);
            writer.Write(0u);
            writer.Write(8u);
            writer.Write(16u);
        }
    }
}
