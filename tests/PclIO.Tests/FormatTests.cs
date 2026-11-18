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
    public void An_Rcs_Without_Colour_Reports_No_Colour_And_No_Intensity()
    {
        using var dir = new TempDirectory();
        using var doc = RcsScanDocument.Open(dir.Write("plain.rcs",
            SyntheticFixtures.CreateSyntheticRcsWithPoints(RcsPoints, (0, 0, 0), withColor: false)));

        var scan = Assert.Single(doc.Scans);
        Assert.False(scan.HasColors);
        Assert.False(scan.HasIntensities);
        Assert.False(scan.HasNormals);
        Assert.False(doc.HasColorData);

        var points = scan.ReadPoints(NoTransform).ToList();
        Assert.Equal(3, points.Count);
        Assert.All(points, p =>
        {
            Assert.Null(p.Color);
            Assert.Null(p.Intensity);
            Assert.Null(p.Normal);
        });
    }

    [Theory]
    [InlineData(12)]
    [InlineData(14)]
    [InlineData(17)]
    [InlineData(21)]
    public void An_Rcs_Node_With_An_Unknown_Record_Size_Is_Rejected(int bytesPerPoint)
    {
        // The decoder knows records of ten, sixteen and twenty bytes; any other size would be read with the
        // wrong stride and return wrong positions, so the file is rejected instead of guessed.
        using var dir = new TempDirectory();
        using var doc = RcsScanDocument.Open(dir.Write("odd.rcs",
            SyntheticFixtures.CreateSyntheticRcsWithPoints(RcsPoints, (0, 0, 0),
                declaredPayloadBytes: (uint)(RcsPoints.Length * bytesPerPoint))));

        Assert.Throws<NotSupportedException>(() => doc.Scans[0].ReadPoints().ToList());
    }

    [Fact]
    public void An_Rcs_Position_Only_Node_Larger_Than_A_Chunk_Keeps_Record_Alignment()
    {
        // A chunk of 65536 bytes holds 6553 whole ten-byte records; the next chunk must start at a record
        // boundary, not six bytes into a record.
        var points = Enumerable.Range(0, 7000)
            .Select(i => new RcsSamplePoint((ushort)(i % 1000), (ushort)(i / 1000), 0, 0, 0, 0, 0)).ToList();
        using var dir = new TempDirectory();
        using var doc = RcsScanDocument.Open(dir.Write("big.rcs",
            SyntheticFixtures.CreateSyntheticRcsWithPoints(points, (0, 0, 0), withColor: false)));

        var all = doc.Scans[0].ReadPoints(NoTransform).ToList();
        Assert.Equal(7000, all.Count);
        AssertNear(new PclVector3(0.0553, 0.0006, 0), all[6553].Position);
        AssertNear(new PclVector3(0.0999, 0.0006, 0), all[6999].Position);
    }

    [Fact]
    public void An_Rcs_With_Colour_And_Normals_Reports_Them()
    {
        using var dir = new TempDirectory();
        using var doc = RcsScanDocument.Open(dir.Write("full.rcs",
            SyntheticFixtures.CreateSyntheticRcsWithPoints(RcsPoints, (0, 0, 0), withNormals: true)));

        var scan = Assert.Single(doc.Scans);
        Assert.True(scan.HasColors);
        Assert.True(scan.HasIntensities);
        Assert.True(scan.HasNormals);
        Assert.True(doc.HasColorData);
        Assert.True(doc.HasNormalData);

        var points = scan.ReadPoints(NoTransform).ToList();
        Assert.Equal(3, points.Count);
        Assert.All(points, p => Assert.NotNull(p.Color));
    }

    [Fact]
    public void An_Rcs_Without_Normals_Reports_No_Normal()
    {
        using var dir = new TempDirectory();
        using var doc = RcsScanDocument.Open(dir.Write("flat.rcs",
            SyntheticFixtures.CreateSyntheticRcsWithPoints(RcsPoints, (0, 0, 0))));

        var scan = Assert.Single(doc.Scans);
        Assert.True(scan.HasColors);
        Assert.True(scan.HasIntensities);
        Assert.False(scan.HasNormals);
        Assert.All(scan.ReadPoints(NoTransform), p => Assert.Null(p.Normal));
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

    [Fact]
    public void An_Rcs_Of_A_Version_Without_A_Described_Layout_Is_Rejected_With_A_Message_That_Names_It()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticRcsWithPoints(RcsPoints, (0, 0, 0));
        bytes[8] = 4;
        using var doc = RcsScanDocument.Open(dir.Write("new.rcs", bytes));

        // The file is listed with its header, but its points cannot be decoded.
        Assert.Equal(4u, doc.MajorVersion);
        var error = Assert.Throws<NotSupportedException>(() => doc.ReadAllPoints().ToList());
        Assert.Contains("4", error.Message, StringComparison.Ordinal);
        Assert.Contains("3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Node_Layouts_That_Are_Decoded_Are_Named()
    {
        Assert.Equal([3u], RcsScanDocument.DecodedNodeLayoutVersions);
    }

    [Fact]
    public void An_Rcs_With_A_Truncated_Node_Table_Is_Rejected()
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticRcsWithPoints(RcsPoints, (0, 0, 0));

        // The file ends in the middle of the node table.
        string path = dir.Write("short.rcs", bytes[..(98605 + 100)]);
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
        var points = new[] { new RcsSamplePoint(1000, 0, 0, 0, 0, 0, 0, 89, 0, 89) };
        dir.Write(Path.Combine("site Support", "scan1.rcs"),
            SyntheticFixtures.CreateSyntheticRcsWithPoints(points, (0, 0, 0), withNormals: true));

        using var doc = RcpProjectDocument.Open(rcp);
        var pt = Assert.Single(doc.ReadAllPoints());
        AssertNear(new PclVector3(0.2, 0, 0), pt.Position);
        AssertNear(new PclVector3(2 / Math.Sqrt(5), 0, 1 / Math.Sqrt(5)), pt.Normal!.Value);
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

        Assert.Throws<InvalidDataException>(() => doc.Scans[0].ReadPoints().ToList());
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

        Assert.Throws<InvalidDataException>(() => doc.Scans[0].ReadPoints().ToList());
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
}
