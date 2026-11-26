using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using PclIO.Formats.E57;
using PclIO.Formats.Faro;
using PclIO.Formats.Leica;
using Xunit;

namespace PclIO.Tests;

public sealed class NativeScanCodecTests
{
    [Fact]
    public void Lgsx_Morton96_Accumulates_Deltas_And_Keeps_Duplicate_Returns()
    {
        using var dir = new TempDirectory();
        byte[] data = SyntheticFixtures.CreateSyntheticLgsx(SyntheticFixtures.DefaultLeicaPoints.Take(4).ToArray(),
            nativeLeicaBlock: true);
        using var doc = LgsxDocument.Open(dir.Write("native.lgsx", data));
        Assert.Equal(4, doc.GetEncodedPointCount());
        var scan = Assert.Single(doc.Scans);
        var points = scan.ReadPoints(new PclReadOptions { Strict = true }).ToArray();

        Assert.Equal(new[] { 102.048, 102.049, 102.049, 102.050 }, points.Select(p => p.Position.X));
        Assert.Equal(new[] { 200.002, 200.002, 200.002, 200.003 }, points.Select(p => p.Position.Y));
        Assert.Equal(new[] { 299.996, 299.996, 299.996, 299.997 }, points.Select(p => p.Position.Z));
        Assert.Equal(new[] { 255, 256, 256, 255 }.Select(v => (float?)(v / 65535.0f)), points.Select(p => p.Intensity));
        Assert.True(scan.HasIntensities);
        Assert.False(scan.HasColors);
        Assert.Equal(new[] { points[0], points[2] with { Index = 1 } },
            scan.ReadPoints(new PclReadOptions { Strict = true, MaxDegreeOfParallelism = 2, SubsampleStep = 2 }));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void Lgsx_Morton96_Decodes_Native_Rgb_Along_The_Point_Sequence(int count)
    {
        using var dir = new TempDirectory();
        var source = SyntheticFixtures.DefaultLeicaPoints.Take(count).ToArray();
        byte[] data = SyntheticFixtures.CreateSyntheticLgsx(source, withColorAndIntensity: true, nativeLeicaBlock: true);
        using var doc = LgsxDocument.Open(dir.Write("rgb.lgsx", data));
        var scan = Assert.Single(doc.Scans);

        Assert.True(scan.HasColors);
        PclColor?[] expected = [new(230, 120, 20), new(67, 200, 254), new(2, 240, 5), new(255, 20, 30)];
        Assert.Equal(expected.Take(count), scan.ReadPoints(new PclReadOptions { Strict = true }).Select(point => point.Color));
        Assert.Equal(expected.Take(count).Where((_, index) => index % 2 == 0),
            scan.ReadPoints(new PclReadOptions { Strict = true, SubsampleStep = 2, MaxDegreeOfParallelism = 2 })
                .Select(point => point.Color));
    }

    [Fact]
    public void Lgsx_Morton96_Rejects_A_Count_That_Does_Not_Match_Its_Planes()
    {
        using var dir = new TempDirectory();
        byte[] data = SyntheticFixtures.CreateSyntheticLgsx(SyntheticFixtures.DefaultLeicaPoints.Take(4).ToArray(),
            nativeLeicaBlock: true);
        int firstBlock = data.AsSpan().IndexOf("APRN"u8);
        Assert.True(firstBlock > 0);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(firstBlock + 20), 5);
        using var doc = LgsxDocument.Open(dir.Write("short.lgsx", data));

        Assert.Throws<InvalidDataException>(() => doc.ReadAllPoints().ToArray());
    }

