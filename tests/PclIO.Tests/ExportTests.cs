using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PclIO.Export;
using Xunit;

namespace PclIO.Tests;

/// <summary>A document whose points come from a delegate, so tests control exactly what the exporters see.</summary>
internal sealed class FakeDocument(Func<IEnumerable<PclPoint>> source, string filePath = "sample.xyz", long declaredCount = 0) : PclDocument(filePath, 0)
{
    private readonly FakeScan _scan = new(source, declaredCount);

    public override PclFileKind Kind => PclFileKind.RecapScan;

    public override IReadOnlyList<PclScan> Scans => [_scan];

    public override long TotalPointCount => _scan.PointCount;

    private sealed class FakeScan(Func<IEnumerable<PclPoint>> source, long declaredCount) : PclScan
    {
        public override string Id => "1";

        public override string Name => "fake";

        public override long PointCount => declaredCount;

        public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null) => source();
    }

    public static FakeDocument Of(params PclPoint[] points) => new(() => points, declaredCount: points.Length);
}

public sealed class ExportTests
{
    private static PclPoint At(double x, double y, double z, PclColor? color = null, float? intensity = null) =>
        new(new PclVector3(x, y, z), color, intensity);

    private static string Text(Func<TextWriter, long> export, out long count)
    {
        var sw = new StringWriter { NewLine = "\n" };
        count = export(sw);
        return sw.ToString();
    }

    private static readonly PclPoint[] Mixed =
    [
        At(1, 2, 3, new PclColor(10, 20, 30), 0.25f),
        At(-4.5, 5.25, 6.125, null, 0.5f),
        At(7, 8, 9),
    ];

    // ---- XYZ -------------------------------------------------------------------------------------------------

    [Fact]
    public void Xyz_Writes_One_Line_Per_Point_With_The_Columns_The_Point_Has()
    {
        using var doc = FakeDocument.Of(Mixed);

        string text = Text(w => PclXyzExporter.Export(doc, w), out long count);

        Assert.Equal(3, count);
        Assert.Equal("1.0000 2.0000 3.0000 10 20 30\n-4.5000 5.2500 6.1250 0.500\n7.0000 8.0000 9.0000\n", text);
    }

    [Fact]
    public void Xyz_Skips_Points_With_Non_Finite_Coordinates_And_Reports_Them()
    {
        using var doc = FakeDocument.Of(At(1, 2, 3), At(double.NaN, 0, 0), At(0, double.PositiveInfinity, 0), At(0, 0, double.NegativeInfinity), At(4, 5, 6));
        var sw = new StringWriter { NewLine = "\n" };

        long written = PclXyzExporter.Export(doc, sw, null, out long skipped);

        Assert.Equal(2, written);
        Assert.Equal(3, skipped);
        Assert.Equal("1.0000 2.0000 3.0000\n4.0000 5.0000 6.0000\n", sw.ToString());
    }

    [Fact]
    public void Xyz_Clamps_Intensity_And_Ignores_NaN_Intensity()
    {
        using var doc = FakeDocument.Of(At(0, 0, 0, null, 7f), At(0, 0, 0, null, -3f), At(0, 0, 0, null, float.NaN));

        string text = Text(w => PclXyzExporter.Export(doc, w), out _);

        Assert.Equal("0.0000 0.0000 0.0000 1.000\n0.0000 0.0000 0.0000 0.000\n0.0000 0.0000 0.0000\n", text);
    }

    [Fact]
    public void Xyz_Applies_The_Limit_Passed_In_The_Options()
    {
        var points = Enumerable.Range(0, 10).Select(i => At(i, 0, 0)).ToArray();
        using var doc = new FakeDocument(() => points);

        Text(w => PclXyzExporter.Export(doc, w, new PclReadOptions { MaxPoints = 4 }), out long limited);
        Text(w => PclXyzExporter.Export(doc, w, new PclReadOptions { MaxPoints = 0 }), out long none);

        Assert.Equal(4, limited);
        Assert.Equal(0, none);
    }

    [Fact]
    public void Xyz_Rejects_Null_Arguments()
    {
        using var doc = FakeDocument.Of();
        Assert.Throws<ArgumentNullException>(() => PclXyzExporter.Export(null!, new StringWriter()));
        Assert.Throws<ArgumentNullException>(() => PclXyzExporter.Export(doc, null!));
    }

    // ---- PTS -------------------------------------------------------------------------------------------------

