using PclIO.Export;
using PclIO.Formats.CcBin;
using PclIO.Formats.Copc;
using PclIO.Formats.E57;
using PclIO.Formats.EdgeWise;
using PclIO.Formats.Ept;
using PclIO.Formats.Faro;
using PclIO.Formats.Las;
using PclIO.Formats.Laz;
using PclIO.Formats.Leica;
using PclIO.Formats.Pcd;
using PclIO.Formats.PinPoint;
using PclIO.Formats.Ply;
using PclIO.Formats.Potree;
using PclIO.Formats.Ptg;
using PclIO.Formats.Pts;
using PclIO.Formats.Ptx;
using PclIO.Formats.Recap;
using PclIO.Formats.Sbf;
using PclIO.Formats.Tiles3d;
using PclIO.Formats.Xyz;
using Xunit;

namespace PclIO.Tests;

/// <summary>Reads small synthetic files for every format and checks the values that were written into them.</summary>
public sealed class FormatTests
{
    private const double Precision = 1e-9;

    private static void AssertNear(PclVector3 expected, PclVector3 actual)
    {
        Assert.Equal(expected.X, actual.X, Precision);
        Assert.Equal(expected.Y, actual.Y, Precision);
        Assert.Equal(expected.Z, actual.Z, Precision);
    }

    private static readonly PclReadOptions NoTransform = new() { ApplyTransform = false };

    // ---- ReCap scan -------------------------------------------------------------------------------------------

    [Fact]
    public void Parse_Synthetic_Rcs_Header()
    {
        using var dir = new TempDirectory();
        string path = dir.Write("sample.rcs", SyntheticFixtures.CreateSyntheticRcs(pointCount: 12345, tx: 10.5, ty: 20.5, tz: 30.5));

        using var doc = RcsScanDocument.Open(path);

        Assert.Equal(PclFileKind.RecapScan, doc.Kind);
        Assert.Equal(3u, doc.MajorVersion);
        Assert.Equal(2u, doc.MinorVersion);
        Assert.Equal(12345, doc.DeclaredPointCount);
        Assert.Equal("12345", doc.Metadata["PointCount"]);
        Assert.Equal(new PclVector3(10.5, 20.5, 30.5), doc.ScanTransform.Translation);
        Assert.Equal(PclVector3.Zero, doc.ScanTransform.Rotation);
        Assert.Equal(PclVector3.One, doc.ScanTransform.Scale);
        Assert.Equal(new PclBounds(new PclVector3(-10, -10, -10), new PclVector3(10, 10, 10)), doc.LocalBounds);
        Assert.Equal(new PclBounds(new PclVector3(-10, -10, -10), new PclVector3(10, 10, 10)), doc.OctreeBounds);
        Assert.Equal("00000000-0000-0000-0000-000000000001", doc.ScanGuid);
        Assert.Equal("LOCAL_GRID_SYSTEM", doc.CoordinateSystem);
        Assert.False(doc.HasColorData);
        Assert.False(doc.HasNormalData);
        Assert.False(doc.HasIntensityData);

        var scan = Assert.Single(doc.Scans);
        Assert.Equal("00000000-0000-0000-0000-000000000001", scan.Id);
        Assert.Equal("sample", scan.Name);
        Assert.Equal(doc.ScanTransform, scan.Transform);
        Assert.Equal(doc.LocalBounds, scan.Bounds);
        Assert.False(scan.HasColors);
        Assert.False(scan.HasNormals);
        Assert.False(scan.HasIntensities);
    }

    [Fact]
    public void Rcs_Without_Leaves_Stores_No_Points()
    {
        // The header states a point count, but the file holds no leaf, so it stores no point.
        using var dir = new TempDirectory();
        using var doc = RcsScanDocument.Open(dir.Write("empty.rcs", SyntheticFixtures.CreateSyntheticRcs(pointCount: 10)));

        Assert.Equal(10, doc.DeclaredPointCount);
        Assert.Equal(0, doc.Scans[0].PointCount);
        Assert.Equal(0, doc.TotalPointCount);
        Assert.Empty(doc.ReadAllPoints());
    }

    [Fact]
    public void Rcs_Point_Count_Is_The_Number_Of_Stored_Points_Not_The_Count_Of_The_Header()
    {
        // The header states a count of its own, which is not the number of the points the file stores.
        using var dir = new TempDirectory();
        var scan = new RcsScanBuilder { PointCount = 9000 };
        scan.AddLeaf((0, 0, 0), 1, new RcsSamplePoint(1, 1, 1), new RcsSamplePoint(2, 2, 2));
        scan.AddLeaf((5, 5, 5), 1, new RcsSamplePoint(3, 3, 3));
        using var doc = RcsScanDocument.Open(dir.Write("count.rcs", scan.Build()));

        Assert.Equal(9000, doc.DeclaredPointCount);
        Assert.Equal(3, doc.Scans[0].PointCount);
        Assert.Equal(3, doc.TotalPointCount);
        Assert.Equal(3, doc.ReadAllPoints().Count());
    }

    // Records of the synthetic scans. The first lies 0.1, 0.2 and 0.3 metres from the corner of its leaf and names a cell
    // near the +z axis; the second is the corner of the leaf and has no normal; the third holds the largest offset an
    // axis can hold and the flag that leaves a point without a normal.
    private static readonly RcsSamplePoint[] RcsPoints =
    [
        new(100, 200, 300, 128, 10, 20, 30, 12194),
        new(0, 0, 0, 255, 1, 2, 3),
        new(RcsScanBuilder.MaxOffset, 1000, 2, 0, 255, 254, 253, 0x4000),
    ];

    [Fact]
    public void Read_Synthetic_Rcs_Points_With_And_Without_Transform()
    {
        using var dir = new TempDirectory();
        string path = dir.Write("points.rcs",
            SyntheticFixtures.CreateSyntheticRcsWithPoints(RcsPoints, (1.5, -2.5, 3.5), withNormals: true, tx: 10, ty: 20, tz: 30));
        using var doc = RcsScanDocument.Open(path);

        var raw = doc.ReadAllPoints(NoTransform).ToList();
        var moved = doc.ReadAllPoints().ToList();

        // A position counts millimetres from the corner of the cube of its leaf; the transform adds the translation.
        Assert.Equal(3, raw.Count);
        Assert.Equal(new PclVector3(1.5 + 100 * 0.001, -2.5 + 200 * 0.001, 3.5 + 300 * 0.001), raw[0].Position);
        Assert.Equal(new PclVector3(1.5, -2.5, 3.5), raw[1].Position);
        Assert.Equal(new PclVector3(1.5 + 262143 * 0.001, -2.5 + 1000 * 0.001, 3.5 + 2 * 0.001), raw[2].Position);
        Assert.Equal(new PclVector3((1.5 + 100 * 0.001) * 1 + 10, (-2.5 + 200 * 0.001) * 1 + 20, (3.5 + 300 * 0.001) * 1 + 30), moved[0].Position);
        Assert.Equal(new[] { 0UL, 1UL, 2UL }, raw.Select(p => p.Index));

        // The colour and the intensity are the bytes of the record.
        Assert.Equal(new PclColor(10, 20, 30), raw[0].Color);
        Assert.Equal(new PclColor(255, 254, 253), raw[2].Color);
        Assert.Equal(128 / 255.0f, raw[0].Intensity);
        Assert.Equal(1.0f, raw[1].Intensity);
        Assert.Equal(0.0f, raw[2].Intensity);

        // The first record names a cell of the +z face; the others state that the point has no normal. A scale of one
        // leaves a normal as it is.
        AssertNear(new PclVector3(0.015101514293334039, 0.015101514293334039, 0.99977191825540723), raw[0].Normal!.Value);
        Assert.Null(raw[1].Normal);
        Assert.Null(raw[2].Normal);
        Assert.Equal(raw.Select(p => p.Index), moved.Select(p => p.Index));
        Assert.Equal(raw.Select(p => p.Color), moved.Select(p => p.Color));
        Assert.Equal(raw.Select(p => p.Intensity), moved.Select(p => p.Intensity));
        Assert.Equal(raw.Select(p => p.Normal), moved.Select(p => p.Normal));
    }

    [Theory]
    [InlineData(1u, 0.60025827522031439, -0.5481850217573937, -0.58239435518416482)]
    [InlineData(52u, 0.60025827522031439, -0.58239435518416482, -0.5481850217573937)]
    [InlineData(53u, 0.61221412644254303, -0.55910368599401761, -0.55910368599401761)]
    [InlineData(2704u, -0.5889765868020802, -0.57144841420681702, -0.57144841420681702)]
    [InlineData(2756u, -0.60025827522031439, -0.58239435518416482, -0.5481850217573937)]
    [InlineData(5408u, -0.57144841420681702, 0.5889765868020802, -0.57144841420681702)]
    [InlineData(5459u, 0.57144841420681702, 0.58897658680208009, -0.57144841420681691)]
    [InlineData(8112u, -0.57144841420681702, -0.5889765868020802, -0.57144841420681702)]
    [InlineData(10816u, -0.57144841420681702, -0.57144841420681702, 0.5889765868020802)]
    [InlineData(10867u, 0.57144841420681702, -0.57144841420681691, 0.58897658680208009)]
    [InlineData(13520u, -0.57144841420681702, -0.57144841420681702, -0.5889765868020802)]
    [InlineData(16223u, 0.57144841420681702, 0.57144841420681702, -0.58897658680208009)]
    public void Rcs_Normals_Are_The_Directions_Of_The_Cells_Their_Codes_Name(ushort code, double x, double y, double z)
    {
        // A code names a cell on a face of a cube: face * 2704 + i * 52 + j, the faces along +x, -x, +y, -y, +z and -z, each
        // with 52 by 52 cells of equal angle. The directions were computed from that definition apart from the reader, for the
        // first cell of every face, for cells that differ in i alone and in j alone, and for the last cell.
        using var dir = new TempDirectory();
        var scan = new RcsScanBuilder { Normals = true };
        scan.AddLeaf((0, 0, 0), 1, new RcsSamplePoint(0, 0, 0, Normal: code));
        using var doc = RcsScanDocument.Open(dir.Write("normal.rcs", scan.Build()));

        var point = Assert.Single(doc.ReadAllPoints(NoTransform));
        PclVector3 normal = point.Normal!.Value;

        AssertNear(new PclVector3(x, y, z), normal);
        Assert.Equal(1.0, normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z, Precision);
    }

    [Fact]
    public void An_Rcs_Point_Has_A_Normal_Only_For_A_Plain_Cell_Code()
    {
        // A point without a normal states a zero code, as some files do, or a flag bit beside the code, as others do. A
        // code above the last cell names no cell either.
        ushort[] fields = [12194, 0, 0x4000, 0x4000 | 12194, 0x8000, 0xC000, 0x3FFF];
        bool[] hasNormal = [true, false, false, false, false, false, false];

        using var dir = new TempDirectory();
        var scan = new RcsScanBuilder { Normals = true };
        scan.AddLeaf((0, 0, 0), 1, [.. fields.Select((field, i) => new RcsSamplePoint((uint)i, 0, 0, Normal: field))]);
        using var doc = RcsScanDocument.Open(dir.Write("flags.rcs", scan.Build()));

        Assert.True(doc.HasNormalData);
        Assert.True(doc.Scans[0].HasNormals);
        var points = doc.ReadAllPoints(NoTransform).ToList();
        Assert.Equal(hasNormal, points.Select(p => p.Normal.HasValue));
        AssertNear(new PclVector3(0.015101514293334039, 0.015101514293334039, 0.99977191825540723), points[0].Normal!.Value);
    }

    [Fact]
    public void An_Rcs_Without_Normals_Reports_None_Whatever_Its_Records_State()
    {
        using var dir = new TempDirectory();
        var scan = new RcsScanBuilder();
        scan.AddLeaf((0, 0, 0), 1, new RcsSamplePoint(1, 2, 3, Normal: 12194));
        using var doc = RcsScanDocument.Open(dir.Write("flat.rcs", scan.Build()));

        Assert.False(doc.HasNormalData);
        Assert.False(doc.Scans[0].HasNormals);
        Assert.Null(Assert.Single(doc.ReadAllPoints(NoTransform)).Normal);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void Rcs_Channels_Follow_The_Flags_Of_The_Header(bool color, bool intensity, bool normals)
    {
        // The bytes of a channel are read only when the header states the channel; a file may hold anything in their place.
        using var dir = new TempDirectory();
        var scan = new RcsScanBuilder { Color = color, Intensity = intensity, Normals = normals };
        scan.AddLeaf((0, 0, 0), 1, new RcsSamplePoint(1, 2, 3, 200, 10, 20, 30, 12194), new RcsSamplePoint(4, 5, 6));
        using var doc = RcsScanDocument.Open(dir.Write("channels.rcs", scan.Build()));

        var rcsScan = Assert.Single(doc.Scans);
        Assert.Equal(color, doc.HasColorData);
        Assert.Equal(normals, doc.HasNormalData);
        Assert.Equal(intensity, doc.HasIntensityData);
        Assert.Equal(color, rcsScan.HasColors);
        Assert.Equal(normals, rcsScan.HasNormals);
        Assert.Equal(intensity, rcsScan.HasIntensities);

        var points = rcsScan.ReadPoints(NoTransform).ToList();
        Assert.Equal(2, points.Count);
        Assert.Equal(color ? new PclColor(10, 20, 30) : (PclColor?)null, points[0].Color);
        Assert.Equal(color ? new PclColor(0, 0, 0) : (PclColor?)null, points[1].Color);
        Assert.Equal(intensity ? 200 / 255.0f : (float?)null, points[0].Intensity);
        Assert.Equal(intensity ? 0.0f : (float?)null, points[1].Intensity);
        Assert.Equal(normals, points[0].Normal.HasValue);
        Assert.Null(points[1].Normal);
    }

    [Fact]
    public void Rcs_Points_Count_From_The_Cube_Of_Their_Own_Leaf_In_File_Order()
    {
        using var dir = new TempDirectory();
        var scan = new RcsScanBuilder();
        scan.AddLeaf((1, 2, 3), 0.5, new RcsSamplePoint(100, 0, 0), new RcsSamplePoint(0, 500, 0));
        // A leaf without points and without a block lies between the two.
        scan.AddLeaf((9, 9, 9), 0.25);
        // The first record of the last leaf states the offsets of the first record of the first leaf.
        scan.AddLeaf((-1, 5, 0.5), 0.25, new RcsSamplePoint(100, 0, 0), new RcsSamplePoint(0, 0, 250), new RcsSamplePoint(7, 8, 9));
        using var doc = RcsScanDocument.Open(dir.Write("leaves.rcs", scan.Build()));

        var rcsScan = Assert.Single(doc.Scans);
        Assert.Equal(5, rcsScan.PointCount);
        Assert.Equal(5, doc.TotalPointCount);

        var points = rcsScan.ReadPoints(NoTransform).ToList();
        Assert.Equal(5, points.Count);
        Assert.Equal(new[]
        {
            new PclVector3(1 + 100 * 0.001, 2, 3),
            new PclVector3(1, 2 + 500 * 0.001, 3),
            new PclVector3(-1 + 100 * 0.001, 5, 0.5),
            new PclVector3(-1, 5, 0.5 + 250 * 0.001),
            new PclVector3(-1 + 7 * 0.001, 5 + 8 * 0.001, 0.5 + 9 * 0.001),
        }, points.Select(p => p.Position).ToArray());
        Assert.Equal(new ulong[] { 0, 1, 2, 3, 4 }, points.Select(p => p.Index).ToArray());

        // The step counts the points of every leaf on their own.
        var stepped = rcsScan.ReadPoints(new PclReadOptions { SubsampleStep = 2, ApplyTransform = false }).ToList();
        Assert.Equal(new[] { points[0].Position, points[2].Position, points[4].Position }, stepped.Select(p => p.Position).ToArray());
        Assert.Equal(new ulong[] { 0, 1, 2 }, stepped.Select(p => p.Index).ToArray());
    }

    [Fact]
    public void Rcs_Sections_Are_Taken_From_The_Table_Whatever_Their_Order()
    {
        // Reading needs the leaves section and the points section; the other sections are skipped wherever they lie.
        RcsSamplePoint[] records = [new(100, 200, 300, 128, 10, 20, 30, 12194), new(0, 0, 0)];
        var plain = new RcsScanBuilder { Normals = true };
        plain.AddLeaf((1, 2, 3), 0.5, records);

        var shuffled = new RcsScanBuilder { Normals = true };
        shuffled.AddLeaf((1, 2, 3), 0.5, records);
        shuffled.OtherSections[3] = new byte[72];
        shuffled.OtherSections[7] = [0x12, 0x00, 0x00, 0x80, 0xFF];
        shuffled.OtherSections[11] = new byte[134];
        shuffled.SectionOrder = [3, 7, RcsScanBuilder.PointsSection, 11, RcsScanBuilder.LeavesSection];

        using var dir = new TempDirectory();
        using var first = RcsScanDocument.Open(dir.Write("plain.rcs", plain.Build()));
        using var second = RcsScanDocument.Open(dir.Write("shuffled.rcs", shuffled.Build()));

        Assert.Equal(2, second.Scans[0].PointCount);
        Assert.Equal(first.ReadAllPoints(NoTransform).ToList(), second.ReadAllPoints(NoTransform).ToList());
    }

    [Fact]
    public void An_Rcs_Leaf_With_More_Index_Words_Than_Points_Is_Read()
    {
        // The records of a block follow its index words. A leaf whose octree needs more words than the one every point has
        // holds its records further back.
        using var dir = new TempDirectory();
        var scan = new RcsScanBuilder();
        scan.AddLeaf((0, 0, 0), 262.144, RcsPoints).IndexBytes = 3 * RcsPoints.Length * 4 + 12;
        using var doc = RcsScanDocument.Open(dir.Write("words.rcs", scan.Build()));
        using var plain = RcsScanDocument.Open(dir.Write("plain.rcs", SyntheticFixtures.CreateSyntheticRcsWithPoints(RcsPoints, (0, 0, 0))));

        Assert.Equal(3, doc.Scans[0].PointCount);
        Assert.Equal(plain.ReadAllPoints(NoTransform).ToList(), doc.ReadAllPoints(NoTransform).ToList());
    }

    [Fact]
    public void The_Top_Byte_Of_The_Word_Of_A_Record_Is_Not_Part_Of_Its_Position()
    {
        RcsSamplePoint[] plain = [new(100, 200, 300, 128, 10, 20, 30, 12194), new(RcsScanBuilder.MaxOffset, RcsScanBuilder.MaxOffset, RcsScanBuilder.MaxOffset)];
        RcsSamplePoint[] marked = [plain[0] with { Top = 0xFF }, plain[1] with { Top = 0xA5 }];

        using var dir = new TempDirectory();
        using var first = RcsScanDocument.Open(dir.Write("plain.rcs", SyntheticFixtures.CreateSyntheticRcsWithPoints(plain, (1, 2, 3), withNormals: true)));
        using var second = RcsScanDocument.Open(dir.Write("marked.rcs", SyntheticFixtures.CreateSyntheticRcsWithPoints(marked, (1, 2, 3), withNormals: true)));

        Assert.Equal(first.ReadAllPoints(NoTransform).ToList(), second.ReadAllPoints(NoTransform).ToList());
    }

    [Fact]
    public void Rcs_Scale_Is_Applied_To_A_Position_Before_The_Translation()
    {
        using var dir = new TempDirectory();
        var scan = new RcsScanBuilder { Translation = (1, 2, 3), Scale = (0.5, 2, 10), Normals = true };
        scan.AddLeaf((0.25, 0, -0.5), 1, new RcsSamplePoint(1000, 2000, 3000, Normal: 2678), new RcsSamplePoint(100, 100, 100));
        using var doc = RcsScanDocument.Open(dir.Write("scaled.rcs", scan.Build()));

        var raw = doc.ReadAllPoints(NoTransform).ToList();
        var scaled = doc.ReadAllPoints().ToList();

        Assert.Equal(new PclVector3(0.25 + 1000 * 0.001, 0 + 2000 * 0.001, -0.5 + 3000 * 0.001), raw[0].Position);
        Assert.Equal(new PclVector3((0.25 + 1000 * 0.001) * 0.5 + 1, (0 + 2000 * 0.001) * 2 + 2, (-0.5 + 3000 * 0.001) * 10 + 3), scaled[0].Position);
        Assert.Equal(new PclVector3((0.25 + 100 * 0.001) * 0.5 + 1, (0 + 100 * 0.001) * 2 + 2, (-0.5 + 100 * 0.001) * 10 + 3), scaled[1].Position);

        // A scale that is not one re-orients a normal, as the transform of any scan does.
        AssertNear(new PclVector3(0.71766355869077281, 0.010840278959110543, 0.69630561169603133), raw[0].Normal!.Value);
        AssertNear(new PclVector3(0.051465119585504454, 0.0031095141795334682, 0.9986699516795412), scaled[0].Normal!.Value);
        Assert.Null(scaled[1].Normal);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(14)]
    [InlineData(17)]
    [InlineData(21)]
    public void An_Rcs_Leaf_Block_That_Does_Not_Fit_Its_Points_Is_Rejected(int bytesPerRecord)
    {
        // A block holds at least one four-byte index word per record, then the 16-byte records: 20 bytes per record. Any
        // other size would place the records elsewhere, so the file is rejected instead of being read at a guess.
        using var dir = new TempDirectory();
        using var doc = RcsScanDocument.Open(dir.Write("odd.rcs",
            SyntheticFixtures.CreateSyntheticRcsWithPoints(RcsPoints, (0, 0, 0), declaredPayloadBytes: (uint)(RcsPoints.Length * bytesPerRecord))));

        Assert.Throws<InvalidDataException>(() => doc.Scans[0].ReadPoints().ToList());
        Assert.Throws<InvalidDataException>(() => doc.Scans[0].PointCount);
    }

    [Fact]
    public void An_Rcs_Leaf_Block_Of_Twenty_Bytes_Per_Record_Is_Read()
    {
        // The smallest block a leaf can hold: one index word and one record per point.
        using var dir = new TempDirectory();
        using var doc = RcsScanDocument.Open(dir.Write("tight.rcs",
            SyntheticFixtures.CreateSyntheticRcsWithPoints(RcsPoints, (0, 0, 0), declaredPayloadBytes: (uint)(RcsPoints.Length * 20))));

        Assert.Equal(3, doc.Scans[0].PointCount);
        Assert.Equal(3, doc.ReadAllPoints().Count());
    }

    [Fact]
    public void An_Rcs_Leaf_Larger_Than_A_Chunk_Keeps_Record_Alignment()
    {
        // A chunk of 65536 bytes holds 4096 whole 16-byte records, so 7000 records run over a chunk boundary.
        RcsSamplePoint[] records = [.. Enumerable.Range(0, 7000).Select(i => new RcsSamplePoint((uint)(i % 1000), (uint)(i / 1000), 0))];
        using var dir = new TempDirectory();
        using var doc = RcsScanDocument.Open(dir.Write("big.rcs", SyntheticFixtures.CreateSyntheticRcsWithPoints(records, (0, 0, 0))));

        var all = doc.Scans[0].ReadPoints(NoTransform).ToList();

        Assert.Equal(7000, all.Count);
        Assert.Equal(records.Select(p => new PclVector3(p.X * 0.001, p.Y * 0.001, p.Z * 0.001)), all.Select(p => p.Position));
        Assert.Equal(Enumerable.Range(0, 7000).Select(i => (ulong)i), all.Select(p => p.Index));
    }

    [Fact]
    public void Read_Synthetic_Rcs_Points_Honours_Limit_And_Step()
    {
        using var dir = new TempDirectory();
        using var doc = RcsScanDocument.Open(dir.Write("points.rcs", SyntheticFixtures.CreateSyntheticRcsWithPoints(RcsPoints, (0, 0, 0))));

        Assert.Equal(2, doc.ReadAllPoints(new PclReadOptions { MaxPoints = 2 }).Count());
        Assert.Equal(3, doc.ReadAllPoints(new PclReadOptions { MaxPoints = 3 }).Count());
        Assert.Equal(3, doc.ReadAllPoints(new PclReadOptions { MaxPoints = 99 }).Count());
        Assert.Equal(2, doc.ReadAllPoints(new PclReadOptions { SubsampleStep = 2 }).Count());
        Assert.Single(doc.ReadAllPoints(new PclReadOptions { SubsampleStep = 3 }));
        Assert.Empty(doc.ReadAllPoints(new PclReadOptions { MaxPoints = 0 }));
    }

    [Fact]
    public void Rcs_Of_An_Unsupported_Major_Version_Is_Rejected_At_Read_Time()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticRcsWithPoints(RcsPoints, (0, 0, 0));
        bytes[8] = 2;
        using var doc = RcsScanDocument.Open(dir.Write("old.rcs", bytes));

        Assert.Equal(2u, doc.MajorVersion);
        var error = Assert.Throws<NotSupportedException>(() => doc.ReadAllPoints().ToList());
        Assert.Contains("2.2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_Rcs_Of_A_Version_Without_A_Described_Layout_Is_Rejected_With_A_Message_That_Names_It()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticRcsWithPoints(RcsPoints, (0, 0, 0));
        bytes[8] = 4;
        using var doc = RcsScanDocument.Open(dir.Write("new.rcs", bytes));

        // The file is listed with its header, but its points cannot be decoded: nothing is read from the file for them,
        // so the count of the header is reported.
        Assert.Equal(4u, doc.MajorVersion);
        Assert.Equal(3, doc.Scans[0].PointCount);
        var error = Assert.Throws<NotSupportedException>(() => doc.ReadAllPoints().ToList());
        Assert.Contains("4.2", error.Message, StringComparison.Ordinal);
        Assert.Contains("3", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Rcs_Declines_Unverified_Minor_Versions(byte minorVersion)
    {
        using var dir = new TempDirectory();
        byte[] file = SyntheticFixtures.CreateSyntheticRcsWithPoints(RcsPoints, (0, 0, 0));
        file[12] = minorVersion;
        using var doc = RcsScanDocument.Open(dir.Write("older.rcs", file));
        var exception = Assert.Throws<NotSupportedException>(() => doc.ReadAllPoints().ToList());
        Assert.Contains($"3.{minorVersion}", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Node_Layouts_That_Are_Decoded_Are_Named()
    {
        Assert.Equal([3u], RcsScanDocument.DecodedNodeLayoutVersions);
    }

    [Fact]
    public void An_Rcs_With_A_Truncated_Leaf_Table_Is_Rejected()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticRcsWithPoints(RcsPoints, (0, 0, 0));

        // The file ends in the middle of the leaf record.
        string path = dir.Write("short.rcs", bytes[..(RcsScanBuilder.HeaderSize + 8 + 100)]);
        using var doc = RcsScanDocument.Open(path);

        Assert.Throws<InvalidDataException>(() => doc.ReadAllPoints().ToList());
    }

    // ---- ReCap project ----------------------------------------------------------------------------------------

    [Fact]
    public void Parse_Synthetic_Rcp_Project()
    {
        using var dir = new TempDirectory();
        using var doc = RcpProjectDocument.Open(dir.Write("project.rcp", SyntheticFixtures.CreateSyntheticRcpZip()));

        Assert.Equal(PclFileKind.RecapProject, doc.Kind);
        Assert.Equal("synthetic-project-id", doc.ProjectId);
        Assert.Equal("EPSG:3857", doc.CoordinateSystem);

        var scan = Assert.Single(doc.Scans);
        Assert.Equal("Station 1", scan.Name);
        Assert.Equal("scan-001", scan.Id);
        Assert.Equal(5000, scan.PointCount);
        Assert.Equal(new PclVector3(10.5, 20.5, 5.0), scan.Transform.Translation);
    }

    [Fact]
    public void Rcp_Scan_Without_A_File_Warns_And_Yields_No_Points()
    {
        using var dir = new TempDirectory();
        using var doc = RcpProjectDocument.Open(dir.Write("project.rcp", SyntheticFixtures.CreateSyntheticRcpZip()));

        Assert.Single(doc.Warnings);
        var scan = (RcpScan)Assert.Single(doc.Scans);
        Assert.Null(scan.RcsFilePath);
        Assert.False(scan.HasColors);
        Assert.False(scan.HasNormals);
        Assert.False(scan.HasIntensities);
        Assert.Empty(scan.ReadPoints());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void Rcp_Scan_Channels_Only_Read_The_Referenced_Rcs_Header(bool color, bool intensity, bool normals)
    {
        using var dir = new TempDirectory();
        string rcp = dir.Write("site.rcp", SyntheticFixtures.CreateSyntheticRcpZip(scanPath: "scan1.rcs"));
        var builder = new RcsScanBuilder { Color = color, Intensity = intensity, Normals = normals };
        builder.AddLeaf((0, 0, 0), 1, new RcsSamplePoint(1, 2, 3));
        dir.Write("scan1.rcs", builder.Build()[..512]);

        using var doc = RcpProjectDocument.Open(rcp);
        var scan = Assert.Single(doc.Scans);
        Assert.Equal(color, scan.HasColors);
        Assert.Equal(intensity, scan.HasIntensities);
        Assert.Equal(normals, scan.HasNormals);
        Assert.Throws<InvalidDataException>(() => scan.ReadPoints().ToList());
    }

    [Fact]
    public void Rcp_Scan_Can_Retry_Reading_The_Channels_After_A_Truncated_Rcs_Header()
    {
        using var dir = new TempDirectory();
        string rcp = dir.Write("site.rcp", SyntheticFixtures.CreateSyntheticRcpZip(scanPath: "scan1.rcs"));
        byte[] bytes = new RcsScanBuilder { Color = true, Intensity = false, Normals = true }.Build();
        dir.Write("scan1.rcs", bytes[..511]);

        using var doc = RcpProjectDocument.Open(rcp);
        var scan = Assert.Single(doc.Scans);
        Assert.Throws<InvalidDataException>(() => { _ = scan.HasColors; });
        Assert.Throws<InvalidDataException>(() => { _ = scan.HasIntensities; });
        Assert.Throws<InvalidDataException>(() => { _ = scan.HasNormals; });

        dir.Write("scan1.rcs", bytes);
        Assert.True(scan.HasColors);
        Assert.False(scan.HasIntensities);
        Assert.True(scan.HasNormals);
    }

    [Fact]
    public void Rcp_Scan_Is_Read_From_The_Support_Folder()
    {
        using var dir = new TempDirectory();
        string rcp = dir.Write("site.rcp", SyntheticFixtures.CreateSyntheticRcpZip(scanPath: "scan1.rcs"));
        dir.Write(Path.Combine("site Support", "scan1.rcs"),
            SyntheticFixtures.CreateSyntheticRcsWithPoints(RcsPoints, (0, 0, 0)));

        using var doc = RcpProjectDocument.Open(rcp);

        Assert.Empty(doc.Warnings);
        var scan = (RcpScan)Assert.Single(doc.Scans);
        Assert.NotNull(scan.RcsFilePath);
        Assert.True(scan.HasColors);
        Assert.Equal(3, doc.ReadAllPoints(NoTransform).Count());
    }

    [Fact]
    public void An_Rcp_Applies_The_Transform_Of_The_Project_To_Its_Points()
    {
        using var dir = new TempDirectory();
        string rcp = dir.Write("site.rcp", SyntheticFixtures.CreateSyntheticRcpZip());
        dir.Write(Path.Combine("site Support", "scan1.rcs"),
            SyntheticFixtures.CreateSyntheticRcsWithPoints(RcsPoints, (0, 0, 0)));

        using var doc = RcpProjectDocument.Open(rcp);

        // The project places the scan at (10.5, 20.5, 5).
        var moved = doc.ReadAllPoints().ToList();
        var raw = doc.ReadAllPoints(NoTransform).ToList();
        Assert.Equal(3, moved.Count);
        for (int i = 0; i < moved.Count; i++)
        {
            AssertNear(new PclVector3(raw[i].X + 10.5, raw[i].Y + 20.5, raw[i].Z + 5.0), moved[i].Position);
        }
    }

    [Fact]
    public void An_Rcp_Without_A_Transform_Of_The_Project_Uses_The_Transform_Of_The_Scan_File()
    {
        using var dir = new TempDirectory();
        string rcp = dir.Write("site.rcp", SyntheticFixtures.CreateSyntheticRcpZip(withTransform: false));
        dir.Write(Path.Combine("site Support", "scan1.rcs"),
            SyntheticFixtures.CreateSyntheticRcsWithPoints(RcsPoints, (0, 0, 0), tx: 1.0, ty: 2.0, tz: 3.0));

        using var doc = RcpProjectDocument.Open(rcp);

        var points = doc.ReadAllPoints().ToList();
        Assert.Equal(3, points.Count);
        AssertNear(new PclVector3(1.1, 2.2, 3.3), points[0].Position);
    }

    [Fact]
    public void An_Rcp_With_A_Damaged_Transform_Is_Rejected()
    {
        using var dir = new TempDirectory();
        string manifest = SyntheticFixtures.RcpManifest("scan1.rcs", "Station 1")
            .Replace("<Translation X=\"10.5\" Y=\"20.5\" Z=\"5.0\" />", "<Translation X=\"NaN\" Y=\"20.5\" Z=\"5.0\" />");

        Assert.Throws<InvalidDataException>(() => RcpProjectDocument.Open(dir.Write("site.rcp", SyntheticFixtures.CreateZip("project.xml", manifest))));
    }

    [Fact]
    public void An_Rcp_Placement_Reorients_The_Normals_With_The_Points()
    {
        // A non-uniform scale stretches the geometry; the normals must be re-oriented with it, not left in the
        // local space of the scan.
        using var dir = new TempDirectory();
        string manifest = SyntheticFixtures.RcpManifest("scan1.rcs", "Station 1")
            .Replace("<Translation X=\"10.5\" Y=\"20.5\" Z=\"5.0\" />", "<Translation X=\"0\" Y=\"0\" Z=\"0\" />")
            .Replace("<Scale X=\"1\" Y=\"1\" Z=\"1\" />", "<Scale X=\"2\" Y=\"1\" Z=\"1\" />");
        string rcp = dir.Write("site.rcp", SyntheticFixtures.CreateZip("project.xml", manifest));
        // The point lies 0.1 metres along x and names the cell (i = 51, j = 26) of the +x face.
        var points = new[] { new RcsSamplePoint(100, 0, 0, Normal: 2678) };
        dir.Write(Path.Combine("site Support", "scan1.rcs"),
            SyntheticFixtures.CreateSyntheticRcsWithPoints(points, (0, 0, 0), withNormals: true));

        using var doc = RcpProjectDocument.Open(rcp);
        var pt = Assert.Single(doc.ReadAllPoints());
        AssertNear(new PclVector3(0.2, 0, 0), pt.Position);
        AssertNear(new PclVector3(0.8996974848469792, 0.0067949470169167041, 0.43646106865911521), pt.Normal!.Value);
    }

    [Fact]
    public void An_Rcp_Node_That_States_Only_A_Rotation_Keeps_The_Transform_Of_The_Scan_File()
    {
        // A rotation is informational and places nothing; stating one alone must not suppress the placement that
        // the scan file itself declares.
        using var dir = new TempDirectory();
        string manifest = SyntheticFixtures.RcpManifest("scan1.rcs", "Station 1")
            .Replace("<Translation X=\"10.5\" Y=\"20.5\" Z=\"5.0\" />", "")
            .Replace("<Rotation Yaw=\"0\" Pitch=\"0\" Roll=\"0\" />", "<Rotation Yaw=\"90\" Pitch=\"0\" Roll=\"0\" />")
            .Replace("<Scale X=\"1\" Y=\"1\" Z=\"1\" />", "");
        string rcp = dir.Write("site.rcp", SyntheticFixtures.CreateZip("project.xml", manifest));
        dir.Write(Path.Combine("site Support", "scan1.rcs"),
            SyntheticFixtures.CreateSyntheticRcsWithPoints(RcsPoints, (0, 0, 0), tx: 1.0, ty: 2.0, tz: 3.0));

        using var doc = RcpProjectDocument.Open(rcp);
        var points = doc.ReadAllPoints().ToList();
        Assert.Equal(3, points.Count);
        AssertNear(new PclVector3(1.1, 2.2, 3.3), points[0].Position);
    }

    // ---- E57 -------------------------------------------------------------------------------------------------

    [Fact]
    public void Parse_Synthetic_E57_Header_And_Scans()
    {
        using var dir = new TempDirectory();
        using var doc = E57Document.Open(dir.Write("sample.e57", SyntheticFixtures.CreateSyntheticE57(guid: "test-e57-guid-1234")));

        Assert.Equal(PclFileKind.E57, doc.Kind);
        Assert.Equal(1u, doc.MajorVersion);
        Assert.Equal(0u, doc.MinorVersion);
        Assert.Equal(1024UL, doc.PageSize);
        Assert.Equal("test-e57-guid-1234", doc.ProjectGuid);

        var scan = (E57Scan)Assert.Single(doc.Scans);
        Assert.Equal("Synthetic Scan 01", scan.Name);
        Assert.Equal("scan-guid-001", scan.Id);
        Assert.Equal(250, scan.PointCount);
        Assert.True(scan.HasColors);
        Assert.True(scan.HasIntensities);
        Assert.Equal(new PclVector3(1.5, 2.5, 3.5), scan.Transform.Translation);
        Assert.Null(scan.Transform.Matrix4x4);
        Assert.Equal(new PclBounds(new PclVector3(0, -249, 0), new PclVector3(124.5, 0, 62.25)), scan.Bounds);
    }

    [Fact]
    public void Read_Synthetic_E57_Points_Across_Packets_And_Page_Boundaries()
    {
        var spec = new E57Spec();
        using var dir = new TempDirectory();
        using var doc = E57Document.Open(dir.Write("points.e57", spec.Build()));

        var plain = doc.Scans[0].ReadPoints(NoTransform).ToList();
        var moved = doc.Scans[0].ReadPoints().ToList();

        // 250 points in three packets of about 1.6 kB: every packet crosses at least one page checksum.
        Assert.Equal(250, plain.Count);
        for (int i = 0; i < plain.Count; i++)
        {
            var written = spec.Points[i];
            Assert.Equal(new PclVector3(written.X, written.Y, written.Z), plain[i].Position);
            Assert.Equal(new PclColor(written.R, written.G, written.B), plain[i].Color);
            Assert.Equal(written.Intensity / 255.0f, plain[i].Intensity);
            Assert.Equal((ulong)i, plain[i].Index);
            Assert.Equal(written.X + 1.5, moved[i].X);
            Assert.Equal(written.Z + 3.5, moved[i].Z);
        }
    }

    [Fact]
    public void E57_Limit_And_Step_Apply_To_The_Record_Index_Of_The_Whole_Scan()
    {
        using var dir = new TempDirectory();
        using var doc = E57Document.Open(dir.Write("points.e57", new E57Spec().Build()));
        var scan = doc.Scans[0];

        Assert.Equal(7, scan.ReadPoints(new PclReadOptions { MaxPoints = 7 }).Count());
        Assert.Equal(250, scan.ReadPoints(new PclReadOptions { MaxPoints = 100_000 }).Count());
        Assert.Equal(125, scan.ReadPoints(new PclReadOptions { SubsampleStep = 2 }).Count());
        Assert.Equal(84, scan.ReadPoints(new PclReadOptions { SubsampleStep = 3 }).Count());
        Assert.Empty(scan.ReadPoints(new PclReadOptions { MaxPoints = 0 }));
    }

    [Fact]
    public void E57_Pose_Rotation_Is_Applied()
    {
        // 90 degrees about Z as a unit quaternion: (1, 0, 0) turns into (0, 1, 0).
        const string pose = """
            <pose type="Structure">
              <rotation type="Structure"><w type="Float">0.7071067811865476</w><x type="Float">0</x><y type="Float">0</y><z type="Float">0.7071067811865476</z></rotation>
              <translation type="Structure"><x type="Float">0</x><y type="Float">0</y><z type="Float">5</z></translation>
            </pose>
            """;
        var spec = new E57Spec { PoseXml = pose, Points = [new E57SamplePoint(1, 0, 0, 1, 2, 3, 4)] };
        using var dir = new TempDirectory();
        using var doc = E57Document.Open(dir.Write("rotated.e57", spec.Build()));

        var scan = doc.Scans[0];
        Assert.NotNull(scan.Transform.Matrix4x4);

        AssertNear(new PclVector3(0, 1, 5), scan.ReadPoints().Single().Position);
        AssertNear(new PclVector3(1, 0, 0), scan.ReadPoints(NoTransform).Single().Position);
    }

    [Fact]
    public void E57_Without_Colour_Or_Intensity_Reports_Them_As_Absent()
    {
        const string prototype = """
            <prototype type="Structure">
              <cartesianX type="Float" precision="single"/>
              <cartesianY type="Float" precision="single"/>
              <cartesianZ type="Float" precision="single"/>
            </prototype>
            """;
        byte[] floats = new byte[8];
        BitConverter.GetBytes(1.0f).CopyTo(floats, 0);
        BitConverter.GetBytes(2.0f).CopyTo(floats, 4);
        var spec = new E57Spec
        {
            PrototypeXml = prototype,
            Points = [],
            RecordCount = 2,
            RawPackets = E57Spec.DataPacket([floats, floats, floats]),
        };
        using var dir = new TempDirectory();
        using var doc = E57Document.Open(dir.Write("plain.e57", spec.Build()));

        var scan = doc.Scans[0];
        Assert.False(scan.HasColors);
        Assert.False(scan.HasIntensities);

        var points = scan.ReadPoints(NoTransform).ToList();
        Assert.Equal(2, points.Count);
        Assert.All(points, p =>
        {
            Assert.Null(p.Color);
            Assert.Null(p.Intensity);
        });
        Assert.Equal(new PclVector3(1, 1, 1), points[0].Position);
        Assert.Equal(new PclVector3(2, 2, 2), points[1].Position);
    }

    [Fact]
    public void E57_With_Scaled_Integer_Coordinates_Is_Decoded()
    {
        const string prototype = """
            <prototype type="Structure">
              <cartesianX type="ScaledInteger" minimum="0" maximum="1000" scale="0.001"/>
              <cartesianY type="ScaledInteger" minimum="-100000" maximum="100000" scale="0.01" offset="5"/>
              <cartesianZ type="Float" precision="single"/>
            </prototype>
            """;
        byte[] xs = [.. BitConverter.GetBytes((ushort)0), .. BitConverter.GetBytes((ushort)1000)];
        byte[] ys = [.. BitConverter.GetBytes(-50000), .. BitConverter.GetBytes(25000)];
        byte[] zs = [.. BitConverter.GetBytes(1.5f), .. BitConverter.GetBytes(-2.5f)];
        var spec = new E57Spec { PrototypeXml = prototype, Points = [], RecordCount = 2, RawPackets = E57Spec.DataPacket([xs, ys, zs]) };
        using var dir = new TempDirectory();
        using var doc = E57Document.Open(dir.Write("scaled.e57", spec.Build()));

        var scan = Assert.Single(doc.Scans);
        var points = scan.ReadPoints(NoTransform).ToList();

        Assert.Equal(2, points.Count);
        AssertNear(new PclVector3(0, -495, 1.5), points[0].Position);
        AssertNear(new PclVector3(1, 255, -2.5), points[1].Position);
    }

    [Fact]
    public void E57_With_Scaled_Integer_Coordinates_Of_Several_Widths_Is_Decoded()
    {
        const string prototype = """
            <prototype type="Structure">
              <cartesianX type="ScaledInteger" minimum="-10" maximum="10" scale="0.5"/>
              <cartesianY type="ScaledInteger" minimum="0" maximum="200000"/>
              <cartesianZ type="ScaledInteger" minimum="-1099511627776" maximum="1099511627776"/>
            </prototype>
            """;
        byte[] xs = [0xF6, 0x0A];
        byte[] ys = [.. BitConverter.GetBytes(0u), .. BitConverter.GetBytes(200000u)];
        byte[] zs = [.. BitConverter.GetBytes(5_000_000_000L), .. BitConverter.GetBytes(-7_000_000_000L)];
        var spec = new E57Spec { PrototypeXml = prototype, Points = [], RecordCount = 2, RawPackets = E57Spec.DataPacket([xs, ys, zs]) };
        using var dir = new TempDirectory();
        using var doc = E57Document.Open(dir.Write("widths.e57", spec.Build()));

        var points = doc.Scans[0].ReadPoints(NoTransform).ToList();

        Assert.Equal(2, points.Count);
        AssertNear(new PclVector3(-5, 0, 5_000_000_000), points[0].Position);
        AssertNear(new PclVector3(5, 200000, -7_000_000_000), points[1].Position);
    }

    [Fact]
    public void E57_With_A_Scaled_Integer_Range_That_Is_Empty_Is_Rejected()
    {
        const string prototype = """
            <prototype type="Structure">
              <cartesianX type="ScaledInteger" minimum="5" maximum="4" scale="0.001"/>
              <cartesianY type="ScaledInteger" minimum="0" maximum="1000" scale="0.001"/>
              <cartesianZ type="ScaledInteger" minimum="0" maximum="1000" scale="0.001"/>
            </prototype>
            """;
        using var dir = new TempDirectory();

        Assert.Throws<InvalidDataException>(() => E57Document.Open(dir.Write("scaled.e57", new E57Spec { PrototypeXml = prototype }.Build())));
    }

    [Fact]
    public void E57_With_A_Scaled_Integer_Scale_That_Is_Not_Finite_Is_Rejected()
    {
        const string prototype = """
            <prototype type="Structure">
              <cartesianX type="ScaledInteger" minimum="0" maximum="1000" scale="NaN"/>
              <cartesianY type="ScaledInteger" minimum="0" maximum="1000" scale="0.001"/>
              <cartesianZ type="ScaledInteger" minimum="0" maximum="1000" scale="0.001"/>
            </prototype>
            """;
        using var dir = new TempDirectory();

        Assert.Throws<InvalidDataException>(() => E57Document.Open(dir.Write("scaled.e57", new E57Spec { PrototypeXml = prototype }.Build())));
    }

    [Fact]
    public void E57_With_A_Partial_Scaled_Integer_Record_Is_Rejected()
    {
        const string prototype = """
            <prototype type="Structure">
              <cartesianX type="ScaledInteger" minimum="0" maximum="1000" scale="0.001"/>
              <cartesianY type="ScaledInteger" minimum="0" maximum="1000" scale="0.001"/>
              <cartesianZ type="ScaledInteger" minimum="0" maximum="1000" scale="0.001"/>
            </prototype>
            """;
        byte[] xs = [0x00, 0x00, 0xE8];
        byte[] ys = [.. BitConverter.GetBytes((ushort)0), .. BitConverter.GetBytes((ushort)1000)];
        byte[] zs = [.. BitConverter.GetBytes((ushort)0), .. BitConverter.GetBytes((ushort)1000)];
        var spec = new E57Spec { PrototypeXml = prototype, Points = [], RecordCount = 2, RawPackets = E57Spec.DataPacket([xs, ys, zs]) };
        using var dir = new TempDirectory();
        using var doc = E57Document.Open(dir.Write("partial.e57", spec.Build()));

        Assert.Throws<InvalidDataException>(() => { _ = doc.Scans[0].ReadPoints().ToList(); });
    }

    [Fact]
    public void E57_With_Spherical_Coordinates_Is_Decoded()
    {
        const string prototype = """
            <prototype type="Structure">
              <sphericalRange type="Float" precision="single"/>
              <sphericalAzimuth type="Float" precision="double"/>
              <sphericalElevation type="Float" precision="double"/>
            </prototype>
            """;
        byte[] rs = [.. BitConverter.GetBytes(2.0f), .. BitConverter.GetBytes(1.0f), .. BitConverter.GetBytes(2.0f)];
        byte[] azs = [.. BitConverter.GetBytes(0.0), .. BitConverter.GetBytes(Math.PI / 2), .. BitConverter.GetBytes(Math.PI)];
        byte[] els = [.. BitConverter.GetBytes(0.0), .. BitConverter.GetBytes(0.0), .. BitConverter.GetBytes(Math.PI / 6)];
        var spec = new E57Spec { PrototypeXml = prototype, Points = [], RecordCount = 3, RawPackets = E57Spec.DataPacket([rs, azs, els]) };
        using var dir = new TempDirectory();
        using var doc = E57Document.Open(dir.Write("spherical.e57", spec.Build()));

        var scan = Assert.Single(doc.Scans);
        Assert.False(scan.HasColors);
        Assert.False(scan.HasIntensities);

        var plain = scan.ReadPoints(NoTransform).ToList();
        Assert.Equal(3, plain.Count);
        // Azimuth is measured from the positive X axis: az=0 lies on +X, az=pi/2 on +Y.
        AssertNear(new PclVector3(2, 0, 0), plain[0].Position);
        AssertNear(new PclVector3(0, 1, 0), plain[1].Position);
        AssertNear(new PclVector3(-Math.Sqrt(3), 0, 1), plain[2].Position);

        // The pose of the scan moves the converted points as it moves Cartesian ones.
        var moved = scan.ReadPoints().ToList();
        AssertNear(new PclVector3(3.5, 2.5, 3.5), moved[0].Position);
    }

    [Fact]
    public void E57_With_Spherical_Coordinates_As_Scaled_Integers_Is_Decoded()
    {
        const string prototype = """
            <prototype type="Structure">
              <sphericalRange type="ScaledInteger" minimum="0" maximum="2000" scale="0.001"/>
              <sphericalAzimuth type="Float" precision="single"/>
              <sphericalElevation type="ScaledInteger" minimum="-180" maximum="180" scale="0.01"/>
            </prototype>
            """;
        byte[] rs = [.. BitConverter.GetBytes((ushort)2000)];
        byte[] azs = [.. BitConverter.GetBytes(0.0f)];
        byte[] els = [.. BitConverter.GetBytes((short)0)];
        var spec = new E57Spec { PrototypeXml = prototype, Points = [], RecordCount = 1, RawPackets = E57Spec.DataPacket([rs, azs, els]) };
        using var dir = new TempDirectory();
        using var doc = E57Document.Open(dir.Write("scaled-spherical.e57", spec.Build()));

        var point = Assert.Single(doc.Scans[0].ReadPoints(NoTransform));
        AssertNear(new PclVector3(2, 0, 0), point.Position);
    }

    [Fact]
    public void E57_With_A_Partial_Spherical_Triple_Lists_But_Is_Not_Decoded()
    {
        const string prototype = """
            <prototype type="Structure">
              <sphericalRange type="Float" precision="single"/>
              <sphericalAzimuth type="Float" precision="single"/>
            </prototype>
            """;
        using var dir = new TempDirectory();
        using var doc = E57Document.Open(dir.Write("spherical.e57", new E57Spec { PrototypeXml = prototype }.Build()));

        Assert.Single(doc.Scans);
        Assert.Throws<NotSupportedException>(() => doc.Scans[0].ReadPoints().ToList());
    }

    [Fact]
    public void E57_With_Both_Cartesian_And_Spherical_Coordinates_Lists_But_Is_Not_Decoded()
    {
        const string prototype = """
            <prototype type="Structure">
              <cartesianX type="Float" precision="single"/>
              <cartesianY type="Float" precision="single"/>
              <cartesianZ type="Float" precision="single"/>
              <sphericalRange type="Float" precision="single"/>
              <sphericalAzimuth type="Float" precision="single"/>
              <sphericalElevation type="Float" precision="single"/>
            </prototype>
            """;
        using var dir = new TempDirectory();
        using var doc = E57Document.Open(dir.Write("both.e57", new E57Spec { PrototypeXml = prototype }.Build()));

        Assert.Single(doc.Scans);
        Assert.Throws<NotSupportedException>(() => doc.Scans[0].ReadPoints().ToList());
    }

    [Fact]
    public void E57_With_A_Partial_Spherical_Record_Is_Rejected()
    {
        const string prototype = """
            <prototype type="Structure">
              <sphericalRange type="Float" precision="single"/>
              <sphericalAzimuth type="Float" precision="single"/>
              <sphericalElevation type="Float" precision="single"/>
            </prototype>
            """;
        byte[] rs = [.. BitConverter.GetBytes(2.0f), .. BitConverter.GetBytes(1.0f)];
        byte[] azs = [.. BitConverter.GetBytes(0.0f), 0x00, 0x00];
        byte[] els = [.. BitConverter.GetBytes(0.0f), .. BitConverter.GetBytes(0.0f)];
        var spec = new E57Spec { PrototypeXml = prototype, Points = [], RecordCount = 2, RawPackets = E57Spec.DataPacket([rs, azs, els]) };
        using var dir = new TempDirectory();
        using var doc = E57Document.Open(dir.Write("partial.e57", spec.Build()));

        Assert.Throws<InvalidDataException>(() => { _ = doc.Scans[0].ReadPoints().ToList(); });
    }

    [Fact]
    public void E57_With_Validity_Flags_Skips_Invalid_Points_And_Channels()
    {
        const string prototype = """
            <prototype type="Structure">
              <cartesianX type="Float" precision="single"/>
              <cartesianY type="Float" precision="single"/>
              <cartesianZ type="Float" precision="single"/>
              <cartesianInvalidState type="Integer" minimum="0" maximum="1"/>
              <colorRed type="Integer" minimum="0" maximum="255"/>
              <colorGreen type="Integer" minimum="0" maximum="255"/>
              <colorBlue type="Integer" minimum="0" maximum="255"/>
              <intensity type="Integer" minimum="0" maximum="255"/>
              <isColorInvalid type="Integer" minimum="0" maximum="1"/>
              <isIntensityInvalid type="Integer" minimum="0" maximum="1"/>
            </prototype>
            """;
        byte[] floats(float a, float b, float c) => [.. BitConverter.GetBytes(a), .. BitConverter.GetBytes(b), .. BitConverter.GetBytes(c)];
        var spec = new E57Spec
        {
            PrototypeXml = prototype,
            Points = [],
            RecordCount = 3,
            RawPackets = E57Spec.DataPacket([
                floats(1, 2, 3), floats(4, 5, 6), floats(7, 8, 9),
                [0, 1, 0],
                [10, 40, 70], [20, 50, 80], [30, 60, 90],
                [100, 110, 120],
                [0, 0, 1],
                [1, 0, 0],
            ]),
        };
        using var dir = new TempDirectory();
        using var doc = E57Document.Open(dir.Write("flags.e57", spec.Build()));

        var points = doc.Scans[0].ReadPoints(NoTransform).ToList();

        Assert.Equal(2, points.Count);
        Assert.Equal([0UL, 1UL], points.Select(p => p.Index));
        AssertNear(new PclVector3(1, 4, 7), points[0].Position);
        AssertNear(new PclVector3(3, 6, 9), points[1].Position);
        Assert.Equal(new PclColor(10, 20, 30), points[0].Color);
        Assert.Null(points[0].Intensity);
        Assert.Null(points[1].Color);
        Assert.Equal(120 / 255.0f, points[1].Intensity);
    }

    [Fact]
    public void E57_With_A_Spherical_Validity_Flag_Skips_Invalid_Points()
    {
        const string prototype = """
            <prototype type="Structure">
              <sphericalRange type="Float" precision="single"/>
              <sphericalAzimuth type="Float" precision="single"/>
              <sphericalElevation type="Float" precision="single"/>
              <sphericalInvalidState type="Integer" minimum="0" maximum="1"/>
            </prototype>
            """;
        byte[] rs = [.. BitConverter.GetBytes(2.0f), .. BitConverter.GetBytes(2.0f)];
        byte[] azs = [.. BitConverter.GetBytes(0.0f), .. BitConverter.GetBytes(0.0f)];
        byte[] els = [.. BitConverter.GetBytes(0.0f), .. BitConverter.GetBytes(0.0f)];
        var spec = new E57Spec { PrototypeXml = prototype, Points = [], RecordCount = 2, RawPackets = E57Spec.DataPacket([rs, azs, els, [1, 0]]) };
        using var dir = new TempDirectory();
        using var doc = E57Document.Open(dir.Write("spherical-flags.e57", spec.Build()));

        var point = Assert.Single(doc.Scans[0].ReadPoints(NoTransform));
        AssertNear(new PclVector3(2, 0, 0), point.Position);
    }

    [Fact]
    public void E57_With_Sixteen_Bit_Colour_Is_Decoded()
    {
        const string prototype = """
            <prototype type="Structure">
              <cartesianX type="Float" precision="single"/>
              <cartesianY type="Float" precision="single"/>
              <cartesianZ type="Float" precision="single"/>
              <colorRed type="Integer" minimum="0" maximum="65535"/>
              <colorGreen type="Integer" minimum="0" maximum="65535"/>
              <colorBlue type="Integer" minimum="0" maximum="65535"/>
            </prototype>
            """;
        byte[] floats(float a, float b) => [.. BitConverter.GetBytes(a), .. BitConverter.GetBytes(b)];
        byte[] words(ushort a, ushort b) => [.. BitConverter.GetBytes(a), .. BitConverter.GetBytes(b)];
        var spec = new E57Spec
        {
            PrototypeXml = prototype,
            Points = [],
            RecordCount = 2,
            RawPackets = E57Spec.DataPacket([floats(1, 2), floats(1, 2), floats(1, 2), words(0, 257), words(0, 257), words(0, 65535)]),
        };
        using var dir = new TempDirectory();
        using var doc = E57Document.Open(dir.Write("color16.e57", spec.Build()));

        var scan = Assert.Single(doc.Scans);
        Assert.True(scan.HasColors);

        var points = scan.ReadPoints(NoTransform).ToList();
        Assert.Equal(new PclColor(0, 0, 0), points[0].Color);
        Assert.Equal(new PclColor(1, 1, 255), points[1].Color);
    }

    [Fact]
    public void E57_With_Float_And_Scaled_Integer_Intensity_Is_Decoded()
    {
        const string floatPrototype = """
            <prototype type="Structure">
              <cartesianX type="Float" precision="single"/>
              <cartesianY type="Float" precision="single"/>
              <cartesianZ type="Float" precision="single"/>
              <intensity type="Float" precision="single"/>
            </prototype>
            """;
        byte[] floats(float a, float b) => [.. BitConverter.GetBytes(a), .. BitConverter.GetBytes(b)];
        var floatSpec = new E57Spec
        {
            PrototypeXml = floatPrototype,
            Points = [],
            RecordCount = 2,
            RawPackets = E57Spec.DataPacket([floats(1, 2), floats(1, 2), floats(1, 2), floats(0.5f, 1.0f)]),
        };

        const string scaledPrototype = """
            <prototype type="Structure">
              <cartesianX type="Float" precision="single"/>
              <cartesianY type="Float" precision="single"/>
              <cartesianZ type="Float" precision="single"/>
              <intensity type="ScaledInteger" minimum="0" maximum="1000" scale="0.001"/>
            </prototype>
            """;
        byte[] words(ushort a, ushort b) => [.. BitConverter.GetBytes(a), .. BitConverter.GetBytes(b)];
        var scaledSpec = new E57Spec
        {
            PrototypeXml = scaledPrototype,
            Points = [],
            RecordCount = 2,
            RawPackets = E57Spec.DataPacket([floats(1, 2), floats(1, 2), floats(1, 2), words(0, 500)]),
        };

        using var dir = new TempDirectory();
        using var floatDoc = E57Document.Open(dir.Write("float-intensity.e57", floatSpec.Build()));
        using var scaledDoc = E57Document.Open(dir.Write("scaled-intensity.e57", scaledSpec.Build()));

        Assert.True(floatDoc.Scans[0].HasIntensities);
        Assert.True(scaledDoc.Scans[0].HasIntensities);

        var floatPoints = floatDoc.Scans[0].ReadPoints(NoTransform).ToList();
        Assert.Equal(0.5f, floatPoints[0].Intensity);
        Assert.Equal(1.0f, floatPoints[1].Intensity);

        var scaledPoints = scaledDoc.Scans[0].ReadPoints(NoTransform).ToList();
        Assert.Equal(0.0f, scaledPoints[0].Intensity);
        Assert.Equal(0.5f, scaledPoints[1].Intensity);
    }

    [Fact]
    public void E57_With_Mixed_Colour_Encodings_Reports_No_Colour()
    {
        const string prototype = """
            <prototype type="Structure">
              <cartesianX type="Float" precision="single"/>
              <cartesianY type="Float" precision="single"/>
              <cartesianZ type="Float" precision="single"/>
              <colorRed type="Integer" minimum="0" maximum="255"/>
              <colorGreen type="Integer" minimum="0" maximum="65535"/>
              <colorBlue type="Integer" minimum="0" maximum="255"/>
            </prototype>
            """;
        byte[] floats(float a, float b) => [.. BitConverter.GetBytes(a), .. BitConverter.GetBytes(b)];
        var spec = new E57Spec
        {
            PrototypeXml = prototype,
            Points = [],
            RecordCount = 2,
            RawPackets = E57Spec.DataPacket([floats(1, 2), floats(1, 2), floats(1, 2), [0, 0], [0, 0], [0, 0]]),
        };
        using var dir = new TempDirectory();
        using var doc = E57Document.Open(dir.Write("mixed.e57", spec.Build()));

        // The colour encodings differ, so the scan is listed without colour and reads its positions.
        var scan = Assert.Single(doc.Scans);
        Assert.False(scan.HasColors);
        Assert.Equal(2, scan.ReadPoints(NoTransform).Count());
    }

    [Fact]
    public void E57_With_A_Validity_Stream_Of_Another_Shape_Returns_All_Points()
    {
        // A validity stream that does not hold one flag byte per record is not interpreted.
        const string prototype = """
            <prototype type="Structure">
              <cartesianX type="Float" precision="single"/>
              <cartesianY type="Float" precision="single"/>
              <cartesianZ type="Float" precision="single"/>
              <cartesianInvalidState type="Integer" minimum="0" maximum="1"/>
            </prototype>
            """;
        byte[] floats(float a, float b) => [.. BitConverter.GetBytes(a), .. BitConverter.GetBytes(b)];
        var spec = new E57Spec
        {
            PrototypeXml = prototype,
            Points = [],
            RecordCount = 2,
            RawPackets = E57Spec.DataPacket([floats(1, 2), floats(1, 2), floats(1, 2), [1]]),
        };
        using var dir = new TempDirectory();
        using var doc = E57Document.Open(dir.Write("flags.e57", spec.Build()));

        Assert.Equal(2, doc.Scans[0].ReadPoints(NoTransform).Count());
    }

    [Fact]
    public void E57_With_A_Multi_Byte_Validity_Range_Returns_All_Points()
    {
        // Only one-byte validity flags are honoured.
        const string prototype = """
            <prototype type="Structure">
              <cartesianX type="Float" precision="single"/>
              <cartesianY type="Float" precision="single"/>
              <cartesianZ type="Float" precision="single"/>
              <cartesianInvalidState type="Integer" minimum="0" maximum="1000"/>
            </prototype>
            """;
        byte[] floats(float a, float b) => [.. BitConverter.GetBytes(a), .. BitConverter.GetBytes(b)];
        byte[] words(ushort a, ushort b) => [.. BitConverter.GetBytes(a), .. BitConverter.GetBytes(b)];
        var spec = new E57Spec
        {
            PrototypeXml = prototype,
            Points = [],
            RecordCount = 2,
            RawPackets = E57Spec.DataPacket([floats(1, 2), floats(1, 2), floats(1, 2), words(1, 1)]),
        };
        using var dir = new TempDirectory();
        using var doc = E57Document.Open(dir.Write("flags.e57", spec.Build()));

        Assert.Equal(2, doc.Scans[0].ReadPoints(NoTransform).Count());
    }

    [Fact]
    public void E57_With_A_Partial_Colour_Record_Is_Rejected()
    {
        const string prototype = """
            <prototype type="Structure">
              <cartesianX type="Float" precision="single"/>
              <cartesianY type="Float" precision="single"/>
              <cartesianZ type="Float" precision="single"/>
              <colorRed type="Integer" minimum="0" maximum="65535"/>
              <colorGreen type="Integer" minimum="0" maximum="65535"/>
              <colorBlue type="Integer" minimum="0" maximum="65535"/>
            </prototype>
            """;
        byte[] floats(float a, float b) => [.. BitConverter.GetBytes(a), .. BitConverter.GetBytes(b)];
        var spec = new E57Spec
        {
            PrototypeXml = prototype,
            Points = [],
            RecordCount = 2,
            RawPackets = E57Spec.DataPacket([floats(1, 2), floats(1, 2), floats(1, 2), floats(1, 2)[..3], floats(1, 2), floats(1, 2)]),
        };
        using var dir = new TempDirectory();
        using var doc = E57Document.Open(dir.Write("partial.e57", spec.Build()));

        Assert.Throws<InvalidDataException>(() => { _ = doc.Scans[0].ReadPoints().ToList(); });
    }

    [Fact]
    public void E57_With_A_Named_Codec_Lists_But_Is_Not_Decoded()
    {
        var spec = new E57Spec { CodecsXml = "<codec><mysteryCodec/></codec>" };
        using var dir = new TempDirectory();
        using var doc = E57Document.Open(dir.Write("codec.e57", spec.Build()));

        Assert.Single(doc.Scans);
        var error = Assert.Throws<NotSupportedException>(() => doc.Scans[0].ReadPoints().ToList());
        Assert.Contains("mysteryCodec", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void E57_With_More_Prototype_Fields_Than_Packets_Can_Hold_Is_Not_Decoded()
    {
        var many = string.Concat(Enumerable.Repeat("<padding type=\"Integer\" minimum=\"0\" maximum=\"0\"/>", 70000));
        string prototype = $"<prototype type=\"Structure\"><cartesianX type=\"Float\" precision=\"single\"/><cartesianY type=\"Float\" precision=\"single\"/><cartesianZ type=\"Float\" precision=\"single\"/>{many}</prototype>";
        using var dir = new TempDirectory();
        using var doc = E57Document.Open(dir.Write("wide.e57", new E57Spec { PrototypeXml = prototype }.Build()));

        Assert.Single(doc.Scans);
        var error = Assert.Throws<NotSupportedException>(() => doc.Scans[0].ReadPoints().ToList());
        Assert.Contains("65535", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void E57_With_A_Prototype_Of_More_Than_A_Thousand_Fields_Is_Decoded()
    {
        // 1500 unknown fields ride along with the coordinates; the packets hold one stream per field.
        var many = string.Concat(Enumerable.Range(0, 1500).Select(i => $"<padding{i} type=\"Integer\" minimum=\"0\" maximum=\"0\"/>"));
        string prototype = $"<prototype type=\"Structure\"><cartesianX type=\"Float\" precision=\"single\"/><cartesianY type=\"Float\" precision=\"single\"/><cartesianZ type=\"Float\" precision=\"single\"/>{many}</prototype>";
        var streams = new List<byte[]>
        {
            BitConverter.GetBytes(1.0f), BitConverter.GetBytes(2.0f), BitConverter.GetBytes(3.0f),
        };
        streams.AddRange(Enumerable.Repeat(new byte[1], 1500));
        var spec = new E57Spec
        {
            PrototypeXml = prototype,
            Points = [],
            RecordCount = 1,
            RawPackets = E57Spec.DataPacket(streams),
        };
        using var dir = new TempDirectory();
        using var doc = E57Document.Open(dir.Write("wide.e57", spec.Build()));

        var point = Assert.Single(doc.Scans[0].ReadPoints(NoTransform));
        AssertNear(new PclVector3(1, 2, 3), point.Position);
    }

    // ---- Leica -----------------------------------------------------------------------------------------------

    [Fact]
    public void Parse_Synthetic_Lgsx_Container()
    {
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("site.lgsx", SyntheticFixtures.CreateSyntheticLgsx()));

        Assert.Equal(PclFileKind.LeicaLgsx, doc.Kind);
        Assert.Equal(["SETUP-3", "PointCloud"], doc.Entries.Select(e => e.Name));
        Assert.Equal("12345678-1234-1234-1234-123456789abc", doc.ProjectGuid);
        Assert.Equal("2024.1.0", doc.SoftwareVersion);
        Assert.NotEmpty(doc.Warnings);

        // One scan for the whole container; the setup entries are only reported.
        var scan = Assert.Single(doc.Scans);
        Assert.Equal("unified-cloud", scan.Id);
        Assert.Equal("site", scan.Name);
        Assert.Contains(doc.Warnings, w => w.Contains("setup entries", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Lgsx_Container_With_Several_Setups_Is_One_Scan_With_All_Points_And_A_Warning()
    {
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("site.lgsx", SyntheticFixtures.CreateSyntheticLgsx(setupCount: 3)));

        var scan = Assert.Single(doc.Scans);
        Assert.Equal(5, scan.PointCount);
        Assert.Equal(5, scan.ReadPoints().Count());
        Assert.Contains(doc.Warnings, w => w.Contains("3 setup entries", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Lgsx_Container_That_Assigns_Its_Points_To_Its_Setups_Has_One_Scan_Per_Setup()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticLgsx(setupCount: 3, assignPointsToSetups: true);
        using var doc = LgsxDocument.Open(dir.Write("site.lgsx", bytes));

        Assert.Equal(3, doc.Scans.Count);
        Assert.Equal(["SETUP-3", "SETUP-4", "SETUP-5"], doc.Scans.Select(s => s.Name));
        Assert.Equal(["3", "4", "5"], doc.Scans.Select(s => s.Id));
        Assert.Equal(["3", "4", "5"], doc.Setups.Select(s => s.Id));

        // The points of all setups together are the points of the container.
        Assert.Equal(5, doc.TotalPointCount);
        Assert.Equal(5, doc.ReadAllPoints().Count());
        Assert.Equal([2L, 2L, 1L], doc.Scans.Select(s => s.PointCount));

        // Every setup holds the points of its own block, in the frame of the container.
        var expected = new List<PclVector3>();
        int setup = 0;
        foreach (var group in new[] { 2, 2, 1 })
        {
            for (int i = 0; i < group; i++)
            {
                var written = SyntheticFixtures.DefaultLeicaPoints[expected.Count];
                expected.Add(new PclVector3(
                    SyntheticFixtures.LeicaOriginX + (written.X * SyntheticFixtures.LeicaScale),
                    SyntheticFixtures.LeicaOriginY + (written.Y * SyntheticFixtures.LeicaScale),
                    SyntheticFixtures.LeicaOriginZ + (written.Z * SyntheticFixtures.LeicaScale)));
            }

            setup++;
        }

        var all = doc.ReadAllPoints(NoTransform).Select(p => p.Position).ToList();
        for (int i = 0; i < expected.Count; i++) AssertNear(expected[i], all[i]);
    }

    [Fact]
    public void A_Lgsx_Setup_Reads_Its_Pose_And_Its_Bounds_And_Applies_The_Pose()
    {
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("site.lgsx",
            SyntheticFixtures.CreateSyntheticLgsx(setupCount: 2, assignPointsToSetups: true)));

        var first = doc.Scans[0];
        var second = doc.Scans[1];

        // Every setup record holds the translation and the bounds of its setup.
        Assert.Equal(new PclVector3(1, 2, 3), first.Transform.Translation);
        Assert.Equal(new PclVector3(2, 3, 4), second.Transform.Translation);
        Assert.Equal(new PclBounds(
            new PclVector3(SyntheticFixtures.LeicaOriginX, SyntheticFixtures.LeicaOriginY, SyntheticFixtures.LeicaOriginZ),
            new PclVector3(
                SyntheticFixtures.LeicaOriginX + (1023 * SyntheticFixtures.LeicaScale),
                SyntheticFixtures.LeicaOriginY + (1023 * SyntheticFixtures.LeicaScale),
                SyntheticFixtures.LeicaOriginZ + (1023 * SyntheticFixtures.LeicaScale))), first.Bounds);

        // The pose moves the points; without it they stay as they are stored.
        var moved = first.ReadPoints().ToList();
        var raw = first.ReadPoints(NoTransform).ToList();
        Assert.Equal(3, moved.Count);
        for (int i = 0; i < moved.Count; i++)
        {
            AssertNear(new PclVector3(raw[i].X + 1, raw[i].Y + 2, raw[i].Z + 3), moved[i].Position);
        }
    }

    [Fact]
    public void A_Lgsx_Setup_With_A_Damaged_Record_Keeps_An_Unknown_Pose()
    {
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("site.lgsx",
            SyntheticFixtures.CreateSyntheticLgsx(setupCount: 2, assignPointsToSetups: true, damagedSetupRecord: true)));

        // The record of the first setup holds a value that is not a number, so it is not read; the setups keep no
        // points of their own and the container stays one scan.
        var scan = Assert.Single(doc.Scans);
        Assert.Equal("unified-cloud", scan.Id);
        Assert.Equal(PclTransform.Identity, scan.Transform);
        Assert.True(scan.Bounds.IsEmpty);
        Assert.Contains(doc.Warnings, w => w.Contains("points are not assigned to them", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Lgsx_Setup_Without_Points_Gets_No_Scan()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticLgsx(
            points: [SyntheticFixtures.DefaultLeicaPoints[0], SyntheticFixtures.DefaultLeicaPoints[1]],
            setupCount: 4, assignPointsToSetups: true);
        using var doc = LgsxDocument.Open(dir.Write("site.lgsx", bytes));

        // Four setups are listed, but only the two with points become scans.
        Assert.Equal(4, doc.Setups.Count);
        Assert.Equal(2, doc.Scans.Count);
        Assert.Equal(["SETUP-3", "SETUP-4"], doc.Scans.Select(s => s.Name));
        Assert.Equal(2, doc.TotalPointCount);
    }

    [Fact]
    public void A_Lgsx_Container_Whose_Setups_Claim_The_Same_Points_Stays_One_Scan()
    {
        // Two setups that declare the same range of the point data cannot be told apart, so the points stay together.
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("site.lgsx",
            SyntheticFixtures.CreateSyntheticLgsx(setupCount: 2, assignPointsToSetups: true, overlappingSetupRanges: true)));

        var scan = Assert.Single(doc.Scans);
        Assert.Equal("unified-cloud", scan.Id);
        Assert.Equal(5, scan.PointCount);
        Assert.Contains(doc.Warnings, w => w.Contains("points are not assigned to them", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Lgsx_Container_Whose_Setup_Names_Share_An_Id_Stays_One_Scan()
    {
        // Two entry names ending in the same suffix share the id after the last dash. Assigning blocks by that id
        // would hand both setups the same blocks and return every point twice, so the points stay together.
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("site.lgsx",
            SyntheticFixtures.CreateSyntheticLgsx(setupCount: 2, assignPointsToSetups: true, collidingSetupNames: true)));

        Assert.Equal(["3", "3"], doc.Setups.Select(s => s.Id));
        var scan = Assert.Single(doc.Scans);
        Assert.Equal("unified-cloud", scan.Id);
        Assert.Equal(5, scan.PointCount);
        Assert.Equal(5, scan.ReadPoints().Count());
        Assert.Equal(5, doc.TotalPointCount);
        Assert.Contains(doc.Warnings, w => w.Contains("points are not assigned to them", StringComparison.Ordinal));
    }

    [Fact]
    public void The_Point_Count_Of_A_Lgsx_Container_Is_The_One_The_Container_States()
    {
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("site.lgsx", SyntheticFixtures.CreateSyntheticLgsx()));

        // The count of the container and the count of the decoded points are the same.
        var scan = Assert.Single(doc.Scans);
        Assert.Equal(5, scan.PointCount);
        Assert.Equal(scan.PointCount, scan.ReadPoints().LongCount());
    }

    [Fact]
    public void The_Point_Count_Of_The_Setups_Of_A_Lgsx_Container_Is_The_One_The_Container_States()
    {
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("site.lgsx",
            SyntheticFixtures.CreateSyntheticLgsx(setupCount: 3, assignPointsToSetups: true)));

        Assert.Equal([2L, 2L, 1L], doc.Scans.Select(s => s.PointCount));
        Assert.Equal([2L, 2L, 1L], doc.Scans.Select(s => s.ReadPoints().LongCount()));
    }

    [Fact]
    public void The_Point_Count_Of_A_Lgsx_Scan_Does_Not_Decode_The_Points()
    {
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("site.lgsx", SyntheticFixtures.CreateSyntheticLgsx(damagedPointData: true)));

        var scan = Assert.Single(doc.Scans);

        // The count comes from the container, so the damaged point data is not decoded to find it.
        Assert.Equal(5, scan.PointCount);
        Assert.Throws<InvalidDataException>(() => scan.ReadPoints().ToList());
    }

    [Fact]
    public void A_Lgsx_Container_Reports_The_Points_That_Were_Dropped()
    {
        // The second point has the code of the origin of the block, so it is dropped as padding.
        var points = new List<LeicaSamplePoint>
        {
            new(1, 2, 3),
            new(0, 0, 0),
            new(4, 5, 6),
            new(0, 0, 0),
        };

        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("site.lgsx", SyntheticFixtures.CreateSyntheticLgsx(points)));

        var scan = Assert.Single(doc.Scans);
        Assert.Equal(2, scan.ReadPoints().Count());
        Assert.Contains(doc.Warnings, w => w.Contains("2 points were dropped", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Lgsx_Container_Reports_The_Compressed_Streams_That_Were_Skipped()
    {
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("site.lgsx", SyntheticFixtures.CreateSyntheticLgsx(oversizedFrame: true)));

        var scan = Assert.Single(doc.Scans);

        // The frames before the oversized one are read, the rest of the block is lost with it.
        Assert.Equal(5, scan.ReadPoints().Count());
        Assert.Contains(doc.Warnings, w => w.Contains("1 compressed streams were skipped", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Lgsx_Container_Reports_Point_Data_That_Ends_At_A_Block_It_Does_Not_Know()
    {
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("site.lgsx", SyntheticFixtures.CreateSyntheticLgsx(unknownBlock: true)));

        var scan = Assert.Single(doc.Scans);
        Assert.Equal(5, scan.ReadPoints().Count());
        Assert.Contains(doc.Warnings, w => w.Contains("ends after 1 blocks", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Lgsx_Container_Whose_First_Chunk_Is_Not_A_Point_Block_Reports_It()
    {
        // The chain is empty because the very first chunk is unknown; the point data still ends at a block the
        // reader does not know, and the declared count is not what a read returns.
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("site.lgsx", SyntheticFixtures.CreateSyntheticLgsx(unknownFirstBlock: true)));

        var scan = Assert.Single(doc.Scans);
        Assert.Equal(5, scan.PointCount);
        Assert.Empty(scan.ReadPoints());
        Assert.Contains(doc.Warnings, w => w.Contains("ends after 0 blocks", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => scan.ReadPoints(new PclReadOptions { Strict = true }).ToList());
    }

    [Fact]
    public void A_Lgsx_Block_Whose_Frame_List_Is_Cut_Short_Is_Reported()
    {
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("site.lgsx", SyntheticFixtures.CreateSyntheticLgsx(truncatedFrameList: true)));

        var scan = Assert.Single(doc.Scans);
        Assert.Equal(5, scan.ReadPoints().Count());
        Assert.Contains(doc.Warnings, w => w.Contains("1 point blocks have a frame list that ends before the block does", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Lgsx_Block_With_Fewer_Than_Four_Planes_Is_Reported()
    {
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("site.lgsx", SyntheticFixtures.CreateSyntheticLgsx(fewPlanes: true)));

        var scan = Assert.Single(doc.Scans);
        Assert.Empty(scan.ReadPoints());
        Assert.Contains(doc.Warnings, w => w.Contains("1 point blocks were skipped because they hold fewer than four planes", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Lgsx_Parallel_Read_Reports_Unusable_Blocks_As_The_Sequential_Read_Does()
    {
        // Blocks that carry no positions are counted in both read paths; the parallel path must not drop them
        // from the incomplete-data report.
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("site.lgsx",
            SyntheticFixtures.CreateSyntheticLgsx(blockCount: 4, fewPlanes: true)));

        var scan = Assert.Single(doc.Scans);
        Assert.Empty(scan.ReadPoints(new PclReadOptions { MaxDegreeOfParallelism = 4 }));
        Assert.Contains(doc.Warnings, w => w.Contains("4 point blocks were skipped because they hold fewer than four planes", StringComparison.Ordinal));
    }

    [Fact]
    public void Interleaved_Reads_Of_A_Lgsx_Scan_Keep_Their_Own_Counts()
    {
        // Two enumerators advance side by side; the counters of one read must not reset or mix with the other's.
        var points = new List<LeicaSamplePoint> { new(1, 2, 3), new(0, 0, 0), new(4, 5, 6), new(0, 0, 0) };
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("site.lgsx", SyntheticFixtures.CreateSyntheticLgsx(points)));

        var scan = Assert.Single(doc.Scans);
        var pairs = scan.ReadPoints().Zip(scan.ReadPoints(), (a, b) => (a, b)).ToList();
        Assert.Equal(2, pairs.Count);
        Assert.Contains(doc.Warnings, w => w.Contains("2 points were dropped", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Lgsx_Container_Fails_In_Strict_Mode_When_Its_Point_Data_Is_Incomplete()
    {
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("site.lgsx", SyntheticFixtures.CreateSyntheticLgsx(unknownBlock: true)));

        var scan = Assert.Single(doc.Scans);
        Assert.Throws<InvalidDataException>(() => scan.ReadPoints(new PclReadOptions { Strict = true }).ToList());
    }

    [Fact]
    public void A_Lgsx_Container_With_Complete_Point_Data_Reports_Nothing()
    {
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("site.lgsx", SyntheticFixtures.CreateSyntheticLgsx()));

        var scan = Assert.Single(doc.Scans);
        Assert.Equal(5, scan.ReadPoints().Count());
        Assert.DoesNotContain(doc.Warnings, w => w.Contains("incomplete", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Lgsx_Block_Is_Not_Cut_At_65536_Points()
    {
        // Codes are never zero, because a point at the block origin is dropped as padding.
        var points = Enumerable.Range(0, 70_000).Select(i => new LeicaSamplePoint((i % 1000) + 1, ((i / 7) % 1000) + 1, ((i / 11) % 1000) + 1)).ToList();
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("big.lgsx", SyntheticFixtures.CreateSyntheticLgsx(points)));

        Assert.Equal(70_000, doc.Scans[0].ReadPoints().Count());
    }

    [Fact]
    public void Read_Synthetic_Lgsx_Points_From_The_Compressed_Block()
    {
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("site.lgsx", SyntheticFixtures.CreateSyntheticLgsx()));
        var scan = doc.Scans[0];

        var points = scan.ReadPoints().ToList();

        Assert.Equal(5, points.Count);
        Assert.Equal(5, scan.PointCount);
        for (int i = 0; i < points.Count; i++)
        {
            var written = SyntheticFixtures.DefaultLeicaPoints[i];
            AssertNear(new PclVector3(
                SyntheticFixtures.LeicaOriginX + (written.X * SyntheticFixtures.LeicaScale),
                SyntheticFixtures.LeicaOriginY + (written.Y * SyntheticFixtures.LeicaScale),
                SyntheticFixtures.LeicaOriginZ + (written.Z * SyntheticFixtures.LeicaScale)), points[i].Position);
        }

        // A block of four position planes carries no colour, intensity or normal; none is invented.
        Assert.False(scan.HasColors);
        Assert.False(scan.HasIntensities);
        Assert.False(scan.HasNormals);
        Assert.All(points, p =>
        {
            Assert.Null(p.Color);
            Assert.Null(p.Intensity);
            Assert.Null(p.Normal);
        });
    }

    [Fact]
    public void Read_Synthetic_Lgsx_Colour_And_Intensity_Planes()
    {
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("full.lgsx", SyntheticFixtures.CreateSyntheticLgsx(withColorAndIntensity: true)));
        var scan = doc.Scans[0];

        var points = scan.ReadPoints().ToList();

        Assert.True(scan.HasColors);
        Assert.True(scan.HasIntensities);
        Assert.False(scan.HasNormals);
        Assert.Equal(new PclColor(255, 0, 0), points[0].Color);
        Assert.Equal(new PclColor(0, 255, 0), points[1].Color);
        Assert.Equal(new PclColor(0, 0, 255), points[2].Color);
        Assert.Equal(new PclColor(255, 255, 255), points[3].Color);
        Assert.Equal(1.0f, points[0].Intensity);
        Assert.Equal(0x8000 / 65535.0f, points[1].Intensity);
        Assert.Equal(0.0f, points[3].Intensity);
    }

    [Fact]
    public void Lgsx_Honours_Limit_And_Step()
    {
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("site.lgsx", SyntheticFixtures.CreateSyntheticLgsx()));
        var scan = doc.Scans[0];

        Assert.Equal(2, scan.ReadPoints(new PclReadOptions { MaxPoints = 2 }).Count());
        Assert.Equal(3, scan.ReadPoints(new PclReadOptions { SubsampleStep = 2 }).Count());
        Assert.Empty(scan.ReadPoints(new PclReadOptions { MaxPoints = 0 }));
    }

    private static IReadOnlyList<LeicaSamplePoint> SixLeicaPoints =>
    [
        new(1, 2, 3),
        new(4, 5, 6),
        new(7, 8, 9),
        new(10, 11, 12),
        new(13, 14, 15),
        new(16, 17, 18),
    ];

    private static List<PclVector3> ExpectedLeicaPositions(IReadOnlyList<LeicaSamplePoint> written) =>
        [.. written.Select(p => new PclVector3(
            SyntheticFixtures.LeicaOriginX + (p.X * SyntheticFixtures.LeicaScale),
            SyntheticFixtures.LeicaOriginY + (p.Y * SyntheticFixtures.LeicaScale),
            SyntheticFixtures.LeicaOriginZ + (p.Z * SyntheticFixtures.LeicaScale)))];

    [Fact]
    public void Lgsx_Range_Reads_Only_The_Selected_Blocks_In_File_Order()
    {
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("multi.lgsx",
            SyntheticFixtures.CreateSyntheticLgsx(SixLeicaPoints, blockCount: 3)));
        var scan = Assert.Single(doc.Scans);
        var expected = ExpectedLeicaPositions(SixLeicaPoints);

        var middle = scan.ReadPoints(new PclReadOptions { Blocks = new BlockRange { StartBlock = 1, BlockCount = 1 } }).ToList();
        Assert.Equal(2, middle.Count);
        Assert.Equal([0UL, 1UL], middle.Select(p => p.Index));
        AssertNear(expected[2], middle[0].Position);
        AssertNear(expected[3], middle[1].Position);

        var tail = scan.ReadPoints(new PclReadOptions { Blocks = new BlockRange { StartBlock = 2 } }).ToList();
        Assert.Equal(2, tail.Count);
        AssertNear(expected[4], tail[0].Position);
        AssertNear(expected[5], tail[1].Position);
    }

    [Fact]
    public void Lgsx_Range_Past_The_Last_Block_Yields_No_Points()
    {
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("multi.lgsx",
            SyntheticFixtures.CreateSyntheticLgsx(SixLeicaPoints, blockCount: 3)));
        var scan = Assert.Single(doc.Scans);

        Assert.Empty(scan.ReadPoints(new PclReadOptions { Blocks = new BlockRange { StartBlock = 3 } }));
        Assert.Empty(scan.ReadPoints(new PclReadOptions { Blocks = new BlockRange { StartBlock = 1, BlockCount = 0 } }));
        Assert.Empty(scan.ReadPoints(new PclReadOptions { Blocks = new BlockRange { StartBlock = 10, BlockCount = 5 } }));
    }

    [Fact]
    public void Lgsx_Parallel_Read_Matches_The_Sequential_Read_In_Order()
    {
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("multi.lgsx",
            SyntheticFixtures.CreateSyntheticLgsx(SixLeicaPoints, blockCount: 3)));
        var scan = Assert.Single(doc.Scans);

        var sequential = scan.ReadPoints(NoTransform).ToList();
        var parallel = scan.ReadPoints(new PclReadOptions { ApplyTransform = false, MaxDegreeOfParallelism = 4 }).ToList();

        Assert.Equal(sequential.Count, parallel.Count);
        Assert.Equal(sequential.Select(p => p.Index), parallel.Select(p => p.Index));
        for (int i = 0; i < sequential.Count; i++)
            AssertNear(sequential[i].Position, parallel[i].Position);
        Assert.Equal([0UL, 1UL, 2UL, 3UL, 4UL, 5UL], parallel.Select(p => p.Index));
    }

    [Fact]
    public void Lgsx_Parallel_Read_With_A_Range_Matches_The_Sequential_Range()
    {
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("multi.lgsx",
            SyntheticFixtures.CreateSyntheticLgsx(SixLeicaPoints, blockCount: 3)));
        var scan = Assert.Single(doc.Scans);
        var range = new PclReadOptions { ApplyTransform = false, Blocks = new BlockRange { StartBlock = 1, BlockCount = 2 } };

        var sequential = scan.ReadPoints(range).ToList();
        var parallel = scan.ReadPoints(range with { MaxDegreeOfParallelism = 4 }).ToList();

        Assert.Equal(4, sequential.Count);
        Assert.Equal(sequential.Select(p => p.Position), parallel.Select(p => p.Position));
        Assert.Equal([0UL, 1UL, 2UL, 3UL], parallel.Select(p => p.Index));
    }

    [Fact]
    public void Lgsx_Parallel_Read_Caps_At_MaxPoints_With_Consecutive_Index()
    {
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("multi.lgsx",
            SyntheticFixtures.CreateSyntheticLgsx(SixLeicaPoints, blockCount: 3)));
        var scan = Assert.Single(doc.Scans);

        var capped = scan.ReadPoints(new PclReadOptions { ApplyTransform = false, MaxPoints = 3, MaxDegreeOfParallelism = 4 }).ToList();
        Assert.Equal(3, capped.Count);
        Assert.Equal([0UL, 1UL, 2UL], capped.Select(p => p.Index));

        var sequential = scan.ReadPoints(new PclReadOptions { ApplyTransform = false, MaxPoints = 3 }).ToList();
        for (int i = 0; i < 3; i++) AssertNear(sequential[i].Position, capped[i].Position);
    }

    [Fact]
    public void Lgsx_Damaged_Block_Fails_And_A_Range_Around_It_Reads()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticLgsx(SixLeicaPoints, blockCount: 3, damagedBlock: 1);
        using var doc = LgsxDocument.Open(dir.Write("damaged.lgsx", bytes));
        var scan = Assert.Single(doc.Scans);

        Assert.Throws<InvalidDataException>(() => scan.ReadPoints(NoTransform).ToList());
        var parallelError = Assert.Throws<InvalidDataException>(() =>
            scan.ReadPoints(new PclReadOptions { ApplyTransform = false, MaxDegreeOfParallelism = 4 }).ToList());
        Assert.IsNotType<AggregateException>(parallelError);

        // The blocks around the damaged one still read.
        var first = scan.ReadPoints(new PclReadOptions { ApplyTransform = false, Blocks = new BlockRange { StartBlock = 0, BlockCount = 1 } }).ToList();
        Assert.Equal(2, first.Count);
        var last = scan.ReadPoints(new PclReadOptions { ApplyTransform = false, Blocks = new BlockRange { StartBlock = 2, BlockCount = 1 } }).ToList();
        Assert.Equal(2, last.Count);
    }

    [Fact]
    public void Lgsx_Truncated_File_Fails_In_Both_Read_Paths()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticLgsx(SixLeicaPoints, blockCount: 2);

        // Truncate the second block in place: its declared length runs past the file.
        byte[] magic = [(byte)'A', (byte)'P', (byte)'R', (byte)'N'];
        int first = bytes.AsSpan().IndexOf(magic);
        Assert.True(first >= 0);
        int second = bytes.AsSpan()[(first + 4)..].IndexOf(magic);
        Assert.True(second >= 0);
        int lengthAt = first + 4 + second + 4;
        BitConverter.GetBytes(uint.MaxValue).CopyTo(bytes, lengthAt);

        using var doc = LgsxDocument.Open(dir.Write("cut.lgsx", bytes));
        var scan = Assert.Single(doc.Scans);

        Assert.Throws<InvalidDataException>(() => scan.ReadPoints(NoTransform).ToList());
        Assert.Throws<InvalidDataException>(() =>
            scan.ReadPoints(new PclReadOptions { ApplyTransform = false, MaxDegreeOfParallelism = 4 }).ToList());
    }

    [Fact]
    public void Lgsx_Truncated_Read_By_MaxPoints_Reports_No_Incomplete_Data()
    {
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("site.lgsx",
            SyntheticFixtures.CreateSyntheticLgsx(SixLeicaPoints, blockCount: 1, unknownBlock: true)));
        var scan = Assert.Single(doc.Scans);

        Assert.Equal(5, scan.ReadPoints(new PclReadOptions { MaxPoints = 5 }).Count());
        Assert.DoesNotContain(doc.Warnings, w => w.Contains("incomplete", StringComparison.Ordinal));

        var parallel = scan.ReadPoints(new PclReadOptions { MaxPoints = 5, MaxDegreeOfParallelism = 4 }).ToList();
        Assert.Equal(5, parallel.Count);
        Assert.DoesNotContain(doc.Warnings, w => w.Contains("incomplete", StringComparison.Ordinal));
    }

    [Fact]
    public void Lgsx_Strict_Mode_Fails_For_A_Selected_Damaged_Block()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticLgsx(SixLeicaPoints, blockCount: 2, damagedBlock: 1);
        using var doc = LgsxDocument.Open(dir.Write("damaged.lgsx", bytes));
        var scan = Assert.Single(doc.Scans);

        Assert.Throws<InvalidDataException>(() => scan.ReadPoints(new PclReadOptions { Strict = true }).ToList());
        Assert.Throws<InvalidDataException>(() => scan.ReadPoints(new PclReadOptions
        {
            Blocks = new BlockRange { StartBlock = 1, BlockCount = 1 },
            Strict = true,
            MaxDegreeOfParallelism = 4,
        }).ToList());
    }

    [Fact]
    public void Other_Readers_Ignore_Blocks_And_Parallel_Options()
    {
        using var dir = new TempDirectory();
        var spec = new E57Spec { Points = [new E57SamplePoint(1, 2, 3, 0, 0, 0, 0), new E57SamplePoint(4, 5, 6, 0, 0, 0, 0)] };
        using var doc = E57Document.Open(dir.Write("small.e57", spec.Build()));

        var plain = doc.Scans[0].ReadPoints(NoTransform).ToList();
        var ranged = doc.Scans[0].ReadPoints(new PclReadOptions
        {
            ApplyTransform = false,
            Blocks = new BlockRange { StartBlock = 1, BlockCount = 1 },
            MaxDegreeOfParallelism = 4,
        }).ToList();

        Assert.Equal(plain.Count, ranged.Count);
        for (int i = 0; i < plain.Count; i++) AssertNear(plain[i].Position, ranged[i].Position);
    }

    [Fact]
    public void Lgsx_Without_A_Point_Data_Entry_Warns_And_Yields_No_Points()
    {
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("empty.lgsx", SyntheticFixtures.CreateSyntheticLgsx(withPointData: false)));

        Assert.Contains(doc.Warnings, w => w.Contains("No point data entry"));
        Assert.Empty(doc.Scans[0].ReadPoints());
    }

    [Fact]
    public void Lgsx_Without_Metadata_In_The_Tail_Leaves_It_Empty()
    {
        // A tail without a GUID or a version must stay empty: nothing is invented.
        byte[] bytes = SyntheticFixtures.CreateSyntheticLgsx();
        string text = System.Text.Encoding.Latin1.GetString(bytes);
        int at = text.IndexOf("build 2024", StringComparison.Ordinal);
        Assert.True(at > 0);
        Array.Fill(bytes, (byte)'x', at, "build 2024.1.0 {12345678-1234-1234-1234-123456789abc}".Length);
        using var dir = new TempDirectory();
        using var doc = LgsxDocument.Open(dir.Write("plain.lgsx", bytes));

        Assert.Equal("", doc.ProjectGuid);
        Assert.Equal("", doc.SoftwareVersion);
        Assert.False(doc.Metadata.ContainsKey("ProjectGuid"));
    }

    // ---- PinPoint --------------------------------------------------------------------------------------------

    [Fact]
    public void Parse_Synthetic_PinPoint_Manifest()
    {
        using var dir = new TempDirectory();
        using var doc = PinPointDocument.Open(dir.Write("project.smf", SyntheticFixtures.CreateSyntheticPinPointSmf()));

        Assert.Equal(PclFileKind.PinPoint, doc.Kind);
        var scan = Assert.Single(doc.Scans);
        Assert.Equal("Pos1", scan.Name);
        Assert.Equal("h1", scan.Id);
        Assert.Equal(42000, scan.PointCount);
        Assert.Equal(new PclVector3(5.0, 10.0, 15.0), scan.Transform.Translation);

        // The manifest names a point file that does not exist.
        Assert.Contains(doc.Warnings, w => w.Contains("not found"));
        Assert.Empty(scan.ReadPoints());
    }

    [Fact]
    public void Read_Synthetic_PinPoint_Channels()
    {
        using var dir = new TempDirectory();
        byte[] bin = SyntheticFixtures.CreateSyntheticPinPointBin(
            [(1, 2, 3), (4, 5, 6)],
            rgba: [10, 20, 30, 255, 40, 50, 60, 255],
            intensities: [0, 255],
            normals: [0, 0, 1, 1, 0, 0]);
        using var doc = PinPointDocument.Open(SyntheticFixtures.WritePinPointProject(dir, bin));
        var scan = (PinPointScan)Assert.Single(doc.Scans);

        Assert.NotNull(scan.BinFilePath);
        Assert.Equal(2, scan.PointCount);
        Assert.True(scan.HasColors);
        Assert.True(scan.HasIntensities);
        Assert.True(scan.HasNormals);

        var raw = scan.ReadPoints(NoTransform).ToList();
        Assert.Equal(2, raw.Count);
        Assert.Equal(new PclVector3(1, 2, 3), raw[0].Position);
        Assert.Equal(new PclColor(10, 20, 30), raw[0].Color);
        Assert.Equal(0.0f, raw[0].Intensity);
        Assert.Equal(1.0f, raw[1].Intensity);
        Assert.Equal(new PclVector3(0, 0, 1), raw[0].Normal);
        Assert.Equal(new PclVector3(1, 0, 0), raw[1].Normal);

        // The sensor pose of the manifest is a translation of (5, 10, 15).
        Assert.Equal(new PclVector3(6, 12, 18), scan.ReadPoints().First().Position);
    }

    [Fact]
    public void PinPoint_Without_Optional_Channels_Reports_No_Colour_Intensity_Or_Normal()
    {
        using var dir = new TempDirectory();
        byte[] bin = SyntheticFixtures.CreateSyntheticPinPointBin([(1, 2, 3), (4, 5, 6), (7, 8, 9)]);
        using var doc = PinPointDocument.Open(SyntheticFixtures.WritePinPointProject(dir, bin));
        var scan = doc.Scans[0];

        Assert.False(scan.HasColors);
        Assert.False(scan.HasIntensities);
        Assert.False(scan.HasNormals);

        var points = scan.ReadPoints().ToList();
        Assert.Equal(3, points.Count);
        Assert.All(points, p =>
        {
            Assert.Null(p.Color);
            Assert.Null(p.Intensity);
            Assert.Null(p.Normal);
        });
        Assert.Equal(2, scan.ReadPoints(new PclReadOptions { SubsampleStep = 2 }).Count());
        Assert.Single(scan.ReadPoints(new PclReadOptions { MaxPoints = 1 }));
    }

    [Fact]
    public void A_PinPoint_Data_File_Without_A_Recognised_Channel_Layout_Is_Listed_With_A_Warning_And_Not_Read()
    {
        using var dir = new TempDirectory();
        using var doc = PinPointDocument.Open(SyntheticFixtures.WritePinPointProject(dir, new byte[240]));

        var scan = Assert.Single(doc.Scans);
        Assert.Contains(doc.Warnings, w => w.Contains("no recognised channel layout", StringComparison.Ordinal));
        Assert.Throws<NotSupportedException>(() => scan.ReadPoints().ToList());
    }

    [Fact]
    public void PinPoint_Points_Beyond_A_Plausible_Range_Are_Not_Reported()
    {
        using var dir = new TempDirectory();
        byte[] bin = SyntheticFixtures.CreateSyntheticPinPointBin([(1, 2, 3), (1e9f, 0, 0), (float.NaN, 0, 0)]);
        using var doc = PinPointDocument.Open(SyntheticFixtures.WritePinPointProject(dir, bin));

        var point = Assert.Single(doc.Scans[0].ReadPoints(NoTransform));
        Assert.Equal(new PclVector3(1, 2, 3), point.Position);
    }

    [Fact]
    public void PinPoint_Magnitude_Limit_Can_Be_Raised_Or_Switched_Off()
    {
        using var dir = new TempDirectory();
        byte[] bin = SyntheticFixtures.CreateSyntheticPinPointBin([(1, 2, 3), (1e9f, 0, 0), (float.NaN, 0, 0)]);
        using var doc = PinPointDocument.Open(SyntheticFixtures.WritePinPointProject(dir, bin));

        // A raised limit keeps the large coordinate; a missing limit keeps it too, but never the NaN.
        var raised = doc.Scans[0].ReadPoints(new PclReadOptions { ApplyTransform = false, MaxCoordinateMagnitude = 2e9 }).ToList();
        Assert.Equal(2, raised.Count);
        Assert.Equal(new PclVector3(1e9f, 0, 0), raised[1].Position);

        var unlimited = doc.Scans[0].ReadPoints(new PclReadOptions { ApplyTransform = false, MaxCoordinateMagnitude = null }).ToList();
        Assert.Equal(2, unlimited.Count);
    }

    [Fact]
    public void PinPoint_Dropped_Points_Are_Reported_With_A_Count()
    {
        using var dir = new TempDirectory();
        byte[] bin = SyntheticFixtures.CreateSyntheticPinPointBin([(1, 2, 3), (1e9f, 0, 0), (float.NaN, 0, 0)]);
        using var doc = PinPointDocument.Open(SyntheticFixtures.WritePinPointProject(dir, bin));

        Assert.Single(doc.Scans[0].ReadPoints(NoTransform));
        Assert.Contains(doc.Warnings, w => w.Contains("dropped 2 points", StringComparison.Ordinal));
    }

    [Fact]
    public void PinPoint_Dropped_Points_Fail_In_Strict_Mode()
    {
        using var dir = new TempDirectory();
        byte[] bin = SyntheticFixtures.CreateSyntheticPinPointBin([(1, 2, 3), (1e9f, 0, 0)]);
        using var doc = PinPointDocument.Open(SyntheticFixtures.WritePinPointProject(dir, bin));

        Assert.Throws<InvalidDataException>(() => doc.Scans[0].ReadPoints(new PclReadOptions { Strict = true }).ToList());
    }

    [Fact]
    public void PinPoint_A_Read_Stopped_By_MaxPoints_Reports_Nothing()
    {
        // The cap stops the read after a part of the point data; the counts of that part say nothing about the
        // rest, so neither the warning nor the strict failure fires.
        using var dir = new TempDirectory();
        byte[] bin = SyntheticFixtures.CreateSyntheticPinPointBin([(float.NaN, 0, 0), (1, 2, 3), (1e9f, 0, 0)]);
        using var doc = PinPointDocument.Open(SyntheticFixtures.WritePinPointProject(dir, bin));

        var scan = doc.Scans[0];
        var capped = new PclReadOptions { ApplyTransform = false, MaxPoints = 1 };
        var point = Assert.Single(scan.ReadPoints(capped));
        Assert.Equal(new PclVector3(1, 2, 3), point.Position);
        Assert.DoesNotContain(doc.Warnings, w => w.Contains("dropped", StringComparison.Ordinal));

        Assert.Single(scan.ReadPoints(capped with { Strict = true }));
        Assert.DoesNotContain(doc.Warnings, w => w.Contains("dropped", StringComparison.Ordinal));

        // A full read of the same data still reports the drops.
        Assert.Throws<InvalidDataException>(() => scan.ReadPoints(new PclReadOptions { ApplyTransform = false, Strict = true }).ToList());
    }

    [Fact]
    public void A_Data_Object_Location_Takes_Its_Point_Count_From_The_Data_File()
    {
        using var dir = new TempDirectory();
        const string manifest = """{ "DataObjects": [ { "HashId": "h1", "Locations": [ { "HashId": "loc1" } ] } ] }""";
        dir.Write("h1.bin", SyntheticFixtures.CreateSyntheticPinPointBin([(1, 2, 3), (4, 5, 6)]));
        using var doc = PinPointDocument.Open(dir.WriteText("project.smf", manifest));

        var scan = Assert.Single(doc.Scans);
        Assert.Equal("loc1", scan.Id);
        Assert.Equal(2, scan.PointCount);
        Assert.Equal(2, doc.TotalPointCount);
        Assert.Equal(2, scan.ReadPoints(NoTransform).Count());
    }

    [Fact]
    public void Locations_That_Share_A_Data_File_Each_Report_Its_Full_Count()
    {
        // The shares of one file are not recorded, so every location reports the whole file.
        using var dir = new TempDirectory();
        const string manifest = """{ "DataObjects": [ { "HashId": "h1", "Locations": [ { "HashId": "a" }, { "HashId": "b" } ] } ] }""";
        dir.Write("h1.bin", SyntheticFixtures.CreateSyntheticPinPointBin([(1, 2, 3), (4, 5, 6)]));
        using var doc = PinPointDocument.Open(dir.WriteText("project.smf", manifest));

        Assert.Equal([2L, 2L], doc.Scans.Select(s => s.PointCount));
        Assert.Equal(4, doc.TotalPointCount);
    }

    [Fact]
    public void A_Data_Object_Location_Without_A_Data_File_Reports_Zero_Points()
    {
        using var dir = new TempDirectory();
        const string manifest = """{ "DataObjects": [ { "HashId": "h1", "Locations": [ { "HashId": "loc1" } ] } ] }""";
        using var doc = PinPointDocument.Open(dir.WriteText("project.smf", manifest));

        var scan = Assert.Single(doc.Scans);
        Assert.Equal(0, scan.PointCount);
        Assert.Contains(doc.Warnings, w => w.Contains("not found", StringComparison.Ordinal));
        Assert.Empty(scan.ReadPoints());
    }

    [Fact]
    public void A_Data_Object_Location_With_A_Damaged_Data_File_Reports_Zero_Points_And_Fails_When_Read()
    {
        using var dir = new TempDirectory();
        const string manifest = """{ "DataObjects": [ { "HashId": "h1", "Locations": [ { "HashId": "loc1" } ] } ] }""";
        dir.Write("h1.bin", new byte[240]);
        using var doc = PinPointDocument.Open(dir.WriteText("project.smf", manifest));

        var scan = Assert.Single(doc.Scans);
        Assert.Equal(0, scan.PointCount);
        Assert.Contains(doc.Warnings, w => w.Contains("no recognised channel layout", StringComparison.Ordinal));
        Assert.Throws<NotSupportedException>(() => scan.ReadPoints().ToList());
    }

    // ---- EdgeWise --------------------------------------------------------------------------------------------

    private static readonly C3Cluster[] Clusters =
    [
        new(1f, 2f, 3f, [(0, 0, 0), (1000, -1000, 32767)]),
        new(10f, 20f, 30f, [(-32768, 0, 0)]),
    ];

    private const double ClusterUnit = 0.128 / 32768.0;

    [Fact]
    public void Read_Synthetic_EdgeWise_Cluster_File()
    {
        using var dir = new TempDirectory();
        using var doc = EdgeWiseDocument.Open(dir.Write("cluster.c3ptx", SyntheticFixtures.CreateSyntheticC3ptx(Clusters)));

        Assert.Equal(PclFileKind.EdgeWise, doc.Kind);
        var scan = Assert.Single(doc.Scans);
        Assert.Equal(3, scan.PointCount);
        Assert.False(scan.HasColors);

        var points = scan.ReadPoints().ToList();
        Assert.Equal(3, points.Count);
        AssertNear(new PclVector3(1, 2, 3), points[0].Position);
        AssertNear(new PclVector3(1 + (1000 * ClusterUnit), 2 - (1000 * ClusterUnit), 3 + (32767 * ClusterUnit)), points[1].Position);
        AssertNear(new PclVector3(10 - (32768 * ClusterUnit), 20, 30), points[2].Position);
        Assert.Equal(2, scan.ReadPoints(new PclReadOptions { MaxPoints = 2 }).Count());
    }

    [Fact]
    public void An_EdgeWise_Scan_Reads_With_The_Stated_Coordinate_Scale()
    {
        using var dir = new TempDirectory();
        using var doc = EdgeWiseDocument.Open(dir.Write("cluster.c3ptx", SyntheticFixtures.CreateSyntheticC3ptx(Clusters)));

        var scan = Assert.Single(doc.Scans);
        var points = scan.ReadPoints(new PclReadOptions { EdgeWiseCoordinateScale = 0.001 }).ToList();

        Assert.Equal(3, points.Count);
        AssertNear(new PclVector3(1, 2, 3), points[0].Position);
        AssertNear(new PclVector3(2, 1, 3 + (32767 * 0.001)), points[1].Position);
    }

    [Fact]
    public void Read_Synthetic_EdgeWise_Database_And_Skip_Missing_Cluster_Files()
    {
        using var dir = new TempDirectory();
        dir.Write(Path.Combine("clusters", "a.c3ptx"), SyntheticFixtures.CreateSyntheticC3ptx(Clusters));
        string db = dir.WriteText("db.c3db", SyntheticFixtures.CreateSyntheticC3db((3, "clusters/a.c3ptx"), (5, "clusters/missing.c3ptx"), (0, "clusters/zero.c3ptx")));

        using var doc = EdgeWiseDocument.Open(db);

        var scan = Assert.Single(doc.Scans);
        Assert.Equal("a", scan.Name);
        Assert.Equal("1", scan.Id);
        Assert.Equal(3, doc.TotalPointCount);
        Assert.Equal("10, 10, 10", doc.Metadata["BoundsMax"]);
        Assert.Single(doc.Warnings);
        Assert.Equal(3, doc.ReadAllPoints().Count());
    }

    // ---- FARO ------------------------------------------------------------------------------------------------

    private static void AssertFaroPoints(IReadOnlyList<PclPoint> points)
    {
        Assert.Equal(SyntheticFixtures.FaroRows * SyntheticFixtures.FaroCols, points.Count);

        // Every return is 2 m away from the scanner, whatever the grid angles are.
        Assert.All(points, p => Assert.Equal(2.0, Math.Sqrt((p.X * p.X) + (p.Y * p.Y) + (p.Z * p.Z)), Precision));
    }

    [Fact]
    public void Read_Synthetic_Faro_Scan_Folder()
    {
        using var dir = new TempDirectory();
        using var doc = FaroFlsDocument.Open(SyntheticFixtures.WriteFaroFolder(dir));

        Assert.Equal(PclFileKind.FaroFls, doc.Kind);
        Assert.Equal(SyntheticFixtures.FaroRows, doc.NumRows);
        Assert.Equal(SyntheticFixtures.FaroCols, doc.NumCols);
        Assert.Equal("TestModel", doc.ScannerModel);
        Assert.Equal("SN-0001", doc.ScannerSerialNumber);

        // The record layout of a folder scan is derived from the size of its data stream, and the document says so.
        Assert.Contains("derived", Assert.Single(doc.Warnings), StringComparison.Ordinal);

        var scan = Assert.Single(doc.Scans);
        Assert.Equal(SyntheticFixtures.FaroRows * SyntheticFixtures.FaroCols, scan.PointCount);

        var points = scan.ReadPoints().ToList();
        AssertFaroPoints(points);
        Assert.All(points, p => Assert.Equal(0.5f, p.Intensity));
        Assert.Equal(10, scan.ReadPoints(new PclReadOptions { MaxPoints = 10 }).Count());
        Assert.Equal(16, scan.ReadPoints(new PclReadOptions { SubsampleStep = 2 }).Count());
    }

    [Fact]
    public void A_Faro_Scan_Folder_With_A_Larger_Sample_Reads_It_With_The_Layout_Of_Its_Files()
    {
        using var dir = new TempDirectory();
        using var doc = FaroFlsDocument.Open(SyntheticFixtures.WriteFaroFolder(dir, recordBytes: 16));

        var scan = Assert.IsType<FaroScan>(Assert.Single(doc.Scans));
        Assert.Equal(16, scan.RecordSize);
        Assert.Equal("16", doc.Metadata["RecordSize"]);

        var points = scan.ReadPoints().ToList();
        Assert.Equal(32, points.Count);
        AssertFaroPoints(points);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void A_Faro_Scan_Folder_With_Samples_Too_Small_For_Distance_And_Intensity_Keeps_The_Assumed_Layout(int recordBytes)
    {
        // A derived record of two or three bytes cannot hold both the distance and the intensity; reading the
        // intensity of the last record of a full buffer would run past it. The assumed layout is used instead.
        using var dir = new TempDirectory();
        using var doc = FaroFlsDocument.Open(SyntheticFixtures.WriteFaroFolder(dir, recordBytes: recordBytes));

        var scan = Assert.IsType<FaroScan>(Assert.Single(doc.Scans));
        Assert.Equal(FaroScan.DefaultRecordSize, scan.RecordSize);
        Assert.Contains("assumed", Assert.Single(doc.Warnings), StringComparison.Ordinal);

        // The count and the read stay usable; they do not crash on the small records.
        Assert.True(scan.PointCount >= 0);
        Assert.Equal(scan.PointCount, scan.ReadPoints().Count());
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(16, true)]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void The_Derived_Faro_Record_Size_Needs_Room_For_Distance_And_Intensity(int size, bool determined)
    {
        long cells = 8192;
        int recordSize = FaroScan.DetermineRecordSize(cells * size, cells, out bool wasDetermined);
        Assert.Equal(determined, wasDetermined);
        Assert.Equal(determined ? size : FaroScan.DefaultRecordSize, recordSize);
    }

    [Fact]
    public void A_Faro_Scan_Folder_With_Sixteen_Bit_Intensity_Uses_That_Scale()
    {
        using var dir = new TempDirectory();
        using var doc = FaroFlsDocument.Open(SyntheticFixtures.WriteFaroFolder(dir, intensity: 60000));

        var points = doc.Scans[0].ReadPoints().ToList();
        Assert.Equal(32, points.Count);
        Assert.All(points, p => Assert.Equal(60000 / 65535.0f, p.Intensity!.Value, 1e-6));
    }

    [Fact]
    public void A_Faro_Scan_Folder_Whose_Sizes_Do_Not_Fit_The_Grid_Keeps_The_Assumed_Layout()
    {
        // Three bytes after the samples: the size of the stream is not a multiple of the size of the grid.
        using var dir = new TempDirectory();
        using var doc = FaroFlsDocument.Open(SyntheticFixtures.WriteFaroFolder(dir, extraBytes: 3));

        Assert.Contains("assumed", Assert.Single(doc.Warnings), StringComparison.Ordinal);
        Assert.Equal(32, doc.Scans[0].ReadPoints().Count());
    }

    [Fact]
    public void A_Faro_Scan_Folder_With_A_Truncated_Stream_Yields_The_Samples_It_Holds()
    {
        // Only ten of the 32 cells of the grid are stored.
        using var dir = new TempDirectory();
        using var doc = FaroFlsDocument.Open(SyntheticFixtures.WriteFaroFolder(dir, samples: 10));

        var scan = Assert.IsType<FaroScan>(Assert.Single(doc.Scans));
        Assert.Equal(10, scan.GridSize);
        Assert.Equal(10, scan.PointCount);
        Assert.Equal(10, scan.ReadPoints().Count());
    }

    [Fact]
    public void Read_Synthetic_Faro_Container_File()
    {
        using var dir = new TempDirectory();
        string path = dir.Write("scan.fls", SyntheticFixtures.CreateSyntheticFlsContainer());
        using var doc = PclFile.Open(path);

        Assert.IsType<FaroFlsDocument>(doc);
        var scan = Assert.Single(doc.Scans);
        Assert.False(scan.HasColors);
        Assert.True(scan.HasIntensities);

        var points = scan.ReadPoints().ToList();
        AssertFaroPoints(points);
        Assert.Equal(0.0f, points[0].Intensity);
        Assert.Equal(8 / 255.0f, points[1].Intensity);
    }

    [Fact]
    public void Faro_Container_Without_A_Grey_Stream_Has_No_Intensity()
    {
        using var dir = new TempDirectory();
        using var doc = PclFile.Open(dir.Write("scan.fls", SyntheticFixtures.CreateSyntheticFlsContainer(withGrey: false)));

        Assert.False(doc.Scans[0].HasIntensities);
        Assert.All(doc.Scans[0].ReadPoints(), p => Assert.Null(p.Intensity));
    }

    [Fact]
    public void A_Faro_Scan_Reports_The_Number_Of_Points_That_A_Full_Read_Returns()
    {
        // Six of the 32 cells of the grid hold no return.
        using var dir = new TempDirectory();
        using var doc = PclFile.Open(dir.Write("scan.fls", SyntheticFixtures.CreateSyntheticFlsContainer(invalidCells: 6)));

        var scan = Assert.IsType<FaroScan>(Assert.Single(doc.Scans));
        Assert.Equal(32, scan.GridSize);
        Assert.Equal(26, scan.PointCount);
        Assert.Equal(scan.PointCount, scan.ReadPoints().LongCount());
    }

    [Fact]
    public void A_Faro_Scan_Folder_Reports_The_Number_Of_Points_That_A_Full_Read_Returns()
    {
        using var dir = new TempDirectory();
        using var doc = FaroFlsDocument.Open(SyntheticFixtures.WriteFaroFolder(dir, invalidCells: 6));

        var scan = Assert.IsType<FaroScan>(Assert.Single(doc.Scans));
        Assert.Equal(32, scan.GridSize);
        Assert.Equal(26, scan.PointCount);
        Assert.Equal(scan.PointCount, scan.ReadPoints().LongCount());
    }

    [Fact]
    public void Faro_Without_Stored_Model_And_Serial_Reports_Them_As_Unknown_With_A_Warning()
    {
        string parameters = """
            NumRows = 4
            NumCols = 8
            RowStartAngle = -1.0
            RowEndAngle = 1.0
            ColEndAngle = 6.28
            """;
        using var dir = new TempDirectory();
        using var doc = FaroFlsDocument.Open(SyntheticFixtures.WriteFaroFolder(dir, parameters));

        Assert.Equal("unknown", doc.ScannerModel);
        Assert.Equal("unknown", doc.ScannerSerialNumber);
        Assert.Equal(2, doc.Warnings.Count(w => w.Contains("is not stored in the file", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("NumRows = 0")]
    [InlineData("NumRows = -3")]
    [InlineData("NumCols = 0")]
    [InlineData("RowEndAngle = -1.0")]
    [InlineData("ColEndAngle = 0")]
    [InlineData("NumRows = 100000\nNumCols = 100000")]
    public void Faro_With_An_Invalid_Grid_Is_Rejected_At_Open(string override_)
    {
        // Later keys win in the text only when they come first, so the override replaces the matching default line.
        var lines = SyntheticFixtures.FaroParams().Split('\n').Where(l => l.Trim().Length > 0).ToList();
        foreach (string line in override_.Split('\n'))
        {
            string key = line.Split('=')[0].Trim();
            lines.RemoveAll(l => l.TrimStart().StartsWith(key + " ", StringComparison.Ordinal));
            lines.Add(line);
        }

        using var dir = new TempDirectory();
        string folder = SyntheticFixtures.WriteFaroFolder(dir, string.Join('\n', lines));

        Assert.ThrowsAny<Exception>(() => FaroFlsDocument.Open(folder));
        Assert.True(IsFormatError(Record.Exception(() => FaroFlsDocument.Open(folder))), "a bad grid must be reported as a malformed or unsupported file");
    }

    [Theory]
    [InlineData("NumRows")]
    [InlineData("NumCols")]
    [InlineData("RowStartAngle")]
    [InlineData("RowEndAngle")]
    [InlineData("ColEndAngle")]
    public void Faro_With_A_Missing_Grid_Parameter_Is_Rejected_At_Open(string key)
    {
        var lines = SyntheticFixtures.FaroParams().Split('\n').Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith(key + " ", StringComparison.Ordinal));
        using var dir = new TempDirectory();
        string folder = SyntheticFixtures.WriteFaroFolder(dir, string.Join('\n', lines));

        Assert.True(IsFormatError(Record.Exception(() => FaroFlsDocument.Open(folder))));
    }

    internal static bool IsFormatError(Exception? exception) => exception is InvalidDataException or NotSupportedException;

    // ---- LAS ----------------------------------------------------------------------------------------------------

    private static readonly IReadOnlyList<LasSamplePoint> LasPoints =
    [
        new(1, 2, 3, 0.25f, new PclColor(10, 20, 30), 5),
        new(-4.5, 5.25, 6.125, 0.5f, null, 2),
        new(7, 8, 9, 1.0f, new PclColor(255, 255, 255), 0),
    ];

    [Fact]
    public void PclFile_Opens_Las_With_Count_Channels_Bounds_And_Metadata()
    {
        using var dir = new TempDirectory();
        using var doc = PclFile.Open(dir.Write("sample.las", SyntheticFixtures.CreateSyntheticLas(LasPoints)));

        Assert.Equal(PclFileKind.Las, doc.Kind);
        var scan = Assert.Single(doc.Scans);
        Assert.Equal("0", scan.Id);
        Assert.Equal("sample", scan.Name);
        Assert.Equal(3, scan.PointCount);
        Assert.Equal(3, doc.TotalPointCount);
        Assert.True(scan.HasColors);
        Assert.True(scan.HasIntensities);
        Assert.False(scan.HasNormals);
        Assert.Equal(new PclBounds(new PclVector3(-4.5, 2, 3), new PclVector3(7, 8, 9)), scan.Bounds);
        Assert.Equal(new PclBounds(new PclVector3(-4.5, 2, 3), new PclVector3(7, 8, 9)), doc.OverallBounds);
        Assert.Equal("1.2", ((LasDocument)doc).Version);
        Assert.Equal(2, ((LasDocument)doc).PointFormat);
        Assert.Equal("ASPRS LAS", doc.Metadata["Format"]);
        Assert.Equal("1.2", doc.Metadata["Version"]);
        Assert.Null(doc.CoordinateSystem);
        Assert.Empty(doc.Warnings);
    }

    [Fact]
    public void Las_Round_Trip_Positions_Intensity_Colour_And_Classification()
    {
        using var dir = new TempDirectory();
        using var doc = LasDocument.Open(dir.Write("sample.las", SyntheticFixtures.CreateSyntheticLas(LasPoints)));

        var points = doc.Scans[0].ReadPoints(NoTransform).ToList();

        Assert.Equal(3, points.Count);
        Assert.Equal([0UL, 1UL, 2UL], points.Select(p => p.Index));
        AssertNear(new PclVector3(1, 2, 3), points[0].Position);
        AssertNear(new PclVector3(-4.5, 5.25, 6.125), points[1].Position);
        AssertNear(new PclVector3(7, 8, 9), points[2].Position);
        Assert.Equal(0.25, (double)points[0].Intensity!.Value, 4);
        Assert.Equal(0.5, (double)points[1].Intensity!.Value, 4);
        Assert.Equal(1.0, (double)points[2].Intensity!.Value, 4);
        Assert.Equal(new PclColor(10, 20, 30), points[0].Color);
        Assert.Equal(new PclColor(255, 255, 255), points[1].Color);
        Assert.Equal(new PclColor(255, 255, 255), points[2].Color);
        Assert.Equal((byte)5, points[0].Classification);
        Assert.Equal((byte)2, points[1].Classification);
        Assert.All(points, p => Assert.Null(p.Normal));
    }

    [Fact]
    public void Las14_Reads_Extended_Counts_Scale_Offset_And_Epsg()
    {
        var points = new[]
        {
            new LasSamplePoint(1, 2, 3, 0.25f, new PclColor(10, 20, 30), 5),
            new LasSamplePoint(-4.5, 5.25, 6.12, 0.5f, null, 2),
        };
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticLas(points, versionMinor: 4, pointFormat: 7,
            scale: 0.01, offset: (10, 20, 30),
            vlrs: [SyntheticFixtures.LasEpsgVlr(3857)],
            legacyCountOverride: 0, extendedCountOverride: (ulong)points.Length);
        using var doc = LasDocument.Open(dir.Write("sample.las", bytes));

        // The legacy count is zero, so the extended count decides.
        var scan = Assert.Single(doc.Scans);
        Assert.Equal(2, scan.PointCount);
        Assert.Equal("EPSG:3857", doc.CoordinateSystem);

        var read = scan.ReadPoints(NoTransform).ToList();
        Assert.Equal(2, read.Count);
        AssertNear(new PclVector3(1, 2, 3), read[0].Position);
        AssertNear(new PclVector3(-4.5, 5.25, 6.12), read[1].Position);
        Assert.Equal(new PclColor(10, 20, 30), read[0].Color);
    }

    [Theory]
    [InlineData(0, 2, false)]
    [InlineData(1, 2, false)]
    [InlineData(2, 2, true)]
    [InlineData(3, 2, true)]
    [InlineData(4, 2, false)]
    [InlineData(5, 2, true)]
    [InlineData(6, 4, false)]
    [InlineData(7, 4, true)]
    [InlineData(8, 4, true)]
    [InlineData(9, 4, false)]
    [InlineData(10, 4, true)]
    public void Las_Point_Formats_Zero_To_Ten_Decode_Positions(int pointFormat, int versionMinor, bool colored)
    {
        var points = new[] { new LasSamplePoint(1, 2, 3, 0.5f, new PclColor(10, 20, 30), 7) };
        using var dir = new TempDirectory();
        using var doc = LasDocument.Open(dir.Write("sample.las",
            SyntheticFixtures.CreateSyntheticLas(points, versionMinor: versionMinor, pointFormat: pointFormat)));

        var scan = Assert.Single(doc.Scans);
        Assert.Equal(colored, scan.HasColors);
        Assert.True(scan.HasIntensities);
        Assert.False(scan.HasNormals);

        var point = Assert.Single(scan.ReadPoints(NoTransform));
        AssertNear(new PclVector3(1, 2, 3), point.Position);
        Assert.Equal(0.5, (double)point.Intensity!.Value, 4);
        Assert.Equal((byte)7, point.Classification);
        Assert.Equal(colored ? new PclColor(10, 20, 30) : null, point.Color);
    }

    [Fact]
    public void Las_Honours_Limit_And_Step_And_Ignores_Blocks_And_Parallelism()
    {
        var points = Enumerable.Range(0, 8).Select(i => new LasSamplePoint(i, 0, 0)).ToList();
        using var dir = new TempDirectory();
        using var doc = LasDocument.Open(dir.Write("sample.las", SyntheticFixtures.CreateSyntheticLas(points)));
        var scan = doc.Scans[0];

        Assert.Equal(3, scan.ReadPoints(new PclReadOptions { MaxPoints = 3 }).Count());
        Assert.Equal(4, scan.ReadPoints(new PclReadOptions { SubsampleStep = 2 }).Count());
        Assert.Empty(scan.ReadPoints(new PclReadOptions { MaxPoints = 0 }));

        // The point stream has no independent blocks: the range and the parallelism are ignored.
        var ranged = scan.ReadPoints(new PclReadOptions
        {
            Blocks = new BlockRange { StartBlock = 1, BlockCount = 1 },
            MaxDegreeOfParallelism = 4,
        }).ToList();
        Assert.Equal(8, ranged.Count);
        Assert.Equal([0UL, 1UL, 2UL, 3UL, 4UL, 5UL, 6UL, 7UL], ranged.Select(p => p.Index));
    }

    [Fact]
    public void Las_Of_Exporter_Output_Reads_Back()
    {
        var source = new[]
        {
            new PclPoint(new PclVector3(1, 2, 3), new PclColor(10, 20, 30), 0.25f, null, 5),
            new PclPoint(new PclVector3(-4.5, 5.25, 6.125), null, 0.5f),
        };
        using var exported = FakeDocument.Of(source);
        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            PclLasExporter.Export(exported, ms);
            bytes = ms.ToArray();
        }

        using var dir = new TempDirectory();
        using var doc = PclFile.Open(dir.Write("exported.las", bytes));

        Assert.Equal(PclFileKind.Las, doc.Kind);
        var points = doc.Scans[0].ReadPoints(NoTransform).ToList();
        Assert.Equal(2, points.Count);
        AssertNear(new PclVector3(1, 2, 3), points[0].Position);
        AssertNear(new PclVector3(-4.5, 5.25, 6.125), points[1].Position);
        Assert.Equal(new PclColor(10, 20, 30), points[0].Color);
        Assert.Equal(new PclColor(255, 255, 255), points[1].Color);
        Assert.Equal(0.25, (double)points[0].Intensity!.Value, 4);
        Assert.Equal((byte)5, points[0].Classification);
    }

    [Fact]
    public void Las_Coordinate_System_Comes_From_Wkt_When_No_Geotiff_Names_One()
    {
        const string wkt = "PROJCS[\"WGS 84 / Test\",GEOGCS[\"WGS 84\"]]";
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticLas(LasPoints,
            vlrs: [SyntheticFixtures.LasWktVlr(wkt), ("LASF_Something", 42, new byte[] { 1, 2, 3 })]);
        using var doc = LasDocument.Open(dir.Write("sample.las", bytes));

        Assert.Equal(wkt, doc.CoordinateSystem);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    public void Las_Versions_One_Zero_To_One_Three_Are_Read(int pointFormat, int versionMinor)
    {
        var points = new[] { new LasSamplePoint(1, 2, 3, 0.5f, null, 7) };
        using var dir = new TempDirectory();
        using var doc = LasDocument.Open(dir.Write("sample.las",
            SyntheticFixtures.CreateSyntheticLas(points, versionMinor: versionMinor, pointFormat: pointFormat)));

        Assert.Equal($"1.{versionMinor}", doc.Version);
        var point = Assert.Single(doc.Scans[0].ReadPoints(NoTransform));
        AssertNear(new PclVector3(1, 2, 3), point.Position);
    }

    [Fact]
    public void Las_With_The_Waveform_Bit_Set_Reads_With_Its_Base_Format()
    {
        // Bit 6 of the format byte flags waveform data packets; the format is the low six bits.
        var points = new[] { new LasSamplePoint(1, 2, 3, 0.5f, null, 7) };
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticLas(points, versionMinor: 4, pointFormat: 4);
        bytes[104] |= 0x40;
        using var doc = LasDocument.Open(dir.Write("sample.las", bytes));

        var scan = Assert.Single(doc.Scans);
        Assert.Equal(4, doc.PointFormat);
        AssertNear(new PclVector3(1, 2, 3), Assert.Single(scan.ReadPoints(NoTransform)).Position);
    }

    [Fact]
    public void Las_With_A_Laszip_Record_Is_Rejected_As_Compressed()
    {
        // A LAZ renamed to .las keeps the LAS signature and header; the laszip record marks it compressed.
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticLas(LasPoints,
            vlrs: [("laszip encoded", 22205, new byte[] { 1, 2, 3, 4 })]);

        Assert.Throws<NotSupportedException>(() => LasDocument.Open(dir.Write("sample.las", bytes)));
        Assert.Throws<NotSupportedException>(() => PclFile.Open(dir.Write("renamed.las", bytes)));
    }

    [Theory]
    [InlineData("LASF_Spec", 2112)]
    [InlineData("LASF_Projection", 2111)]
    public void Las_Wkt_In_The_Wrong_Record_Identity_Is_Not_A_Crs(string userId, int recordId)
    {
        const string wkt = "PROJCS[\"WGS 84 / Test\",GEOGCS[\"WGS 84\"]]";
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticLas(LasPoints,
            vlrs: [(userId, (ushort)recordId, System.Text.Encoding.UTF8.GetBytes(wkt + "\0"))]);
        using var doc = LasDocument.Open(dir.Write("sample.las", bytes));

        Assert.Null(doc.CoordinateSystem);
    }

    [Fact]
    public void Las_User_Defined_Epsg_Sentinel_Is_Not_A_Crs()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticLas(LasPoints,
            vlrs: [SyntheticFixtures.LasEpsgVlr(32767)]);
        using var doc = LasDocument.Open(dir.Write("sample.las", bytes));

        Assert.Null(doc.CoordinateSystem);
    }

    [Fact]
    public void Las_With_A_Huge_Declared_Count_Fails_Cleanly_When_Read()
    {
        // The count is not checked against the file at open; a read of the missing records fails with a truncation
        // error rather than allocating for the declared count.
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticLas(LasPoints, legacyCountOverride: uint.MaxValue);
        using var doc = LasDocument.Open(dir.Write("sample.las", bytes));

        Assert.Equal(uint.MaxValue, doc.Scans[0].PointCount);
        Assert.Throws<InvalidDataException>(() => { _ = doc.Scans[0].ReadPoints().ToList(); });
    }

    [Fact]
    public void Las_Without_A_Recognised_Crs_Leaves_It_Unknown()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticLas(LasPoints,
            vlrs: [("LASF_Something", 42, new byte[] { 1, 2, 3 })]);
        using var doc = LasDocument.Open(dir.Write("sample.las", bytes));

        Assert.Null(doc.CoordinateSystem);
        Assert.Equal(LasPoints.Count, doc.Scans[0].ReadPoints().Count());
    }

    [Fact]
    public void Las_Wrong_Signature_Is_Rejected()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticLas(LasPoints);
        bytes[0] = (byte)'X';

        Assert.Throws<InvalidDataException>(() => LasDocument.Open(dir.Write("sample.las", bytes)));
        Assert.Equal(PclFileKind.Unknown, PclFile.DetectKind(dir.Write("other.las", bytes)));
        Assert.Throws<NotSupportedException>(() => PclFile.Open(dir.PathOf("other.las")));
    }

    [Fact]
    public void Las_Unsupported_Version_Is_Rejected()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticLas(LasPoints, versionMinor: 2);
        bytes[25] = 5;

        Assert.Throws<NotSupportedException>(() => LasDocument.Open(dir.Write("future.las", bytes)));

        byte[] major = SyntheticFixtures.CreateSyntheticLas(LasPoints);
        major[24] = 2;
        Assert.Throws<NotSupportedException>(() => LasDocument.Open(dir.Write("major.las", major)));
    }

    [Theory]
    [InlineData(11, 2)]
    [InlineData(6, 2)]
    public void Las_Unsupported_Point_Format_Is_Rejected(int pointFormat, int versionMinor)
    {
        using var dir = new TempDirectory();
        byte[] bytes = pointFormat > 10
            ? SyntheticFixtures.CreateSyntheticLas([], versionMinor: versionMinor, pointFormat: pointFormat)
            : SyntheticFixtures.CreateSyntheticLas(LasPoints, versionMinor: versionMinor, pointFormat: pointFormat);

        Assert.Throws<NotSupportedException>(() => LasDocument.Open(dir.Write("sample.las", bytes)));
    }

    [Fact]
    public void Las_Truncated_Points_Are_Rejected_When_Read()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticLas(LasPoints);
        byte[] cut = bytes[..(bytes.Length - 10)];
        using var doc = LasDocument.Open(dir.Write("cut.las", cut));

        Assert.Equal(3, doc.Scans[0].PointCount);
        Assert.Throws<InvalidDataException>(() => { _ = doc.Scans[0].ReadPoints().ToList(); });
    }

    [Fact]
    public void Las_Record_Length_Smaller_Than_The_Format_Needs_Is_Rejected()
    {
        using var dir = new TempDirectory();
        int @base = SyntheticFixtures.LasBaseRecordSize(2);
        byte[] bytes = SyntheticFixtures.CreateSyntheticLas(LasPoints, pointFormat: 2, recordLengthOverride: @base - 1);

        Assert.Throws<InvalidDataException>(() => LasDocument.Open(dir.Write("sample.las", bytes)));
    }

    [Fact]
    public void Las_Oversized_Vlr_Is_Rejected()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticLas(LasPoints,
            vlrs: [("LASF_Projection", 34735, new byte[8])]);
        // The data length of the first VLR lives at header size + 20.
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(227 + 20, 4), uint.MaxValue);

        Assert.Throws<InvalidDataException>(() => LasDocument.Open(dir.Write("sample.las", bytes)));
    }

    [Fact]
    public void Las_Vlr_Overrunning_Point_Data_Is_Rejected()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticLas(LasPoints,
            vlrs: [("LASF_Projection", 34735, new byte[8])]);
        // The point data is declared to start right after the header, inside the VLR.
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(96, 4), 227);

        Assert.Throws<InvalidDataException>(() => LasDocument.Open(dir.Write("sample.las", bytes)));
    }

    [Fact]
    public void Las_Too_Many_Vlrs_Are_Rejected()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticLas(LasPoints);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(100, 4), 5000);

        Assert.Throws<InvalidDataException>(() => LasDocument.Open(dir.Write("sample.las", bytes)));
    }

    [Fact]
    public void Las_Extra_Bytes_Per_Record_Are_Skipped()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticLas(LasPoints, pointFormat: 2, extraBytesPerRecord: 4);
        using var doc = LasDocument.Open(dir.Write("sample.las", bytes));

        var points = doc.Scans[0].ReadPoints(NoTransform).ToList();
        Assert.Equal(3, points.Count);
        AssertNear(new PclVector3(1, 2, 3), points[0].Position);
        Assert.Equal(new PclColor(10, 20, 30), points[0].Color);
    }

    // ---- PCD ----------------------------------------------------------------------------------------------------

    [Fact]
    public void PclFile_Opens_Pcd_With_Count_Channels_Viewpoint_And_Metadata()
    {
        using var dir = new TempDirectory();
        using var doc = PclFile.Open(dir.Write("sample.pcd", SyntheticFixtures.CreateSyntheticPcd()));

        Assert.Equal(PclFileKind.Pcd, doc.Kind);
        var scan = Assert.Single(doc.Scans);
        Assert.Equal("0", scan.Id);
        Assert.Equal("sample", scan.Name);
        Assert.Equal(3, scan.PointCount);
        Assert.Equal(3, doc.TotalPointCount);
        Assert.True(scan.HasColors);
        Assert.True(scan.HasIntensities);
        Assert.True(scan.HasNormals);
        Assert.True(scan.Bounds.IsEmpty);
        Assert.Equal("0.7", ((PcdDocument)doc).Version);
        Assert.Equal("0 0 0 1 0 0 0", ((PcdDocument)doc).Viewpoint);
        Assert.Equal("Point Cloud Library PCD", doc.Metadata["Format"]);
        Assert.Null(doc.CoordinateSystem);
        Assert.Empty(doc.Warnings);
    }

    [Theory]
    [InlineData("ascii")]
    [InlineData("binary")]
    [InlineData("binary_compressed")]
    public void Pcd_Round_Trip_Positions_Intensity_Colour_And_Normals(string data)
    {
        using var dir = new TempDirectory();
        using var doc = PcdDocument.Open(dir.Write("sample.pcd", SyntheticFixtures.CreateSyntheticPcd(data: data)));

        var points = doc.Scans[0].ReadPoints(NoTransform).ToList();

        Assert.Equal(3, points.Count);
        Assert.Equal([0UL, 1UL, 2UL], points.Select(p => p.Index));
        AssertNear(new PclVector3(1, 2, 3), points[0].Position);
        AssertNear(new PclVector3(-4.5, 5.25, 6.125), points[1].Position);
        AssertNear(new PclVector3(7, 8, 9), points[2].Position);
        Assert.Equal(0.25f, points[0].Intensity);
        Assert.Equal(0.5f, points[1].Intensity);
        Assert.Equal(0f, points[2].Intensity);
        Assert.Equal(new PclColor(10, 20, 30), points[0].Color);
        Assert.Equal(new PclColor(255, 255, 255), points[1].Color);
        Assert.Equal(new PclColor(255, 255, 255), points[2].Color);
        Assert.Equal(new PclVector3(0, 0, 1), points[0].Normal);
        // Stored zeros read back as-is: PCD marks nothing as missing.
        Assert.Equal(new PclVector3(0, 0, 0), points[1].Normal);
        Assert.Equal(new PclVector3(0, 0, 0), points[2].Normal);
        Assert.All(points, p => Assert.Null(p.Classification));
    }

    [Fact]
    public void Pcd_Organised_Cloud_Streams_Row_After_Row()
    {
        var points = new[]
        {
            new PcdSamplePoint(1, 0, 0), new PcdSamplePoint(2, 0, 0),
            new PcdSamplePoint(3, 0, 0), new PcdSamplePoint(4, 0, 0),
        };
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticPcd(points,
            fields: [("x", 4, 'F', 1), ("y", 4, 'F', 1), ("z", 4, 'F', 1)],
            heightOverride: 2, widthOverride: 2);
        using var doc = PcdDocument.Open(dir.Write("grid.pcd", bytes));

        var scan = Assert.Single(doc.Scans);
        Assert.Equal(4, scan.PointCount);
        Assert.False(scan.HasColors);
        Assert.False(scan.HasIntensities);

        var read = scan.ReadPoints(NoTransform).ToList();
        Assert.Equal([1.0, 2.0, 3.0, 4.0], read.Select(p => p.X));
        Assert.All(read, p => Assert.Null(p.Intensity));
    }

    [Fact]
    public void Pcd_Points_Without_Finite_Coordinates_Are_Not_Returned()
    {
        var points = new[]
        {
            new PcdSamplePoint(1, 2, 3),
            new PcdSamplePoint(double.NaN, 0, 0),
            new PcdSamplePoint(4, 5, 6),
        };
        foreach (string data in new[] { "ascii", "binary", "binary_compressed" })
        {
            using var dir = new TempDirectory();
            using var doc = PcdDocument.Open(dir.Write("sample.pcd",
                SyntheticFixtures.CreateSyntheticPcd(points, data: data)));

            var read = doc.Scans[0].ReadPoints(NoTransform).ToList();
            Assert.Equal(2, read.Count);
            Assert.Equal([0UL, 1UL], read.Select(p => p.Index));
        }
    }

    [Fact]
    public void Pcd_Honours_Limit_And_Step_And_Ignores_Blocks_And_Parallelism()
    {
        var points = Enumerable.Range(0, 8).Select(i => new PcdSamplePoint(i, 0, 0)).ToList();
        using var dir = new TempDirectory();
        using var doc = PcdDocument.Open(dir.Write("sample.pcd", SyntheticFixtures.CreateSyntheticPcd(points)));
        var scan = doc.Scans[0];

        Assert.Equal(3, scan.ReadPoints(new PclReadOptions { MaxPoints = 3 }).Count());
        Assert.Equal(4, scan.ReadPoints(new PclReadOptions { SubsampleStep = 2 }).Count());
        Assert.Empty(scan.ReadPoints(new PclReadOptions { MaxPoints = 0 }));

        var ranged = scan.ReadPoints(new PclReadOptions
        {
            Blocks = new BlockRange { StartBlock = 1, BlockCount = 1 },
            MaxDegreeOfParallelism = 4,
        }).ToList();
        Assert.Equal(8, ranged.Count);
    }

    [Fact]
    public void Pcd_Without_X_Y_Or_Z_Is_Not_Read()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticPcd(
            fields: [("y", 4, 'F', 1), ("z", 4, 'F', 1), ("rgb", 4, 'F', 1)]);

        Assert.Throws<NotSupportedException>(() => PcdDocument.Open(dir.Write("sample.pcd", bytes)));
    }

    [Fact]
    public void Pcd_Unusable_Colour_Leaves_Points_Without_Colour()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticPcd(
            fields: [("x", 4, 'F', 1), ("y", 4, 'F', 1), ("z", 4, 'F', 1), ("rgb", 1, 'U', 1)]);
        using var doc = PcdDocument.Open(dir.Write("sample.pcd", bytes));

        var scan = Assert.Single(doc.Scans);
        Assert.False(scan.HasColors);
        Assert.Equal(3, scan.ReadPoints(NoTransform).Count());
        Assert.All(scan.ReadPoints(NoTransform), p => Assert.Null(p.Color));
    }

    [Fact]
    public void Pcd_Unknown_Data_Type_Is_Rejected()
    {
        using var dir = new TempDirectory();
        var header = new[]
        {
            "VERSION 0.7", "FIELDS x y z", "SIZE 4 4 4", "TYPE F F F", "COUNT 1 1 1",
            "WIDTH 1", "HEIGHT 1", "VIEWPOINT 0 0 0 1 0 0 0", "POINTS 1", "DATA base64",
        };

        Assert.Throws<InvalidDataException>(() =>
            PcdDocument.Open(dir.Write("sample.pcd", SyntheticFixtures.CreatePcdFile(header, [0]))));
    }

    [Fact]
    public void Pcd_Header_Out_Of_Order_Is_Rejected()
    {
        using var dir = new TempDirectory();
        var header = new[]
        {
            "VERSION 0.7", "SIZE 4 4 4", "FIELDS x y z", "TYPE F F F", "COUNT 1 1 1",
            "WIDTH 1", "HEIGHT 1", "VIEWPOINT 0 0 0 1 0 0 0", "POINTS 1", "DATA ascii",
        };

        Assert.Throws<InvalidDataException>(() =>
            PcdDocument.Open(dir.Write("sample.pcd", SyntheticFixtures.CreatePcdFile(header, "1 2 3"u8.ToArray()))));
    }

    [Fact]
    public void Pcd_Grid_That_Does_Not_Hold_The_Point_Count_Is_Rejected()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticPcd(widthOverride: 2, heightOverride: 2);

        Assert.Throws<InvalidDataException>(() => PcdDocument.Open(dir.Write("sample.pcd", bytes)));
    }

    [Fact]
    public void Pcd_Truncated_Ascii_Point_Data_Is_Rejected_When_Read()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticPcd(data: "ascii");
        byte[] cut = bytes[..(bytes.Length - 5)];
        using var doc = PcdDocument.Open(dir.Write("cut.pcd", cut));

        Assert.Equal(3, doc.Scans[0].PointCount);
        Assert.Throws<InvalidDataException>(() => { _ = doc.Scans[0].ReadPoints().ToList(); });
    }

    [Theory]
    [InlineData("binary")]
    [InlineData("binary_compressed")]
    public void Pcd_Truncated_Binary_Point_Data_Is_Rejected_At_Open(string data)
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticPcd(data: data);
        byte[] cut = bytes[..(bytes.Length - 5)];

        Assert.Throws<InvalidDataException>(() => PcdDocument.Open(dir.Write("cut.pcd", cut)));
    }

    [Fact]
    public void Pcd_Corrupt_Lzf_Is_Rejected_When_Read()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticPcd(data: "binary_compressed");

        // The header ends at the first DATA line; the first compressed byte follows the two sizes.
        int dataAt = System.Text.Encoding.ASCII.GetString(bytes).IndexOf("DATA binary_compressed", StringComparison.Ordinal);
        int bodyAt = System.Text.Encoding.ASCII.GetString(bytes).IndexOf('\n', dataAt) + 1;
        bytes[bodyAt + 8] = 0xFF; // a match reaching before the start of the output
        using var doc = PcdDocument.Open(dir.Write("sample.pcd", bytes));

        Assert.Throws<InvalidDataException>(() => { _ = doc.Scans[0].ReadPoints().ToList(); });
    }

    [Fact]
    public void Pcd_Compressed_Size_Mismatch_Is_Rejected()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticPcd(data: "binary_compressed");

        // The header ends at the first DATA line; the sizes start 8 bytes after it.
        int dataAt = System.Text.Encoding.ASCII.GetString(bytes).IndexOf("DATA binary_compressed", StringComparison.Ordinal);
        int bodyAt = System.Text.Encoding.ASCII.GetString(bytes).IndexOf('\n', dataAt) + 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bodyAt + 4, 4), 1);

        Assert.Throws<InvalidDataException>(() => PcdDocument.Open(dir.Write("sample.pcd", bytes)));
    }

    [Fact]
    public void Pcd_Huge_Point_Count_With_No_Data_Is_Rejected_At_Open()
    {
        // POINTS * record size wraps past zero unchecked; the checked product rejects the file instead of
        // opening it and crashing mid-read.
        using var dir = new TempDirectory();
        var header = new[]
        {
            "# .PCD v.7", "VERSION 0.7", "FIELDS x y z", "SIZE 4 4 4", "TYPE F F F", "COUNT 1 1 1",
            "WIDTH 4611686018427387904", "HEIGHT 1", "VIEWPOINT 0 0 0 1 0 0 0",
            "POINTS 4611686018427387904", "DATA binary",
        };
        byte[] bytes = SyntheticFixtures.CreatePcdFile(header, []);

        Assert.Throws<InvalidDataException>(() => PcdDocument.Open(dir.Write("sample.pcd", bytes)));
    }

    [Fact]
    public void Pcd_Intensity_Of_Uninterpretable_Shape_Stays_Unread()
    {
        // A one-byte float intensity has no representation; the points come without intensity rather than
        // crashing on the short span.
        using var dir = new TempDirectory();
        var header = new[]
        {
            "# .PCD v.7", "VERSION 0.7", "FIELDS x y z intensity", "SIZE 4 4 4 1", "TYPE F F F F",
            "COUNT 1 1 1 1", "WIDTH 1", "HEIGHT 1", "VIEWPOINT 0 0 0 1 0 0 0", "POINTS 1", "DATA binary",
        };
        byte[] body = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(0, 4), 1f);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(4, 4), 2f);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(8, 4), 3f);
        body[12] = 200;
        using var doc = PcdDocument.Open(dir.Write("sample.pcd", SyntheticFixtures.CreatePcdFile(header, body)));

        var scan = Assert.Single(doc.Scans);
        Assert.False(scan.HasIntensities);
        var point = Assert.Single(scan.ReadPoints(NoTransform));
        AssertNear(new PclVector3(1, 2, 3), point.Position);
        Assert.Null(point.Intensity);
    }

    [Fact]
    public void Pcd_Repeated_Padding_Field_Names_Are_Accepted()
    {
        // "_" is the padding placeholder and may repeat; the format allows repeated padding names.
        using var dir = new TempDirectory();
        var header = new[]
        {
            "# .PCD v.7", "VERSION 0.7", "FIELDS x y z _ rgb", "SIZE 4 4 4 4 4", "TYPE F F F F U",
            "COUNT 1 1 1 1 1", "WIDTH 1", "HEIGHT 1", "VIEWPOINT 0 0 0 1 0 0 0", "POINTS 1", "DATA binary",
        };
        byte[] body = new byte[20];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(0, 4), 1f);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(4, 4), 2f);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(8, 4), 3f);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16, 4), (10u << 16) | (20u << 8) | 30u);
        using var doc = PcdDocument.Open(dir.Write("sample.pcd", SyntheticFixtures.CreatePcdFile(header, body)));

        var scan = Assert.Single(doc.Scans);
        Assert.True(scan.HasColors);
        var point = Assert.Single(scan.ReadPoints(NoTransform));
        AssertNear(new PclVector3(1, 2, 3), point.Position);
        Assert.Equal(new PclColor(10, 20, 30), point.Color);
    }

    [Fact]
    public void Pcd_Viewpoint_May_Precede_Width_And_Height()
    {
        // The format lets VERSION, WIDTH, HEIGHT and VIEWPOINT float; only the field entries keep order.
        using var dir = new TempDirectory();
        var header = new[]
        {
            "# .PCD v.7", "VERSION 0.7", "VIEWPOINT 0 0 0 1 0 0 0", "FIELDS x y z", "SIZE 4 4 4",
            "TYPE F F F", "HEIGHT 1", "WIDTH 1", "POINTS 1", "DATA ascii",
        };
        using var doc = PcdDocument.Open(dir.Write("sample.pcd", SyntheticFixtures.CreatePcdFile(header, "1 2 3"u8.ToArray())));

        AssertNear(new PclVector3(1, 2, 3), Assert.Single(doc.Scans[0].ReadPoints(NoTransform)).Position);
        Assert.Equal(PclFileKind.Pcd, PclFile.DetectKind(dir.Write("detect.pcd", SyntheticFixtures.CreatePcdFile(header, "1 2 3"u8.ToArray()))));
    }

    [Fact]
    public void Pcd_Binary_Read_Streams_Records_Across_Chunk_Boundaries()
    {
        // A twelve-byte record does not divide the 64 KiB chunk; records that straddle a boundary must stay aligned.
        var points = Enumerable.Range(0, 7000).Select(i => new PcdSamplePoint(i % 1000, i / 1000, 0)).ToList();
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticPcd(points,
            fields: [("x", 4, 'F', 1), ("y", 4, 'F', 1), ("z", 4, 'F', 1)], data: "binary");
        using var doc = PcdDocument.Open(dir.Write("sample.pcd", bytes));

        var all = doc.Scans[0].ReadPoints(NoTransform).ToList();
        Assert.Equal(7000, all.Count);
        AssertNear(new PclVector3(553, 6, 0), all[6553].Position);
        AssertNear(new PclVector3(999, 6, 0), all[6999].Position);
    }

    [Fact]
    public void Pcd_Overlong_Ascii_Line_Is_Rejected()
    {
        using var dir = new TempDirectory();
        var header = new[]
        {
            "# .PCD v.7", "VERSION 0.7", "FIELDS x y z", "SIZE 4 4 4", "TYPE F F F", "COUNT 1 1 1",
            "WIDTH 1", "HEIGHT 1", "VIEWPOINT 0 0 0 1 0 0 0", "POINTS 1", "DATA ascii",
        };
        byte[] body = System.Text.Encoding.ASCII.GetBytes(new string('1', (1 << 20) + 1) + "\n");
        using var doc = PcdDocument.Open(dir.Write("sample.pcd", SyntheticFixtures.CreatePcdFile(header, body)));

        Assert.Throws<InvalidDataException>(() => { _ = doc.Scans[0].ReadPoints().ToList(); });
    }

    [Fact]
    public void Pcd_Detection_Accepts_A_Long_Comment_Line()
    {
        // The probe's line bound matches the reader's, so a long comment does not hide the format.
        using var dir = new TempDirectory();
        var header = new[]
        {
            "# " + new string('c', 5000), "# .PCD v.7", "VERSION 0.7", "FIELDS x y z", "SIZE 4 4 4",
            "TYPE F F F", "COUNT 1 1 1", "WIDTH 1", "HEIGHT 1", "VIEWPOINT 0 0 0 1 0 0 0", "POINTS 1", "DATA ascii",
        };
        byte[] bytes = SyntheticFixtures.CreatePcdFile(header, "1 2 3"u8.ToArray());

        Assert.Equal(PclFileKind.Pcd, PclFile.DetectKind(dir.Write("sample.pcd", bytes)));
    }

    // ---- PLY ----------------------------------------------------------------------------------------------------

    [Fact]
    public void PclFile_Opens_Ply_With_Count_Channels_And_Metadata()
    {
        using var dir = new TempDirectory();
        using var doc = PclFile.Open(dir.Write("sample.ply", SyntheticFixtures.CreateSyntheticPly()));

        Assert.Equal(PclFileKind.Ply, doc.Kind);
        var scan = Assert.Single(doc.Scans);
        Assert.Equal("0", scan.Id);
        Assert.Equal("sample", scan.Name);
        Assert.Equal(3, scan.PointCount);
        Assert.Equal(3, doc.TotalPointCount);
        Assert.True(scan.HasColors);
        Assert.True(scan.HasIntensities);
        Assert.True(scan.HasNormals);
        Assert.True(scan.Bounds.IsEmpty);
        Assert.Equal("1.0", ((PlyDocument)doc).Version);
        Assert.Equal("ascii", ((PlyDocument)doc).Data);
        Assert.Equal("Stanford PLY", doc.Metadata["Format"]);
        Assert.Null(doc.CoordinateSystem);
        Assert.Empty(doc.Warnings);
    }

    [Theory]
    [InlineData("ascii")]
    [InlineData("binary_little_endian")]
    [InlineData("binary_big_endian")]
    public void Ply_Round_Trip_Positions_Intensity_Colour_And_Normals(string format)
    {
        using var dir = new TempDirectory();
        using var doc = PlyDocument.Open(dir.Write("sample.ply", SyntheticFixtures.CreateSyntheticPly(format: format)));

        var points = doc.Scans[0].ReadPoints(NoTransform).ToList();

        Assert.Equal(3, points.Count);
        Assert.Equal([0UL, 1UL, 2UL], points.Select(p => p.Index));
        AssertNear(new PclVector3(1, 2, 3), points[0].Position);
        AssertNear(new PclVector3(-4.5, 5.25, 6.125), points[1].Position);
        AssertNear(new PclVector3(7, 8, 9), points[2].Position);
        Assert.Equal(0.25f, points[0].Intensity);
        Assert.Equal(0.5f, points[1].Intensity);
        Assert.Equal(0f, points[2].Intensity);
        Assert.Equal(new PclColor(10, 20, 30), points[0].Color);
        Assert.Equal(new PclColor(255, 255, 255), points[1].Color);
        Assert.Equal(new PclColor(255, 255, 255), points[2].Color);
        Assert.Equal(new PclVector3(0, 0, 1), points[0].Normal);
        Assert.Equal(new PclVector3(0, 0, 0), points[1].Normal);
        Assert.All(points, p => Assert.Null(p.Classification));
    }

    [Fact]
    public void Ply_Faces_Are_Ignored()
    {
        foreach (string format in new[] { "ascii", "binary_little_endian", "binary_big_endian" })
        {
            using var dir = new TempDirectory();
            using var doc = PlyDocument.Open(dir.Write("sample.ply",
                SyntheticFixtures.CreateSyntheticPly(format: format, withFaces: true)));

            Assert.Equal(3, doc.Scans[0].ReadPoints(NoTransform).Count());
        }
    }

    [Fact]
    public void Ply_Points_Without_Finite_Coordinates_Are_Not_Returned()
    {
        var points = new[]
        {
            new PlySamplePoint(1, 2, 3),
            new PlySamplePoint(double.NaN, 0, 0),
            new PlySamplePoint(4, 5, 6),
        };
        foreach (string format in new[] { "ascii", "binary_little_endian", "binary_big_endian" })
        {
            using var dir = new TempDirectory();
            using var doc = PlyDocument.Open(dir.Write("sample.ply",
                SyntheticFixtures.CreateSyntheticPly(points, format: format)));

            var read = doc.Scans[0].ReadPoints(NoTransform).ToList();
            Assert.Equal(2, read.Count);
            Assert.Equal([0UL, 1UL], read.Select(p => p.Index));
        }
    }

    [Fact]
    public void Ply_Honours_Limit_And_Step_And_Ignores_Blocks_And_Parallelism()
    {
        var points = Enumerable.Range(0, 8).Select(i => new PlySamplePoint(i, 0, 0)).ToList();
        using var dir = new TempDirectory();
        using var doc = PlyDocument.Open(dir.Write("sample.ply", SyntheticFixtures.CreateSyntheticPly(points)));
        var scan = doc.Scans[0];

        Assert.Equal(3, scan.ReadPoints(new PclReadOptions { MaxPoints = 3 }).Count());
        Assert.Equal(4, scan.ReadPoints(new PclReadOptions { SubsampleStep = 2 }).Count());
        Assert.Empty(scan.ReadPoints(new PclReadOptions { MaxPoints = 0 }));

        var ranged = scan.ReadPoints(new PclReadOptions
        {
            Blocks = new BlockRange { StartBlock = 1, BlockCount = 1 },
            MaxDegreeOfParallelism = 4,
        }).ToList();
        Assert.Equal(8, ranged.Count);
    }

    [Fact]
    public void Ply_Without_X_Y_Or_Z_Is_Not_Read()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticPly(
            properties: [("y", "float"), ("z", "float"), ("red", "uchar")]);

        Assert.Throws<NotSupportedException>(() => PlyDocument.Open(dir.Write("sample.ply", bytes)));
    }

    [Fact]
    public void Ply_Without_A_Vertex_Element_Is_Not_Read()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreatePlyFile(
        [
            "ply",
            "format ascii 1.0",
            "element face 1",
            "property list uchar int vertex_indices",
            "end_header",
            "3 0 1 2",
        ], []);

        Assert.Throws<NotSupportedException>(() => PlyDocument.Open(dir.Write("sample.ply", bytes)));
    }

    [Fact]
    public void Ply_Unsupported_Version_Is_Rejected()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticPly();
        byte[] patched = System.Text.Encoding.ASCII.GetBytes(
            System.Text.Encoding.ASCII.GetString(bytes).Replace("format ascii 1.0", "format ascii 2.0"));

        Assert.Throws<NotSupportedException>(() => PlyDocument.Open(dir.Write("sample.ply", patched)));
    }

    [Fact]
    public void Ply_Elements_Before_Vertices_Are_Skipped()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreatePlyFile(
        [
            "ply",
            "format ascii 1.0",
            "comment Skipped elements come first",
            "element range_grid 2",
            "property int min",
            "property int max",
            "element vertex 1",
            "property float x",
            "property float y",
            "property float z",
            "end_header",
            "0 10",
            "20 30",
            "1 2 3",
        ], []);

        var point = Assert.Single(PlyDocument.Open(dir.Write("sample.ply", bytes)).Scans[0].ReadPoints(NoTransform));
        AssertNear(new PclVector3(1, 2, 3), point.Position);
    }

    [Fact]
    public void Ply_Variable_Element_Before_Vertices_Is_Not_Read()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreatePlyFile(
        [
            "ply",
            "format ascii 1.0",
            "element face 1",
            "property list uchar int vertex_indices",
            "element vertex 1",
            "property float x",
            "property float y",
            "property float z",
            "end_header",
            "3 0 1 2",
            "1 2 3",
        ], []);

        Assert.Throws<NotSupportedException>(() => PlyDocument.Open(dir.Write("sample.ply", bytes)));
    }

    [Theory]
    [InlineData("ascii")]
    [InlineData("binary_little_endian")]
    [InlineData("binary_big_endian")]
    public void Ply_Truncated_Vertex_Data_Is_Rejected_When_Read(string format)
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticPly(format: format);
        // Binary layouts state their size up front, so a short file is rejected at open.
        if (format != "ascii")
        {
            Assert.Throws<InvalidDataException>(() => PlyDocument.Open(dir.Write("cut.ply", bytes[..(bytes.Length - 5)])));
            return;
        }

        using var doc = PlyDocument.Open(dir.Write("cut.ply", bytes[..(bytes.Length - 5)]));
        Assert.Equal(3, doc.Scans[0].PointCount);
        Assert.Throws<InvalidDataException>(() => { _ = doc.Scans[0].ReadPoints().ToList(); });
    }

    [Fact]
    public void Ply_Vertex_Line_With_The_Wrong_Value_Count_Is_Rejected()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreatePlyFile(
        [
            "ply",
            "format ascii 1.0",
            "element vertex 1",
            "property float x",
            "property float y",
            "property float z",
            "end_header",
            "1 2",
        ], []);
        using var doc = PlyDocument.Open(dir.Write("sample.ply", bytes));

        Assert.Throws<InvalidDataException>(() => { _ = doc.Scans[0].ReadPoints().ToList(); });
    }

    [Fact]
    public void Ply_Unknown_Property_Type_Is_Rejected()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticPly(
            properties: [("x", "float"), ("y", "float"), ("z", "float"), ("label", "string")]);

        Assert.Throws<InvalidDataException>(() => PlyDocument.Open(dir.Write("sample.ply", bytes)));
    }

    [Fact]
    public void Ply_Wrong_Signature_Is_Rejected()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticPly();
        bytes[0] = (byte)'X';

        Assert.Throws<InvalidDataException>(() => PlyDocument.Open(dir.Write("sample.ply", bytes)));
        Assert.Equal(PclFileKind.Unknown, PclFile.DetectKind(dir.Write("other.ply", bytes)));
        Assert.Throws<NotSupportedException>(() => PclFile.Open(dir.PathOf("other.ply")));
    }

    [Theory]
    [InlineData("binary_little_endian", true)]
    [InlineData("binary_big_endian", false)]
    public void Ply_Binary_Elements_Before_Vertices_Are_Skipped(string format, bool littleEndian)
    {
        // A fixed-size camera element precedes the vertices; its bytes are skipped, not read as points.
        using var dir = new TempDirectory();
        var header = new[]
        {
            "ply", $"format {format} 1.0", "element camera 1", "property double value",
            "element vertex 1", "property float x", "property float y", "property float z", "end_header",
        };
        byte[] body = new byte[20];
        Span<byte> cam = body.AsSpan(0, 8);
        Span<byte> vx = body.AsSpan(8, 4), vy = body.AsSpan(12, 4), vz = body.AsSpan(16, 4);
        if (littleEndian)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(cam, 12345.6789);
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(vx, 1f);
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(vy, 2f);
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(vz, 3f);
        }
        else
        {
            System.Buffers.Binary.BinaryPrimitives.WriteDoubleBigEndian(cam, 12345.6789);
            System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(vx, 1f);
            System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(vy, 2f);
            System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(vz, 3f);
        }
        using var doc = PlyDocument.Open(dir.Write("sample.ply", SyntheticFixtures.CreatePlyFile(header, body)));

        AssertNear(new PclVector3(1, 2, 3), Assert.Single(doc.Scans[0].ReadPoints(NoTransform)).Position);
    }

    [Fact]
    public void Ply_Huge_Preceding_Element_Count_Is_Rejected_At_Open()
    {
        // count * stride wraps past zero unchecked; the checked product rejects the file at open instead of
        // reading the camera bytes as vertices.
        using var dir = new TempDirectory();
        var header = new[]
        {
            "ply", "format binary_little_endian 1.0", "element camera 2305843009213693952", "property double value",
            "element vertex 1", "property float x", "property float y", "property float z", "end_header",
        };
        byte[] body = new byte[20];
        Assert.Throws<InvalidDataException>(() =>
            PlyDocument.Open(dir.Write("sample.ply", SyntheticFixtures.CreatePlyFile(header, body))));
    }

    [Fact]
    public void Ply_Ascii_Preceding_Element_Counts_That_Wrap_Are_Rejected()
    {
        // Two preceding counts sum past long.MaxValue; the wrapped line skip would read the wrong lines.
        using var dir = new TempDirectory();
        var header = new[]
        {
            "ply", "format ascii 1.0",
            "element camera 9223372036854775807", "property float x", "property float y", "property float z",
            "element edge 1", "property float x", "property float y", "property float z",
            "element vertex 1", "property float x", "property float y", "property float z", "end_header",
        };
        Assert.Throws<InvalidDataException>(() =>
            PlyDocument.Open(dir.Write("sample.ply", SyntheticFixtures.CreatePlyFile(header, "1 2 3\n"u8.ToArray()))));
    }

    [Fact]
    public void Ply_Float_Colour_Is_Left_Unread_With_A_Warning()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticPly(
            properties: [("x", "float"), ("y", "float"), ("z", "float"), ("red", "float"), ("green", "float"), ("blue", "float")]);
        using var doc = PlyDocument.Open(dir.Write("sample.ply", bytes));

        var scan = Assert.Single(doc.Scans);
        Assert.False(scan.HasColors);
        Assert.Equal(3, scan.ReadPoints(NoTransform).Count());
        Assert.All(scan.ReadPoints(NoTransform), p => Assert.Null(p.Color));
        Assert.Contains(doc.Warnings, w => w.Contains("colour is not stored as 8-bit integers", StringComparison.Ordinal));
    }

    [Fact]
    public void Pts_Open_Inspects_Document_Properties_And_Scan()
    {
        using var dir = new TempDirectory();
        using var doc = PtsDocument.Open(dir.Write("sample.pts", SyntheticFixtures.CreateSyntheticPts()));

        Assert.Equal(PclFileKind.Pts, doc.Kind);
        Assert.Equal("Leica PTS", doc.Metadata["Format"]);
        Assert.Equal("7", doc.Metadata["Columns"]);
        Assert.Equal("3", doc.Metadata["DeclaredPoints"]);
        Assert.True(doc.HasCountHeader);
        Assert.Equal(3, doc.DeclaredPointCount);
        Assert.Equal(7, doc.Columns);

        var scan = Assert.Single(doc.Scans);
        Assert.Equal("0", scan.Id);
        Assert.Equal("sample", scan.Name);
        Assert.Equal(3, scan.PointCount);
        Assert.True(scan.HasColors);
        Assert.True(scan.HasIntensities);
        Assert.False(scan.HasNormals);
        Assert.Equal(PclBounds.Empty, scan.Bounds);
        Assert.Equal(PclTransform.Identity, scan.Transform);
        Assert.Equal("7", scan.Properties["Columns"]);
        Assert.Equal("True", scan.Properties["HasCountHeader"]);
    }

    [Fact]
    public void Pts_Round_Trip_Positions_Intensity_And_Colour()
    {
        using var dir = new TempDirectory();
        using var doc = PtsDocument.Open(dir.Write("sample.pts", SyntheticFixtures.CreateSyntheticPts()));

        var points = doc.Scans[0].ReadPoints(NoTransform).ToList();

        Assert.Equal(3, points.Count);
        Assert.Equal([0UL, 1UL, 2UL], points.Select(p => p.Index));
        AssertNear(new PclVector3(1, 2, 3), points[0].Position);
        AssertNear(new PclVector3(-4.5, 5.25, 6.125), points[1].Position);
        AssertNear(new PclVector3(7, 8, 9), points[2].Position);
        Assert.Equal(511f, points[0].Intensity);
        Assert.Equal(1023f, points[1].Intensity);
        Assert.Equal(0f, points[2].Intensity);
        Assert.Equal(new PclColor(10, 20, 30), points[0].Color);
        Assert.Equal(new PclColor(255, 255, 255), points[1].Color);
        Assert.Equal(new PclColor(255, 255, 255), points[2].Color);
        Assert.All(points, p => Assert.Null(p.Normal));
        Assert.All(points, p => Assert.Null(p.Classification));
    }

    [Fact]
    public void Pts_Round_Trip_With_PclPtsExporter()
    {
        using var dir = new TempDirectory();
        // Create an existing doc from synthetic PCD/PLY points and export to PTS
        using var sourceDoc = PcdDocument.Open(dir.Write("source.pcd", SyntheticFixtures.CreateSyntheticPcd()));
        string ptsPath = dir.PathOf("exported.pts");
        using (var writer = File.CreateText(ptsPath))
        {
            long written = PclPtsExporter.Export(sourceDoc, writer);
            Assert.Equal(3, written);
        }

        using var doc = PclFile.Open(ptsPath);
        Assert.Equal(PclFileKind.Pts, doc.Kind);
        Assert.Equal(3, doc.TotalPointCount);

        var points = doc.ReadAllPoints(NoTransform).ToList();
        Assert.Equal(3, points.Count);
        AssertNear(new PclVector3(1, 2, 3), points[0].Position);
        AssertNear(new PclVector3(-4.5, 5.25, 6.125), points[1].Position);
        AssertNear(new PclVector3(7, 8, 9), points[2].Position);
        // Intensity in PclPtsExporter is clamped to 0..1 and multiplied by 2047:
        // 0.25 * 2047 = 511; 0.5 * 2047 = 1023; 0 * 2047 = 0
        Assert.Equal(511f, points[0].Intensity);
        Assert.Equal(1023f, points[1].Intensity);
        Assert.Equal(0f, points[2].Intensity);
        Assert.Equal(new PclColor(10, 20, 30), points[0].Color);
        Assert.Equal(new PclColor(255, 255, 255), points[1].Color);
        Assert.Equal(new PclColor(255, 255, 255), points[2].Color);
    }

    [Fact]
    public void Pts_Reads_Headerless_File()
    {
        using var dir = new TempDirectory();
        using var doc = PtsDocument.Open(dir.Write("sample.pts",
            SyntheticFixtures.CreateSyntheticPts(withCountHeader: false)));

        Assert.False(doc.HasCountHeader);
        Assert.Equal(-1, doc.DeclaredPointCount);
        Assert.Equal(3, doc.Scans[0].PointCount);

        var points = doc.Scans[0].ReadPoints(NoTransform).ToList();
        Assert.Equal(3, points.Count);
        AssertNear(new PclVector3(1, 2, 3), points[0].Position);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Pts_Reads_Without_Colour(int columns)
    {
        using var dir = new TempDirectory();
        using var doc = PtsDocument.Open(dir.Write("sample.pts",
            SyntheticFixtures.CreateSyntheticPts(columns: columns)));

        var scan = Assert.Single(doc.Scans);
        Assert.False(scan.HasColors);
        Assert.Equal(columns == 4, scan.HasIntensities);

        var points = scan.ReadPoints(NoTransform).ToList();
        Assert.Equal(3, points.Count);
        Assert.All(points, p => Assert.Null(p.Color));
        if (columns == 4)
            Assert.Equal(511f, points[0].Intensity);
        else
            Assert.All(points, p => Assert.Null(p.Intensity));
    }

    [Fact]
    public void Pts_Reads_6_Columns_With_Rgb_Or_Normals()
    {
        using var dir = new TempDirectory();

        // 6 columns with RGB
        using var docRgb = PtsDocument.Open(dir.Write("rgb.pts",
            SyntheticFixtures.CreateSyntheticPts(columns: 6)));
        var scanRgb = Assert.Single(docRgb.Scans);
        Assert.True(scanRgb.HasColors);
        Assert.False(scanRgb.HasNormals);
        Assert.Equal(new PclColor(10, 20, 30), scanRgb.ReadPoints(NoTransform).First().Color);

        // 6 columns with negative normal coordinates (like bunny-1000.pts)
        var normalPoints = new[]
        {
            new PtsSamplePoint(1, 2, 3, Normal: new PclVector3(-0.3, -0.8, -0.5)),
            new PtsSamplePoint(4, 5, 6, Normal: new PclVector3(-0.1, -0.9, 0.1)),
        };
        using var docNorm = PtsDocument.Open(dir.Write("normals.pts",
            SyntheticFixtures.CreateSyntheticPts(normalPoints, columns: 6)));
        var scanNorm = Assert.Single(docNorm.Scans);
        Assert.False(scanNorm.HasColors);
        Assert.True(scanNorm.HasNormals);
        AssertNear(new PclVector3(-0.3, -0.8, -0.5), scanNorm.ReadPoints(NoTransform).First().Normal!.Value);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void Pts_Reads_Extended_Columns(int columns)
    {
        using var dir = new TempDirectory();
        using var doc = PtsDocument.Open(dir.Write("extended.pts",
            SyntheticFixtures.CreateSyntheticPts(columns: columns)));

        var scan = Assert.Single(doc.Scans);
        Assert.True(scan.HasColors);
        Assert.Equal(columns != 9, scan.HasIntensities);
        Assert.Equal(columns >= 9, scan.HasNormals);

        var pt = scan.ReadPoints(NoTransform).First();
        Assert.Equal(new PclColor(10, 20, 30), pt.Color);
        if (columns >= 9)
            AssertNear(new PclVector3(0, 0, 1), pt.Normal!.Value);
    }

    [Fact]
    public void Pts_Skips_NonFinite_Coordinates()
    {
        var points = new[]
        {
            new PtsSamplePoint(1, 2, 3),
            new PtsSamplePoint(double.NaN, 5, 6),
            new PtsSamplePoint(7, double.PositiveInfinity, 9),
            new PtsSamplePoint(10, 11, 12),
        };
        using var dir = new TempDirectory();
        using var doc = PtsDocument.Open(dir.Write("nan.pts",
            SyntheticFixtures.CreateSyntheticPts(points, columns: 3)));

        var yielded = doc.Scans[0].ReadPoints(NoTransform).ToList();
        Assert.Equal(2, yielded.Count);
        Assert.Equal([0UL, 1UL], yielded.Select(p => p.Index));
        AssertNear(new PclVector3(1, 2, 3), yielded[0].Position);
        AssertNear(new PclVector3(10, 11, 12), yielded[1].Position);
    }

    [Fact]
    public void Pts_Honours_MaxPoints_And_SubsampleStep()
    {
        using var dir = new TempDirectory();
        using var doc = PtsDocument.Open(dir.Write("sample.pts", SyntheticFixtures.CreateSyntheticPts()));

        var limit1 = doc.Scans[0].ReadPoints(new PclReadOptions { MaxPoints = 1 }).ToList();
        Assert.Single(limit1);
        AssertNear(new PclVector3(1, 2, 3), limit1[0].Position);

        var step2 = doc.Scans[0].ReadPoints(new PclReadOptions { SubsampleStep = 2 }).ToList();
        Assert.Equal(2, step2.Count);
        AssertNear(new PclVector3(1, 2, 3), step2[0].Position);
        AssertNear(new PclVector3(7, 8, 9), step2[1].Position);
    }

    [Fact]
    public void Pts_Reads_Empty_File_With_Zero_Declared_Count()
    {
        using var dir = new TempDirectory();
        using var doc = PtsDocument.Open(dir.Write("empty.pts", "0\n"u8.ToArray()));

        Assert.True(doc.HasCountHeader);
        Assert.Equal(0, doc.DeclaredPointCount);
        Assert.Equal(0, doc.Scans[0].PointCount);
        Assert.Empty(doc.Scans[0].ReadPoints(NoTransform));
    }

    [Fact]
    public void Pts_Rejects_Negative_Declared_Count()
    {
        using var dir = new TempDirectory();
        Assert.Throws<InvalidDataException>(() =>
            PtsDocument.Open(dir.Write("bad.pts", "-5\n1 2 3 50 255 0 0\n"u8.ToArray())));
    }

    [Fact]
    public void Pts_Rejects_Oversized_Declared_Count()
    {
        using var dir = new TempDirectory();
        Assert.Throws<InvalidDataException>(() =>
            PtsDocument.Open(dir.Write("bad.pts", "1000000000\n1 2 3\n"u8.ToArray())));
    }

    [Fact]
    public void Pts_Rejects_Truncated_Points()
    {
        using var dir = new TempDirectory();
        using var doc = PtsDocument.Open(dir.WriteText("truncated.pts",
            "3\n1.0 2.0 3.0 100 255 0 0\n4.0 5.0 6.0 100 0 255 0\n"));

        Assert.Throws<InvalidDataException>(() => doc.Scans[0].ReadPoints(NoTransform).ToList());
    }

    [Fact]
    public void Pts_Rejects_Mismatched_Or_Unsupported_Columns()
    {
        using var dir = new TempDirectory();
        // 5 columns is unsupported
        Assert.Throws<NotSupportedException>(() =>
            PtsDocument.Open(dir.Write("bad.pts", "1\n1 2 3 4 5\n"u8.ToArray())));

        // Mismatched columns across lines
        using var doc = PtsDocument.Open(dir.Write("mismatch.pts", "2\n1 2 3\n1 2 3 4 5 6 7\n"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => doc.Scans[0].ReadPoints(NoTransform).ToList());
    }

    [Fact]
    public void Pts_Rejects_Invalid_Numeric_Values()
    {
        using var dir = new TempDirectory();
        Assert.Throws<InvalidDataException>(() =>
            PtsDocument.Open(dir.Write("bad.pts", "1\n1 abc 3\n"u8.ToArray())));
    }

    [Fact]
    public void Pts_Rejects_Line_Exceeding_Max_Length()
    {
        using var dir = new TempDirectory();
        string longLine = "1 2 3 " + new string('9', (1 << 20) + 10) + "\n";
        Assert.Throws<InvalidDataException>(() =>
            PtsDocument.Open(dir.WriteText("long.pts", "1\n" + longLine)));
    }

    // ---- Leica Cyclone PTX ------------------------------------------------------------------------------------

    [Fact]
    public void Ptx_Reads_Header_And_Exposes_Properties_And_Metadata()
    {
        using var dir = new TempDirectory();
        using var doc = PtxDocument.Open(dir.Write("sample.ptx", SyntheticFixtures.CreateSyntheticPtx()));

        Assert.Equal(PclFileKind.Ptx, doc.Kind);
        Assert.Equal("Leica Cyclone PTX", doc.Metadata["Format"]);
        Assert.Equal("1", doc.Metadata["Scans"]);
        Assert.Equal("3", doc.Metadata["TotalPoints"]);
        Assert.Equal(3, doc.TotalPointCount);

        var scan = Assert.Single(doc.Scans);
        Assert.IsType<PtxScan>(scan);
        var ptxScan = (PtxScan)scan;

        Assert.Equal("0", ptxScan.Id);
        Assert.Equal("sample", ptxScan.Name);
        Assert.Equal(3, ptxScan.Columns);
        Assert.Equal(1, ptxScan.Rows);
        Assert.Equal(3, ptxScan.GridSize);
        Assert.Equal(3, ptxScan.PointCount);
        Assert.Equal(7, ptxScan.ColumnsPerPoint);
        Assert.True(ptxScan.HasColors);
        Assert.True(ptxScan.HasIntensities);
        Assert.False(ptxScan.HasNormals);
        Assert.Equal(PclBounds.Empty, ptxScan.Bounds);
        Assert.Equal(PclTransform.Identity, ptxScan.Transform);
        Assert.Equal(PclVector3.Zero, ptxScan.ScannerPosition);
        Assert.Equal(new PclVector3(1, 0, 0), ptxScan.ScannerXAxis);
        Assert.Equal(new PclVector3(0, 1, 0), ptxScan.ScannerYAxis);
        Assert.Equal(new PclVector3(0, 0, 1), ptxScan.ScannerZAxis);
        Assert.Equal("3", ptxScan.Properties["Columns"]);
        Assert.Equal("1", ptxScan.Properties["Rows"]);
        Assert.Equal("3", ptxScan.Properties["GridSize"]);
        Assert.Equal("7", ptxScan.Properties["ColumnsPerPoint"]);
    }

    [Fact]
    public void Ptx_Round_Trip_Positions_Intensity_And_Colour()
    {
        using var dir = new TempDirectory();
        using var doc = PtxDocument.Open(dir.Write("sample.ptx", SyntheticFixtures.CreateSyntheticPtx()));

        var points = doc.Scans[0].ReadPoints(NoTransform).ToList();

        Assert.Equal(3, points.Count);
        Assert.Equal([0UL, 1UL, 2UL], points.Select(p => p.Index));
        AssertNear(new PclVector3(1, 2, 3), points[0].Position);
        AssertNear(new PclVector3(-4.5, 5.25, 6.125), points[1].Position);
        AssertNear(new PclVector3(7, 8, 9), points[2].Position);
        Assert.Equal(0.5f, points[0].Intensity);
        Assert.Equal(1.0f, points[1].Intensity);
        Assert.Equal(0f, points[2].Intensity);
        Assert.Equal(new PclColor(10, 20, 30), points[0].Color);
        Assert.Equal(new PclColor(255, 255, 255), points[1].Color);
        Assert.Equal(new PclColor(255, 255, 255), points[2].Color);
        Assert.All(points, p => Assert.Null(p.Normal));
        Assert.All(points, p => Assert.Null(p.Classification));
    }

    [Fact]
    public void Ptx_Applies_Transformation_Matrix()
    {
        using var dir = new TempDirectory();
        // Matrix in PTX: Row 4 is translation Tx, Ty, Tz, 1
        double[] matrix =
        [
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            10, 20, 30, 1
        ];

        using var doc = PtxDocument.Open(dir.Write("transformed.ptx",
            SyntheticFixtures.CreateSyntheticPtx(matrix: matrix)));

        var scan = doc.Scans[0];
        Assert.False(scan.Transform.IsIdentity);
        Assert.Equal(new PclVector3(10, 20, 30), scan.Transform.Translation);

        // With ApplyTransform = true (default)
        var transformed = scan.ReadPoints().ToList();
        Assert.Equal(3, transformed.Count);
        AssertNear(new PclVector3(11, 22, 33), transformed[0].Position);
        AssertNear(new PclVector3(5.5, 25.25, 36.125), transformed[1].Position);
        AssertNear(new PclVector3(17, 28, 39), transformed[2].Position);

        // With ApplyTransform = false
        var raw = scan.ReadPoints(NoTransform).ToList();
        AssertNear(new PclVector3(1, 2, 3), raw[0].Position);
    }

    [Fact]
    public void Ptx_Skips_Empty_Grid_Cells_And_Non_Finite_Coordinates()
    {
        using var dir = new TempDirectory();
        PtxSamplePoint[] pointsWithEmpty =
        [
            new(1, 2, 3, 0.5f, new PclColor(10, 20, 30)),
            new(0, 0, 0, 0.0f, new PclColor(0, 0, 0)),        // Empty cell (0, 0, 0)
            new(double.NaN, 5, 6, 0.5f, new PclColor(0, 0, 0)), // Non-finite NaN
            new(double.PositiveInfinity, 0, 0, 0.5f, null),    // Non-finite Inf
            new(7, 8, 9, 1.0f, new PclColor(255, 255, 255)),   // Valid
        ];

        using var doc = PtxDocument.Open(dir.Write("empty_cells.ptx",
            SyntheticFixtures.CreateSyntheticPtx(pointsWithEmpty, cols: 5, rows: 1)));

        Assert.Equal(5, doc.Scans[0].PointCount);

        var points = doc.Scans[0].ReadPoints(NoTransform).ToList();
        Assert.Equal(2, points.Count);
        Assert.Equal(0UL, points[0].Index);
        AssertNear(new PclVector3(1, 2, 3), points[0].Position);
        Assert.Equal(1UL, points[1].Index);
        AssertNear(new PclVector3(7, 8, 9), points[1].Position);
    }

    [Fact]
    public void Ptx_Supports_Multiple_Scans_In_Single_File()
    {
        using var dir = new TempDirectory();

        PtxSamplePoint[] scan1Points =
        [
            new(1, 2, 3, 0.5f, new PclColor(10, 20, 30)),
            new(0, 0, 0, 0f, new PclColor(0, 0, 0)),
            new(4, 5, 6, 0.8f, new PclColor(40, 50, 60)),
            new(7, 8, 9, 1.0f, new PclColor(70, 80, 90)),
        ];

        PtxSamplePoint[] scan2Points =
        [
            new(10, 20, 30, 0.2f),
            new(40, 50, 60, 0.9f),
        ];

        double[] identityMatrix = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
        double[] transMatrix = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 100, 200, 300, 1];

        PtxSampleScan[] scans =
        [
            new(2, 2, PclVector3.Zero, new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), identityMatrix, scan1Points, 7),
            new(2, 1, new(100, 200, 300), new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), transMatrix, scan2Points, 4),
        ];

        byte[] ptxBytes = SyntheticFixtures.CreateSyntheticMultiScanPtx(scans);
        using var doc = PtxDocument.Open(dir.Write("multi.ptx", ptxBytes));

        Assert.Equal(2, doc.Scans.Count);
        Assert.Equal("multi_0", doc.Scans[0].Name);
        Assert.Equal("multi_1", doc.Scans[1].Name);
        Assert.Equal(4, doc.Scans[0].PointCount);
        Assert.Equal(2, doc.Scans[1].PointCount);
        Assert.Equal(6, doc.TotalPointCount);

        Assert.True(doc.Scans[0].HasColors);
        Assert.True(doc.Scans[0].HasIntensities);

        Assert.False(doc.Scans[1].HasColors);
        Assert.True(doc.Scans[1].HasIntensities);

        // Read Scan 0 points (1 zero cell skipped)
        var s0Points = doc.Scans[0].ReadPoints().ToList();
        Assert.Equal(3, s0Points.Count);
        AssertNear(new PclVector3(1, 2, 3), s0Points[0].Position);
        AssertNear(new PclVector3(4, 5, 6), s0Points[1].Position);
        AssertNear(new PclVector3(7, 8, 9), s0Points[2].Position);

        // Read Scan 1 points with transform applied (translation 100, 200, 300)
        var s1Points = doc.Scans[1].ReadPoints().ToList();
        Assert.Equal(2, s1Points.Count);
        AssertNear(new PclVector3(110, 220, 330), s1Points[0].Position);
        AssertNear(new PclVector3(140, 250, 360), s1Points[1].Position);

        // Read all points across both scans
        var allPoints = doc.ReadAllPoints().ToList();
        Assert.Equal(5, allPoints.Count);
    }

    [Theory]
    [InlineData(3, false, false)]
    [InlineData(4, false, true)]
    [InlineData(6, true, false)]
    [InlineData(7, true, true)]
    public void Ptx_Reads_Various_Column_Counts(int columns, bool expectedColors, bool expectedIntensities)
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticPtx(columnsPerPoint: columns);
        using var doc = PtxDocument.Open(dir.Write($"cols_{columns}.ptx", bytes));

        var scan = Assert.Single(doc.Scans);
        Assert.Equal(expectedColors, scan.HasColors);
        Assert.Equal(expectedIntensities, scan.HasIntensities);

        var points = scan.ReadPoints(NoTransform).ToList();
        Assert.Equal(3, points.Count);
        AssertNear(new PclVector3(1, 2, 3), points[0].Position);

        if (expectedIntensities)
            Assert.NotNull(points[0].Intensity);
        else
            Assert.Null(points[0].Intensity);

        if (expectedColors)
            Assert.NotNull(points[0].Color);
        else
            Assert.Null(points[0].Color);
    }

    [Fact]
    public void Ptx_Honors_MaxPoints_And_SubsampleStep()
    {
        using var dir = new TempDirectory();
        using var doc = PtxDocument.Open(dir.Write("sample.ptx", SyntheticFixtures.CreateSyntheticPtx()));

        var scan = doc.Scans[0];
        Assert.Equal(2, scan.ReadPoints(new PclReadOptions { MaxPoints = 2 }).Count());
        Assert.Empty(scan.ReadPoints(new PclReadOptions { MaxPoints = 0 }));

        // SubsampleStep: step 2 of 3 points yields points at index 0 and index 2
        var stepped = scan.ReadPoints(new PclReadOptions { SubsampleStep = 2, ApplyTransform = false }).ToList();
        Assert.Equal(2, stepped.Count);
        AssertNear(new PclVector3(1, 2, 3), stepped[0].Position);
        AssertNear(new PclVector3(7, 8, 9), stepped[1].Position);
    }

    [Fact]
    public void Ptx_Handles_Empty_Grid_Scan()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticPtx(points: [], cols: 0, rows: 0);
        using var doc = PtxDocument.Open(dir.Write("empty.ptx", bytes));

        var scan = Assert.Single(doc.Scans);
        Assert.Equal(0, scan.PointCount);
        Assert.Empty(scan.ReadPoints());
    }

    [Fact]
    public void Ptx_Rejects_Negative_Grid_Dimensions()
    {
        using var dir = new TempDirectory();
        string badHeader = "-5\n10\n0 0 0\n1 0 0\n0 1 0\n0 0 1\n1 0 0 0\n0 1 0 0\n0 0 1 0\n0 0 0 1\n";
        Assert.Throws<InvalidDataException>(() =>
            PtxDocument.Open(dir.WriteText("bad.ptx", badHeader + "1 2 3\n")));
    }

    [Fact]
    public void Ptx_Rejects_Grid_Count_Exceeding_Physical_Capacity()
    {
        using var dir = new TempDirectory();
        string badHeader = "1000000000\n1000000000\n0 0 0\n1 0 0\n0 1 0\n0 0 1\n1 0 0 0\n0 1 0 0\n0 0 1 0\n0 0 0 1\n";
        Assert.Throws<InvalidDataException>(() =>
            PtxDocument.Open(dir.WriteText("huge.ptx", badHeader + "1 2 3\n")));
    }

    [Fact]
    public void Ptx_Rejects_Truncated_Scan_Header()
    {
        using var dir = new TempDirectory();
        Assert.Throws<InvalidDataException>(() =>
            PtxDocument.Open(dir.WriteText("trunc.ptx", "10\n10\n0 0 0\n1 0 0\n")));
    }

    [Fact]
    public void Ptx_Rejects_Truncated_Points()
    {
        using var dir = new TempDirectory();
        string header = "3\n1\n0 0 0\n1 0 0\n0 1 0\n0 0 1\n1 0 0 0\n0 1 0 0\n0 0 1 0\n0 0 0 1\n";
        // 3 points declared, but only 2 lines present
        string content = header + "1.0 2.0 3.0 1.0 255 0 0\n4.0 5.0 6.0 1.0 0 255 0\n";
        using var doc = PtxDocument.Open(dir.WriteText("trunc_pts.ptx", content));
        var scan = Assert.Single(doc.Scans);
        Assert.Throws<InvalidDataException>(() => scan.ReadPoints().ToList());
    }

    [Fact]
    public void Ptx_Rejects_Unsupported_Column_Count()
    {
        using var dir = new TempDirectory();
        string header = "1\n1\n0 0 0\n1 0 0\n0 1 0\n0 0 1\n1 0 0 0\n0 1 0 0\n0 0 1 0\n0 0 0 1\n";
        // 5 columns (unsupported)
        Assert.Throws<NotSupportedException>(() =>
            PtxDocument.Open(dir.WriteText("five_cols.ptx", header + "1 2 3 4 5\n")));
    }

    [Fact]
    public void Ptx_Rejects_Invalid_Numeric_Header_Values()
    {
        using var dir = new TempDirectory();
        string header = "10\nabc\n0 0 0\n1 0 0\n0 1 0\n0 0 1\n1 0 0 0\n0 1 0 0\n0 0 1 0\n0 0 0 1\n";
        Assert.Throws<InvalidDataException>(() =>
            PtxDocument.Open(dir.WriteText("bad.ptx", header + "1 2 3\n")));
    }

    [Fact]
    public void Ptx_Rejects_Invalid_Numeric_Point_Values()
    {
        using var dir = new TempDirectory();
        string header = "1\n1\n0 0 0\n1 0 0\n0 1 0\n0 0 1\n1 0 0 0\n0 1 0 0\n0 0 1 0\n0 0 0 1\n";
        Assert.Throws<InvalidDataException>(() =>
            PtxDocument.Open(dir.WriteText("bad_pt.ptx", header + "1 abc 3\n")));

        string header2 = "2\n1\n0 0 0\n1 0 0\n0 1 0\n0 0 1\n1 0 0 0\n0 1 0 0\n0 0 1 0\n0 0 0 1\n";
        using var doc2 = PtxDocument.Open(dir.WriteText("bad_pt2.ptx", header2 + "1.0 2.0 3.0\n1.0 abc 3.0\n"));
        var scan2 = Assert.Single(doc2.Scans);
        Assert.Throws<InvalidDataException>(() => scan2.ReadPoints().ToList());
    }

    [Fact]
    public void Ptx_Rejects_Line_Exceeding_Max_Length()
    {
        using var dir = new TempDirectory();
        string header = "1\n1\n0 0 0\n1 0 0\n0 1 0\n0 0 1\n1 0 0 0\n0 1 0 0\n0 0 1 0\n0 0 0 1\n";
        string longLine = "1 2 3 " + new string('9', (1 << 20) + 10) + "\n";
        Assert.Throws<InvalidDataException>(() =>
            PtxDocument.Open(dir.WriteText("long.ptx", header + longLine)));

        string header2 = "2\n1\n0 0 0\n1 0 0\n0 1 0\n0 0 1\n1 0 0 0\n0 1 0 0\n0 0 1 0\n0 0 0 1\n";
        using var doc2 = PtxDocument.Open(dir.WriteText("long2.ptx", header2 + "1 2 3\n" + longLine));
        var scan2 = Assert.Single(doc2.Scans);
        Assert.Throws<InvalidDataException>(() => scan2.ReadPoints().ToList());
    }

    [Fact]
    public void Xyz_Reads_Document_Properties_And_Metadata()
    {
        using var dir = new TempDirectory();
        using var doc = XyzDocument.Open(dir.Write("sample.xyz", SyntheticFixtures.CreateSyntheticXyz()));

        Assert.Equal(PclFileKind.Xyz, doc.Kind);
        Assert.Equal(3, doc.Columns);
        Assert.Equal(' ', doc.Delimiter);
        Assert.False(doc.HasHeader);
        Assert.Equal("x,y,z", doc.ColumnLayout);
        Assert.Equal(1.0, doc.CoordinateScale);
        Assert.Equal(3, doc.TotalPoints);

        Assert.Equal("Delimited Text (XYZ)", doc.Metadata["Format"]);
        Assert.Equal("space", doc.Metadata["Delimiter"]);
        Assert.Equal(" ", doc.Metadata["DelimiterChar"]);
        Assert.Equal("3", doc.Metadata["Columns"]);
        Assert.Equal("x,y,z", doc.Metadata["ColumnLayout"]);
        Assert.Equal("False", doc.Metadata["HasHeader"]);
        Assert.Equal("1", doc.Metadata["CoordinateScale"]);
        Assert.Equal("3", doc.Metadata["TotalPoints"]);

        var scan = Assert.Single(doc.Scans);
        Assert.Equal("0", scan.Id);
        Assert.Equal("sample", scan.Name);
        Assert.Equal(3, scan.PointCount);
        Assert.False(scan.HasColors);
        Assert.False(scan.HasIntensities);
        Assert.False(scan.HasNormals);
        Assert.Equal(' ', ((XyzScan)scan).Delimiter);
        Assert.Equal("x,y,z", ((XyzScan)scan).ColumnLayout);
        Assert.Equal(1.0, ((XyzScan)scan).CoordinateScale);

        Assert.Equal("3", scan.Properties["Columns"]);
        Assert.Equal("space", scan.Properties["Delimiter"]);
        Assert.Equal(" ", scan.Properties["DelimiterChar"]);
        Assert.Equal("x,y,z", scan.Properties["ColumnLayout"]);
        Assert.Equal("False", scan.Properties["HasHeader"]);

        var points = scan.ReadPoints().ToList();
        Assert.Equal(3, points.Count);
        Assert.Equal(0u, points[0].Index);
        Assert.Equal(1u, points[1].Index);
        Assert.Equal(2u, points[2].Index);
        AssertNear(new PclVector3(1, 2, 3), points[0].Position);
        AssertNear(new PclVector3(-4.5, 5.25, 6.125), points[1].Position);
        AssertNear(new PclVector3(7, 8, 9), points[2].Position);
    }

    [Theory]
    [InlineData(',', ',')]
    [InlineData(';', ';')]
    [InlineData('\t', '\t')]
    [InlineData(' ', ' ')]
    public void Xyz_Supports_Various_Delimiters(char delimiter, char expectedChar)
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticXyz(delimiter: delimiter);
        string filename = delimiter switch
        {
            ',' => "sample.csv",
            ';' => "sample.txt",
            '\t' => "sample.asc",
            _ => "sample.xyz",
        };

        using var doc = XyzDocument.Open(dir.Write(filename, bytes));
        Assert.Equal(expectedChar, doc.Delimiter);
        var points = doc.ReadAllPoints().ToList();
        Assert.Equal(3, points.Count);
        AssertNear(new PclVector3(1, 2, 3), points[0].Position);
        AssertNear(new PclVector3(-4.5, 5.25, 6.125), points[1].Position);
        AssertNear(new PclVector3(7, 8, 9), points[2].Position);
    }

    [Fact]
    public void Xyz_Parses_Header_Line_With_Normals()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticXyz(
            delimiter: ',',
            header: "x,y,z,nx,ny,nz",
            columns: 6);

        using var doc = XyzDocument.Open(dir.Write("normals.csv", bytes));
        Assert.True(doc.HasHeader);
        Assert.Equal(6, doc.Columns);
        Assert.Equal(',', doc.Delimiter);
        Assert.Equal("x,y,z,nx,ny,nz", doc.ColumnLayout);

        var scan = Assert.Single(doc.Scans);
        Assert.True(scan.HasNormals);
        Assert.False(scan.HasColors);
        Assert.False(scan.HasIntensities);

        var points = scan.ReadPoints().ToList();
        Assert.Equal(3, points.Count);
        Assert.NotNull(points[0].Normal);
        Assert.NotNull(points[1].Normal);
    }

    [Fact]
    public void Xyz_Parses_Header_Line_With_Intensity_And_Colors()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticXyz(
            delimiter: ';',
            header: "x;y;z;intensity;r;g;b",
            columns: 7);

        using var doc = XyzDocument.Open(dir.Write("colors.txt", bytes));
        Assert.True(doc.HasHeader);
        Assert.Equal(7, doc.Columns);
        Assert.Equal(';', doc.Delimiter);

        var scan = Assert.Single(doc.Scans);
        Assert.True(scan.HasColors);
        Assert.True(scan.HasIntensities);
        Assert.False(scan.HasNormals);

        var points = scan.ReadPoints().ToList();
        Assert.Equal(3, points.Count);
        Assert.NotNull(points[0].Intensity);
        Assert.NotNull(points[0].Color);
        Assert.Equal(10, points[0].Color!.Value.R);
        Assert.Equal(20, points[0].Color!.Value.G);
        Assert.Equal(30, points[0].Color!.Value.B);
    }

    [Theory]
    [InlineData(3, false, false, false)]
    [InlineData(4, false, true, false)]
    [InlineData(6, true, false, false)]
    [InlineData(7, true, true, false)]
    [InlineData(8, true, true, false)]
    [InlineData(9, true, false, true)]
    [InlineData(10, true, true, true)]
    public void Xyz_Column_Heuristics_For_Headerless_Files(int columns, bool hasColors, bool hasIntensities, bool hasNormals)
    {
        using var dir = new TempDirectory();
        // Use positive normal for color cases so heuristic chooses color for 6 and 7 cols
        var points = new List<XyzSamplePoint>
        {
            new(1, 2, 3, 500f, new PclColor(100, 150, 200), new PclVector3(0, 0, 1)),
            new(4, 5, 6, 750f, new PclColor(200, 220, 240), new PclVector3(0, 1, 0)),
        };

        byte[] bytes = SyntheticFixtures.CreateSyntheticXyz(points, columns: columns);
        using var doc = XyzDocument.Open(dir.Write("sample.xyz", bytes));
        var scan = Assert.Single(doc.Scans);

        Assert.Equal(hasColors, scan.HasColors);
        Assert.Equal(hasIntensities, scan.HasIntensities);
        Assert.Equal(hasNormals, scan.HasNormals);

        var readPts = scan.ReadPoints().ToList();
        Assert.Equal(2, readPts.Count);
    }

    [Fact]
    public void Xyz_Detects_Normals_In_Headerless_6_Columns_When_Negative()
    {
        using var dir = new TempDirectory();
        var normalPoints = new List<XyzSamplePoint>
        {
            new(1, 2, 3, Normal: new PclVector3(-0.3, -0.8, -0.5)),
            new(4, 5, 6, Normal: new PclVector3(-0.1, -0.9, 0.1)),
        };

        byte[] bytes = SyntheticFixtures.CreateSyntheticXyz(normalPoints, columns: 6);
        using var doc = XyzDocument.Open(dir.Write("sample.xyz", bytes));
        var scan = Assert.Single(doc.Scans);

        Assert.True(scan.HasNormals);
        Assert.False(scan.HasColors);

        var readPts = scan.ReadPoints().ToList();
        Assert.Equal(2, readPts.Count);
        Assert.NotNull(readPts[0].Normal);
    }

    [Fact]
    public void Xyz_Honors_TextColumns_Option()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticXyz(columns: 4);

        var options = new PclReadOptions { TextColumns = "x,y,z,scalar" };
        using var doc = XyzDocument.Open(dir.Write("sample.xyz", bytes), options);

        var scan = Assert.Single(doc.Scans);
        Assert.True(scan.HasIntensities);
        Assert.False(scan.HasColors);

        var points = scan.ReadPoints().ToList();
        Assert.Equal(3, points.Count);
        Assert.NotNull(points[0].Intensity);
    }

    [Fact]
    public void Xyz_Honors_TextDelimiter_Option()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticXyz(delimiter: ';');

        var options = new PclReadOptions { TextDelimiter = ';' };
        using var doc = XyzDocument.Open(dir.Write("sample.xyz", bytes), options);

        Assert.Equal(';', doc.Delimiter);
        var points = doc.ReadAllPoints().ToList();
        Assert.Equal(3, points.Count);
    }

    [Fact]
    public void Xyz_Honors_TextCoordinateScale_Option()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticXyz();

        var options = new PclReadOptions { TextCoordinateScale = 0.001 };
        using var doc = XyzDocument.Open(dir.Write("sample.xyz", bytes), options);

        Assert.Equal(0.001, doc.CoordinateScale);
        var points = doc.ReadAllPoints().ToList();
        Assert.Equal(3, points.Count);
        AssertNear(new PclVector3(0.001, 0.002, 0.003), points[0].Position);
    }

    [Fact]
    public void Xyz_Honors_TextCommentPrefix_And_Skips_Comments()
    {
        using var dir = new TempDirectory();
        var comments = new[] { "Generated by scanner", "Date: 2026-11-18", "Coordinate system: local" };
        byte[] bytes = SyntheticFixtures.CreateSyntheticXyz(comments: comments, commentPrefix: "# ");

        using var doc = XyzDocument.Open(dir.Write("comments.xyz", bytes));
        Assert.Equal(3, doc.TotalPoints);
        var points = doc.ReadAllPoints().ToList();
        Assert.Equal(3, points.Count);
    }

    [Fact]
    public void Xyz_Recognizes_Header_In_Comment_Line()
    {
        using var dir = new TempDirectory();
        var comments = new[] { "x,y,z,nx,ny,nz" };
        byte[] bytes = SyntheticFixtures.CreateSyntheticXyz(delimiter: ',', comments: comments, commentPrefix: "# ", columns: 6);

        using var doc = XyzDocument.Open(dir.Write("comment_header.csv", bytes));
        Assert.True(doc.HasHeader);
        Assert.True(doc.Scans[0].HasNormals);
        var points = doc.ReadAllPoints().ToList();
        Assert.Equal(3, points.Count);
    }

    [Fact]
    public void Xyz_Skips_Non_Finite_Coordinates()
    {
        using var dir = new TempDirectory();
        var points = new List<XyzSamplePoint>
        {
            new(1, 2, 3),
            new(double.NaN, 5, 6),
            new(7, double.PositiveInfinity, 9),
            new(10, 11, 12),
        };

        byte[] bytes = SyntheticFixtures.CreateSyntheticXyz(points);
        using var doc = XyzDocument.Open(dir.Write("nan.xyz", bytes));

        var readPts = doc.ReadAllPoints().ToList();
        Assert.Equal(2, readPts.Count);
        AssertNear(new PclVector3(1, 2, 3), readPts[0].Position);
        AssertNear(new PclVector3(10, 11, 12), readPts[1].Position);
    }

    [Fact]
    public void Xyz_Supports_MaxPoints_And_SubsampleStep()
    {
        using var dir = new TempDirectory();
        var points = Enumerable.Range(0, 10)
            .Select(i => new XyzSamplePoint(i, i * 2, i * 3))
            .ToList();

        byte[] bytes = SyntheticFixtures.CreateSyntheticXyz(points);
        using var doc = XyzDocument.Open(dir.Write("sample.xyz", bytes));

        var scan = Assert.Single(doc.Scans);
        var stepped = scan.ReadPoints(new PclReadOptions { SubsampleStep = 2 }).ToList();
        Assert.Equal(5, stepped.Count);
        AssertNear(new PclVector3(0, 0, 0), stepped[0].Position);
        AssertNear(new PclVector3(2, 4, 6), stepped[1].Position);

        var limited = scan.ReadPoints(new PclReadOptions { MaxPoints = 2 }).ToList();
        Assert.Equal(2, limited.Count);
    }

    [Fact]
    public void Xyz_Supports_ApplyTransform()
    {
        using var dir = new TempDirectory();
        using var doc = XyzDocument.Open(dir.Write("sample.xyz", SyntheticFixtures.CreateSyntheticXyz()));

        // Scan transform
        var transform = new PclTransform { Translation = new PclVector3(10, 20, 30) };
        var scan = (XyzScan)Assert.Single(doc.Scans);
        typeof(PclScan).GetProperty("Transform")!.SetValue(scan, transform);

        var points = scan.ReadPoints(new PclReadOptions { ApplyTransform = true }).ToList();
        AssertNear(new PclVector3(11, 22, 33), points[0].Position);

        var untransformed = scan.ReadPoints(new PclReadOptions { ApplyTransform = false }).ToList();
        AssertNear(new PclVector3(1, 2, 3), untransformed[0].Position);
    }

    [Fact]
    public void Xyz_Rejects_Corrupted_Points()
    {
        using var dir = new TempDirectory();
        // Mismatched column count
        string badColCount = "1.0 2.0 3.0\n4.0 5.0\n";
        using var doc1 = XyzDocument.Open(dir.WriteText("bad_col.xyz", badColCount));
        var scan1 = Assert.Single(doc1.Scans);
        Assert.Throws<InvalidDataException>(() => scan1.ReadPoints().ToList());

        // Invalid numeric values on first point line
        string badNumFirst = "1.0 abc 3.0\n4.0 5.0 6.0\n";
        Assert.Throws<InvalidDataException>(() => XyzDocument.Open(dir.WriteText("bad_num1.xyz", badNumFirst)));

        // Invalid numeric values on second point line
        string badNumSecond = "1.0 2.0 3.0\n4.0 abc 6.0\n";
        using var doc2 = XyzDocument.Open(dir.WriteText("bad_num2.xyz", badNumSecond));
        var scan2 = Assert.Single(doc2.Scans);
        Assert.Throws<InvalidDataException>(() => scan2.ReadPoints().ToList());
    }

    [Fact]
    public void Xyz_Rejects_Empty_And_Truncated_Files()
    {
        using var dir = new TempDirectory();
        Assert.Throws<InvalidDataException>(() => XyzDocument.Open(dir.Write("empty.xyz", [])));
        Assert.Throws<InvalidDataException>(() => XyzDocument.Open(dir.WriteText("comments_only.xyz", "# comment\n# comment 2\n")));
    }

    [Fact]
    public void Xyz_Rejects_Line_Exceeding_Max_Length()
    {
        using var dir = new TempDirectory();
        string longLine = "1 2 3 " + new string('9', (1 << 20) + 10) + "\n";
        Assert.Throws<InvalidDataException>(() =>
            XyzDocument.Open(dir.WriteText("long.xyz", longLine)));
    }

    [Fact]
    public void Xyz_Rejects_Unsupported_Column_Count_Without_Header()
    {
        using var dir = new TempDirectory();
        string fiveCols = "1 2 3 4 5\n6 7 8 9 10\n";
        Assert.Throws<NotSupportedException>(() =>
            XyzDocument.Open(dir.WriteText("five.xyz", fiveCols)));
    }

    [Fact]
    public void Xyz_Opens_Via_PclFile_Open()
    {
        using var dir = new TempDirectory();
        string path = dir.Write("sample.xyz", SyntheticFixtures.CreateSyntheticXyz());

        using var doc = PclFile.Open(path);
        Assert.Equal(PclFileKind.Xyz, doc.Kind);
        Assert.IsType<XyzDocument>(doc);
        Assert.Equal(3, doc.ReadAllPoints().Count());
    }

    // ---- Simple Binary Format (SBF) --------------------------------------------------------------------------

    [Fact]
    public void Sbf_Document_Properties_And_Metadata()
    {
        using var dir = new TempDirectory();
        var (hdr, data) = SyntheticFixtures.CreateSyntheticSbf();
        string sbfPath = dir.Write("sample.sbf", hdr);
        dir.Write("sample.sbf.data", data);

        using var doc = SbfDocument.Open(sbfPath);
        Assert.Equal(PclFileKind.Sbf, doc.Kind);
        Assert.Equal(3, doc.TotalPoints);
        Assert.Equal(1, doc.ScalarFieldCount);
        Assert.Equal(["Intensity"], doc.ScalarFields);
        Assert.True(doc.IsBigEndian);
        Assert.Equal(PclVector3.Zero, doc.GlobalShift);
        Assert.Equal(1.0, doc.GlobalScale);
        Assert.Equal("Simple Binary (SBF)", doc.Metadata["Format"]);
        Assert.Equal("BigEndian", doc.Metadata["Endianness"]);
        Assert.Equal("sample.sbf", doc.Metadata["CompanionFile"]);

        var scan = Assert.Single(doc.Scans);
        Assert.Equal("0", scan.Id);
        Assert.Equal("sample", scan.Name);
        Assert.Equal(3, scan.PointCount);
        Assert.True(scan.HasIntensities);
        Assert.False(scan.HasColors);
        Assert.False(scan.HasNormals);

        var points = scan.ReadPoints().ToList();
        Assert.Equal(3, points.Count);
        Assert.Equal(1.0, points[0].X, 4);
        Assert.Equal(2.0, points[0].Y, 4);
        Assert.Equal(3.0, points[0].Z, 4);
        Assert.Equal(100.0f, points[0].Intensity);
    }

    [Fact]
    public void Sbf_Opens_From_Data_Path_Directly()
    {
        using var dir = new TempDirectory();
        var (hdr, data) = SyntheticFixtures.CreateSyntheticSbf();
        dir.Write("sample.sbf", hdr);
        string dataPath = dir.Write("sample.sbf.data", data);

        using var doc = SbfDocument.Open(dataPath);
        Assert.Equal(PclFileKind.Sbf, doc.Kind);
        Assert.Equal(3, doc.TotalPoints);
        Assert.Equal(1, doc.ScalarFieldCount);

        var points = doc.ReadAllPoints().ToList();
        Assert.Equal(3, points.Count);
        Assert.Equal(100.0f, points[0].Intensity);
    }

    [Fact]
    public void Sbf_Opens_Standalone_Data_Without_Companion()
    {
        using var dir = new TempDirectory();
        var (_, data) = SyntheticFixtures.CreateSyntheticSbf();
        string dataPath = dir.Write("standalone.sbf.data", data);

        using var doc = SbfDocument.Open(dataPath);
        Assert.Equal(PclFileKind.Sbf, doc.Kind);
        Assert.Equal(3, doc.TotalPoints);
        Assert.Equal(1, doc.ScalarFieldCount);
        Assert.Null(doc.CompanionPath);

        var points = doc.ReadAllPoints().ToList();
        Assert.Equal(3, points.Count);
        Assert.Equal(1.0, points[0].X, 4);
    }

    [Fact]
    public void Sbf_ScalarFields_Mapped_To_Color()
    {
        using var dir = new TempDirectory();
        var pts = new[]
        {
            new SyntheticFixtures.SbfSamplePoint(1, 2, 3, [255f, 128f, 64f]),
            new SyntheticFixtures.SbfSamplePoint(4, 5, 6, [0f, 255f, 0f]),
        };
        var (hdr, data) = SyntheticFixtures.CreateSyntheticSbf(pts, sfNames: ["Red", "Green", "Blue"]);
        string sbfPath = dir.Write("color.sbf", hdr);
        dir.Write("color.sbf.data", data);

        using var doc = SbfDocument.Open(sbfPath);
        var scan = Assert.Single(doc.Scans);
        Assert.True(scan.HasColors);
        Assert.False(scan.HasIntensities);
        Assert.False(scan.HasNormals);

        var points = scan.ReadPoints().ToList();
        Assert.Equal(2, points.Count);
        Assert.Equal(new PclColor(255, 128, 64), points[0].Color);
        Assert.Equal(new PclColor(0, 255, 0), points[1].Color);
    }

    [Fact]
    public void Sbf_ScalarFields_Mapped_To_Normals()
    {
        using var dir = new TempDirectory();
        var pts = new[]
        {
            new SyntheticFixtures.SbfSamplePoint(1, 2, 3, [0f, 0f, 1f]),
            new SyntheticFixtures.SbfSamplePoint(4, 5, 6, [1f, 0f, 0f]),
        };
        var (hdr, data) = SyntheticFixtures.CreateSyntheticSbf(pts, sfNames: ["Nx", "Ny", "Nz"]);
        string sbfPath = dir.Write("normals.sbf", hdr);
        dir.Write("normals.sbf.data", data);

        using var doc = SbfDocument.Open(sbfPath);
        var scan = Assert.Single(doc.Scans);
        Assert.False(scan.HasColors);
        Assert.False(scan.HasIntensities);
        Assert.True(scan.HasNormals);

        var points = scan.ReadPoints().ToList();
        Assert.Equal(2, points.Count);
        AssertNear(new PclVector3(0, 0, 1), points[0].Normal!.Value);
        AssertNear(new PclVector3(1, 0, 0), points[1].Normal!.Value);
    }

    [Fact]
    public void Sbf_ScalarFields_Mapped_To_Intensity_Color_And_Normals()
    {
        using var dir = new TempDirectory();
        var pts = new[]
        {
            new SyntheticFixtures.SbfSamplePoint(1, 2, 3, [42.5f, 255f, 0f, 128f, 0f, 1f, 0f]),
        };
        var sfNames = new[] { "Intensity", "Red", "Green", "Blue", "Nx", "Ny", "Nz" };
        var (hdr, data) = SyntheticFixtures.CreateSyntheticSbf(pts, sfNames);
        string sbfPath = dir.Write("all.sbf", hdr);
        dir.Write("all.sbf.data", data);

        using var doc = SbfDocument.Open(sbfPath);
        var scan = Assert.Single(doc.Scans);
        Assert.True(scan.HasIntensities);
        Assert.True(scan.HasColors);
        Assert.True(scan.HasNormals);

        var points = scan.ReadPoints().ToList();
        var pt = Assert.Single(points);
        Assert.Equal(42.5f, pt.Intensity);
        Assert.Equal(new PclColor(255, 0, 128), pt.Color);
        AssertNear(new PclVector3(0, 1, 0), pt.Normal!.Value);
    }

    [Fact]
    public void Sbf_GlobalShift_And_GlobalScale_Transform()
    {
        using var dir = new TempDirectory();
        var pts = new[]
        {
            new SyntheticFixtures.SbfSamplePoint(2, 4, 6, [10f]),
        };
        var shift = new PclVector3(100.0, 200.0, 300.0);
        var (hdr, data) = SyntheticFixtures.CreateSyntheticSbf(pts, globalShift: shift, globalScale: 2.0);
        string sbfPath = dir.Write("shift.sbf", hdr);
        dir.Write("shift.sbf.data", data);

        using var doc = SbfDocument.Open(sbfPath);
        var scan = Assert.Single(doc.Scans);

        // With ApplyTransform: local point is scaled by 1/2.0 then shifted by -100, -200, -300
        var transformed = scan.ReadPoints(new PclReadOptions { ApplyTransform = true }).ToList();
        var ptT = Assert.Single(transformed);
        Assert.Equal(2.0 / 2.0 - 100.0, ptT.X, 4);
        Assert.Equal(4.0 / 2.0 - 200.0, ptT.Y, 4);
        Assert.Equal(6.0 / 2.0 - 300.0, ptT.Z, 4);

        // Without ApplyTransform: coordinates as stored
        var raw = scan.ReadPoints(new PclReadOptions { ApplyTransform = false }).ToList();
        var ptR = Assert.Single(raw);
        Assert.Equal(2.0, ptR.X, 4);
        Assert.Equal(4.0, ptR.Y, 4);
        Assert.Equal(6.0, ptR.Z, 4);
    }

    [Fact]
    public void Sbf_LittleEndian_Support()
    {
        using var dir = new TempDirectory();
        var (hdr, data) = SyntheticFixtures.CreateSyntheticSbf(isBigEndian: false);
        string sbfPath = dir.Write("le.sbf", hdr);
        dir.Write("le.sbf.data", data);

        using var doc = SbfDocument.Open(sbfPath);
        Assert.False(doc.IsBigEndian);
        Assert.Equal("LittleEndian", doc.Metadata["Endianness"]);

        var points = doc.ReadAllPoints().ToList();
        Assert.Equal(3, points.Count);
        Assert.Equal(1.0, points[0].X, 4);
        Assert.Equal(2.0, points[0].Y, 4);
        Assert.Equal(3.0, points[0].Z, 4);
        Assert.Equal(100.0f, points[0].Intensity);
    }

    [Fact]
    public void Sbf_Options_MaxPoints_And_SubsampleStep()
    {
        using var dir = new TempDirectory();
        var pts = new[]
        {
            new SyntheticFixtures.SbfSamplePoint(1, 1, 1),
            new SyntheticFixtures.SbfSamplePoint(2, 2, 2),
            new SyntheticFixtures.SbfSamplePoint(3, 3, 3),
            new SyntheticFixtures.SbfSamplePoint(4, 4, 4),
            new SyntheticFixtures.SbfSamplePoint(5, 5, 5),
        };
        var (hdr, data) = SyntheticFixtures.CreateSyntheticSbf(pts);
        string sbfPath = dir.Write("sub.sbf", hdr);
        dir.Write("sub.sbf.data", data);

        using var doc = SbfDocument.Open(sbfPath);

        // MaxPoints
        var limited = doc.ReadAllPoints(new PclReadOptions { MaxPoints = 2 }).ToList();
        Assert.Equal(2, limited.Count);
        Assert.Equal(1.0, limited[0].X, 4);
        Assert.Equal(2.0, limited[1].X, 4);

        // SubsampleStep
        var stepped = doc.ReadAllPoints(new PclReadOptions { SubsampleStep = 2 }).ToList();
        Assert.Equal(3, stepped.Count);
        Assert.Equal(1.0, stepped[0].X, 4);
        Assert.Equal(3.0, stepped[1].X, 4);
        Assert.Equal(5.0, stepped[2].X, 4);
    }

    [Fact]
    public void Sbf_Skips_NonFinite_Coordinates()
    {
        using var dir = new TempDirectory();
        var pts = new[]
        {
            new SyntheticFixtures.SbfSamplePoint(1, 2, 3),
            new SyntheticFixtures.SbfSamplePoint(double.NaN, 5, 6),
            new SyntheticFixtures.SbfSamplePoint(7, double.PositiveInfinity, 9),
            new SyntheticFixtures.SbfSamplePoint(10, 11, 12),
        };
        var (hdr, data) = SyntheticFixtures.CreateSyntheticSbf(pts);
        string sbfPath = dir.Write("nan.sbf", hdr);
        dir.Write("nan.sbf.data", data);

        using var doc = SbfDocument.Open(sbfPath);
        var points = doc.ReadAllPoints().ToList();
        Assert.Equal(2, points.Count);
        Assert.Equal(1.0, points[0].X, 4);
        Assert.Equal(10.0, points[1].X, 4);
    }

    [Fact]
    public void Sbf_Rejects_Missing_Companion_Data()
    {
        using var dir = new TempDirectory();
        var (hdr, _) = SyntheticFixtures.CreateSyntheticSbf();
        string sbfPath = dir.Write("nodata.sbf", hdr);

        Assert.Throws<FileNotFoundException>(() => SbfDocument.Open(sbfPath));
    }

    [Fact]
    public void Sbf_Rejects_Bad_Magic()
    {
        using var dir = new TempDirectory();
        var (hdr, data) = SyntheticFixtures.CreateSyntheticSbf();
        data[0] = 0x00;
        data[1] = 0x00;
        string sbfPath = dir.Write("badmagic.sbf", hdr);
        dir.Write("badmagic.sbf.data", data);

        Assert.Throws<InvalidDataException>(() => SbfDocument.Open(sbfPath));
    }

    [Fact]
    public void Sbf_Rejects_Truncated_Binary_Header()
    {
        using var dir = new TempDirectory();
        var (hdr, _) = SyntheticFixtures.CreateSyntheticSbf();
        string sbfPath = dir.Write("trunc_hdr.sbf", hdr);
        dir.Write("trunc_hdr.sbf.data", new byte[32]);

        Assert.Throws<InvalidDataException>(() => SbfDocument.Open(sbfPath));
    }

    [Fact]
    public void Sbf_Rejects_Truncated_Data_Points()
    {
        using var dir = new TempDirectory();
        var (hdr, data) = SyntheticFixtures.CreateSyntheticSbf();
        // Truncate last few bytes
        byte[] trunc = data[..^8];
        string sbfPath = dir.Write("trunc_pts.sbf", hdr);
        dir.Write("trunc_pts.sbf.data", trunc);

        Assert.Throws<InvalidDataException>(() => SbfDocument.Open(sbfPath));
    }

    [Fact]
    public void Sbf_Rejects_Oversized_File_Length()
    {
        using var dir = new TempDirectory();
        var (hdr, data) = SyntheticFixtures.CreateSyntheticSbf();
        // Append extra garbage bytes
        byte[] oversized = new byte[data.Length + 10];
        Array.Copy(data, oversized, data.Length);
        string sbfPath = dir.Write("oversized.sbf", hdr);
        dir.Write("oversized.sbf.data", oversized);

        Assert.Throws<InvalidDataException>(() => SbfDocument.Open(sbfPath));
    }

    [Fact]
    public void Sbf_Rejects_Mismatched_Companion_Points()
    {
        using var dir = new TempDirectory();
        var (_, data) = SyntheticFixtures.CreateSyntheticSbf();
        string badHdr = "[SBF]\nPoints=999\nSFCount=1\nSF1=Intensity\n";
        string sbfPath = dir.WriteText("mismatch_pts.sbf", badHdr);
        dir.Write("mismatch_pts.sbf.data", data);

        Assert.Throws<InvalidDataException>(() => SbfDocument.Open(sbfPath));
    }

    [Fact]
    public void Sbf_Rejects_Mismatched_Companion_SFCount()
    {
        using var dir = new TempDirectory();
        var (_, data) = SyntheticFixtures.CreateSyntheticSbf();
        string badHdr = "[SBF]\nPoints=3\nSFCount=5\nSF1=Intensity\n";
        string sbfPath = dir.WriteText("mismatch_sf.sbf", badHdr);
        dir.Write("mismatch_sf.sbf.data", data);

        Assert.Throws<InvalidDataException>(() => SbfDocument.Open(sbfPath));
    }

    [Fact]
    public void Sbf_Rejects_Invalid_Companion_Header()
    {
        using var dir = new TempDirectory();
        var (_, data) = SyntheticFixtures.CreateSyntheticSbf();
        string badHdr = "NOT SBF SECTION\nPoints=3\n";
        string sbfPath = dir.WriteText("bad_header.sbf", badHdr);
        dir.Write("bad_header.sbf.data", data);

        Assert.Throws<InvalidDataException>(() => SbfDocument.Open(sbfPath));
    }

    [Fact]
    public void Sbf_Rejects_Excessive_Scalar_Fields()
    {
        using var dir = new TempDirectory();
        byte[] data = new byte[64];
        data[0] = 0x2A;
        data[1] = 0x2A;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(2, 8), 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(10, 2), 2000);
        string dataPath = dir.Write("excessive.sbf.data", data);

        Assert.Throws<NotSupportedException>(() => SbfDocument.Open(dataPath));
    }

    [Fact]
    public void Sbf_Opens_Via_PclFile_Open()
    {
        using var dir = new TempDirectory();
        var (hdr, data) = SyntheticFixtures.CreateSyntheticSbf();
        string sbfPath = dir.Write("sample.sbf", hdr);
        dir.Write("sample.sbf.data", data);

        using var doc = PclFile.Open(sbfPath);
        Assert.Equal(PclFileKind.Sbf, doc.Kind);
        Assert.IsType<SbfDocument>(doc);
        Assert.Equal(3, doc.ReadAllPoints().Count());
    }

    // ==============================================================================================================
    // 3D Tiles (*.pnts, *.glb, tileset.json)
    // ==============================================================================================================

    [Fact]
    public void Pnts_Reads_Basic_Positions_And_Metadata()
    {
        using var dir = new TempDirectory();
        var pts = new[]
        {
            new PntsSamplePoint(1.0, 2.0, 3.0),
            new PntsSamplePoint(4.0, 5.0, 6.0),
            new PntsSamplePoint(7.0, 8.0, 9.0),
        };
        byte[] pntsData = SyntheticFixtures.CreateSyntheticPnts(pts);
        string path = dir.Write("points.pnts", pntsData);

        using var doc = Tiles3dDocument.Open(path);
        Assert.Equal(PclFileKind.Tiles3d, doc.Kind);
        Assert.Equal("3D Tiles Point Cloud (PNTS)", doc.Metadata["Format"]);
        Assert.Single(doc.Scans);
        Assert.Equal(3, doc.TotalPoints);

        var points = doc.ReadAllPoints().ToList();
        Assert.Equal(3, points.Count);
        Assert.Equal(1.0, points[0].X, 4);
        Assert.Equal(2.0, points[0].Y, 4);
        Assert.Equal(3.0, points[0].Z, 4);
        Assert.Equal(4.0, points[1].X, 4);
        Assert.Equal(7.0, points[2].X, 4);
    }

    [Fact]
    public void Pnts_Reads_Rgb_Colors()
    {
        using var dir = new TempDirectory();
        var pts = new[]
        {
            new PntsSamplePoint(1, 2, 3, Color: new PclColor(255, 0, 0, 255)),
            new PntsSamplePoint(4, 5, 6, Color: new PclColor(0, 255, 0, 255)),
            new PntsSamplePoint(7, 8, 9, Color: new PclColor(0, 0, 255, 255)),
        };
        byte[] pntsData = SyntheticFixtures.CreateSyntheticPnts(pts, colorFormat: "RGB");
        string path = dir.Write("rgb.pnts", pntsData);

        using var doc = Tiles3dDocument.Open(path);
        Assert.True(doc.Scans[0].HasColors);
        var points = doc.ReadAllPoints().ToList();
        Assert.Equal(new PclColor(255, 0, 0, 255), points[0].Color);
        Assert.Equal(new PclColor(0, 255, 0, 255), points[1].Color);
        Assert.Equal(new PclColor(0, 0, 255, 255), points[2].Color);
    }

    [Fact]
    public void Pnts_Reads_Rgba_Colors()
    {
        using var dir = new TempDirectory();
        var pts = new[]
        {
            new PntsSamplePoint(1, 2, 3, Color: new PclColor(10, 20, 30, 40)),
            new PntsSamplePoint(4, 5, 6, Color: new PclColor(50, 60, 70, 80)),
        };
        byte[] pntsData = SyntheticFixtures.CreateSyntheticPnts(pts, colorFormat: "RGBA");
        string path = dir.Write("rgba.pnts", pntsData);

        using var doc = Tiles3dDocument.Open(path);
        Assert.True(doc.Scans[0].HasColors);
        var points = doc.ReadAllPoints().ToList();
        Assert.Equal(new PclColor(10, 20, 30, 40), points[0].Color);
        Assert.Equal(new PclColor(50, 60, 70, 80), points[1].Color);
    }

    [Fact]
    public void Pnts_Reads_Rgb565_Colors()
    {
        using var dir = new TempDirectory();
        var pts = new[]
        {
            new PntsSamplePoint(1, 2, 3, Color: new PclColor(255, 0, 0, 255)),
            new PntsSamplePoint(4, 5, 6, Color: new PclColor(0, 255, 0, 255)),
        };
        byte[] pntsData = SyntheticFixtures.CreateSyntheticPnts(pts, colorFormat: "RGB565");
        string path = dir.Write("rgb565.pnts", pntsData);

        using var doc = Tiles3dDocument.Open(path);
        Assert.True(doc.Scans[0].HasColors);
        var points = doc.ReadAllPoints().ToList();
        Assert.NotNull(points[0].Color);
        Assert.NotNull(points[1].Color);
        // Red channel should be near 255
        Assert.True(points[0].Color!.Value.R > 240);
        // Green channel should be near 255
        Assert.True(points[1].Color!.Value.G > 240);
    }

    [Fact]
    public void Pnts_Reads_Constant_Rgba()
    {
        using var dir = new TempDirectory();
        var pts = new[]
        {
            new PntsSamplePoint(1, 2, 3),
            new PntsSamplePoint(4, 5, 6),
        };
        var expectedColor = new PclColor(111, 222, 123, 255);
        byte[] pntsData = SyntheticFixtures.CreateSyntheticPnts(pts, colorFormat: "CONSTANT_RGBA", constantColor: expectedColor);
        string path = dir.Write("const.pnts", pntsData);

        using var doc = Tiles3dDocument.Open(path);
        Assert.True(doc.Scans[0].HasColors);
        var points = doc.ReadAllPoints().ToList();
        Assert.Equal(expectedColor, points[0].Color);
        Assert.Equal(expectedColor, points[1].Color);
    }

    [Fact]
    public void Pnts_Reads_Normals_Vec3Float()
    {
        using var dir = new TempDirectory();
        var pts = new[]
        {
            new PntsSamplePoint(1, 2, 3, Normal: new PclVector3(0, 0, 1)),
            new PntsSamplePoint(4, 5, 6, Normal: new PclVector3(1, 0, 0)),
        };
        byte[] pntsData = SyntheticFixtures.CreateSyntheticPnts(pts, normalFormat: "NORMAL");
        string path = dir.Write("norm.pnts", pntsData);

        using var doc = Tiles3dDocument.Open(path);
        Assert.True(doc.Scans[0].HasNormals);
        var points = doc.ReadAllPoints().ToList();
        Assert.NotNull(points[0].Normal);
        Assert.Equal(0.0, points[0].Normal!.Value.X, 4);
        Assert.Equal(0.0, points[0].Normal!.Value.Y, 4);
        Assert.Equal(1.0, points[0].Normal!.Value.Z, 4);
    }

    [Fact]
    public void Pnts_Reads_Normals_Oct16p()
    {
        using var dir = new TempDirectory();
        var pts = new[]
        {
            new PntsSamplePoint(1, 2, 3, Normal: new PclVector3(0, 0, 1)),
            new PntsSamplePoint(4, 5, 6, Normal: new PclVector3(1, 0, 0)),
            new PntsSamplePoint(7, 8, 9, Normal: new PclVector3(0, 1, 0)),
        };
        byte[] pntsData = SyntheticFixtures.CreateSyntheticPnts(pts, normalFormat: "NORMAL_OCT16P");
        string path = dir.Write("oct16.pnts", pntsData);

        using var doc = Tiles3dDocument.Open(path);
        Assert.True(doc.Scans[0].HasNormals);
        var points = doc.ReadAllPoints().ToList();
        Assert.NotNull(points[0].Normal);
        Assert.True(points[0].Normal!.Value.Z > 0.95);
        Assert.True(points[1].Normal!.Value.X > 0.95);
        Assert.True(points[2].Normal!.Value.Y > 0.95);
    }

    [Fact]
    public void Pnts_Reads_Position_Quantized()
    {
        using var dir = new TempDirectory();
        var offset = new PclVector3(10.0, 20.0, 30.0);
        var scale = new PclVector3(100.0, 100.0, 100.0);
        var pts = new[]
        {
            new PntsSamplePoint(10.0, 20.0, 30.0), // at offset
            new PntsSamplePoint(110.0, 120.0, 130.0), // at offset + scale
            new PntsSamplePoint(60.0, 70.0, 80.0), // center
        };
        byte[] pntsData = SyntheticFixtures.CreateSyntheticPnts(pts, isQuantized: true, qOffset: offset, qScale: scale);
        string path = dir.Write("quantized.pnts", pntsData);

        using var doc = Tiles3dDocument.Open(path);
        var scan = (PntsScan)doc.Scans[0];
        Assert.True(scan.IsQuantized);
        Assert.Equal(offset.X, scan.Bounds.Min.X, 4);
        Assert.Equal(offset.X + scale.X, scan.Bounds.Max.X, 4);

        var points = doc.ReadAllPoints().ToList();
        Assert.Equal(3, points.Count);
        Assert.Equal(10.0, points[0].X, 1);
        Assert.Equal(20.0, points[0].Y, 1);
        Assert.Equal(30.0, points[0].Z, 1);
        Assert.Equal(110.0, points[1].X, 1);
        Assert.Equal(60.0, points[2].X, 1);
    }

    [Fact]
    public void Pnts_Reads_RtcCenter_With_And_Without_ApplyTransform()
    {
        using var dir = new TempDirectory();
        var rtc = new PclVector3(1000.0, 2000.0, 3000.0);
        var pts = new[]
        {
            new PntsSamplePoint(1.0, 2.0, 3.0),
            new PntsSamplePoint(4.0, 5.0, 6.0),
        };
        byte[] pntsData = SyntheticFixtures.CreateSyntheticPnts(pts, rtcCenter: rtc);
        string path = dir.Write("rtc.pnts", pntsData);

        using var doc = Tiles3dDocument.Open(path);
        var scan = (PntsScan)doc.Scans[0];
        Assert.Equal(rtc, scan.RtcCenter);

        // With ApplyTransform: coordinates are in world space (local + RTC)
        var transformed = doc.ReadAllPoints(new PclReadOptions { ApplyTransform = true }).ToList();
        Assert.Equal(1001.0, transformed[0].X, 4);
        Assert.Equal(2002.0, transformed[0].Y, 4);
        Assert.Equal(3003.0, transformed[0].Z, 4);

        // Without ApplyTransform: coordinates are in local space
        var raw = doc.ReadAllPoints(new PclReadOptions { ApplyTransform = false }).ToList();
        Assert.Equal(1.0, raw[0].X, 4);
        Assert.Equal(2.0, raw[0].Y, 4);
        Assert.Equal(3.0, raw[0].Z, 4);
    }

    [Fact]
    public void Pnts_Reads_BatchTable_Intensity_Binary_And_Json()
    {
        using var dir = new TempDirectory();
        var pts = new[]
        {
            new PntsSamplePoint(1, 2, 3, Intensity: 42.5f),
            new PntsSamplePoint(4, 5, 6, Intensity: 88.0f),
        };

        // Binary batch table
        byte[] pntsBin = SyntheticFixtures.CreateSyntheticPnts(pts, hasIntensity: true, intensityBinary: true);
        string pathBin = dir.Write("bin_int.pnts", pntsBin);
        using (var docBin = Tiles3dDocument.Open(pathBin))
        {
            Assert.True(docBin.Scans[0].HasIntensities);
            var points = docBin.ReadAllPoints().ToList();
            Assert.Equal(42.5f, points[0].Intensity);
            Assert.Equal(88.0f, points[1].Intensity);
        }

        // JSON batch table array
        byte[] pntsJson = SyntheticFixtures.CreateSyntheticPnts(pts, hasIntensity: true, intensityBinary: false);
        string pathJson = dir.Write("json_int.pnts", pntsJson);
        using (var docJson = Tiles3dDocument.Open(pathJson))
        {
            Assert.True(docJson.Scans[0].HasIntensities);
            var points = docJson.ReadAllPoints().ToList();
            Assert.Equal(42.5f, points[0].Intensity);
            Assert.Equal(88.0f, points[1].Intensity);
        }
    }

    [Fact]
    public void Pnts_Reads_BatchTable_Classification()
    {
        using var dir = new TempDirectory();
        var pts = new[]
        {
            new PntsSamplePoint(1, 2, 3, Classification: 2), // Ground
            new PntsSamplePoint(4, 5, 6, Classification: 5), // High vegetation
        };
        byte[] pntsData = SyntheticFixtures.CreateSyntheticPnts(pts, hasClassification: true);
        string path = dir.Write("class.pnts", pntsData);

        using var doc = Tiles3dDocument.Open(path);
        var points = doc.ReadAllPoints().ToList();
        Assert.Equal((byte)2, points[0].Classification);
        Assert.Equal((byte)5, points[1].Classification);
    }

    [Fact]
    public void Pnts_Options_MaxPoints_And_SubsampleStep()
    {
        using var dir = new TempDirectory();
        var pts = new[]
        {
            new PntsSamplePoint(1, 1, 1),
            new PntsSamplePoint(2, 2, 2),
            new PntsSamplePoint(3, 3, 3),
            new PntsSamplePoint(4, 4, 4),
            new PntsSamplePoint(5, 5, 5),
        };
        byte[] pntsData = SyntheticFixtures.CreateSyntheticPnts(pts);
        string path = dir.Write("sub.pnts", pntsData);

        using var doc = Tiles3dDocument.Open(path);

        // MaxPoints
        var limited = doc.ReadAllPoints(new PclReadOptions { MaxPoints = 2 }).ToList();
        Assert.Equal(2, limited.Count);
        Assert.Equal(1.0, limited[0].X, 4);
        Assert.Equal(2.0, limited[1].X, 4);

        // SubsampleStep
        var stepped = doc.ReadAllPoints(new PclReadOptions { SubsampleStep = 2 }).ToList();
        Assert.Equal(3, stepped.Count);
        Assert.Equal(1.0, stepped[0].X, 4);
        Assert.Equal(3.0, stepped[1].X, 4);
        Assert.Equal(5.0, stepped[2].X, 4);
    }

    [Fact]
    public void Pnts_Skips_NonFinite_Coordinates()
    {
        using var dir = new TempDirectory();
        var pts = new[]
        {
            new PntsSamplePoint(1, 2, 3),
            new PntsSamplePoint(double.NaN, 5, 6),
            new PntsSamplePoint(7, double.PositiveInfinity, 9),
            new PntsSamplePoint(10, 11, 12),
        };
        byte[] pntsData = SyntheticFixtures.CreateSyntheticPnts(pts);
        string path = dir.Write("nan.pnts", pntsData);

        using var doc = Tiles3dDocument.Open(path);
        var points = doc.ReadAllPoints().ToList();
        Assert.Equal(2, points.Count);
        Assert.Equal(1.0, points[0].X, 4);
        Assert.Equal(10.0, points[1].X, 4);
    }

    [Fact]
    public void Pnts_Rejects_Bad_Magic()
    {
        using var dir = new TempDirectory();
        byte[] data = SyntheticFixtures.CreateSyntheticPnts([new(1, 2, 3)]);
        data[0] = (byte)'X';
        string path = dir.Write("badmagic.pnts", data);

        Assert.Throws<InvalidDataException>(() => Tiles3dDocument.Open(path));
    }

    [Fact]
    public void Pnts_Rejects_Unsupported_Version()
    {
        using var dir = new TempDirectory();
        byte[] data = SyntheticFixtures.CreateSyntheticPnts([new(1, 2, 3)]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), 2); // version 2
        string path = dir.Write("badversion.pnts", data);

        Assert.Throws<NotSupportedException>(() => Tiles3dDocument.Open(path));
    }

    [Fact]
    public void Pnts_Rejects_Mismatched_ByteLength()
    {
        using var dir = new TempDirectory();
        byte[] data = SyntheticFixtures.CreateSyntheticPnts([new(1, 2, 3)]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), 99999);
        string path = dir.Write("badlen.pnts", data);

        Assert.Throws<InvalidDataException>(() => Tiles3dDocument.Open(path));
    }

    [Fact]
    public void Pnts_Rejects_Truncated_Binary_Data()
    {
        using var dir = new TempDirectory();
        byte[] data = SyntheticFixtures.CreateSyntheticPnts([new(1, 2, 3), new(4, 5, 6)]);
        byte[] trunc = data[..^6];
        string path = dir.Write("trunc.pnts", trunc);

        Assert.Throws<InvalidDataException>(() => Tiles3dDocument.Open(path));
    }

    [Fact]
    public void Glb_Reads_Points_Mode0()
    {
        using var dir = new TempDirectory();
        var pts = new[]
        {
            new PntsSamplePoint(1.0, 2.0, 3.0),
            new PntsSamplePoint(4.0, 5.0, 6.0),
            new PntsSamplePoint(7.0, 8.0, 9.0),
        };
        byte[] glbData = SyntheticFixtures.CreateSyntheticGlbPoints(pts);
        string path = dir.Write("points.glb", glbData);

        using var doc = Tiles3dDocument.Open(path);
        Assert.Equal(PclFileKind.Tiles3d, doc.Kind);
        Assert.Equal("3D Tiles Point Cloud (GLB)", doc.Metadata["Format"]);
        Assert.Single(doc.Scans);
        Assert.Equal(3, doc.TotalPoints);

        var points = doc.ReadAllPoints().ToList();
        Assert.Equal(3, points.Count);
        Assert.Equal(1.0, points[0].X, 4);
        Assert.Equal(2.0, points[0].Y, 4);
        Assert.Equal(3.0, points[0].Z, 4);
    }

    [Fact]
    public void Glb_Reads_Colors_Ubyte_Ushort_Float()
    {
        using var dir = new TempDirectory();
        var pts = new[]
        {
            new PntsSamplePoint(1, 2, 3, Color: new PclColor(200, 100, 50, 255)),
        };

        // UBYTE
        byte[] glbUbyte = SyntheticFixtures.CreateSyntheticGlbPoints(pts, hasColors: true, colorComponentType: 5121);
        string pathUbyte = dir.Write("ubyte.glb", glbUbyte);
        using (var doc = Tiles3dDocument.Open(pathUbyte))
        {
            Assert.True(doc.Scans[0].HasColors);
            var p = doc.ReadAllPoints().First();
            Assert.Equal(new PclColor(200, 100, 50, 255), p.Color);
        }

        // USHORT
        byte[] glbUshort = SyntheticFixtures.CreateSyntheticGlbPoints(pts, hasColors: true, colorComponentType: 5123);
        string pathUshort = dir.Write("ushort.glb", glbUshort);
        using (var doc = Tiles3dDocument.Open(pathUshort))
        {
            Assert.True(doc.Scans[0].HasColors);
            var p = doc.ReadAllPoints().First();
            Assert.NotNull(p.Color);
            Assert.True(Math.Abs(p.Color!.Value.R - 200) <= 1);
        }

        // FLOAT
        byte[] glbFloat = SyntheticFixtures.CreateSyntheticGlbPoints(pts, hasColors: true, colorComponentType: 5126);
        string pathFloat = dir.Write("float.glb", glbFloat);
        using (var doc = Tiles3dDocument.Open(pathFloat))
        {
            Assert.True(doc.Scans[0].HasColors);
            var p = doc.ReadAllPoints().First();
            Assert.NotNull(p.Color);
            Assert.True(Math.Abs(p.Color!.Value.R - 200) <= 1);
        }
    }

    [Fact]
    public void Glb_Reads_Normals_And_Intensity()
    {
        using var dir = new TempDirectory();
        var pts = new[]
        {
            new PntsSamplePoint(1, 2, 3, Normal: new PclVector3(0, 0, 1), Intensity: 77.0f),
        };
        byte[] glbData = SyntheticFixtures.CreateSyntheticGlbPoints(pts, hasNormals: true, hasIntensity: true);
        string path = dir.Write("full.glb", glbData);

        using var doc = Tiles3dDocument.Open(path);
        Assert.True(doc.Scans[0].HasNormals);
        Assert.True(doc.Scans[0].HasIntensities);

        var p = doc.ReadAllPoints().First();
        Assert.Equal(1.0, p.Normal!.Value.Z, 4);
        Assert.Equal(77.0f, p.Intensity);
    }

    [Fact]
    public void Glb_Applies_Node_Matrix()
    {
        using var dir = new TempDirectory();
        var pts = new[]
        {
            new PntsSamplePoint(1.0, 2.0, 3.0),
        };
        // 4x4 translation column-major matrix: translate by (10, 20, 30)
        double[] colMajor = [
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            10, 20, 30, 1
        ];
        byte[] glbData = SyntheticFixtures.CreateSyntheticGlbPoints(pts, nodeMatrixColMajor: colMajor);
        string path = dir.Write("matrix.glb", glbData);

        using var doc = Tiles3dDocument.Open(path);
        var transformed = doc.ReadAllPoints(new PclReadOptions { ApplyTransform = true }).First();
        Assert.Equal(11.0, transformed.X, 4);
        Assert.Equal(22.0, transformed.Y, 4);
        Assert.Equal(33.0, transformed.Z, 4);

        var raw = doc.ReadAllPoints(new PclReadOptions { ApplyTransform = false }).First();
        Assert.Equal(1.0, raw.X, 4);
        Assert.Equal(2.0, raw.Y, 4);
        Assert.Equal(3.0, raw.Z, 4);
    }

    [Fact]
    public void Glb_Rejects_Truncated_Bin()
    {
        using var dir = new TempDirectory();
        byte[] glbData = SyntheticFixtures.CreateSyntheticGlbPoints([new(1, 2, 3)]);
        byte[] trunc = glbData[..^8];
        string path = dir.Write("trunc.glb", trunc);

        Assert.Throws<InvalidDataException>(() => Tiles3dDocument.Open(path));
    }

    [Fact]
    public void TilesetJson_Reads_Hierarchy_And_Applies_Transforms()
    {
        using var dir = new TempDirectory();
        var pts = new[]
        {
            new PntsSamplePoint(1.0, 2.0, 3.0),
        };
        byte[] pntsData = SyntheticFixtures.CreateSyntheticPnts(pts);
        dir.Write("tile.pnts", pntsData);

        // 4x4 translation column-major matrix: translate by (100, 200, 300)
        double[] colMajor = [
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            100, 200, 300, 1
        ];
        string tilesetJson = SyntheticFixtures.CreateSyntheticTilesetJson("tile.pnts", transformColMajor: colMajor);
        string tilesetPath = dir.WriteText("tileset.json", tilesetJson);

        using var doc = PclFile.Open(tilesetPath);
        Assert.Equal(PclFileKind.Tiles3d, doc.Kind);
        Assert.IsType<Tiles3dDocument>(doc);
        Assert.Single(doc.Scans);

        var transformed = doc.ReadAllPoints(new PclReadOptions { ApplyTransform = true }).First();
        Assert.Equal(101.0, transformed.X, 4);
        Assert.Equal(202.0, transformed.Y, 4);
        Assert.Equal(303.0, transformed.Z, 4);
    }

    [Fact]
    public void TilesetJson_Opens_From_Directory()
    {
        using var dir = new TempDirectory();
        byte[] pntsData = SyntheticFixtures.CreateSyntheticPnts([new(1, 2, 3)]);
        dir.Write("tile.pnts", pntsData);
        dir.WriteText("tileset.json", SyntheticFixtures.CreateSyntheticTilesetJson("tile.pnts"));

        using var doc = PclFile.Open(dir.Root);
        Assert.Equal(PclFileKind.Tiles3d, doc.Kind);
        Assert.Single(doc.Scans);
        Assert.Equal(1, doc.TotalPointCount);
    }

    [Fact]
    public void TilesetJson_Handles_Missing_Tile_With_Warning()
    {
        using var dir = new TempDirectory();
        string tilesetJson = SyntheticFixtures.CreateSyntheticTilesetJson("nonexistent.pnts");
        string tilesetPath = dir.WriteText("tileset.json", tilesetJson);

        using var doc = Tiles3dDocument.Open(tilesetPath);
        Assert.NotEmpty(doc.Warnings);
        Assert.Empty(doc.Scans);
    }

    [Fact]
    public void TilesetJson_Rejects_Corrupt_Json()
    {
        using var dir = new TempDirectory();
        string tilesetPath = dir.WriteText("tileset.json", "{ not valid json ... ");

        Assert.Throws<InvalidDataException>(() => Tiles3dDocument.Open(tilesetPath));
    }

    // ---- Potree 1.x & 2.x tests --------------------------------------------------------------------------

    [Fact]
    public void Potree1x_Reads_Basic_Dataset()
    {
        using var dir = new TempDirectory();
        var points = new PotreeSamplePoint[]
        {
            new(10.1, 20.2, 30.3, new PclColor(255, 0, 0, 255), 100, new PclVector3(0, 0, 1), 2),
            new(11.1, 21.2, 31.3, new PclColor(0, 255, 0, 255), 200, new PclVector3(0, 1, 0), 3),
            new(12.1, 22.2, 32.3, new PclColor(0, 0, 255, 255), 300, new PclVector3(1, 0, 0), 4),
        };

        var scale = new PclVector3(0.001, 0.001, 0.001);
        var offset = new PclVector3(10.0, 20.0, 30.0);
        var (cloudJs, rBin, rHrc) = SyntheticFixtures.CreateSyntheticPotree1x(points, scale, offset);

        string cloudPath = dir.WriteText("cloud.js", cloudJs);
        string dataR = Path.Combine(dir.Root, "data", "r");
        Directory.CreateDirectory(dataR);
        File.WriteAllBytes(Path.Combine(dataR, "r.bin"), rBin);
        File.WriteAllBytes(Path.Combine(dataR, "r.hrc"), rHrc);

        using var doc = PotreeDocument.Open(cloudPath);
        Assert.Equal(PclFileKind.Potree, doc.Kind);
        Assert.Equal("1.7", doc.PotreeVersion);
        Assert.Equal(3, doc.TotalPoints);
        Assert.Single(doc.Scans);

        var scan = doc.Scans[0];
        Assert.Equal(3, scan.PointCount);
        Assert.True(scan.HasColors);
        Assert.True(scan.HasNormals);
        Assert.True(scan.HasIntensities);

        var pts = scan.ReadPoints().ToList();
        Assert.Equal(3, pts.Count);

        Assert.Equal(10.1, pts[0].Position.X, 2);
        Assert.Equal(20.2, pts[0].Position.Y, 2);
        Assert.Equal(30.3, pts[0].Position.Z, 2);
        Assert.Equal(new PclColor(255, 0, 0, 255), pts[0].Color);
        Assert.Equal(100f, pts[0].Intensity);
        Assert.Equal((byte)2, pts[0].Classification);
        Assert.NotNull(pts[0].Normal);
        Assert.True(Math.Abs(pts[0].Normal!.Value.Z - 1.0) < 0.05);

        Assert.Equal(11.1, pts[1].Position.X, 2);
        Assert.Equal(new PclColor(0, 255, 0, 255), pts[1].Color);
        Assert.Equal(200f, pts[1].Intensity);
        Assert.Equal((byte)3, pts[1].Classification);
        Assert.NotNull(pts[1].Normal);
        Assert.True(Math.Abs(pts[1].Normal!.Value.Y - 1.0) < 0.05);
    }

    [Fact]
    public void Potree1x_Reads_Oct16_Normals()
    {
        using var dir = new TempDirectory();
        var points = new PotreeSamplePoint[]
        {
            new(1.0, 2.0, 3.0, Normal: new PclVector3(0, 0, 1)),
            new(4.0, 5.0, 6.0, Normal: new PclVector3(1, 0, 0)),
        };

        var scale = new PclVector3(0.01, 0.01, 0.01);
        var offset = PclVector3.Zero;
        var (cloudJs, rBin, rHrc) = SyntheticFixtures.CreateSyntheticPotree1x(
            points, scale, offset, normalType: "NORMAL_OCT16");

        string cloudPath = dir.WriteText("cloud.js", cloudJs);
        string dataR = Path.Combine(dir.Root, "data", "r");
        Directory.CreateDirectory(dataR);
        File.WriteAllBytes(Path.Combine(dataR, "r.bin"), rBin);

        using var doc = PotreeDocument.Open(cloudPath);
        var pts = doc.Scans[0].ReadPoints().ToList();
        Assert.Equal(2, pts.Count);
        Assert.NotNull(pts[0].Normal);
        Assert.True(Math.Abs(pts[0].Normal!.Value.Z - 1.0) < 0.05);
        Assert.NotNull(pts[1].Normal);
        Assert.True(Math.Abs(pts[1].Normal!.Value.X - 1.0) < 0.05);
    }

    [Fact]
    public void Potree1x_Reads_Float_Normals()
    {
        using var dir = new TempDirectory();
        var points = new PotreeSamplePoint[]
        {
            new(1.0, 2.0, 3.0, Normal: new PclVector3(0, 0.707106, 0.707106)),
        };

        var scale = new PclVector3(0.01, 0.01, 0.01);
        var offset = PclVector3.Zero;
        var (cloudJs, rBin, rHrc) = SyntheticFixtures.CreateSyntheticPotree1x(
            points, scale, offset, normalType: "NORMAL_FLOATS");

        string cloudPath = dir.WriteText("cloud.js", cloudJs);
        string dataR = Path.Combine(dir.Root, "data", "r");
        Directory.CreateDirectory(dataR);
        File.WriteAllBytes(Path.Combine(dataR, "r.bin"), rBin);

        using var doc = PotreeDocument.Open(cloudPath);
        var pts = doc.Scans[0].ReadPoints().ToList();
        Assert.Single(pts);
        Assert.NotNull(pts[0].Normal);
        Assert.Equal(0.0, pts[0].Normal!.Value.X, 3);
        Assert.Equal(0.707, pts[0].Normal!.Value.Y, 3);
        Assert.Equal(0.707, pts[0].Normal!.Value.Z, 3);
    }

    [Fact]
    public void Potree1x_Reads_Rgb_Without_Alpha()
    {
        using var dir = new TempDirectory();
        var points = new PotreeSamplePoint[]
        {
            new(1.0, 2.0, 3.0, Color: new PclColor(12, 34, 56, 255)),
        };

        var scale = new PclVector3(0.01, 0.01, 0.01);
        var offset = PclVector3.Zero;
        var (cloudJs, rBin, _) = SyntheticFixtures.CreateSyntheticPotree1x(
            points, scale, offset, hasColors: true, hasAlpha: false);

        string cloudPath = dir.WriteText("cloud.js", cloudJs);
        string dataR = Path.Combine(dir.Root, "data", "r");
        Directory.CreateDirectory(dataR);
        File.WriteAllBytes(Path.Combine(dataR, "r.bin"), rBin);

        using var doc = PotreeDocument.Open(cloudPath);
        var pt = doc.Scans[0].ReadPoints().Single();
        Assert.Equal(new PclColor(12, 34, 56, 255), pt.Color);
    }

    [Fact]
    public void Potree1x_Reads_Multiple_Nodes()
    {
        using var dir = new TempDirectory();
        var pointsNode1 = new PotreeSamplePoint[] { new(1, 2, 3), new(4, 5, 6) };
        var pointsNode2 = new PotreeSamplePoint[] { new(7, 8, 9) };

        var scale = new PclVector3(0.01, 0.01, 0.01);
        var offset = PclVector3.Zero;
        var (cloudJs, rBin1, _) = SyntheticFixtures.CreateSyntheticPotree1x(pointsNode1, scale, offset, hasColors: false, normalType: "", hasIntensity: false, hasClassification: false);
        var (_, rBin2, _) = SyntheticFixtures.CreateSyntheticPotree1x(pointsNode2, scale, offset, hasColors: false, normalType: "", hasIntensity: false, hasClassification: false);

        string cloudPath = dir.WriteText("cloud.js", cloudJs);
        string dataR = Path.Combine(dir.Root, "data", "r");
        Directory.CreateDirectory(dataR);
        File.WriteAllBytes(Path.Combine(dataR, "r.bin"), rBin1);
        File.WriteAllBytes(Path.Combine(dataR, "r0.bin"), rBin2);

        using var doc = PotreeDocument.Open(cloudPath);
        Assert.Equal(3, doc.TotalPoints);
        var pts = doc.Scans[0].ReadPoints().ToList();
        Assert.Equal(3, pts.Count);
        Assert.Equal(1.0, pts[0].Position.X, 2);
        Assert.Equal(4.0, pts[1].Position.X, 2);
        Assert.Equal(7.0, pts[2].Position.X, 2);
    }

    [Fact]
    public void Potree1x_Can_Open_From_Directory_And_Bin()
    {
        using var dir = new TempDirectory();
        var points = new PotreeSamplePoint[] { new(1, 2, 3) };
        var (cloudJs, rBin, _) = SyntheticFixtures.CreateSyntheticPotree1x(points, PclVector3.One, PclVector3.Zero);

        dir.WriteText("cloud.js", cloudJs);
        string dataR = Path.Combine(dir.Root, "data", "r");
        Directory.CreateDirectory(dataR);
        string binPath = Path.Combine(dataR, "r.bin");
        File.WriteAllBytes(binPath, rBin);

        // Open directory
        using (var docDir = PclFile.Open(dir.Root))
        {
            Assert.Equal(PclFileKind.Potree, docDir.Kind);
            Assert.Equal(1, docDir.TotalPointCount);
        }

        // Open .bin file
        using (var docBin = PclFile.Open(binPath))
        {
            Assert.Equal(PclFileKind.Potree, docBin.Kind);
            Assert.Equal(1, docBin.TotalPointCount);
        }
    }

    [Fact]
    public void Potree2x_Reads_Basic_Dataset()
    {
        using var dir = new TempDirectory();
        var points = new PotreeSamplePoint[]
        {
            new(10.5, 20.5, 30.5, new PclColor(10, 20, 30, 255), 500, new PclVector3(0, 0, 1), 1),
            new(40.5, 50.5, 60.5, new PclColor(40, 50, 60, 255), 600, new PclVector3(0, 1, 0), 2),
        };

        var scale = new PclVector3(0.01, 0.01, 0.01);
        var offset = new PclVector3(10.0, 20.0, 30.0);
        var (metaJson, octreeBin) = SyntheticFixtures.CreateSyntheticPotree2x(points, scale, offset);

        string metaPath = dir.WriteText("metadata.json", metaJson);
        File.WriteAllBytes(Path.Combine(dir.Root, "octree.bin"), octreeBin);

        using var doc = PotreeDocument.Open(metaPath);
        Assert.Equal(PclFileKind.Potree, doc.Kind);
        Assert.Equal("2.0", doc.PotreeVersion);
        Assert.Equal(2, doc.TotalPoints);

        var pts = doc.Scans[0].ReadPoints().ToList();
        Assert.Equal(2, pts.Count);
        Assert.Equal(10.5, pts[0].Position.X, 2);
        Assert.Equal(20.5, pts[0].Position.Y, 2);
        Assert.Equal(30.5, pts[0].Position.Z, 2);
        Assert.Equal(new PclColor(10, 20, 30, 255), pts[0].Color);
        Assert.Equal(500f, pts[0].Intensity);
        Assert.Equal((byte)1, pts[0].Classification);
        Assert.NotNull(pts[0].Normal);
        Assert.True(Math.Abs(pts[0].Normal!.Value.Z - 1.0) < 0.05);
    }

    [Fact]
    public void Potree2x_Reads_16Bit_Colors()
    {
        using var dir = new TempDirectory();
        var points = new PotreeSamplePoint[]
        {
            new(1.0, 2.0, 3.0, Color: new PclColor(200, 100, 50, 255)),
        };

        var scale = new PclVector3(0.01, 0.01, 0.01);
        var offset = PclVector3.Zero;
        var (metaJson, octreeBin) = SyntheticFixtures.CreateSyntheticPotree2x(
            points, scale, offset, color16Bit: true, hasNormals: false, hasIntensity: false, hasClassification: false);

        string metaPath = dir.WriteText("metadata.json", metaJson);
        File.WriteAllBytes(Path.Combine(dir.Root, "octree.bin"), octreeBin);

        using var doc = PotreeDocument.Open(metaPath);
        var pt = doc.Scans[0].ReadPoints().Single();
        Assert.Equal(new PclColor(200, 100, 50, 255), pt.Color);
    }

    [Fact]
    public void Potree2x_Reads_Float_Positions()
    {
        using var dir = new TempDirectory();
        var points = new PotreeSamplePoint[]
        {
            new(123.456, 789.012, 345.678),
        };

        var scale = PclVector3.One;
        var offset = PclVector3.Zero;
        var (metaJson, octreeBin) = SyntheticFixtures.CreateSyntheticPotree2x(
            points, scale, offset, floatPositions: true, hasColors: false, hasNormals: false, hasIntensity: false, hasClassification: false);

        string metaPath = dir.WriteText("metadata.json", metaJson);
        File.WriteAllBytes(Path.Combine(dir.Root, "octree.bin"), octreeBin);

        using var doc = PotreeDocument.Open(metaPath);
        var pt = doc.Scans[0].ReadPoints().Single();
        Assert.Equal(123.456, pt.Position.X, 3);
        Assert.Equal(789.012, pt.Position.Y, 3);
        Assert.Equal(345.678, pt.Position.Z, 3);
    }

    [Fact]
    public void Potree_Applies_ReadOptions()
    {
        using var dir = new TempDirectory();
        var points = Enumerable.Range(0, 10).Select(i => new PotreeSamplePoint(i, i, i)).ToArray();
        var (cloudJs, rBin, _) = SyntheticFixtures.CreateSyntheticPotree1x(points, PclVector3.One, PclVector3.Zero);

        string cloudPath = dir.WriteText("cloud.js", cloudJs);
        string dataR = Path.Combine(dir.Root, "data", "r");
        Directory.CreateDirectory(dataR);
        File.WriteAllBytes(Path.Combine(dataR, "r.bin"), rBin);

        using var doc = PotreeDocument.Open(cloudPath);
        var scan = doc.Scans[0];

        // MaxPoints
        var limited = scan.ReadPoints(new PclReadOptions { MaxPoints = 3 }).ToList();
        Assert.Equal(3, limited.Count);

        // SubsampleStep
        var stepped = scan.ReadPoints(new PclReadOptions { SubsampleStep = 2 }).ToList();
        Assert.Equal(5, stepped.Count);
        Assert.Equal(0.0, stepped[0].Position.X);
        Assert.Equal(2.0, stepped[1].Position.X);

        // ApplyTransform
        var transform = new PclTransform { Translation = new PclVector3(100, 200, 300) };
        typeof(PclScan).GetProperty("Transform")!.SetValue(scan, transform);

        var transformed = scan.ReadPoints(new PclReadOptions { ApplyTransform = true, MaxPoints = 1 }).Single();
        Assert.Equal(100.0, transformed.Position.X);
        Assert.Equal(200.0, transformed.Position.Y);
        Assert.Equal(300.0, transformed.Position.Z);
    }

    [Fact]
    public void Potree_Rejects_Corrupt_Json()
    {
        using var dir = new TempDirectory();
        string cloudPath = dir.WriteText("cloud.js", "{ broken json ... ");
        Assert.Throws<InvalidDataException>(() => PotreeDocument.Open(cloudPath));
    }

    [Fact]
    public void Potree_Rejects_Truncated_Bin()
    {
        using var dir = new TempDirectory();
        var points = new PotreeSamplePoint[] { new(1, 2, 3), new(4, 5, 6) };
        var (cloudJs, rBin, _) = SyntheticFixtures.CreateSyntheticPotree1x(points, PclVector3.One, PclVector3.Zero);

        string cloudPath = dir.WriteText("cloud.js", cloudJs);
        string dataR = Path.Combine(dir.Root, "data", "r");
        Directory.CreateDirectory(dataR);
        // Truncate by 1 byte so length % recordSize != 0
        File.WriteAllBytes(Path.Combine(dataR, "r.bin"), rBin[..^1]);

        Assert.Throws<InvalidDataException>(() => PotreeDocument.Open(cloudPath));
    }

    [Fact]
    public void Potree_Rejects_NonFinite_Scale()
    {
        using var dir = new TempDirectory();
        string cloudJs = """
            {
                "version": "1.7",
                "octreeDir": "data",
                "scale": 0,
                "boundingBox": { "lx": 0, "ly": 0, "lz": 0, "ux": 1, "uy": 1, "uz": 1 },
                "pointAttributes": [ "POSITION_CARTESIAN" ]
            }
            """;
        string cloudPath = dir.WriteText("cloud.js", cloudJs);
        string dataR = Path.Combine(dir.Root, "data", "r");
        Directory.CreateDirectory(dataR);
        File.WriteAllBytes(Path.Combine(dataR, "r.bin"), new byte[12]);

        Assert.Throws<InvalidDataException>(() => PotreeDocument.Open(cloudPath));
    }

    [Fact]
    public void Potree_Rejects_Missing_Bin_File()
    {
        using var dir = new TempDirectory();
        var (cloudJs, _, _) = SyntheticFixtures.CreateSyntheticPotree1x([new(1, 2, 3)], PclVector3.One, PclVector3.Zero);
        string cloudPath = dir.WriteText("cloud.js", cloudJs);
        // Do not create dataDir / r.bin

        Assert.Throws<InvalidDataException>(() => PotreeDocument.Open(cloudPath));
    }

    // ---- LAZ (*.laz) --------------------------------------------------------------------------------------------------

    [Fact]
    public void Laz_Opens_Lists_Scans_And_Streams_Points()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticLaz(
            x: 10.0, y: 20.0, z: 30.0,
            intensity: 0.5f,
            color: new PclColor(254, 0, 0),
            classification: 2);

        string path = dir.Write("test.laz", bytes);
        using var doc = PclFile.Open(path);

        Assert.Equal(PclFileKind.Laz, doc.Kind);
        var scan = Assert.Single(doc.Scans);
        Assert.Equal(1, scan.PointCount);
        Assert.True(scan.HasColors);
        Assert.True(scan.HasIntensities);
        Assert.False(scan.HasNormals);

        var pt = Assert.Single(scan.ReadPoints());
        AssertNear(new PclVector3(10, 20, 30), pt.Position);
        Assert.NotNull(pt.Intensity);
        Assert.InRange(pt.Intensity.Value, 0.49f, 0.51f);
        Assert.Equal((byte)2, pt.Classification);
        Assert.NotNull(pt.Color);
        Assert.Equal((byte)254, pt.Color.Value.R);
        Assert.Equal((byte)0, pt.Color.Value.G);
        Assert.Equal((byte)0, pt.Color.Value.B);
    }

    [Fact]
    public void Laz_Wrong_Content_Throws_InvalidDataException()
    {
        using var dir = new TempDirectory();
        string path = dir.Write("corrupt.laz", new byte[100]);
        Assert.Throws<NotSupportedException>(() => PclFile.Open(path));
        Assert.Throws<InvalidDataException>(() => LazDocument.Open(path));
    }

    [Fact]
    public void Laz_Rejects_Corrupt_Chunk_Table_With_InvalidDataException()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticLaz(corruptChunkTable: true);
        string path = dir.Write("bad_table.laz", bytes);

        Assert.Throws<InvalidDataException>(() => LazDocument.Open(path));
    }

    [Fact]
    public void Laz_Rejects_Truncated_Data_With_InvalidDataException()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticLaz(truncateData: true);
        string path = dir.Write("truncated.laz", bytes);

        Assert.Throws<InvalidDataException>(() => LazDocument.Open(path));
    }

    [Fact]
    public void Laz_Rejects_Unsupported_Compressor_With_NotSupportedException()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticLaz(compressor: 3);
        string path = dir.Write("unsupported.laz", bytes);

        Assert.Throws<NotSupportedException>(() => LazDocument.Open(path));
    }

    [Fact]
    public void Laz_Rejects_Unsupported_Chunk_Table_Version_With_NotSupportedException()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticLaz(chunkTableVersion: 1);
        string path = dir.Write("bad_version.laz", bytes);

        Assert.Throws<NotSupportedException>(() => LazDocument.Open(path));
    }

    [Fact]
    public void Copc_Opens_Lists_Scans_And_Streams_Points()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticCopc(
            x: 10.0, y: 20.0, z: 30.0,
            intensity: 0.5f,
            color: new PclColor(200, 100, 50),
            classification: 2,
            pointFormat: 2);

        string path = dir.Write("test.copc.laz", bytes);
        using var doc = PclFile.Open(path) as CopcDocument;
        Assert.NotNull(doc);

        Assert.Equal(PclFileKind.Copc, doc.Kind);
        Assert.Equal(10.0, doc.Center.X);
        Assert.Equal(20.0, doc.Center.Y);
        Assert.Equal(30.0, doc.Center.Z);
        Assert.Equal(50.0, doc.HalfSize);
        Assert.Equal(1.0, doc.Spacing);
        Assert.Single(doc.Nodes);
        Assert.Equal(new CopcVoxelKey(0, 0, 0, 0), doc.Nodes[0].Key);

        var scan = Assert.Single(doc.Scans);
        Assert.Equal(1, scan.PointCount);
        Assert.True(scan.HasColors);
        Assert.True(scan.HasIntensities);
        Assert.False(scan.HasNormals);

        var pt = Assert.Single(scan.ReadPoints());
        AssertNear(new PclVector3(10, 20, 30), pt.Position);
        Assert.NotNull(pt.Intensity);
        Assert.InRange(pt.Intensity.Value, 0.49f, 0.51f);
        Assert.Equal((byte)2, pt.Classification);
        Assert.NotNull(pt.Color);
        Assert.Equal((byte)200, pt.Color.Value.R);
        Assert.Equal((byte)100, pt.Color.Value.G);
        Assert.Equal((byte)50, pt.Color.Value.B);
    }

    [Fact]
    public void Copc_Point_Format_7_Decodes_RGB_And_Coordinates()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticCopc(
            x: 15.0, y: 25.0, z: 35.0,
            intensity: 0.75f,
            color: new PclColor(120, 180, 240),
            classification: 3,
            pointFormat: 7);

        string path = dir.Write("format7.copc.laz", bytes);
        using var doc = CopcDocument.Open(path);

        Assert.Equal(7, doc.PointFormat);
        var pt = Assert.Single(doc.ReadAllPoints());
        AssertNear(new PclVector3(15, 25, 35), pt.Position);
        Assert.NotNull(pt.Color);
        Assert.Equal((byte)120, pt.Color.Value.R);
        Assert.Equal((byte)180, pt.Color.Value.G);
        Assert.Equal((byte)240, pt.Color.Value.B);
        Assert.Equal((byte)3, pt.Classification);
    }

    [Fact]
    public void Copc_FindNodes_And_ReadPoints_By_Bounds()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticCopc(
            x: 10.0, y: 20.0, z: 30.0,
            pointFormat: 2);

        string path = dir.Write("test.copc.laz", bytes);
        using var doc = CopcDocument.Open(path);

        // Bounding box encompassing the root voxel
        var queryBox = new PclBounds(new PclVector3(0, 0, 0), new PclVector3(50, 50, 50));
        var matchingNodes = doc.FindNodes(queryBox);
        Assert.Single(matchingNodes);

        var pointsInBox = doc.ReadPoints(queryBox).ToList();
        Assert.Single(pointsInBox);
        AssertNear(new PclVector3(10, 20, 30), pointsInBox[0].Position);

        // Bounding box outside the root voxel
        var outsideBox = new PclBounds(new PclVector3(500, 500, 500), new PclVector3(600, 600, 600));
        Assert.Empty(doc.FindNodes(outsideBox));
        Assert.Empty(doc.ReadPoints(outsideBox));

        // Read points directly from node
        var nodePoints = doc.ReadPoints(doc.Nodes[0]).ToList();
        Assert.Single(nodePoints);
        AssertNear(new PclVector3(10, 20, 30), nodePoints[0].Position);
    }

    [Fact]
    public void Copc_Subsample_And_MaxPoints_Honored()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticCopc(x: 10, y: 20, z: 30);
        string path = dir.Write("test.copc.laz", bytes);
        using var doc = CopcDocument.Open(path);

        Assert.Empty(doc.ReadAllPoints(new PclReadOptions { MaxPoints = 0 }));
        Assert.Single(doc.ReadAllPoints(new PclReadOptions { MaxPoints = 1 }));
    }

    [Fact]
    public void Copc_Wrong_Content_Throws_InvalidDataException()
    {
        using var dir = new TempDirectory();
        string path = dir.Write("corrupt.copc.laz", new byte[100]);
        Assert.Throws<NotSupportedException>(() => PclFile.Open(path));
        Assert.Throws<InvalidDataException>(() => CopcDocument.Open(path));
    }

    [Fact]
    public void Copc_Rejects_Corrupt_Hierarchy_Offset_With_InvalidDataException()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticCopc(corruptHierarchyOffset: true);
        string path = dir.Write("bad_offset.copc.laz", bytes);

        Assert.Throws<InvalidDataException>(() => CopcDocument.Open(path));
    }

    [Fact]
    public void Copc_Rejects_Cyclic_Hierarchy_With_InvalidDataException()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticCopc(cyclicHierarchy: true);
        string path = dir.Write("cyclic.copc.laz", bytes);

        Assert.Throws<InvalidDataException>(() => CopcDocument.Open(path));
    }

    [Fact]
    public void Copc_Rejects_Corrupt_Vlr_Size_With_InvalidDataException()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticCopc(corruptVlrSize: true);
        string path = dir.Write("bad_vlr.copc.laz", bytes);

        Assert.Throws<InvalidDataException>(() => CopcDocument.Open(path));
    }

    [Fact]
    public void Copc_Rejects_Truncated_Data_With_InvalidDataException()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticCopc(truncateData: true);
        string path = dir.Write("truncated.copc.laz", bytes);

        Assert.Throws<InvalidDataException>(() => CopcDocument.Open(path));
    }

    // ---- Entwine Point Tile (EPT) tests -------------------------------------------------------------------

    [Fact]
    public void Ept_Opens_Lists_Scans_And_Streams_Points()
    {
        using var dir = new TempDirectory();
        var points = new EptSamplePoint[]
        {
            new(10.0, 20.0, 30.0, new PclColor(255, 0, 0), Intensity: 0.5f, Classification: 2),
            new(11.0, 21.0, 31.0, new PclColor(0, 255, 0), Intensity: 0.8f, Classification: 1),
            new(12.0, 22.0, 32.0, new PclColor(0, 0, 255), Intensity: 1.0f, Classification: 3),
        };

        var (manifest, hier, data, ext) = SyntheticFixtures.CreateSyntheticEpt(points, dataType: "binary");
        string eptPath = dir.WriteText("ept.json", manifest);
        dir.WriteText("ept-hierarchy/0-0-0-0.json", hier);
        string dataPath = dir.Write($"ept-data/0-0-0-0.{ext}", data);

        // Open via ept.json
        using var docFile = EptDocument.Open(eptPath);
        Assert.Equal(PclFileKind.Ept, docFile.Kind);
        Assert.Equal("1.0.0", docFile.Version);
        Assert.Equal("binary", docFile.DataType);
        Assert.Equal(128, docFile.Span);
        Assert.Equal(3, docFile.DeclaredPoints);
        Assert.Single(docFile.Scans);
        Assert.Single(docFile.Nodes);
        Assert.Equal(3, docFile.TotalPointCount);

        // Open via directory
        using var docDir = PclFile.Open(dir.Root);
        Assert.Equal(PclFileKind.Ept, docDir.Kind);
        Assert.Equal(3, docDir.TotalPointCount);

        // Open via tile path (ancestor search)
        using var docTile = EptDocument.Open(dataPath);
        Assert.Equal(PclFileKind.Ept, docTile.Kind);

        // Read points
        var readPoints = docFile.ReadAllPoints().ToList();
        Assert.Equal(3, readPoints.Count);

        Assert.Equal(10.0, readPoints[0].X, Precision);
        Assert.Equal(20.0, readPoints[0].Y, Precision);
        Assert.Equal(30.0, readPoints[0].Z, Precision);
        Assert.Equal(new PclColor(255, 0, 0), readPoints[0].Color);
        Assert.Equal((byte)2, readPoints[0].Classification);

        Assert.Equal(11.0, readPoints[1].X, Precision);
        Assert.Equal(21.0, readPoints[1].Y, Precision);
        Assert.Equal(31.0, readPoints[1].Z, Precision);
        Assert.Equal(new PclColor(0, 255, 0), readPoints[1].Color);
        Assert.Equal((byte)1, readPoints[1].Classification);
    }

    [Fact]
    public void Ept_Reads_Laszip_Data()
    {
        using var dir = new TempDirectory();
        var points = new EptSamplePoint[]
        {
            new(5.0, 10.0, 15.0, new PclColor(128, 64, 32), Intensity: 0.5f, Classification: 1),
        };

        var (manifest, hier, data, ext) = SyntheticFixtures.CreateSyntheticEpt(points, dataType: "laszip");
        string eptPath = dir.WriteText("ept.json", manifest);
        dir.WriteText("ept-hierarchy/0-0-0-0.json", hier);
        dir.Write($"ept-data/0-0-0-0.{ext}", data);

        using var doc = EptDocument.Open(eptPath);
        Assert.Equal("laszip", doc.DataType);
        var readPoints = doc.ReadAllPoints().ToList();
        Assert.Single(readPoints);
        Assert.Equal(5.0, readPoints[0].X, 0.01);
        Assert.Equal(10.0, readPoints[0].Y, 0.01);
        Assert.Equal(15.0, readPoints[0].Z, 0.01);
    }

    [Fact]
    public void Ept_Reads_Zstandard_Data()
    {
        using var dir = new TempDirectory();
        var points = new EptSamplePoint[]
        {
            new(100.0, 200.0, 300.0, new PclColor(10, 20, 30), Intensity: 0.75f, Classification: 5),
            new(105.0, 205.0, 305.0, new PclColor(40, 50, 60), Intensity: 0.25f, Classification: 6),
        };

        var (manifest, hier, data, ext) = SyntheticFixtures.CreateSyntheticEpt(points, dataType: "zstandard");
        string eptPath = dir.WriteText("ept.json", manifest);
        dir.WriteText("ept-hierarchy/0-0-0-0.json", hier);
        dir.Write($"ept-data/0-0-0-0.{ext}", data);

        using var doc = EptDocument.Open(eptPath);
        Assert.Equal("zstandard", doc.DataType);
        var readPoints = doc.ReadAllPoints().ToList();
        Assert.Equal(2, readPoints.Count);
        Assert.Equal(100.0, readPoints[0].X, Precision);
        Assert.Equal(200.0, readPoints[0].Y, Precision);
        Assert.Equal(300.0, readPoints[0].Z, Precision);
        Assert.Equal(new PclColor(10, 20, 30), readPoints[0].Color);
        Assert.Equal((byte)5, readPoints[0].Classification);

        Assert.Equal(105.0, readPoints[1].X, Precision);
        Assert.Equal(205.0, readPoints[1].Y, Precision);
        Assert.Equal(305.0, readPoints[1].Z, Precision);
        Assert.Equal(new PclColor(40, 50, 60), readPoints[1].Color);
        Assert.Equal((byte)6, readPoints[1].Classification);
    }

    [Fact]
    public void Ept_Spatial_Queries_FindNodes_And_ReadPoints()
    {
        using var dir = new TempDirectory();
        var rootBounds = new PclBounds(new PclVector3(0, 0, 0), new PclVector3(100, 100, 100));

        var pts1 = new EptSamplePoint[] { new(10, 10, 10) };
        var pts2 = new EptSamplePoint[] { new(60, 60, 60) };

        var (manifest, _, data1, ext) = SyntheticFixtures.CreateSyntheticEpt(pts1, dataType: "binary", bounds: rootBounds);
        string eptPath = dir.WriteText("ept.json", manifest);

        // Two nodes in octree: 1-0-0-0 (voxel [0..50, 0..50, 0..50]) and 1-1-1-1 (voxel [50..100, 50..100, 50..100])
        string hier = "{\"1-0-0-0\": 1, \"1-1-1-1\": 1}";
        dir.WriteText("ept-hierarchy/0-0-0-0.json", hier);
        dir.Write($"ept-data/1-0-0-0.{ext}", data1);

        var (_, _, data2, _) = SyntheticFixtures.CreateSyntheticEpt(pts2, dataType: "binary", bounds: rootBounds);
        dir.Write($"ept-data/1-1-1-1.{ext}", data2);

        using var doc = EptDocument.Open(eptPath);
        Assert.Equal(2, doc.Nodes.Count);

        // Query spatial bounds covering only the first voxel [0..20, 0..20, 0..20]
        var queryBounds = new PclBounds(new PclVector3(5, 5, 5), new PclVector3(25, 25, 25));
        var matchingNodes = doc.FindNodes(queryBounds);
        Assert.Single(matchingNodes);
        Assert.Equal("1-0-0-0", matchingNodes[0].Key.ToString());

        var filteredPoints = doc.ReadPoints(queryBounds).ToList();
        Assert.Single(filteredPoints);
        Assert.Equal(10.0, filteredPoints[0].X, Precision);

        // Read specific node directly
        var nodePoints = doc.ReadPoints(doc.Nodes[1]).ToList();
        Assert.Single(nodePoints);
        Assert.Equal(60.0, nodePoints[0].X, Precision);
    }

    [Fact]
    public void Ept_Hierarchy_MultiPage_And_Cycle_Detection()
    {
        using var dir = new TempDirectory();
        var pts = new EptSamplePoint[] { new(1, 2, 3) };
        var (manifest, _, data, ext) = SyntheticFixtures.CreateSyntheticEpt(pts, dataType: "binary");
        string eptPath = dir.WriteText("ept.json", manifest);

        // 0-0-0-0.json refers to 1-0-0-0 as sub-page (-1)
        dir.WriteText("ept-hierarchy/0-0-0-0.json", "{\"1-0-0-0\": -1}");
        // 1-0-0-0.json defines 1-0-0-0 as having 1 point
        dir.WriteText("ept-hierarchy/1-0-0-0.json", "{\"1-0-0-0\": 1}");
        dir.Write($"ept-data/1-0-0-0.{ext}", data);

        using (var doc = EptDocument.Open(eptPath))
        {
            Assert.Single(doc.Nodes);
            Assert.Equal("1-0-0-0", doc.Nodes[0].Key.ToString());
        }

        // Cyclic hierarchy reference should throw InvalidDataException
        dir.WriteText("ept-hierarchy/1-0-0-0.json", "{\"0-0-0-0\": -1}");
        Assert.Throws<InvalidDataException>(() => EptDocument.Open(eptPath));
    }

    [Fact]
    public void Ept_Applies_ReadOptions()
    {
        using var dir = new TempDirectory();
        var points = Enumerable.Range(0, 10)
            .Select(i => new EptSamplePoint(i, i * 2, i * 3))
            .ToArray();

        var (manifest, hier, data, ext) = SyntheticFixtures.CreateSyntheticEpt(points, dataType: "binary");
        string eptPath = dir.WriteText("ept.json", manifest);
        dir.WriteText("ept-hierarchy/0-0-0-0.json", hier);
        dir.Write($"ept-data/0-0-0-0.{ext}", data);

        using var doc = EptDocument.Open(eptPath);

        // MaxPoints
        var limited = doc.ReadAllPoints(new PclReadOptions { MaxPoints = 4 }).ToList();
        Assert.Equal(4, limited.Count);

        // SubsampleStep
        var subsampled = doc.ReadAllPoints(new PclReadOptions { SubsampleStep = 3 }).ToList();
        Assert.Equal(4, subsampled.Count); // indices 0, 3, 6, 9
        Assert.Equal(0.0, subsampled[0].X, Precision);
        Assert.Equal(3.0, subsampled[1].X, Precision);
        Assert.Equal(6.0, subsampled[2].X, Precision);
        Assert.Equal(9.0, subsampled[3].X, Precision);

        // ApplyTransform
        var transform = new PclTransform { Translation = new PclVector3(10, 20, 30) };
        typeof(PclScan).GetProperty("Transform")!.SetValue(doc.Scans[0], transform);
        var transformed = doc.ReadAllPoints(new PclReadOptions { MaxPoints = 1, ApplyTransform = true }).First();
        Assert.Equal(10.0, transformed.X, Precision);
        Assert.Equal(20.0, transformed.Y, Precision);
        Assert.Equal(30.0, transformed.Z, Precision);
    }

    [Fact]
    public void Ept_Rejects_Corrupt_Manifest_Json()
    {
        using var dir = new TempDirectory();
        string path = dir.WriteText("ept.json", "NOT JSON");
        Assert.Throws<InvalidDataException>(() => EptDocument.Open(path));
    }

    [Fact]
    public void Ept_Rejects_Missing_DataType()
    {
        using var dir = new TempDirectory();
        string json = "{\"bounds\":[0,0,0,1,1,1],\"schema\":[{\"name\":\"X\"},{\"name\":\"Y\"},{\"name\":\"Z\"}]}";
        string path = dir.WriteText("ept.json", json);
        Assert.Throws<InvalidDataException>(() => EptDocument.Open(path));
    }

    [Fact]
    public void Ept_Rejects_Unsupported_DataType()
    {
        using var dir = new TempDirectory();
        string json = "{\"bounds\":[0,0,0,1,1,1],\"dataType\":\"parquet\",\"schema\":[{\"name\":\"X\"},{\"name\":\"Y\"},{\"name\":\"Z\"}]}";
        string path = dir.WriteText("ept.json", json);
        Assert.Throws<NotSupportedException>(() => EptDocument.Open(path));
    }

    [Fact]
    public void Ept_Rejects_Missing_Or_NonFinite_Bounds()
    {
        using var dir = new TempDirectory();
        string jsonMissing = "{\"dataType\":\"binary\",\"schema\":[{\"name\":\"X\"},{\"name\":\"Y\"},{\"name\":\"Z\"}]}";
        string path1 = dir.WriteText("missing_bounds/ept.json", jsonMissing);
        Assert.Throws<InvalidDataException>(() => EptDocument.Open(path1));

        string jsonNaN = "{\"dataType\":\"binary\",\"bounds\":[0,0,0,\"NaN\",1,1],\"schema\":[{\"name\":\"X\"},{\"name\":\"Y\"},{\"name\":\"Z\"}]}";
        string path2 = dir.WriteText("nan_bounds/ept.json", jsonNaN);
        Assert.Throws<InvalidDataException>(() => EptDocument.Open(path2));
    }

    [Fact]
    public void Ept_Rejects_Missing_Schema_Or_Dimensions()
    {
        using var dir = new TempDirectory();
        string jsonNoSchema = "{\"dataType\":\"binary\",\"bounds\":[0,0,0,1,1,1]}";
        string path1 = dir.WriteText("no_schema/ept.json", jsonNoSchema);
        Assert.Throws<InvalidDataException>(() => EptDocument.Open(path1));

        string jsonNoXYZ = "{\"dataType\":\"binary\",\"bounds\":[0,0,0,1,1,1],\"schema\":[{\"name\":\"Intensity\"}]}";
        string path2 = dir.WriteText("no_xyz/ept.json", jsonNoXYZ);
        Assert.Throws<InvalidDataException>(() => EptDocument.Open(path2));
    }

    [Fact]
    public void Ept_Rejects_Truncated_Binary_Tile()
    {
        using var dir = new TempDirectory();
        var (manifest, hier, _, ext) = SyntheticFixtures.CreateSyntheticEpt([new(1, 2, 3)], dataType: "binary");
        string eptPath = dir.WriteText("ept.json", manifest);
        dir.WriteText("ept-hierarchy/0-0-0-0.json", hier);
        // Point record size is 21 bytes. Write 10 bytes instead to simulate truncated tile.
        dir.Write($"ept-data/0-0-0-0.{ext}", new byte[10]);

        using var doc = EptDocument.Open(eptPath);
        Assert.Throws<InvalidDataException>(() => doc.ReadAllPoints().ToList());
    }

    [Fact]
    public void Read_Ptg_Binary_Float_With_Intensity_And_Color()
    {
        using var dir = new TempDirectory();
        var points = new[]
        {
            new SyntheticFixtures.PtgSamplePoint(1.25, 2.5, 3.75, 0.5f, new PclColor(10, 20, 30)),
            new SyntheticFixtures.PtgSamplePoint(4.0, 5.0, 6.0, 0.8f, new PclColor(100, 150, 200)),
        };
        byte[] ptgBytes = SyntheticFixtures.CreateSyntheticPtgBinary(
            numCols: 2, numRows: 1, properties: 0x01 | 0x04 | 0x08, points: points, scanName: "StationA");

        string path = dir.Write("scan.ptg", ptgBytes);
        using var doc = PtgDocument.Open(path);

        Assert.Equal(PclFileKind.LeicaPtg, doc.Kind);
        Assert.Single(doc.Scans);
        var scan = (PtgScan)doc.Scans[0];
        Assert.Equal("StationA", scan.Name);
        Assert.Equal(2, scan.Columns);
        Assert.Equal(1, scan.Rows);
        Assert.Equal(2, scan.GridSize);
        Assert.Equal(2, scan.PointCount);
        Assert.True(scan.HasColors);
        Assert.True(scan.HasIntensities);

        var readPts = scan.ReadPoints().ToList();
        Assert.Equal(2, readPts.Count);

        Assert.Equal(1.25, readPts[0].X, 3);
        Assert.Equal(2.5, readPts[0].Y, 3);
        Assert.Equal(3.75, readPts[0].Z, 3);
        Assert.Equal(0.5f, readPts[0].Intensity);
        Assert.Equal(new PclColor(10, 20, 30), readPts[0].Color);
        Assert.Equal(0UL, readPts[0].Index);

        Assert.Equal(4.0, readPts[1].X, 3);
        Assert.Equal(5.0, readPts[1].Y, 3);
        Assert.Equal(6.0, readPts[1].Z, 3);
        Assert.Equal(0.8f, readPts[1].Intensity);
        Assert.Equal(new PclColor(100, 150, 200), readPts[1].Color);
        Assert.Equal(1UL, readPts[1].Index);
    }

    [Fact]
    public void Read_Ptg_Binary_Double_Coordinates()
    {
        using var dir = new TempDirectory();
        var points = new[]
        {
            new SyntheticFixtures.PtgSamplePoint(1234567.89012345, -987654.32109876, 5555.55555555),
        };
        byte[] ptgBytes = SyntheticFixtures.CreateSyntheticPtgBinary(
            numCols: 1, numRows: 1, properties: 0x02, points: points);

        string path = dir.Write("double.ptg", ptgBytes);
        using var doc = PtgDocument.Open(path);

        var scan = (PtgScan)doc.Scans[0];
        Assert.False(scan.HasColors);
        Assert.False(scan.HasIntensities);
        Assert.Equal("double", scan.Properties["CoordinateType"]);

        var pt = Assert.Single(scan.ReadPoints());
        Assert.Equal(1234567.89012345, pt.X, 8);
        Assert.Equal(-987654.32109876, pt.Y, 8);
        Assert.Equal(5555.55555555, pt.Z, 8);
        Assert.Null(pt.Intensity);
        Assert.Null(pt.Color);
    }

    [Fact]
    public void Read_Ptg_Binary_With_Missing_Returns()
    {
        using var dir = new TempDirectory();
        // 2 cols x 2 rows = 4 cells; row 1 of col 0 and row 0 of col 1 are missing
        var points = new[]
        {
            new SyntheticFixtures.PtgSamplePoint(1, 2, 3, IsValid: true),
            new SyntheticFixtures.PtgSamplePoint(0, 0, 0, IsValid: false),
            new SyntheticFixtures.PtgSamplePoint(0, 0, 0, IsValid: false),
            new SyntheticFixtures.PtgSamplePoint(4, 5, 6, IsValid: true),
        };
        byte[] ptgBytes = SyntheticFixtures.CreateSyntheticPtgBinary(
            numCols: 2, numRows: 2, properties: 0x01, points: points);

        string path = dir.Write("missing.ptg", ptgBytes);
        using var doc = PtgDocument.Open(path);
        var scan = doc.Scans[0];

        Assert.Equal(2, scan.PointCount);
        var readPts = scan.ReadPoints().ToList();
        Assert.Equal(2, readPts.Count);
        Assert.Equal(1, readPts[0].X);
        Assert.Equal(4, readPts[1].X);
    }

    [Fact]
    public void Read_Ptg_Binary_With_Transform()
    {
        using var dir = new TempDirectory();
        var points = new[] { new SyntheticFixtures.PtgSamplePoint(1, 2, 3) };
        // PTG stores a row-vector matrix: the translation lives in the last row.
        double[] matrix =
        [
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            10, 20, 30, 1
        ];
        byte[] ptgBytes = SyntheticFixtures.CreateSyntheticPtgBinary(
            numCols: 1, numRows: 1, properties: 0x01, points: points, transformMatrix: matrix);

        string path = dir.Write("transformed.ptg", ptgBytes);
        using var doc = PtgDocument.Open(path);
        var scan = doc.Scans[0];

        // ApplyTransform = true (default)
        var ptTransformed = Assert.Single(scan.ReadPoints());
        Assert.Equal(11, ptTransformed.X, 3);
        Assert.Equal(22, ptTransformed.Y, 3);
        Assert.Equal(33, ptTransformed.Z, 3);

        // ApplyTransform = false
        var ptRaw = Assert.Single(scan.ReadPoints(new PclReadOptions { ApplyTransform = false }));
        Assert.Equal(1, ptRaw.X, 3);
        Assert.Equal(2, ptRaw.Y, 3);
        Assert.Equal(3, ptRaw.Z, 3);
    }

    [Fact]
    public void Read_Ptg_Binary_Rotation_Transform_Convention()
    {
        using var dir = new TempDirectory();
        var points = new[] { new SyntheticFixtures.PtgSamplePoint(1, 0, 0) };
        // A +90 degree rotation about Z in the PTG row-vector convention, plus a translation.
        // The local X axis maps to +Y, so (1,0,0) becomes (10,21,30).
        double[] matrix =
        [
            0, 1, 0, 0,
            -1, 0, 0, 0,
            0, 0, 1, 0,
            10, 20, 30, 1
        ];
        byte[] ptgBytes = SyntheticFixtures.CreateSyntheticPtgBinary(
            numCols: 1, numRows: 1, properties: 0x01, points: points, transformMatrix: matrix);

        string path = dir.Write("rotated.ptg", ptgBytes);
        using var doc = PtgDocument.Open(path);
        var pt = Assert.Single(doc.Scans[0].ReadPoints());
        Assert.Equal(10, pt.X, 3);
        Assert.Equal(21, pt.Y, 3);
        Assert.Equal(30, pt.Z, 3);
    }

    [Fact]
    public void Read_Ptg_Binary_Metadata_Properties()
    {
        using var dir = new TempDirectory();
        byte[] ptgBytes = SyntheticFixtures.CreateSyntheticPtgBinary(scanName: "StationA");
        string path = dir.Write("meta.ptg", ptgBytes);
        using var doc = PtgDocument.Open(path);
        var scan = (PtgScan)doc.Scans[0];

        Assert.Equal("StationA", scan.Name);
        Assert.Equal("1", scan.Properties["Version"]);
        Assert.Equal("Scanner", scan.Properties["ScannerName"]);
        Assert.Equal("Model", scan.Properties["ScannerModel"]);
        Assert.Equal("SyntheticSoftware", scan.Properties["SoftwareName"]);
        Assert.Equal("2000-01-01-0000", scan.Properties["CreationDate"]);
        Assert.Equal("2000-01-01-0000", scan.Properties["CreationTime"]);
    }

    [Fact]
    public void Read_Ptg_Binary_Subsampling_And_MaxPoints()
    {
        using var dir = new TempDirectory();
        var points = new[]
        {
            new SyntheticFixtures.PtgSamplePoint(1, 0, 0),
            new SyntheticFixtures.PtgSamplePoint(2, 0, 0),
            new SyntheticFixtures.PtgSamplePoint(3, 0, 0),
            new SyntheticFixtures.PtgSamplePoint(4, 0, 0),
        };
        byte[] ptgBytes = SyntheticFixtures.CreateSyntheticPtgBinary(
            numCols: 4, numRows: 1, properties: 0x01, points: points);

        string path = dir.Write("options.ptg", ptgBytes);
        using var doc = PtgDocument.Open(path);
        var scan = doc.Scans[0];

        // MaxPoints
        var limited = scan.ReadPoints(new PclReadOptions { MaxPoints = 2 }).ToList();
        Assert.Equal(2, limited.Count);
        Assert.Equal(1, limited[0].X);
        Assert.Equal(2, limited[1].X);

        // SubsampleStep
        var subsampled = scan.ReadPoints(new PclReadOptions { SubsampleStep = 2 }).ToList();
        Assert.Equal(2, subsampled.Count);
        Assert.Equal(1, subsampled[0].X);
        Assert.Equal(3, subsampled[1].X);
    }

    [Fact]
    public void Read_Ptg_Index_MultiScan()
    {
        using var dir = new TempDirectory();
        byte[] ptg1 = SyntheticFixtures.CreateSyntheticPtgBinary(
            numCols: 2, numRows: 1, properties: 0x01,
            points: [new(1, 1, 1), new(2, 2, 2)], scanName: "ScanOne");
        byte[] ptg2 = SyntheticFixtures.CreateSyntheticPtgBinary(
            numCols: 1, numRows: 1, properties: 0x01,
            points: [new(3, 3, 3)], scanName: "ScanTwo");

        string scanFolder = Path.Combine(dir.Root, "Scans");
        Directory.CreateDirectory(scanFolder);
        File.WriteAllBytes(Path.Combine(scanFolder, "scan1.ptg"), ptg1);
        File.WriteAllBytes(Path.Combine(scanFolder, "scan2.ptg"), ptg2);

        byte[] idxBytes = SyntheticFixtures.CreateSyntheticPtgIndex(
            [@"Scans\scan1.ptg", @"Scans\scan2.ptg"]);
        string indexPath = dir.Write("project.ptg", idxBytes);

        using var doc = PtgDocument.Open(indexPath);
        Assert.Equal(2, doc.Scans.Count);
        Assert.Equal(3, doc.TotalPointCount);

        Assert.Equal("ScanOne", doc.Scans[0].Name);
        Assert.Equal(2, doc.Scans[0].PointCount);

        Assert.Equal("ScanTwo", doc.Scans[1].Name);
        Assert.Equal(1, doc.Scans[1].PointCount);

        var allPts = doc.ReadAllPoints().ToList();
        Assert.Equal(3, allPts.Count);
        Assert.Equal(1, allPts[0].X);
        Assert.Equal(2, allPts[1].X);
        Assert.Equal(3, allPts[2].X);
    }

    [Fact]
    public void Read_Ptg_Index_Path_Traversal_Rejected()
    {
        using var dir = new TempDirectory();
        byte[] idxBytes = SyntheticFixtures.CreateSyntheticPtgIndex(["../../outside.ptg"]);
        string indexPath = dir.Write("escape.ptg", idxBytes);

        Assert.Throws<InvalidDataException>(() => PtgDocument.Open(indexPath));
    }

    [Fact]
    public void Ptg_Rejects_Corrupt_Magic_Or_Signature()
    {
        using var dir = new TempDirectory();
        byte[] corruptMagic = SyntheticFixtures.CreateSyntheticPtgBinary(corruptMagic: true);
        string path = dir.Write("corrupt.ptg", corruptMagic);

        Assert.Throws<InvalidDataException>(() => PtgScan.Create(path, "scan-0"));
    }

    [Fact]
    public void Ptg_Rejects_Missing_Header_End()
    {
        using var dir = new TempDirectory();
        // Records stop before %%header_end and the file ends: the next record length cannot be read.
        byte[] records = SyntheticFixtures.PtgHeaderRecords(
            SyntheticFixtures.PtgTlvRecord("%%header_begin", []),
            SyntheticFixtures.PtgU32Record("%%rows", 1),
            SyntheticFixtures.PtgU32Record("%%cols", 1),
            SyntheticFixtures.PtgU32Record("%%properties", 1));
        byte[] file = [0x50, 0x54, 0x47, 0x00, 0xC7, 0xA3, 0x8F, 0x92, .. records];
        string path = dir.Write("no_end.ptg", file);

        Assert.Throws<InvalidDataException>(() => PtgScan.Create(path, "scan-0"));
    }

    [Fact]
    public void Ptg_Rejects_Missing_Or_Invalid_Dimensions()
    {
        using var dir = new TempDirectory();
        // Missing %%rows
        byte[] noRows = SyntheticFixtures.PtgHeaderRecords(
            SyntheticFixtures.PtgTlvRecord("%%header_begin", []),
            SyntheticFixtures.PtgU32Record("%%cols", 1),
            SyntheticFixtures.PtgU32Record("%%properties", 1),
            SyntheticFixtures.PtgTlvRecord("%%header_end", []));
        string path1 = dir.Write("no_rows.ptg", SyntheticFixtures.CreateSyntheticPtgBinary(customHeader: noRows));
        Assert.Throws<InvalidDataException>(() => PtgScan.Create(path1, "scan-0"));

        // Zero rows
        byte[] zeroRows = SyntheticFixtures.PtgHeaderRecords(
            SyntheticFixtures.PtgTlvRecord("%%header_begin", []),
            SyntheticFixtures.PtgU32Record("%%rows", 0),
            SyntheticFixtures.PtgU32Record("%%cols", 1),
            SyntheticFixtures.PtgU32Record("%%properties", 1),
            SyntheticFixtures.PtgTlvRecord("%%header_end", []));
        string path2 = dir.Write("zero_rows.ptg", SyntheticFixtures.CreateSyntheticPtgBinary(customHeader: zeroRows));
        Assert.Throws<InvalidDataException>(() => PtgScan.Create(path2, "scan-0"));

        // Oversized dimensions
        byte[] huge = SyntheticFixtures.PtgHeaderRecords(
            SyntheticFixtures.PtgTlvRecord("%%header_begin", []),
            SyntheticFixtures.PtgU32Record("%%rows", 200000),
            SyntheticFixtures.PtgU32Record("%%cols", 1),
            SyntheticFixtures.PtgU32Record("%%properties", 1),
            SyntheticFixtures.PtgTlvRecord("%%header_end", []));
        string path3 = dir.Write("huge.ptg", SyntheticFixtures.CreateSyntheticPtgBinary(customHeader: huge));
        Assert.Throws<InvalidDataException>(() => PtgScan.Create(path3, "scan-0"));
    }

    [Fact]
    public void Ptg_Rejects_Invalid_Properties()
    {
        using var dir = new TempDirectory();
        // Both float (0x1) and double (0x2)
        byte[] both = SyntheticFixtures.PtgHeaderRecords(
            SyntheticFixtures.PtgTlvRecord("%%header_begin", []),
            SyntheticFixtures.PtgU32Record("%%rows", 1),
            SyntheticFixtures.PtgU32Record("%%cols", 1),
            SyntheticFixtures.PtgU32Record("%%properties", 3),
            SyntheticFixtures.PtgTlvRecord("%%header_end", []));
        string path1 = dir.Write("both.ptg", SyntheticFixtures.CreateSyntheticPtgBinary(customHeader: both));
        Assert.Throws<InvalidDataException>(() => PtgScan.Create(path1, "scan-0"));

        // Neither float nor double (only intensity 0x4)
        byte[] neither = SyntheticFixtures.PtgHeaderRecords(
            SyntheticFixtures.PtgTlvRecord("%%header_begin", []),
            SyntheticFixtures.PtgU32Record("%%rows", 1),
            SyntheticFixtures.PtgU32Record("%%cols", 1),
            SyntheticFixtures.PtgU32Record("%%properties", 4),
            SyntheticFixtures.PtgTlvRecord("%%header_end", []));
        string path2 = dir.Write("neither.ptg", SyntheticFixtures.CreateSyntheticPtgBinary(customHeader: neither));
        Assert.Throws<InvalidDataException>(() => PtgScan.Create(path2, "scan-0"));

        // Unsupported property bits
        byte[] unsupported = SyntheticFixtures.PtgHeaderRecords(
            SyntheticFixtures.PtgTlvRecord("%%header_begin", []),
            SyntheticFixtures.PtgU32Record("%%rows", 1),
            SyntheticFixtures.PtgU32Record("%%cols", 1),
            SyntheticFixtures.PtgU32Record("%%properties", 0x21),
            SyntheticFixtures.PtgTlvRecord("%%header_end", []));
        string path3 = dir.Write("unsupported.ptg", SyntheticFixtures.CreateSyntheticPtgBinary(customHeader: unsupported));
        Assert.Throws<InvalidDataException>(() => PtgScan.Create(path3, "scan-0"));
    }

    [Fact]
    public void Ptg_Rejects_Unknown_Header_Key_Or_Version()
    {
        using var dir = new TempDirectory();
        byte[] unknownKey = SyntheticFixtures.PtgHeaderRecords(
            SyntheticFixtures.PtgTlvRecord("%%header_begin", []),
            SyntheticFixtures.PtgStringRecord("%%mystery", "value"),
            SyntheticFixtures.PtgU32Record("%%rows", 1),
            SyntheticFixtures.PtgU32Record("%%cols", 1),
            SyntheticFixtures.PtgU32Record("%%properties", 1),
            SyntheticFixtures.PtgTlvRecord("%%header_end", []));
        string path1 = dir.Write("unknown_key.ptg", SyntheticFixtures.CreateSyntheticPtgBinary(customHeader: unknownKey));
        Assert.Throws<NotSupportedException>(() => PtgScan.Create(path1, "scan-0"));

        byte[] badVersion = SyntheticFixtures.PtgHeaderRecords(
            SyntheticFixtures.PtgTlvRecord("%%header_begin", []),
            SyntheticFixtures.PtgU32Record("%%version", 2),
            SyntheticFixtures.PtgU32Record("%%rows", 1),
            SyntheticFixtures.PtgU32Record("%%cols", 1),
            SyntheticFixtures.PtgU32Record("%%properties", 1),
            SyntheticFixtures.PtgTlvRecord("%%header_end", []));
        string path2 = dir.Write("bad_version.ptg", SyntheticFixtures.CreateSyntheticPtgBinary(customHeader: badVersion));
        Assert.Throws<NotSupportedException>(() => PtgScan.Create(path2, "scan-0"));
    }

    [Fact]
    public void Ptg_Rejects_Oversized_Name_Or_Truncated_Transform()
    {
        using var dir = new TempDirectory();
        // A record name length beyond the 256-byte bound.
        byte[] hugeName = [0x50, 0x54, 0x47, 0x00, 0xC7, 0xA3, 0x8F, 0x92, 0xFF, 0xFF, 0xFF, 0xFF];
        string path1 = dir.Write("huge_name.ptg", hugeName);
        Assert.Throws<InvalidDataException>(() => PtgScan.Create(path1, "scan-0"));

        // A %%transform record whose value is cut short; the file ends inside the matrix.
        byte[] records = SyntheticFixtures.PtgHeaderRecords(
            SyntheticFixtures.PtgTlvRecord("%%header_begin", []),
            SyntheticFixtures.PtgU32Record("%%rows", 1),
            SyntheticFixtures.PtgU32Record("%%cols", 1),
            SyntheticFixtures.PtgU32Record("%%properties", 1),
            SyntheticFixtures.PtgTlvRecord("%%transform", new byte[64]));
        byte[] file = [0x50, 0x54, 0x47, 0x00, 0xC7, 0xA3, 0x8F, 0x92, .. records];
        string path2 = dir.Write("short_transform.ptg", file);
        Assert.Throws<InvalidDataException>(() => PtgScan.Create(path2, "scan-0"));
    }

    [Fact]
    public void Ptg_Rejects_Corrupt_Offsets_Or_Truncated_Data()
    {
        using var dir = new TempDirectory();
        // Corrupt offset (points to offset 0)
        byte[] badOffset = SyntheticFixtures.CreateSyntheticPtgBinary(corruptOffsets: true);
        string path1 = dir.Write("bad_offset.ptg", badOffset);
        Assert.Throws<InvalidDataException>(() => PtgScan.Create(path1, "scan-0"));

        // Truncated data
        byte[] normal = SyntheticFixtures.CreateSyntheticPtgBinary();
        byte[] truncated = normal[..(normal.Length - 10)];
        string path2 = dir.Write("truncated.ptg", truncated);
        Assert.Throws<InvalidDataException>(() => PtgScan.Create(path2, "scan-0"));
    }

    [Fact]
    public void Ptg_Index_Rejects_Empty_References_Or_Missing_Files()
    {
        using var dir = new TempDirectory();
        // Empty index
        byte[] emptyIdx = SyntheticFixtures.CreateSyntheticPtgIndex([]);
        string emptyPath = dir.Write("empty.ptg", emptyIdx);
        Assert.Throws<InvalidDataException>(() => PtgDocument.Open(emptyPath));

        // Missing referenced scan
        byte[] missingScanIdx = SyntheticFixtures.CreateSyntheticPtgIndex(["nonexistent.ptg"]);
        string missingPath = dir.Write("missing_ref.ptg", missingScanIdx);
        Assert.Throws<FileNotFoundException>(() => PtgDocument.Open(missingPath));
    }

    // ---- CC BIN (.bin) ------------------------------------------------------------------------------------------

    [Fact]
    public void Cc_Bin_Reads_Points_Coordinates_Colors_And_Intensity()
    {
        using var dir = new TempDirectory();
        var samplePoints = new List<CcBinSamplePoint>
        {
            new(1.0, 2.0, 3.0, new PclColor(255, 0, 0), 10.5f),
            new(4.0, 5.0, 6.0, new PclColor(0, 255, 0), 20.0f),
            new(7.0, 8.0, 9.0, new PclColor(0, 0, 255), 30.25f),
        };

        byte[] bytes = SyntheticFixtures.CreateSyntheticCcBin(samplePoints, name: "TestCloud");
        string path = dir.Write("test.bin", bytes);

        using var doc = CcBinDocument.Open(path);
        Assert.Equal(PclFileKind.CcBin, doc.Kind);
        Assert.Single(doc.Scans);

        var scan = doc.Scans[0];
        Assert.Equal("TestCloud", scan.Name);
        Assert.Equal(3, scan.PointCount);
        Assert.True(scan.HasColors);
        Assert.True(scan.HasIntensities);
        Assert.False(scan.HasNormals);

        var points = scan.ReadPoints().ToList();
        Assert.Equal(3, points.Count);

        Assert.Equal(1.0, points[0].X, precision: 4);
        Assert.Equal(2.0, points[0].Y, precision: 4);
        Assert.Equal(3.0, points[0].Z, precision: 4);
        Assert.Equal(new PclColor(255, 0, 0), points[0].Color);
        Assert.Equal(10.5f, points[0].Intensity);
        Assert.Equal(0UL, points[0].Index);

        Assert.Equal(4.0, points[1].X, precision: 4);
        Assert.Equal(5.0, points[1].Y, precision: 4);
        Assert.Equal(6.0, points[1].Z, precision: 4);
        Assert.Equal(new PclColor(0, 255, 0), points[1].Color);
        Assert.Equal(20.0f, points[1].Intensity);
        Assert.Equal(1UL, points[1].Index);

        Assert.Equal(7.0, points[2].X, precision: 4);
        Assert.Equal(8.0, points[2].Y, precision: 4);
        Assert.Equal(9.0, points[2].Z, precision: 4);
        Assert.Equal(new PclColor(0, 0, 255), points[2].Color);
        Assert.Equal(30.25f, points[2].Intensity);
        Assert.Equal(2UL, points[2].Index);
    }

    [Fact]
    public void Cc_Bin_Applies_Shift_And_Scale_Transform()
    {
        using var dir = new TempDirectory();
        var samplePoints = new List<CcBinSamplePoint>
        {
            new(100.0, 200.0, 300.0),
        };

        // Shift by (10, 20, 30) and scale by 2.0 -> original global coordinates:
        // P_global = P_local / scale - shift: (100 / 2 - 10, 200 / 2 - 20, 300 / 2 - 30) = (40, 80, 120)
        byte[] bytes = SyntheticFixtures.CreateSyntheticCcBin(
            samplePoints,
            shiftX: 10.0,
            shiftY: 20.0,
            shiftZ: 30.0,
            scale: 2.0);

        string path = dir.Write("shifted.bin", bytes);
        using var doc = CcBinDocument.Open(path);
        var scan = doc.Scans[0];

        // Without transform: raw local
        var raw = scan.ReadPoints(new PclReadOptions { ApplyTransform = false }).First();
        Assert.Equal(100.0, raw.X, precision: 4);
        Assert.Equal(200.0, raw.Y, precision: 4);
        Assert.Equal(300.0, raw.Z, precision: 4);

        // With transform
        var transformed = scan.ReadPoints(new PclReadOptions { ApplyTransform = true }).First();
        Assert.Equal(40.0, transformed.X, precision: 4);
        Assert.Equal(80.0, transformed.Y, precision: 4);
        Assert.Equal(120.0, transformed.Z, precision: 4);
    }

    [Theory]
    [InlineData(41u, false)]
    [InlineData(42u, true)]
    [InlineData(50u, false)]
    [InlineData(50u, true)]
    public void Cc_Bin_Reads_Normals_Across_Batches_And_Optional_Colors(uint version, bool withColors)
    {
        using var dir = new TempDirectory();
        var points = Enumerable.Range(0, 1027).Select(i => new CcBinSamplePoint(i, 0, 0, Intensity: i)).ToArray();
        var codes = new uint[points.Length];
        codes[513] = 0x130000;
        codes[1026] = 0x200000;
        byte[] bytes = SyntheticFixtures.CreateSyntheticCcBin(points, version: version, withColors: withColors,
            normalCodes: codes);

        using var doc = PclFile.Open(dir.Write("normals.bin", bytes));
        var scan = Assert.Single(doc.Scans);
        Assert.True(scan.HasNormals);
        Assert.True(scan.HasIntensities);
        Assert.Equal(withColors, scan.HasColors);

        var sample = scan.ReadPoints(new PclReadOptions { SubsampleStep = 513, MaxPoints = 3 }).ToArray();
        Assert.Equal(new[] { 0d, 513d, 1026d }, sample.Select(p => p.X));
        Assert.Equal(new float?[] { 0, 513, 1026 }, sample.Select(p => p.Intensity));
        double axisLength = Math.Sqrt(1023 * 1023 + 2);
        Assert.Equal(new PclVector3(1023 / axisLength, 1 / axisLength, 1 / axisLength), sample[0].Normal);
        double reflectedLength = Math.Sqrt(1 + 2 * 511 * 511);
        Assert.Equal(new PclVector3(-1 / reflectedLength, 511 / reflectedLength, 511 / reflectedLength), sample[1].Normal);
        Assert.Equal(PclVector3.Zero, sample[2].Normal);
    }

    [Fact]
    public void Cc_Bin_Transforms_Normal_Directions_Without_Shift_Or_Scale_Magnitude()
    {
        using var dir = new TempDirectory();
        double[] matrix = [0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
        byte[] bytes = SyntheticFixtures.CreateSyntheticCcBin([new(1, 0, 0)], version: 50, normalCodes: [0],
            matrix16: matrix, shiftX: 12, scale: 1e-200);

        using var doc = PclFile.Open(dir.Write("rotated.bin", bytes));
        var a = Assert.Single(doc.ReadAllPoints(new PclReadOptions { ApplyTransform = false })).Normal!.Value;
        var b = Assert.Single(doc.ReadAllPoints()).Normal!.Value;
        Assert.Equal(-a.Y, b.X, precision: 14);
        Assert.Equal(a.X, b.Y, precision: 14);
        Assert.Equal(a.Z, b.Z, precision: 14);
    }

    [Fact]
    public void Cc_Bin_Rejects_Truncated_And_Mismatched_Normal_Arrays()
    {
        using var dir = new TempDirectory();
        byte[] mismatch = SyntheticFixtures.CreateSyntheticCcBin([new(1, 2, 3), new(4, 5, 6)], version: 50,
            normalCodes: [0]);
        Assert.Throws<InvalidDataException>(() => CcBinDocument.Open(dir.Write("bad_count.bin", mismatch)));

        byte[] truncated = SyntheticFixtures.CreateSyntheticCcBin([new(1, 2, 3)], version: 50,
            normalCodes: [0], withIntensity: false);
        Assert.Throws<InvalidDataException>(() => CcBinDocument.Open(dir.Write("cut_normals.bin", truncated[..^5])));
    }

    [Fact]
    public void Cc_Bin_Rejects_Out_Of_Range_Normal_Codes()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticCcBin([new(1, 2, 3)], version: 50, normalCodes: [0x200001]);
        using var doc = CcBinDocument.Open(dir.Write("bad_index.bin", bytes));
        Assert.Throws<InvalidDataException>(() => doc.ReadAllPoints().Single());
    }

    [Fact]
    public void Cc_Bin_Rejects_Normals_In_An_Unsupported_Version()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticCcBin([new(1, 2, 3)], version: 47, normalCodes: [0]);
        Assert.Throws<NotSupportedException>(() => CcBinDocument.Open(dir.Write("unsupported_normals.bin", bytes)));
    }

    [Fact]
    public void Cc_Bin_Opens_Adjacent_Multi_Cloud_Entities()
    {
        using var dir = new TempDirectory();
        var cloud1 = new List<CcBinSamplePoint> { new(1, 2, 3), new(4, 5, 6) };
        var cloud2 = new List<CcBinSamplePoint> { new(7, 8, 9), new(10, 11, 12) };

        byte[] bytes = SyntheticFixtures.CreateSyntheticCcBinMultiCloud([cloud1, cloud2]);
        string path = dir.Write("multi.bin", bytes);

        using var doc = CcBinDocument.Open(path);
        Assert.Equal(2, doc.Scans.Count);
        Assert.Equal("Cloud_1", doc.Scans[0].Name);
        Assert.Equal(2, doc.Scans[0].PointCount);
        Assert.Equal("Cloud_2", doc.Scans[1].Name);
        Assert.Equal(2, doc.Scans[1].PointCount);
    }

    [Fact]
    public void Cc_Bin_Rejects_Truncated_Coordinates_In_A_Later_Cloud()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticCcBinMultiCloud(
            [[new(1, 2, 3)], [new(4, 5, 6), new(7, 8, 9)]], truncateLastCloudPoints: true);

        var error = Assert.Throws<InvalidDataException>(() => PclFile.Open(dir.Write("truncated_xyz.bin", bytes)));
        Assert.Contains("XYZ coordinates", error.Message);
    }

    [Fact]
    public void Cc_Bin_Rejects_An_Oversized_Point_Count_In_A_Later_Cloud()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticCcBinMultiCloud(
            [[new(1, 2, 3)], [new(4, 5, 6)]], declaredLastCloudPointCount: uint.MaxValue);

        var error = Assert.Throws<InvalidDataException>(() => PclFile.Open(dir.Write("oversized_count.bin", bytes)));
        Assert.Contains("XYZ coordinates", error.Message);
    }

    [Fact]
    public void Cc_Bin_Reads_Intensity_Within_Declared_Count_Only()
    {
        using var dir = new TempDirectory();
        var samplePoints = new List<CcBinSamplePoint>
        {
            new(1.0, 2.0, 3.0, Intensity: 12f),
            new(4.0, 5.0, 6.0, Intensity: 0f),
        };

        // 2 points, but only 1 intensity value declared in the scalar field
        byte[] bytes = SyntheticFixtures.CreateSyntheticCcBin(
            samplePoints,
            declaredIntensityCount: 1);

        string path = dir.Write("partial_intensity.bin", bytes);
        using var doc = CcBinDocument.Open(path);
        var scan = doc.Scans[0];

        var points = scan.ReadPoints().ToList();
        Assert.Equal(2, points.Count);
        Assert.Equal(12f, points[0].Intensity);
        Assert.Null(points[1].Intensity);
    }

    [Fact]
    public void Cc_Bin_Rejects_Truncated_Color_Block()
    {
        using var dir = new TempDirectory();
        var samplePoints = new List<CcBinSamplePoint> { new(1.0, 2.0, 3.0) };

        // Color flag is 1, but file ends right after the flag
        byte[] bytes = SyntheticFixtures.CreateSyntheticCcBin(samplePoints, truncateColorBlock: true);
        string path = dir.Write("cut_color.bin", bytes);

        Assert.Throws<InvalidDataException>(() => CcBinDocument.Open(path));
    }

    [Fact]
    public void Cc_Bin_Rejects_Truncated_Scalar_Field_Data()
    {
        using var dir = new TempDirectory();
        var samplePoints = new List<CcBinSamplePoint> { new(1.0, 2.0, 3.0), new(4.0, 5.0, 6.0) };

        // 2 points declared in scalar field, but stream truncated after 1 value
        byte[] bytes = SyntheticFixtures.CreateSyntheticCcBin(samplePoints, truncateIntensityData: true);
        string path = dir.Write("cut_scalar.bin", bytes);

        Assert.Throws<InvalidDataException>(() => CcBinDocument.Open(path));
    }

    [Fact]
    public void Cc_Bin_Rejects_Truncated_Scalar_Field_Count()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticCcBin([new(1, 2, 3)], withIntensity: false);
        string path = dir.Write("cut_scalar_count.bin", bytes[..^2]);

        Assert.Throws<InvalidDataException>(() => CcBinDocument.Open(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cc_Bin_Reads_Intensity_After_Scalar_Display_Metadata(bool withPalette)
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticCcBin([new(1, 2, 3, Intensity: 42)]);
        int fieldStart = bytes.AsSpan().IndexOf("Intensity"u8);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(fieldStart - 4, 4), 2);
        byte[] firstField = bytes[fieldStart..(fieldStart + 261 + sizeof(float))];
        firstField.AsSpan(0, 256).Clear();
        "Time"u8.CopyTo(firstField);

        byte[] display = new byte[53];
        byte[] palette = [];
        if (withPalette)
        {
            display[^1] = 1;
            byte[] nameAndId =
            [
                0, 0, 0, 8, 0, (byte)'G', 0, (byte)'r', 0, (byte)'e', 0, (byte)'y',
                0, 0, 0, 2, 0, (byte)'1',
            ];
            byte[] paletteSteps = new byte[18 + 4 + 2 * 19];
            paletteSteps[18] = 2;
            palette = [.. nameAndId, .. paletteSteps];
        }

        string path = dir.Write("two_scalars.bin", [..bytes.AsSpan(0, fieldStart), ..firstField, ..display,
            ..palette, ..new byte[16], ..bytes.AsSpan(fieldStart)]);

        using var doc = PclFile.Open(path);
        Assert.Equal(42f, Assert.Single(doc.ReadAllPoints()).Intensity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cc_Bin_Transforms_Finite_Points_With_Subnormal_Scale(bool withMatrix)
    {
        using var dir = new TempDirectory();
        const float x = 1e-30f;
        double[]? matrix = withMatrix ? [1, 0, 0, 0, 0, -1, 0, 0, 0, 0, -1, 0, 0, 0, 0, 1] : null;
        byte[] bytes = SyntheticFixtures.CreateSyntheticCcBin([new(x, 0, 0), new(1, 0, 0)],
            scale: double.Epsilon, matrix16: matrix);

        using var doc = PclFile.Open(dir.Write("subnormal.bin", bytes));
        var point = Assert.Single(doc.ReadAllPoints());
        Assert.Equal(new PclVector3(x / double.Epsilon, 0, 0), point.Position);
        Assert.Equal(point.Position, doc.Scans[0].Transform.TransformPoint(new PclVector3(x, 0, 0)));
        Assert.Equal(2, doc.ReadAllPoints(new PclReadOptions { ApplyTransform = false }).Count());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Cc_Bin_Rejects_Invalid_Global_Scale(double scale)
    {
        using var dir = new TempDirectory();
        var path = dir.Write("bad_scale.bin", SyntheticFixtures.CreateSyntheticCcBin([new(1, 2, 3)], scale: scale));
        Assert.Throws<InvalidDataException>(() => CcBinDocument.Open(path));
    }

    [Fact]
    public void Cc_Bin_Transforms_With_Tiny_Scale()
    {
        using var dir = new TempDirectory();
        var samplePoints = new List<CcBinSamplePoint> { new(1.0, 0.0, 0.0) };

        byte[] bytes = SyntheticFixtures.CreateSyntheticCcBin(
            samplePoints,
            scale: 1e-13);

        string path = dir.Write("tiny_scale.bin", bytes);
        using var doc = CcBinDocument.Open(path);
        var scan = doc.Scans[0];

        var point = scan.ReadPoints(new PclReadOptions { ApplyTransform = true }).First();
        Assert.Equal(1e13, point.X, precision: 1);
    }

    [Fact]
    public void Cc_Bin_Composes_Transform_Matrix_With_Shift_And_Scale()
    {
        using var dir = new TempDirectory();
        var samplePoints = new List<CcBinSamplePoint> { new(0.0, 0.0, 0.0) };

        // Rotation around X (4x4 matrix):
        // 1  0  0  0
        // 0  0 -1  0
        // 0  1  0  0
        // 0  0  0  1
        double[] rotX =
        [
            1, 0, 0, 0,
            0, 0, -1, 0,
            0, 1, 0, 0,
            0, 0, 0, 1
        ];

        byte[] bytes = SyntheticFixtures.CreateSyntheticCcBin(
            samplePoints,
            shiftX: 10.0,
            shiftY: 0.0,
            shiftZ: 0.0,
            scale: 1.0,
            matrix16: rotX);

        string path = dir.Write("rot_shift.bin", bytes);
        using var doc = CcBinDocument.Open(path);
        var scan = doc.Scans[0];

        var point = scan.ReadPoints(new PclReadOptions { ApplyTransform = true }).First();
        // Point is (0, 0, 0). After rotation around X: (0, 0, 0).
        // Then global shift and scale: P_global = P_rot / 1.0 - shiftX = 0 - 10 = -10.
        Assert.Equal(-10.0, point.X, precision: 4);
    }

    [Fact]
    public void Cc_Bin_Supports_Subsample_And_MaxPoints()
    {
        using var dir = new TempDirectory();
        var samplePoints = Enumerable.Range(0, 100)
            .Select(i => new CcBinSamplePoint(i, i * 2, i * 3))
            .ToList();

        byte[] bytes = SyntheticFixtures.CreateSyntheticCcBin(samplePoints);
        string path = dir.Write("large.bin", bytes);
        using var doc = CcBinDocument.Open(path);
        var scan = doc.Scans[0];

        Assert.Equal(10, scan.ReadPoints(new PclReadOptions { MaxPoints = 10 }).Count());
        Assert.Equal(20, scan.ReadPoints(new PclReadOptions { SubsampleStep = 5 }).Count());
        Assert.Empty(scan.ReadPoints(new PclReadOptions { MaxPoints = 0 }));
    }

    [Fact]
    public void Cc_Bin_Rejects_Invalid_Signature_Or_Version()
    {
        using var dir = new TempDirectory();
        var pts = new List<CcBinSamplePoint> { new(1, 2, 3) };

        // Wrong signature
        byte[] badSig = SyntheticFixtures.CreateSyntheticCcBin(pts, rawMagic: "XXXX"u8.ToArray());
        string path1 = dir.Write("bad_sig.bin", badSig);
        Assert.Throws<InvalidDataException>(() => CcBinDocument.Open(path1));

        // Unsupported version
        byte[] badVer = SyntheticFixtures.CreateSyntheticCcBin(pts, version: 5);
        string path2 = dir.Write("bad_ver.bin", badVer);
        Assert.Throws<InvalidDataException>(() => CcBinDocument.Open(path2));
    }

    [Fact]
    public void Cc_Bin_Rejects_Truncated_Files()
    {
        using var dir = new TempDirectory();
        var pts = new List<CcBinSamplePoint> { new(1, 2, 3), new(4, 5, 6) };

        // Header truncated < 8 bytes
        string path1 = dir.Write("cut_header.bin", [0x43, 0x43, 0x42]);
        Assert.Throws<InvalidDataException>(() => CcBinDocument.Open(path1));

        // Truncated points data
        byte[] cutPts = SyntheticFixtures.CreateSyntheticCcBin(pts, truncatePoints: true);
        string path2 = dir.Write("cut_points.bin", cutPts);
        Assert.Throws<InvalidDataException>(() => CcBinDocument.Open(path2));
    }

    [Fact]
    public void Cc_Bin_Loads_Custom_Properties()
    {
        using var dir = new TempDirectory();
        var pts = new List<CcBinSamplePoint> { new(1, 2, 3) };
        var customProps = new Dictionary<string, double>
        {
            ["Custom.Step"] = 0.05,
            ["Scale.Factor"] = 1.25,
        };

        byte[] bytes = SyntheticFixtures.CreateSyntheticCcBin(pts, doubleProperties: customProps);
        string path = dir.Write("props.bin", bytes);
        using var doc = CcBinDocument.Open(path);
        var scan = doc.Scans[0];

        Assert.Equal("0.05", scan.Properties["Custom.Step"]);
        Assert.Equal("1.25", scan.Properties["Scale.Factor"]);
    }
}