    [Theory]
    [InlineData((byte)'*')]
    [InlineData((byte)'X')]
    public void Leica_Unknown_Point_Payload_Reports_An_Error_For_Its_Container(byte marker)
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticLgsx(containerMarker: marker, assignPointsToSetups: true);
        int pointData = bytes.AsSpan().IndexOf("mMPK"u8);
        Assert.True(pointData > 0);
        bytes.AsSpan(pointData, 4).Clear();
        using var doc = LgsxDocument.Open(dir.Write("scan.lgsx", bytes));
        Assert.Contains(doc.Entries, entry => entry.Name == "PointCloud");
        var scan = Assert.Single(doc.Scans);
        if (marker == (byte)'*')
        {
            Assert.Equal(PclFileKind.LeicaLgs, doc.Kind);
            Assert.Null(doc.GetEncodedPointCount());
            Assert.Null(Assert.Single(doc.Setups).Translation);
            Assert.False(scan.HasColors);
            Assert.Throws<NotSupportedException>(() => scan.PointCount);
            var error = Assert.Throws<NotSupportedException>(() => scan.ReadPoints().First());
            Assert.Contains("Convert the project to LGSx", error.Message);
        }
        else
        {
            Assert.Equal(PclFileKind.LeicaLgsx, doc.Kind);
            Assert.Throws<InvalidDataException>(() => scan.ReadPoints().First());
        }
    }

    [Fact]
    public void E57_Image_Blob_Checks_Header_And_Each_Page()
    {
        const int blobOffset = 80;
        byte[] image = Enumerable.Range(0, 1_201).Select(i => unchecked((byte)i)).ToArray();
        byte[] section = new byte[1_220];
        BinaryPrimitives.WriteUInt64LittleEndian(section.AsSpan(8), (ulong)section.Length);
        image.CopyTo(section, 16);
        byte[] file = new E57Spec { Points = [], RawPackets = section }.Build();
        using var dir = new TempDirectory();
        string path = dir.Write("image.e57", file);
        using var doc = E57Document.Open(path);

        Assert.Equal(image, doc.ReadImageBlob(blobOffset, image.Length));
        Assert.Throws<InvalidDataException>(() => doc.ReadImageBlob(blobOffset, image.Length - 1));
        Assert.Throws<InvalidDataException>(() => doc.ReadImageBlob(1020, image.Length));

        file[1054] ^= 1;
        File.WriteAllBytes(path, file);
        Assert.Throws<InvalidDataException>(() => doc.ReadImageBlob(blobOffset, image.Length));
    }

    [Fact]
    public void Faro_Delta_Planes_Keep_Both_Halves_And_Reset_At_Each_Column()
    {
        using var dir = new TempDirectory();
        using var doc = FaroFlsDocument.Open(dir.Write("delta.fls", FaroDeltaFixture()));
        var scan = Assert.Single(doc.Scans);
        var points = scan.ReadPoints().ToArray();

        Assert.Equal(4, scan.PointCount);
        Assert.Equal(new[] { 1.0, 0.5, 2.0, 3.0 }, points.Select(p =>
        {
            var (x, y, z) = p.Position;
            return Math.Round(Math.Sqrt(x * x + y * y + z * z), 8);
        }));
        Assert.True(scan.HasColors);
        Assert.True(scan.HasIntensities);
        Assert.Equal(new[] { 0UL, 1UL, 2UL, 3UL }, points.Select(p => p.Index));
        Assert.Null(points[0].Color);
        Assert.Equal(new PclColor(1, 2, 4), points[3].Color);
        Assert.Equal(0.5f, points[3].Intensity!.Value, 3);
        Assert.True(points[0].Position.Y > 0 && points[3].Position.Y < 0);
        Assert.Equal(new[] { points[0], points[2] with { Index = 1 }, points[3] with { Index = 2 } },
            scan.ReadPoints(new PclReadOptions { SubsampleStep = 2 }));
    }

    [Fact]
    public void Faro_Panorama_Projects_The_Colour_Grid_To_World_Directions()
    {
        using var dir = new TempDirectory();
        using var doc = FaroFlsDocument.Open(dir.Write("colour.fls", FaroDeltaFixture()));

        const int width = 64;
        byte[] panorama = Assert.IsType<byte[]>(doc.ReadColorPanorama(width));
        Assert.Equal(width * (width / 2) * 3, panorama.Length);
        int offset = (25 * width + 16) * 3;
        Assert.Equal(new byte[] { 1, 2, 4 }, panorama.AsSpan(offset, 3).ToArray());
        Assert.Equal(new byte[] { 0, 0, 0 }, panorama.AsSpan(0, 3).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.ReadColorPanorama(63));
    }

    [Theory]
    [InlineData(27, false, typeof(NotSupportedException))]
    [InlineData(26, true, typeof(InvalidDataException))]
    public void Faro_Delta_Planes_Reject_Unknown_Encoding_And_Short_Grid(int mode, bool shortHalf, Type error)
    {
        using var dir = new TempDirectory();
        using var doc = FaroFlsDocument.Open(dir.Write("bad.fls", FaroDeltaFixture(mode, shortHalf)));

        Assert.Throws(error, () => doc.ReadAllPoints().ToArray());
    }

    private static byte[] FaroDeltaFixture(int mode = 26, bool shortHalf = false)
    {
        var builder = new Ole2Builder();
        int scans = builder.AddStorage(0, "Scans");
        int one = builder.AddStorage(scans, "1");
        builder.AddStream(one, "Params", Encoding.ASCII.GetBytes(SyntheticFixtures.FaroParams("StorageDistFact 32")));

        // The leading 32 is a sample whose byte happens to be a space. Each of the four rows has one delta,
        // followed by a new column whose first sample is +64. In the other half, the first sample is +96.
        byte[] first = new byte[18];
        new byte[] { 32, 80, 80, 0, 128, 1, 192, 1 }.CopyTo(first, 0);
        byte[] second = new byte[18];
        new byte[] { 160, 1, 224, 1 }.CopyTo(second, 0);
        byte[] color = new byte[22];
        new byte[] { 145, 132, 128, 16, 209, 132, 128, 16 }.CopyTo(color, 0);
        byte[] grey = new byte[18];
        new byte[] { 128, 16, 192, 16 }.CopyTo(grey, 0);
        string distanceHeader = $"DataMode {mode}\nDistDataFormat 1\n";
        builder.AddStream(one, "Dist0", DeltaPlane(distanceHeader, first));
        builder.AddStream(one, "Dist1", DeltaPlane(distanceHeader, shortHalf ? second[..^3] : second));
        builder.AddStream(one, "Color1", DeltaPlane("ColorDataFormat 1\n", color));
        builder.AddStream(one, "Grey1", DeltaPlane("ColorDataFormat 1\n", grey));
        return builder.Build();
    }

    private static byte[] DeltaPlane(string header, byte[] payload)
    {
        using var storage = new MemoryStream();
        using (var gzip = new GZipStream(storage, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(Encoding.ASCII.GetBytes($"\n{header}Scan2DataBase 32 {{ "));
            gzip.Write(payload);
            gzip.Write("\n}"u8);
        }
        return storage.ToArray();
    }
}
