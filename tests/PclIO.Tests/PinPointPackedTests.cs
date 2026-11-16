using System.Buffers.Binary;
using PclIO.Formats.PinPoint;
using Xunit;

namespace PclIO.Tests;

/// <summary>PinPoint point data files in the sectioned layout with Snappy-compressed channels.</summary>
public sealed class PinPointPackedTests
{
    private static readonly (float X, float Y, float Z)[] Three = [(1, 2, 3), (-4, 5.5f, 6), (7, 8, -9.25f)];

    private static PinPointDocument Open(TempDirectory dir, PackedBin bin) =>
        PinPointDocument.Open(SyntheticFixtures.WritePinPointProject(dir, bin.Bytes));

    private static List<PclPoint> ReadAll(PinPointDocument doc, PclReadOptions? options = null) =>
        doc.ReadAllPoints(options ?? new PclReadOptions { ApplyTransform = false }).ToList();

    [Fact]
    public void Positions_Colours_And_Intensities_Are_Read()
    {
        using var dir = new TempDirectory();
        byte[] rgb = [10, 20, 30, 40, 50, 60, 70, 80, 90];
        byte[] intensities = [0, 51, 255];
        using var doc = Open(dir, PinPointPackedFixtures.Create(Three, rgb, intensities));

        var scan = (PinPointScan)Assert.Single(doc.Scans);
        Assert.Empty(doc.Warnings);
        Assert.Equal(3, scan.PointCount);
        Assert.True(scan.HasColors);
        Assert.True(scan.HasIntensities);
        Assert.False(scan.HasNormals);

        var points = ReadAll(doc);
        Assert.Equal(3, points.Count);
        Assert.Equal(Three.Select(p => ((double)p.X, (double)p.Y, (double)p.Z)), points.Select(p => (p.X, p.Y, p.Z)));
        Assert.Equal([new PclColor(10, 20, 30), new PclColor(40, 50, 60), new PclColor(70, 80, 90)], points.Select(p => p.Color!.Value));
        Assert.Equal([0f, 51 / 255f, 1f], points.Select(p => p.Intensity!.Value));
        Assert.Equal([0UL, 1UL, 2UL], points.Select(p => p.Index));
    }

    [Fact]
    public void Colour_And_Intensity_Are_Absent_Without_Their_Channels()
    {
        using var dir = new TempDirectory();
        using var doc = Open(dir, PinPointPackedFixtures.Create(Three));

        var scan = (PinPointScan)Assert.Single(doc.Scans);
        Assert.False(scan.HasColors);
        Assert.False(scan.HasIntensities);
        Assert.All(ReadAll(doc), p =>
        {
            Assert.Null(p.Color);
            Assert.Null(p.Intensity);
            Assert.Null(p.Normal);
        });
    }

    [Fact]
    public void Flagged_Points_Are_Returned_By_Default_And_Excluded_On_Request()
    {
        using var dir = new TempDirectory();
        var bin = PinPointPackedFixtures.Create(Three, filterBits: [0, 1, 0]);
        using var doc = Open(dir, bin);

        var scan = (PinPointScan)Assert.Single(doc.Scans);
        Assert.True(scan.HasFilterBits);

        // By default every stored point is returned.
        var all = ReadAll(doc);
        Assert.Equal(3, all.Count);

        // On request the point with a non-zero flag is not returned, and the rest keeps consecutive indices.
        var kept = ReadAll(doc, new PclReadOptions { ApplyTransform = false, ExcludeFilteredPoints = true });
        Assert.Equal(2, kept.Count);
        Assert.Equal([0UL, 1UL], kept.Select(p => p.Index));
        Assert.Equal([(1.0, 2.0, 3.0), (7.0, 8.0, -9.25)], kept.Select(p => (p.X, p.Y, p.Z)));
    }

