using PclIO.Formats.E57;
using PclIO.Formats.EdgeWise;
using PclIO.Formats.Faro;
using PclIO.Formats.Leica;
using PclIO.Formats.PinPoint;
using PclIO.Formats.Recap;
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
        Assert.Equal(12345, doc.DeclaredPointCount);
        Assert.Equal(new PclVector3(10.5, 20.5, 30.5), doc.ScanTransform.Translation);
        Assert.Equal(PclVector3.One, doc.ScanTransform.Scale);
        Assert.Equal(new PclBounds(new PclVector3(-10, -10, -10), new PclVector3(10, 10, 10)), doc.LocalBounds);
        Assert.Equal("00000000-0000-0000-0000-000000000001", doc.ScanGuid);
        Assert.Equal("LOCAL_GRID_SYSTEM", doc.CoordinateSystem);

        var scan = Assert.Single(doc.Scans);
        Assert.Equal(12345, scan.PointCount);
        Assert.Equal(12345, doc.TotalPointCount);
    }

    [Fact]
    public void Rcs_With_An_Empty_Node_Table_Yields_No_Points()
    {
        using var dir = new TempDirectory();
        using var doc = RcsScanDocument.Open(dir.Write("empty.rcs", SyntheticFixtures.CreateSyntheticRcs(pointCount: 10)));

        Assert.Empty(doc.ReadAllPoints());
    }

    private static readonly RcsSamplePoint[] RcsPoints =
    [
        new(1000, 2000, 3000, 128, 10, 20, 30, 0, 0, 127),
        new(0, 0, 0, 255, 1, 2, 3),
        new(65535, 1, 2, 0, 255, 254, 253, 127, 0, 0),
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

        Assert.Equal(3, raw.Count);
        AssertNear(new PclVector3(1.6, -2.3, 3.8), raw[0].Position);
        AssertNear(new PclVector3(1.5, -2.5, 3.5), raw[1].Position);
        AssertNear(new PclVector3(1.5 + 6.5535, -2.4999, 3.5002), raw[2].Position);
        AssertNear(new PclVector3(11.6, 17.7, 33.8), moved[0].Position);

        Assert.Equal(new PclColor(10, 20, 30), raw[0].Color);
        Assert.Equal(128 / 255.0f, raw[0].Intensity);
        Assert.Equal(1.0f, raw[1].Intensity);
        Assert.Equal(new[] { 0UL, 1UL, 2UL }, raw.Select(p => p.Index));

        // Packed normals are unit vectors; an all-zero normal means "none".
        AssertNear(new PclVector3(0, 0, 1), raw[0].Normal!.Value);
        Assert.Null(raw[1].Normal);
        AssertNear(new PclVector3(1, 0, 0), raw[2].Normal!.Value);
    }

    [Fact]
    public void Read_Synthetic_Rcs_Points_Honours_Limit_And_Step()
    {
        using var dir = new TempDirectory();
        using var doc = RcsScanDocument.Open(dir.Write("points.rcs", SyntheticFixtures.CreateSyntheticRcsWithPoints(RcsPoints, (0, 0, 0))));

        Assert.Equal(2, doc.ReadAllPoints(new PclReadOptions { MaxPoints = 2 }).Count());
        Assert.Equal(3, doc.ReadAllPoints(new PclReadOptions { MaxPoints = 99 }).Count());
        Assert.Equal(2, doc.ReadAllPoints(new PclReadOptions { SubsampleStep = 2 }).Count());
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
        Assert.Throws<NotSupportedException>(() => doc.ReadAllPoints().ToList());
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
        Assert.Empty(scan.ReadPoints());
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
    public void E57_With_Scaled_Integer_Coordinates_Opens_But_Is_Not_Decoded()
    {
        const string prototype = """
            <prototype type="Structure">
              <cartesianX type="ScaledInteger" minimum="0" maximum="1000" scale="0.001"/>
              <cartesianY type="ScaledInteger" minimum="0" maximum="1000" scale="0.001"/>
              <cartesianZ type="ScaledInteger" minimum="0" maximum="1000" scale="0.001"/>
            </prototype>
            """;
        using var dir = new TempDirectory();
        using var doc = E57Document.Open(dir.Write("scaled.e57", new E57Spec { PrototypeXml = prototype }.Build()));

        Assert.Single(doc.Scans);
        Assert.Throws<NotSupportedException>(() => doc.Scans[0].ReadPoints().ToList());
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

        // The record layout of a folder scan is assumed, and the document says so.
        Assert.Contains("assumed", Assert.Single(doc.Warnings), StringComparison.Ordinal);

        var scan = Assert.Single(doc.Scans);
        Assert.Equal(SyntheticFixtures.FaroRows * SyntheticFixtures.FaroCols, scan.PointCount);

        var points = scan.ReadPoints().ToList();
        AssertFaroPoints(points);
        Assert.All(points, p => Assert.Equal(0.5f, p.Intensity));
        Assert.Equal(10, scan.ReadPoints(new PclReadOptions { MaxPoints = 10 }).Count());
        Assert.Equal(16, scan.ReadPoints(new PclReadOptions { SubsampleStep = 2 }).Count());
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
}
