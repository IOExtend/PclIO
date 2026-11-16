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
        Assert.Equal(PclFileKind.Unknown, Detect(SyntheticFixtures.CreateSyntheticLas(), "sample.laz"));    [Fact]
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

    [Fact]
    public void Detects_Files_On_Disk_The_Same_Way()
    {
        using var dir = new TempDirectory();
        Assert.Equal(PclFileKind.E57, PclFile.DetectKind(dir.Write("a.e57", SyntheticFixtures.CreateSyntheticE57())));
        Assert.Equal(PclFileKind.Unknown, PclFile.DetectKind(dir.Write("zero.fls", new byte[100])));
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