    [Fact]
    public void A_FilterBits_Channel_With_The_Wrong_Size_Is_Rejected()
    {
        using var dir = new TempDirectory();
        byte[] positions = new byte[Three.Length * 12];
        var bad = PinPointPackedFixtures.Assemble(
        [
            ("RowColIndex", PinPointPackedFixtures.Literals(new byte[Three.Length * 8])),
            ("Points", PinPointPackedFixtures.Literals(positions)),
            ("FilterBits", PinPointPackedFixtures.Literals(new byte[Three.Length - 1])),
        ], Three.Length);

        using var doc = PinPointDocument.Open(SyntheticFixtures.WritePinPointProject(dir, bad.Bytes));

        Assert.Contains(doc.Warnings, w => w.Contains("FilterBits", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => ReadAll(doc));
    }

    [Fact]
    public void The_Point_Count_Comes_From_The_Data_Section_Unless_The_Manifest_Gives_One()
    {
        using var dir = new TempDirectory();
        var bin = PinPointPackedFixtures.Create(Three);
        string manifest = SyntheticFixtures.PinPointManifest("Pos1", "h1", 2);
        using var withCount = PinPointDocument.Open(SyntheticFixtures.WritePinPointProject(dir, bin.Bytes, manifest));

        Assert.Equal(2, withCount.Scans[0].PointCount);
        using var other = new TempDirectory();
        using var without = Open(other, bin);
        Assert.Equal(3, without.Scans[0].PointCount);
    }

    [Fact]
    public void Points_Beyond_One_Batch_Are_Read_In_Order_With_Their_Colours_And_Intensities()
    {
        const int count = 10_000;
        var points = Enumerable.Range(0, count).Select(i => (X: i * 0.5f, Y: -i * 0.25f, Z: (float)(i % 100))).ToArray();
        byte[] rgb = new byte[count * 3];
        byte[] intensities = new byte[count];
        for (int i = 0; i < count; i++)
        {
            rgb[(i * 3) + 0] = (byte)i;
            rgb[(i * 3) + 1] = (byte)(i >> 8);
            rgb[(i * 3) + 2] = (byte)(i * 7);
            intensities[i] = (byte)(i * 3);
        }

        using var dir = new TempDirectory();
        using var doc = Open(dir, PinPointPackedFixtures.Create(points, rgb, intensities));

        var read = ReadAll(doc);

        Assert.Equal(count, read.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(points[i].X, read[i].X);
            Assert.Equal(points[i].Z, read[i].Z);
            Assert.Equal(new PclColor(rgb[i * 3], rgb[(i * 3) + 1], rgb[(i * 3) + 2]), read[i].Color);
            Assert.Equal(intensities[i] / 255f, read[i].Intensity);
        }

        // A step keeps every n-th stored point, and a limit stops early, in the middle of a batch.
        var stepped = ReadAll(doc, new PclReadOptions { ApplyTransform = false, SubsampleStep = 3 });
        Assert.Equal(Enumerable.Range(0, count).Where(i => i % 3 == 0).Select(i => (double)points[i].X), stepped.Select(p => p.X));
        Assert.Equal(4097, ReadAll(doc, new PclReadOptions { ApplyTransform = false, MaxPoints = 4097 }).Count);
        Assert.Empty(ReadAll(doc, new PclReadOptions { MaxPoints = 0 }));
    }

    [Fact]
    public void A_Colour_Channel_Stored_As_Copies_Is_Decoded()
    {
        const int count = 5_000;
        var points = Enumerable.Range(0, count).Select(i => (X: (float)i, Y: 0f, Z: 0f)).ToArray();
        byte[] rgb = Enumerable.Range(0, count).SelectMany(_ => new byte[] { 200, 100, 50 }).ToArray();
        using var dir = new TempDirectory();
        using var doc = Open(dir, PinPointPackedFixtures.Create(points, rgb, runEncodedColors: true));

        var read = ReadAll(doc);

        Assert.Equal(count, read.Count);
        Assert.All(read, p => Assert.Equal(new PclColor(200, 100, 50), p.Color));
    }

    [Fact]
    public void The_Pose_Of_The_Scan_Is_Applied_Unless_Switched_Off()
    {
        using var dir = new TempDirectory();
        using var doc = Open(dir, PinPointPackedFixtures.Create([(1, 2, 3)]));

        var plain = ReadAll(doc)[0];
        var placed = doc.ReadAllPoints().Single();

        // The manifest of the fixture holds a pure translation of (5, 10, 15).
        Assert.Equal((1d, 2d, 3d), (plain.X, plain.Y, plain.Z));
        Assert.Equal((6d, 12d, 18d), (placed.X, placed.Y, placed.Z));
    }

    // ---- Files that are not this layout, and files that are damaged ---------------------------------------------------

    [Fact]
    public void A_File_With_The_Header_Of_Another_Layout_Is_Listed_With_A_Warning_And_Not_Read()
    {
        using var dir = new TempDirectory();
        byte[] bytes = new byte[240];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), 356);
        using var doc = PinPointDocument.Open(SyntheticFixtures.WritePinPointProject(dir, bytes));

