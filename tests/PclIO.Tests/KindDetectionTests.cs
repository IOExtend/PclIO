using System.Buffers.Binary;
using Xunit;

namespace PclIO.Tests;

public sealed class KindDetectionTests
{
    private static readonly byte[] Ole2Signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private static PclFileKind Detect(byte[] bytes, string name)
    {
        using var ms = new MemoryStream(bytes);
        return PclFile.DetectKind(ms, name);
    }

    [Fact]
    public void Detects_ASTM_E57_Signature() =>
        Assert.Equal(PclFileKind.E57, Detect(SyntheticFixtures.CreateSyntheticE57(), "sample.e57"));

    [Fact]
    public void Detects_Recap_Rcs_Signature() =>
        Assert.Equal(PclFileKind.RecapScan, Detect(SyntheticFixtures.CreateSyntheticRcs(), "scan.rcs"));

    [Fact]
    public void Detects_Recap_Rcp_Archive_By_Extension() =>
        Assert.Equal(PclFileKind.RecapProject, Detect(SyntheticFixtures.CreateSyntheticRcpZip(), "project.rcp"));

    [Fact]
    public void Detects_Leica_Lgsx_Signature() =>
        Assert.Equal(PclFileKind.LeicaLgsx, Detect(SyntheticFixtures.CreateSyntheticLgsx(), "site.lgsx"));

    [Fact]
    public void Detects_Leica_Lgs_By_Extension() =>
        Assert.Equal(PclFileKind.LeicaLgs, Detect(SyntheticFixtures.CreateSyntheticLgsx(), "site.lgs"));

    [Theory]
    [InlineData("project.smf")]
    [InlineData("project.srf")]
    public void Detects_PinPoint_Json_Manifests(string name) =>
        Assert.Equal(PclFileKind.PinPoint, Detect(SyntheticFixtures.CreateSyntheticPinPointSmf(), name));

    [Fact]
    public void Detects_Faro_Fls_With_Container_Signature_And_Extension()
    {
        byte[] bytes = SyntheticFixtures.CreateSyntheticFlsContainer();
        Assert.Equal(PclFileKind.FaroFls, Detect(bytes, "scanner.fls"));
    }

    [Theory]
    [InlineData("sample.las")]
    [InlineData("SAMPLE.LAS")]
    [InlineData("sample.bin")]
    public void Detects_Las_By_Signature(string name) =>
        Assert.Equal(PclFileKind.Las, Detect(SyntheticFixtures.CreateSyntheticLas(), name));

    [Fact]
    public void Las_Needs_The_Signature_Not_Just_The_Extension() =>
        Assert.Equal(PclFileKind.Unknown, Detect(new byte[100], "sample.las"));

    [Fact]
    public void A_Laz_File_Is_Not_Detected_As_Las() =>
        Assert.Equal(PclFileKind.Unknown, Detect(SyntheticFixtures.CreateSyntheticLas(), "sample.laz"));

    [Theory]
    [InlineData("sample.ply")]
    [InlineData("SAMPLE.PLY")]
    [InlineData("sample.dat")]
    public void Detects_Ply_By_Header_Structure(string name) =>
        Assert.Equal(PclFileKind.Ply, Detect(SyntheticFixtures.CreateSyntheticPly(), name));

    [Fact]
    public void Ply_Needs_The_Header_Structure_Not_Just_The_Extension() =>
        Assert.Equal(PclFileKind.Unknown, Detect("just some text"u8.ToArray(), "sample.ply"));

    [Theory]
    [InlineData("sample.pcd")]
    [InlineData("SAMPLE.PCD")]
    [InlineData("sample.dat")]
    public void Detects_Pcd_By_Header_Structure(string name) =>
        Assert.Equal(PclFileKind.Pcd, Detect(SyntheticFixtures.CreateSyntheticPcd(), name));

    [Fact]
    public void Pcd_Needs_The_Header_Structure_Not_Just_The_Extension() =>
        Assert.Equal(PclFileKind.Unknown, Detect("just some text"u8.ToArray(), "sample.pcd"));

    [Theory]
    [InlineData("sample.pts")]
    [InlineData("SAMPLE.PTS")]
    [InlineData("sample.dat")]
    public void Detects_Pts_By_Count_Header(string name) =>
        Assert.Equal(PclFileKind.Pts, Detect(SyntheticFixtures.CreateSyntheticPts(), name));