    [Fact]
    public void Pts_Header_Equals_The_Number_Of_Point_Lines()
    {
        using var doc = FakeDocument.Of(Mixed);

        string text = Text(w => PclPtsExporter.Export(doc, w), out long count);
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, count);
        Assert.Equal("3", lines[0]);
        Assert.Equal(lines.Length - 1, long.Parse(lines[0], CultureInfo.InvariantCulture));
        Assert.Equal("1.0000 2.0000 3.0000 511 10 20 30", lines[1]);
        Assert.Equal("-4.5000 5.2500 6.1250 1023 255 255 255", lines[2]);

        // No intensity means full intensity, and every line has all seven columns.
        Assert.Equal("7.0000 8.0000 9.0000 2047 255 255 255", lines[3]);
        Assert.All(lines.Skip(1), l => Assert.Equal(7, l.Split(' ').Length));
    }

    [Fact]
    public void Pts_Does_Not_Count_Skipped_Points_In_The_Header()
    {
        using var doc = FakeDocument.Of(At(1, 1, 1), At(double.NaN, 1, 1), At(2, 2, 2));
        var sw = new StringWriter { NewLine = "\n" };

        long written = PclPtsExporter.Export(doc, sw, null, out long skipped);

        Assert.Equal(2, written);
        Assert.Equal(1, skipped);
        string[] lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("2", lines[0]);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void Pts_Of_An_Empty_Source_Is_Just_A_Zero_Header()
    {
        using var doc = FakeDocument.Of();
        Assert.Equal("0\n", Text(w => PclPtsExporter.Export(doc, w), out _));
    }

    [Fact]
    public void Pts_Fails_When_The_Source_Changes_Between_The_Two_Passes()
    {
        int pass = 0;
        using var doc = new FakeDocument(() => ++pass == 1 ? [At(1, 1, 1)] : [At(1, 1, 1), At(2, 2, 2)]);

        Assert.Throws<InvalidOperationException>(() => PclPtsExporter.Export(doc, new StringWriter()));
    }

    [Fact]
    public void Pts_Fails_When_The_Source_Loses_Points_Between_The_Two_Passes()
    {
        int pass = 0;
        using var doc = new FakeDocument(() => ++pass == 1 ? [At(1, 1, 1), At(2, 2, 2)] : [At(1, 1, 1)]);

        Assert.Throws<InvalidOperationException>(() => PclPtsExporter.Export(doc, new StringWriter()));
    }

    // ---- LAS -------------------------------------------------------------------------------------------------

    private const int LasHeaderSize = 227;
    private const int LasRecordSize = 26;

    private static byte[] Las(PclDocument doc, PclReadOptions? options = null)
    {
        using var ms = new MemoryStream();
        PclLasExporter.Export(doc, ms, options);
        return ms.ToArray();
    }

    [Fact]
    public void Las_Header_Describes_The_Records_That_Follow()
    {
        using var doc = FakeDocument.Of(Mixed);

        byte[] bytes = Las(doc);

        Assert.Equal(LasHeaderSize + (3 * LasRecordSize), bytes.Length);
        Assert.Equal("LASF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(1, bytes[24]);
        Assert.Equal(2, bytes[25]);
        Assert.Equal(LasHeaderSize, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(94, 2)));
        Assert.Equal((uint)LasHeaderSize, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(96, 4)));
        Assert.Equal(2, bytes[104]);
        Assert.Equal(LasRecordSize, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(105, 2)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(107, 4)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(111, 4)));

        double Double(int at) => BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(at, 8));
        Assert.Equal(0.001, Double(131));
        Assert.Equal(-4.5, Double(155));
        Assert.Equal(2.0, Double(163));
        Assert.Equal(3.0, Double(171));

        // Maximum and minimum are stored interleaved per axis: X max, X min, Y max, Y min, Z max, Z min.
        Assert.Equal(7.0, Double(179));
        Assert.Equal(-4.5, Double(187));
        Assert.Equal(8.0, Double(195));
        Assert.Equal(2.0, Double(203));
        Assert.Equal(9.0, Double(211));
        Assert.Equal(3.0, Double(219));
    }

    [Fact]
    public void Las_Records_Hold_Scaled_Coordinates_Intensity_And_Colour()
    {
        using var doc = FakeDocument.Of(Mixed);

        byte[] bytes = Las(doc);
        ReadOnlySpan<byte> first = bytes.AsSpan(LasHeaderSize, LasRecordSize);
        ReadOnlySpan<byte> second = bytes.AsSpan(LasHeaderSize + LasRecordSize, LasRecordSize);
        ReadOnlySpan<byte> third = bytes.AsSpan(LasHeaderSize + (2 * LasRecordSize), LasRecordSize);

        // Offsets are the minimum of each axis, the scale is one millimetre.
        Assert.Equal(5500, BinaryPrimitives.ReadInt32LittleEndian(first[..4]));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(first.Slice(4, 4)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(first.Slice(8, 4)));
        Assert.Equal(11500, BinaryPrimitives.ReadInt32LittleEndian(third[..4]));

        Assert.Equal((ushort)Math.Round(0.25f * 65535f), BinaryPrimitives.ReadUInt16LittleEndian(first.Slice(12, 2)));
        Assert.Equal(0x09, first[14]);

        // 8-bit channels are spread over the 16-bit range, so 255 becomes 65535.
        Assert.Equal(10 * 257, BinaryPrimitives.ReadUInt16LittleEndian(first.Slice(20, 2)));
        Assert.Equal(20 * 257, BinaryPrimitives.ReadUInt16LittleEndian(first.Slice(22, 2)));
        Assert.Equal(30 * 257, BinaryPrimitives.ReadUInt16LittleEndian(first.Slice(24, 2)));

        // Missing colour is written white, missing intensity as full intensity.
        Assert.Equal(65535, BinaryPrimitives.ReadUInt16LittleEndian(second.Slice(20, 2)));
        Assert.Equal(65535, BinaryPrimitives.ReadUInt16LittleEndian(third.Slice(12, 2)));
        Assert.Equal(65535, BinaryPrimitives.ReadUInt16LittleEndian(third.Slice(24, 2)));
    }

    [Fact]
    public void Las_Of_An_Empty_Source_Is_A_Header_With_Zero_Points()
    {
        using var doc = FakeDocument.Of();

        byte[] bytes = Las(doc);

        Assert.Equal(LasHeaderSize, bytes.Length);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(107, 4)));
    }

    [Fact]
    public void Las_Skips_Non_Finite_Points_And_Keeps_The_Header_Consistent()
    {
        using var doc = FakeDocument.Of(At(1, 1, 1), At(double.NaN, 0, 0), At(3, 3, 3));
        using var ms = new MemoryStream();

        long written = PclLasExporter.Export(doc, ms, null, out long skipped);

        Assert.Equal(2, written);
        Assert.Equal(1, skipped);
        Assert.Equal(LasHeaderSize + (2 * LasRecordSize), ms.Length);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(ms.ToArray().AsSpan(107, 4)));
    }

    [Fact]
    public void Las_Rejects_An_Extent_That_Does_Not_Fit_The_Coordinate_Scale()
    {
        using var doc = FakeDocument.Of(At(0, 0, 0), At(3_000_000, 0, 0));
        using var ms = new MemoryStream();

        Assert.Throws<NotSupportedException>(() => PclLasExporter.Export(doc, ms));
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void Las_Fails_When_The_Source_Changes_Between_The_Two_Passes()
    {
        int pass = 0;
        using var doc = new FakeDocument(() => ++pass == 1 ? [At(1, 1, 1)] : [At(1, 1, 1), At(2, 2, 2)]);

        Assert.Throws<InvalidOperationException>(() => PclLasExporter.Export(doc, new MemoryStream()));
    }

    [Fact]
    public void Las_Applies_The_Point_Limit()
    {
        var points = Enumerable.Range(0, 10).Select(i => At(i, 0, 0)).ToArray();
        using var doc = new FakeDocument(() => points);

        using var ms = new MemoryStream();
        long written = PclLasExporter.Export(doc, ms, new PclReadOptions { MaxPoints = 4 });

        Assert.Equal(4, written);
        Assert.Equal(LasHeaderSize + (4 * LasRecordSize), ms.Length);
    }

    [Fact]
    public void Las_Of_Real_Synthetic_Rcs_Points_Matches_What_Was_Written()
    {
        using var dir = new TempDirectory();
        string path = dir.Write("points.rcs", SyntheticFixtures.CreateSyntheticRcsWithPoints(
            [new RcsSamplePoint(1000, 2000, 3000, 255, 1, 2, 3), new RcsSamplePoint(0, 0, 0, 0, 4, 5, 6)], (0, 0, 0)));
        using var doc = PclFile.Open(path);

        byte[] bytes = Las(doc);

        Assert.Equal(LasHeaderSize + (2 * LasRecordSize), bytes.Length);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(107, 4)));
    }

    // ---- JSON ------------------------------------------------------------------------------------------------

    [Fact]
    public void Json_Describes_The_Document_And_Lists_The_Requested_Sample_Points()
    {
        using var doc = FakeDocument.Of(Mixed);

        string json = Text(w => PclJsonExporter.Export(doc, w, indented: false, maxPoints: 2), out long written);
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;

        Assert.Equal(2, written);
        Assert.Equal("RecapScan", root.GetProperty("format").GetString());
        Assert.Equal(3, root.GetProperty("totalPointCount").GetInt64());
        Assert.Equal("1", root.GetProperty("scans")[0].GetProperty("id").GetString());
        Assert.False(root.TryGetProperty("coordinateSystem", out _));

        var samples = root.GetProperty("samplePoints");
        Assert.Equal(2, samples.GetArrayLength());
        Assert.Equal(1.0, samples[0].GetProperty("x").GetDouble());
        Assert.Equal(10, samples[0].GetProperty("r").GetInt32());
        Assert.False(samples[1].TryGetProperty("r", out _));
    }

    [Fact]
    public void Json_Writes_Only_The_File_Name_Never_The_Directory()
    {
        string path = Path.Combine(Path.GetTempPath(), "some-folder", "scan.rcs");
        using var doc = new FakeDocument(() => [], path);

        string json = Text(w => PclJsonExporter.Export(doc, w), out _);
        using var parsed = JsonDocument.Parse(json);

        Assert.Equal("scan.rcs", parsed.RootElement.GetProperty("fileName").GetString());
        Assert.DoesNotContain("some-folder", json);
    }

    [Fact]
    public void Json_Without_A_Limit_Has_No_Sample_Points()
    {
        using var doc = FakeDocument.Of(Mixed);

        using var none = JsonDocument.Parse(Text(w => PclJsonExporter.Export(doc, w), out long a));
        using var zero = JsonDocument.Parse(Text(w => PclJsonExporter.Export(doc, w, true, 0), out long b));

        Assert.Equal(0, a + b);
        Assert.False(none.RootElement.TryGetProperty("samplePoints", out _));
        Assert.False(zero.RootElement.TryGetProperty("samplePoints", out _));
    }

    [Fact]
    public void Json_Skips_Non_Finite_Sample_Points_And_Stays_Valid()
    {
        using var doc = FakeDocument.Of(At(double.NaN, 1, 1), At(1, 2, 3), At(double.PositiveInfinity, 0, 0));

        string json = Text(w => PclJsonExporter.Export(doc, w, true, 10, out _), out long written);
        using var parsed = JsonDocument.Parse(json);

        Assert.Equal(1, written);
        Assert.Equal(1, parsed.RootElement.GetProperty("samplePoints").GetArrayLength());
    }

    [Fact]
    public void Json_Reports_The_Number_Of_Skipped_Sample_Points()
    {
        using var doc = FakeDocument.Of(At(double.NaN, 1, 1), At(1, 2, 3));
        var sw = new StringWriter();

        long written = PclJsonExporter.Export(doc, sw, true, 10, out long skipped);

        Assert.Equal(1, written);
        Assert.Equal(1, skipped);
    }

    // ---- Text output is the same in every culture ---------------------------------------------------------------

    [Fact]
    public void Text_Exports_Do_Not_Depend_On_The_Current_Culture()
    {
        using var doc = FakeDocument.Of(Mixed);

        string All() => string.Join("|",
            Text(w => PclXyzExporter.Export(doc, w), out _),
            Text(w => PclPtsExporter.Export(doc, w), out _),
            Text(w => PclJsonExporter.Export(doc, w, false, 3), out _));

        string expected = ModelAndMathTests.WithCulture(CultureInfo.InvariantCulture, All);

        Assert.Contains("-4.5000", expected);
        Assert.Equal(expected, ModelAndMathTests.WithCulture(new CultureInfo("fr-FR"), All));
        Assert.Equal(expected, ModelAndMathTests.WithCulture(new CultureInfo("de-DE"), All));
    }

    [Fact]
    public void Exports_Are_Deterministic()
    {
        using var doc = FakeDocument.Of(Mixed);

        Assert.Equal(Text(w => PclXyzExporter.Export(doc, w), out _), Text(w => PclXyzExporter.Export(doc, w), out _));
        Assert.Equal(Text(w => PclPtsExporter.Export(doc, w), out _), Text(w => PclPtsExporter.Export(doc, w), out _));
        Assert.Equal(Text(w => PclJsonExporter.Export(doc, w, true, 3), out _), Text(w => PclJsonExporter.Export(doc, w, true, 3), out _));
        Assert.Equal(Las(doc), Las(doc));
    }
}