        Assert.Contains(doc.Warnings, w => w.Contains("no recognised channel layout", StringComparison.Ordinal));
        Assert.Throws<NotSupportedException>(() => ReadAll(doc));
    }

    public static TheoryData<string> DamageNames =>
    [
        "stored count above the stream",
        "stored count below the stream",
        "points stream longer than its section",
        "points preamble declares one byte more",
        "a byte after the last section",
        "last byte missing",
        "sections that do not follow each other",
        "section length beyond the file",
        "a channel listed twice",
        "no Points channel",
        "a property table that is not a map",
        "a property without a name",
        "bytes after the property table",
        "a property table nested too deeply",
        "stream missing for a listed channel",
        "bytes after the last stream",
        "an element tag the table does not know",
        "a stored count that cannot be real",
    ];

    // The result of each damage: an InvalidDataException, or a NotSupportedException when the file uses a feature the reader lacks.
    private static (byte[] Bytes, Type Expected) Damage(string name)
    {
        var good = PinPointPackedFixtures.Create(Three, [1, 2, 3, 4, 5, 6, 7, 8, 9], [1, 2, 3]);
        byte[] bytes = [.. good.Bytes];
        var invalid = typeof(InvalidDataException);
        var unsupported = typeof(NotSupportedException);

        void PutU64(int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
        ulong GetU64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
        IReadOnlyList<(string, byte[])> channels(params string[] names) => [.. names.Select(n => (n, PinPointPackedFixtures.Literals(new byte[Three.Length * 12])))];

        switch (name)
        {
            case "stored count above the stream":
                PutU64(good.DataOffset + 8, 4);
                return (bytes, invalid);
            case "stored count below the stream":
                PutU64(good.DataOffset + 8, 2);
                return (bytes, invalid);
            case "points stream longer than its section":
                PutU64(good.PointsStreamOffset - 8, 1UL << 40);
                return (bytes, invalid);
            case "points preamble declares one byte more":
                bytes[good.PointsStreamOffset] = (byte)(bytes[good.PointsStreamOffset] + 1);
                return (bytes, invalid);
            case "a byte after the last section":
                return ([.. bytes, 0], invalid);
            case "last byte missing":
                return (bytes[..^1], invalid);
            case "sections that do not follow each other":
                PutU64(24 + 20 + 4, GetU64(24 + 20 + 4) + 1);
                return (bytes, invalid);
            case "section length beyond the file":
                PutU64(24 + 40 + 12, 1UL << 50);
                return (bytes, invalid);
            case "a channel listed twice":
                return (PinPointPackedFixtures.Assemble(channels("Points", "Points"), 3).Bytes, invalid);
            case "no Points channel":
                return (PinPointPackedFixtures.Assemble([("FilterBits", PinPointPackedFixtures.Literals(new byte[3]))], 3).Bytes, invalid);
            case "a property table that is not a map":
                return (PinPointPackedFixtures.Assemble(channels("Points"), 3, PinPointPackedFixtures.Str("PropertyTable")).Bytes, invalid);
            case "a property without a name":
                return (PinPointPackedFixtures.Assemble(channels("Points"), 3, PinPointPackedFixtures.Map(
                    ("PropertyTable", PinPointPackedFixtures.Array(PinPointPackedFixtures.Map(("other", PinPointPackedFixtures.Str("x"))))))).Bytes, invalid);
            case "bytes after the property table":
                return (PinPointPackedFixtures.Assemble(channels("Points"), 3, [.. PinPointPackedFixtures.PropertyTable(["Points"]), 0x61, (byte)'x']).Bytes, invalid);
            case "a property table nested too deeply":
                byte[] deep = PinPointPackedFixtures.Str("end");
                for (int i = 0; i < 20; i++) deep = PinPointPackedFixtures.Array(deep);
                return (PinPointPackedFixtures.Assemble(channels("Points"), 3, deep).Bytes, invalid);
            case "stream missing for a listed channel":
                return (PinPointPackedFixtures.Assemble([("Points", PinPointPackedFixtures.Literals(new byte[Three.Length * 12]))], 3,
                    PinPointPackedFixtures.PropertyTable(["Points", "RGB8"])).Bytes, invalid);
            case "bytes after the last stream":
                return (PinPointPackedFixtures.Assemble(channels("Points"), 3, afterChannels: [0]).Bytes, invalid);
            case "an element tag the table does not know":
                return (PinPointPackedFixtures.Assemble(channels("Points"), 3, [0xFB, 0, 0, 0, 0, 0, 0, 0, 0]).Bytes, unsupported);
            case "a stored count that cannot be real":
                PutU64(good.DataOffset + 8, ulong.MaxValue);
                return (bytes, invalid);
            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, null);
        }
    }

    [Theory]
    [MemberData(nameof(DamageNames))]
    public void A_Damaged_File_Is_Listed_With_A_Warning_And_Rejected_When_Read(string damage)
    {
        var (bytes, expected) = Damage(damage);
        using var dir = new TempDirectory();
        using var doc = PinPointDocument.Open(SyntheticFixtures.WritePinPointProject(dir, bytes));

        Assert.NotEmpty(doc.Warnings);
        var thrown = Assert.ThrowsAny<Exception>(() => ReadAll(doc));
        Assert.True(thrown.GetType() == expected, $"{damage}: expected {expected.Name}, got {thrown.GetType().Name}: {thrown.Message}");
    }

    [Fact]
    public void A_Stream_That_Fails_Midway_Is_Rejected()
    {
        // The points channel decodes to the right size, but its last literal is cut short.
        byte[] positions = new byte[Three.Length * 12];
        byte[] stream = new SnappyBuilder().Literal(positions.AsSpan(0, 12)).Raw(0x00).Build(positions.Length);
        var bin = PinPointPackedFixtures.Assemble([("Points", stream)], Three.Length);
        using var dir = new TempDirectory();
        using var doc = Open(dir, bin);

        Assert.Throws<InvalidDataException>(() => ReadAll(doc));
    }

    [Fact]
    public void Bytes_After_The_Last_Element_Of_A_Stream_Are_Rejected_Once_All_Points_Are_Read()
    {
        byte[] positions = new byte[Three.Length * 12];
        byte[] stream = new SnappyBuilder().Literal(positions).Raw(0x00, 0x00).Build();
        var bin = PinPointPackedFixtures.Assemble([("Points", stream)], Three.Length);
        using var dir = new TempDirectory();
        using var doc = Open(dir, bin);

        Assert.Throws<InvalidDataException>(() => ReadAll(doc));
    }

    [Fact]
    public void A_Channel_That_Is_Not_Read_May_Appear_More_Than_Once()
    {
        byte[] positions = new byte[Three.Length * 12];
        byte[] other = PinPointPackedFixtures.Literals(new byte[Three.Length]);
        var bin = PinPointPackedFixtures.Assemble(
            [("Points", PinPointPackedFixtures.Literals(positions)), ("Flags", other), ("Flags", other)], Three.Length);
        using var dir = new TempDirectory();
        using var doc = Open(dir, bin);

        Assert.Empty(doc.Warnings);
        Assert.Equal(3, ReadAll(doc).Count);
    }

    [Fact]
    public void A_File_Without_Points_Reports_No_Colours_Or_Intensities_And_Reads_Nothing()
    {
        using var dir = new TempDirectory();
        using var doc = Open(dir, PinPointPackedFixtures.Create([], [], []));

        var scan = (PinPointScan)Assert.Single(doc.Scans);
        Assert.False(scan.HasColors);
        Assert.False(scan.HasIntensities);
        Assert.Empty(ReadAll(doc));
    }

    [Fact]
    public void Every_Prefix_Of_A_File_Is_Rejected_Or_Read_Without_Another_Exception()
    {
        var bin = PinPointPackedFixtures.Create(Three, [1, 2, 3, 4, 5, 6, 7, 8, 9], [1, 2, 3]);

        for (int length = 0; length < bin.Bytes.Length; length++)
        {
            using var dir = new TempDirectory();
            AssertOnlyExpectedFailures(dir, bin.Bytes[..length], $"a file cut to {length} bytes");
        }
    }

    [Fact]
    public void Every_Single_Byte_Change_In_The_Structure_Is_Rejected_Or_Read_Without_Another_Exception()
    {
        var bin = PinPointPackedFixtures.Create(Three, [1, 2, 3, 4, 5, 6, 7, 8, 9], [1, 2, 3]);
        for (int i = 0; i < bin.Bytes.Length; i++)
        {
            foreach (byte xor in new byte[] { 0x01, 0x80, 0xFF })
            {
                byte[] changed = [.. bin.Bytes];
                changed[i] ^= xor;
                using var dir = new TempDirectory();
                AssertOnlyExpectedFailures(dir, changed, $"byte {i} changed by {xor:X2}");
            }
        }
    }

    private static void AssertOnlyExpectedFailures(TempDirectory dir, byte[] bytes, string what)
    {
        try
        {
            using var doc = PinPointDocument.Open(SyntheticFixtures.WritePinPointProject(dir, bytes));
            ReadAll(doc);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            // Rejected, which is the usual outcome; some changes leave a readable file.
        }
        catch (Exception ex)
        {
            Assert.Fail($"{what} threw {ex.GetType().Name} instead of InvalidDataException or NotSupportedException: {ex.Message}");
        }
    }
}