    [Theory]
    [InlineData("sample.pts")]
    [InlineData("SAMPLE.PTS")]
    public void Detects_Pts_Headerless_By_Extension(string name) =>
        Assert.Equal(PclFileKind.Pts, Detect(SyntheticFixtures.CreateSyntheticPts(withCountHeader: false), name));

    [Fact]
    public void Pts_Headerless_Needs_The_Pts_Extension() =>
        Assert.Equal(PclFileKind.Unknown, Detect(SyntheticFixtures.CreateSyntheticPts(withCountHeader: false), "sample.dat"));

    [Fact]
    public void Pts_Needs_The_Structure_Not_Just_The_Extension()
    {
        Assert.Equal(PclFileKind.Unknown, Detect("just some text"u8.ToArray(), "sample.pts"));
        Assert.Equal(PclFileKind.Unknown, Detect(new byte[100], "sample.pts"));
    }

    [Theory]
    [InlineData("sample.ptx")]
    [InlineData("SAMPLE.PTX")]
    [InlineData("sample.dat")]
    public void Detects_Ptx_By_Header_Structure(string name) =>
        Assert.Equal(PclFileKind.Ptx, Detect(SyntheticFixtures.CreateSyntheticPtx(), name));

    [Fact]
    public void Ptx_Needs_The_Header_Structure_Not_Just_The_Extension()
    {
        Assert.Equal(PclFileKind.Unknown, Detect("just some text"u8.ToArray(), "sample.ptx"));
        Assert.Equal(PclFileKind.Unknown, Detect(new byte[100], "sample.ptx"));
    }

    [Theory]
    [InlineData("sample.xyz")]
    [InlineData("SAMPLE.XYZ")]
    [InlineData("sample.asc")]
    [InlineData("SAMPLE.ASC")]
    [InlineData("sample.txt")]
    [InlineData("SAMPLE.TXT")]
    [InlineData("sample.csv")]
    [InlineData("SAMPLE.CSV")]
    public void Detects_Xyz_Numeric_Lines_By_Extension(string name) =>
        Assert.Equal(PclFileKind.Xyz, Detect(SyntheticFixtures.CreateSyntheticXyz(), name));

    [Theory]
    [InlineData("sample.xyz")]
    [InlineData("sample.csv")]
    [InlineData("sample.asc")]
    [InlineData("sample.txt")]
    [InlineData("sample.dat")]
    public void Detects_Xyz_Explicit_Header_In_Any_Extension(string name)
    {
        byte[] bytes = SyntheticFixtures.CreateSyntheticXyz(delimiter: ',', header: "x,y,z,nx,ny,nz", columns: 6);
        Assert.Equal(PclFileKind.Xyz, Detect(bytes, name));
    }

    [Theory]
    [InlineData("sample.xyz")]
    [InlineData("sample.csv")]
    [InlineData("sample.asc")]
    [InlineData("sample.txt")]
    public void Xyz_Rejects_Right_Extension_With_Wrong_Content(string name)
    {
        Assert.Equal(PclFileKind.Unknown, Detect("just some text\nline two\nline three"u8.ToArray(), name));
        Assert.Equal(PclFileKind.Unknown, Detect("Name,Age,City\nAlice,30,London\nBob,25,Paris"u8.ToArray(), name));
        Assert.Equal(PclFileKind.Unknown, Detect(new byte[100], name));
    }

    [Fact]
    public void Xyz_Headerless_Needs_Recognized_Extension() =>
        Assert.Equal(PclFileKind.Unknown, Detect(SyntheticFixtures.CreateSyntheticXyz(), "sample.dat"));

    [Fact]
    public void Faro_Needs_The_Container_Signature_Not_Just_The_Extension() =>
        Assert.Equal(PclFileKind.Unknown, Detect(new byte[100], "scanner.fls"));

    [Fact]
    public void A_Generic_Compound_File_With_Another_Extension_Is_Unknown()
    {
        byte[] bytes = SyntheticFixtures.CreateSyntheticFlsContainer();
        Assert.Equal(PclFileKind.Unknown, Detect(bytes, "scanner.doc"));
        Assert.Equal(PclFileKind.Unknown, Detect(Ole2Signature.Concat(new byte[64]).ToArray(), "scanner.c3sc"));
    }

    [Fact]
    public void An_Arbitrary_Zip_Is_Not_A_Project()
    {
        byte[] zip = SyntheticFixtures.CreateZip("readme.txt", "hello");
        Assert.Equal(PclFileKind.Unknown, Detect(zip, "archive.zip"));
        Assert.Equal(PclFileKind.Unknown, Detect(SyntheticFixtures.CreateSyntheticRcpZip(), "project.zip"));
    }

    [Fact]
    public void An_Rcp_Name_On_Something_That_Is_Not_A_Zip_Is_Unknown() =>
        Assert.Equal(PclFileKind.Unknown, Detect("not an archive"u8.ToArray(), "project.rcp"));

    [Fact]
    public void A_Random_Json_File_Is_Unknown() =>
        Assert.Equal(PclFileKind.Unknown, Detect("""{ "a": 1 }"""u8.ToArray(), "data.json"));

    [Fact]
    public void A_Json_Start_Needs_The_PinPoint_Extension() =>
        Assert.Equal(PclFileKind.Unknown, Detect(SyntheticFixtures.CreateSyntheticPinPointSmf(), "project.txt"));

    [Theory]
    [InlineData("database.c3db")]
    [InlineData("cluster.c3ptx")]
    [InlineData("DATABASE.C3DB")]
    public void EdgeWise_Is_Detected_By_Extension(string name) =>
        Assert.Equal(PclFileKind.EdgeWise, Detect(new byte[16], name));

    [Theory]
    [InlineData("database.c3db")]
    [InlineData("cluster.c3ptx")]
    public void EdgeWise_Extension_Applies_To_A_Stream_Shorter_Than_The_Shortest_Signature(string name) =>
        Assert.Equal(PclFileKind.EdgeWise, Detect(new byte[2], name));

    [Fact]
    public void EdgeWise_Text_Signatures_Are_Detected_Without_Extension()
    {
        Assert.Equal(PclFileKind.EdgeWise, Detect("_Tree ( 1 1 1 ) ( 0 0 0 )"u8.ToArray(), "data.dat"));
        Assert.Equal(PclFileKind.EdgeWise, Detect("2\r\n _Tree"u8.ToArray(), "data.dat"));
    }

    [Fact]
    public void Returns_Unknown_For_Random_Bytes() =>
        Assert.Equal(PclFileKind.Unknown, Detect([0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04], "test.dat"));

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Returns_Unknown_For_Streams_Shorter_Than_The_Shortest_Signature(int length) =>
        Assert.Equal(PclFileKind.Unknown, Detect(new byte[length], "empty.bin"));

    [Fact]
    public void Detection_Restores_The_Stream_Position()
    {
        using var ms = new MemoryStream(SyntheticFixtures.CreateSyntheticE57());
        ms.Position = 0;
        PclFile.DetectKind(ms, "sample.e57");
        Assert.Equal(0, ms.Position);
    }

    [Fact]
    public void A_Stream_That_Cannot_Seek_Is_Rejected()
    {
        using var ms = new NonSeekableStream(SyntheticFixtures.CreateSyntheticE57());
        Assert.Throws<NotSupportedException>(() => PclFile.DetectKind(ms, "sample.e57"));
    }

    [Fact]
    public void A_Missing_Path_Throws_FileNotFound()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.e57");
        Assert.Throws<FileNotFoundException>(() => PclFile.DetectKind(missing));
    }

    [Fact]
    public void Detects_Directory_Layouts()
    {
        using var dir = new TempDirectory();

        string faro = SyntheticFixtures.WriteFaroFolder(dir);
        Assert.Equal(PclFileKind.FaroFls, PclFile.DetectKind(faro));

        string byManifest = Path.GetDirectoryName(dir.WriteText("pin1/ScanProject.json", "{}"))!;
        Assert.Equal(PclFileKind.PinPoint, PclFile.DetectKind(byManifest));

        string bySmf = Path.GetDirectoryName(dir.WriteText("pin2/project.smf", "{}"))!;
        Assert.Equal(PclFileKind.PinPoint, PclFile.DetectKind(bySmf));

        string empty = dir.PathOf("empty");
        Directory.CreateDirectory(empty);
        Assert.Equal(PclFileKind.Unknown, PclFile.DetectKind(empty));
    }

    [Theory]
    [InlineData("sample.sbf")]
    [InlineData("SAMPLE.SBF")]
    [InlineData("sample.txt")]
    [InlineData("sample.dat")]
    public void Detects_Sbf_Text_Header_By_Content(string name)
    {
        var (header, _) = SyntheticFixtures.CreateSyntheticSbf();
        Assert.Equal(PclFileKind.Sbf, Detect(header, name));
    }

    [Theory]
    [InlineData("sample.sbf.data")]
    [InlineData("sample.data")]
    [InlineData("sample.bin")]
    public void Detects_Sbf_Binary_Data_By_Structure(string name)
    {
        var (_, data) = SyntheticFixtures.CreateSyntheticSbf();
        Assert.Equal(PclFileKind.Sbf, Detect(data, name));
    }

    [Theory]
    [InlineData("sample.sbf")]
    [InlineData("SAMPLE.SBF")]
    public void Sbf_Rejects_Right_Extension_With_Wrong_Content(string name)
    {
        Assert.Equal(PclFileKind.Unknown, Detect("just some text\nline two"u8.ToArray(), name));
        Assert.Equal(PclFileKind.Unknown, Detect("POINTS 100\n1 2 3"u8.ToArray(), name));
        Assert.Equal(PclFileKind.Unknown, Detect(new byte[100], name));
    }

    [Theory]
    [InlineData("sample.pnts")]
    [InlineData("SAMPLE.PNTS")]
    [InlineData("sample.bin")]
    public void Detects_Pnts_By_Signature_And_Header(string name)
    {
        byte[] pnts = SyntheticFixtures.CreateSyntheticPnts([new(1, 2, 3)]);
        Assert.Equal(PclFileKind.Tiles3d, Detect(pnts, name));
    }

    [Theory]
    [InlineData("sample.pnts")]
    [InlineData("SAMPLE.PNTS")]
    public void Pnts_Rejects_Right_Extension_With_Wrong_Content(string name)
    {
        Assert.Equal(PclFileKind.Unknown, Detect("just some random text"u8.ToArray(), name));
        Assert.Equal(PclFileKind.Unknown, Detect(new byte[100], name));
        // Wrong magic but 28 bytes
        byte[] wrongMagic = new byte[28];
        wrongMagic[0] = 0xAA;
        Assert.Equal(PclFileKind.Unknown, Detect(wrongMagic, name));
        // Right magic but corrupt header length
        byte[] badLen = SyntheticFixtures.CreateSyntheticPnts([new(1, 2, 3)]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(badLen.AsSpan(8, 4), 999999);
        Assert.Equal(PclFileKind.Unknown, Detect(badLen, name));
    }

    [Theory]
    [InlineData("sample.glb")]
    [InlineData("SAMPLE.GLB")]
    [InlineData("sample.bin")]
    public void Detects_Glb_Point_Cloud_By_Signature_And_Mode0(string name)
    {
        byte[] glb = SyntheticFixtures.CreateSyntheticGlbPoints([new(1, 2, 3)]);
        Assert.Equal(PclFileKind.Tiles3d, Detect(glb, name));
    }

    [Theory]
    [InlineData("sample.glb")]
    [InlineData("SAMPLE.GLB")]
    public void Glb_Rejects_Right_Extension_With_Wrong_Content_Or_Mesh_Only(string name)
    {
        Assert.Equal(PclFileKind.Unknown, Detect(new byte[100], name));
        Assert.Equal(PclFileKind.Unknown, Detect("glTF not really a gltf"u8.ToArray(), name));
    }

    [Theory]
    [InlineData("tileset.json")]
    [InlineData("TILESET.JSON")]
    [InlineData("my_tileset.json")]
    public void Detects_Tileset_Json_By_Content(string name)
    {
        string json = SyntheticFixtures.CreateSyntheticTilesetJson("points.pnts");
        Assert.Equal(PclFileKind.Tiles3d, Detect(System.Text.Encoding.UTF8.GetBytes(json), name));
    }

    [Fact]
    public void Tileset_Json_Rejects_Arbitrary_Json()
    {
        Assert.Equal(PclFileKind.Unknown, Detect("{\"name\": \"John\", \"age\": 30}"u8.ToArray(), "data.json"));
    }

    [Theory]
    [InlineData("cloud.js")]
    [InlineData("CLOUD.JS")]
    [InlineData("data.js")]
    public void Detects_Potree_CloudJs(string name)
    {
        var (cloudJs, _, _) = SyntheticFixtures.CreateSyntheticPotree1x(
            [new(1, 2, 3)], new PclVector3(0.01, 0.01, 0.01), PclVector3.Zero);
        Assert.Equal(PclFileKind.Potree, Detect(System.Text.Encoding.UTF8.GetBytes(cloudJs), name));
    }

    [Theory]
    [InlineData("metadata.json")]
    [InlineData("METADATA.JSON")]
    [InlineData("meta.json")]
    public void Detects_Potree_MetadataJson(string name)
    {
        var (metaJson, _) = SyntheticFixtures.CreateSyntheticPotree2x(
            [new(1, 2, 3)], new PclVector3(0.01, 0.01, 0.01), PclVector3.Zero);
        Assert.Equal(PclFileKind.Potree, Detect(System.Text.Encoding.UTF8.GetBytes(metaJson), name));
    }

    [Fact]
    public void Detects_Potree_Hrc()
    {
        byte[] hrc = new byte[10]; // 2 valid nodes
        BinaryPrimitives.WriteUInt32LittleEndian(hrc.AsSpan(1, 4), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(hrc.AsSpan(6, 4), 200);
        Assert.Equal(PclFileKind.Potree, Detect(hrc, "r.hrc"));
    }

    [Fact]
    public void Potree_Rejects_Invalid_Hrc_Or_Js()
    {
        // Not divisible by 5
        Assert.Equal(PclFileKind.Unknown, Detect(new byte[7], "r.hrc"));
        // Count too high
        byte[] badHrc = new byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(badHrc.AsSpan(1, 4), 600_000_000);
        Assert.Equal(PclFileKind.Unknown, Detect(badHrc, "r.hrc"));
        // Invalid JS/JSON
        Assert.Equal(PclFileKind.Unknown, Detect("var x = 42;"u8.ToArray(), "cloud.js"));
    }

    [Fact]
    public void Detects_Files_On_Disk_The_Same_Way()
    {
        using var dir = new TempDirectory();
        Assert.Equal(PclFileKind.E57, PclFile.DetectKind(dir.Write("a.e57", SyntheticFixtures.CreateSyntheticE57())));
        Assert.Equal(PclFileKind.Unknown, PclFile.DetectKind(dir.Write("zero.fls", new byte[100])));
        Assert.Equal(PclFileKind.Tiles3d, PclFile.DetectKind(dir.Write("points.pnts", SyntheticFixtures.CreateSyntheticPnts([new(1, 2, 3)]))));
        Assert.Equal(PclFileKind.Tiles3d, PclFile.DetectKind(dir.Write("model.glb", SyntheticFixtures.CreateSyntheticGlbPoints([new(1, 2, 3)]))));
        Assert.Equal(PclFileKind.Tiles3d, PclFile.DetectKind(dir.WriteText("tileset.json", SyntheticFixtures.CreateSyntheticTilesetJson("points.pnts"))));
        // Directory with tileset.json
        Assert.Equal(PclFileKind.Tiles3d, PclFile.DetectKind(dir.Root));

        // Potree directory and ancestor .bin
        using var potreeDir = new TempDirectory();
        var (cloudJs, rBin, rHrc) = SyntheticFixtures.CreateSyntheticPotree1x([new(1, 2, 3)], new PclVector3(0.01, 0.01, 0.01), PclVector3.Zero);
        potreeDir.WriteText("cloud.js", cloudJs);
        string dataR = Path.Combine(potreeDir.Root, "data", "r");
        Directory.CreateDirectory(dataR);
        string binPath = Path.Combine(dataR, "r.bin");
        File.WriteAllBytes(binPath, rBin);

        Assert.Equal(PclFileKind.Potree, PclFile.DetectKind(potreeDir.Root));
        Assert.Equal(PclFileKind.Potree, PclFile.DetectKind(binPath));
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
