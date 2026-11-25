using System.Buffers.Binary;
using System.Text;
using IOScene;
using PclIO;
using PclIO.Scene;
using PclIO.Tests;
using Xunit;

namespace PclIO.Scene.Tests;

public sealed class PclSceneReaderTests
{
    private static string XyzFile(TempDirectory dir, int points, string name = "cloud.xyz")
    {
        var sb = new StringBuilder();
        for (int i = 0; i < points; i++)
            sb.Append(i).Append(' ').Append(i * 2).Append(' ').Append(i * 3).Append('\n');
        return dir.Write(name, Encoding.ASCII.GetBytes(sb.ToString()));
    }

    private static string PtsFileWithChannels(TempDirectory dir)
    {
        string text = "2\n1 2 3 0.5 10 20 30\n4 5 6 0.8 100 150 200\n";
        return dir.Write("channels.pts", Encoding.ASCII.GetBytes(text));
    }

    private static string PlyFileWithNormals(TempDirectory dir)
    {
        string text =
            "ply\nformat ascii 1.0\nelement vertex 2\n" +
            "property float x\nproperty float y\nproperty float z\n" +
            "property float nx\nproperty float ny\nproperty float nz\n" +
            "end_header\n" +
            "1 2 3 0 0 1\n4 5 6 0 1 0\n";
        return dir.Write("normals.ply", Encoding.ASCII.GetBytes(text));
    }

    /// <summary>A one-row PTG scan with one point per column, float XYZ only (properties 0x01), in the binary TLV layout.
    /// The optional transform is in the PTG row-vector convention (translation in the last row).</summary>
    private static byte[] PtgChunk((float X, float Y, float Z)[] columnPoints, double[]? transform = null, bool pointValid = true)
    {
        using var ms = new MemoryStream();
        ms.Write([0x50, 0x54, 0x47, 0x00]);
        Span<byte> magic = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(magic, 0x928FA3C7);
        ms.Write(magic);

        void Record(string key, byte[] value)
        {
            byte[] name = Encoding.ASCII.GetBytes(key + "\0");
            Span<byte> u32 = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)name.Length);
            ms.Write(u32);
            ms.Write(name);
            ms.Write(value);
        }
        void U32(string key, uint value)
        {
            byte[] buf = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
            Record(key, buf);
        }

        Record("%%header_begin", []);
        U32("%%version", 1);
        U32("%%cols", (uint)columnPoints.Length);
        U32("%%rows", 1);
        if (transform is { Length: 16 } m)
        {
            byte[] matrixBytes = new byte[128];
            for (int i = 0; i < 16; i++)
                BinaryPrimitives.WriteDoubleLittleEndian(matrixBytes.AsSpan(i * 8, 8), m[i]);
            Record("%%transform", matrixBytes);
        }
        U32("%%properties", 0x01);
        Record("%%header_end", []);

        long dataStart = ms.Position + columnPoints.Length * 8;
        int blockSize = 1 + (pointValid ? 12 : 0);
        Span<byte> u64 = stackalloc byte[8];
        for (int c = 0; c < columnPoints.Length; c++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(u64, dataStart + c * blockSize);
            ms.Write(u64);
        }
        Span<byte> record = stackalloc byte[12];
        foreach (var point in columnPoints)
        {
            ms.Write([pointValid ? (byte)0x80 : (byte)0x00]); // one row, most significant bit first
            if (!pointValid) continue;
            BinaryPrimitives.WriteSingleLittleEndian(record[0..], point.X);
            BinaryPrimitives.WriteSingleLittleEndian(record[4..], point.Y);
            BinaryPrimitives.WriteSingleLittleEndian(record[8..], point.Z);
            ms.Write(record);
        }
        return ms.ToArray();
    }

    private static string PtgIndex(TempDirectory dir)
    {
        string folder = Path.Combine(dir.Root, "Scans");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "a.ptg"), PtgChunk([(1, 2, 3), (4, 5, 6)]));
        File.WriteAllBytes(Path.Combine(folder, "b.ptg"), PtgChunk([(7, 8, 9)]));
        string index = "PTG index file\n--------------\nScans\\a.ptg\nScans\\b.ptg\n";
        return dir.Write("site.ptg", Encoding.ASCII.GetBytes(index));
    }

    private static void AssertNoErrors(SceneDocument scene)
    {
        var errors = scene.Validate().Where(d => d.Severity == SceneSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("; ", errors.Select(e => $"{e.Code}: {e.Message}")));
    }

    [Fact]
    public void Read_Xyz_File_Produces_A_Valid_Scene()
    {
        using var dir = new TempDirectory();
        string path = XyzFile(dir, 3);

        var result = PclSceneReader.Read(path);

        Assert.Equal(1, result.ScanCount);
        Assert.Equal(1, result.ScansWithPoints);
        Assert.Equal(3, result.PointCount);
        Assert.Equal(1.0, result.MetresPerUnit);
        Assert.Empty(result.Diagnostics);

        SceneDocument scene = result.Document;
        Assert.Equal(SceneDocument.DocumentKind, scene.Kind);
        Assert.Equal(SceneDocument.CurrentFormat, scene.Format);
        Assert.Equal("cloud.xyz", scene.Title);
        AssertNoErrors(scene);

        SceneModel model = Assert.Single(scene.Models);
        Assert.Equal("cloud.xyz", model.Key);
        Assert.Equal("cloud.xyz", model.Name);
        Assert.Equal("xyz", model.SourceFormat);
        Assert.Equal(path, model.Path);
        Assert.StartsWith("PclIO.Scene ", model.Producer);

        Assert.Equal(2, scene.Elements.Count);
        SceneElement root = scene.Elements[0];
        Assert.Equal(SceneNodeKind.Model, root.Kind);
        Assert.Null(root.SourceId);
        SceneElement scan = scene.Elements[1];
        Assert.Equal(SceneNodeKind.Element, scan.Kind);
        Assert.Equal(root.Id, scan.ParentId);
        Assert.Equal("Point Cloud", scan.Category);
        Assert.Equal("xyz", scan.TypeName);

        // The frozen identity derivation: the element's SourceId is the engine's scan id.
        using var document = PclFile.Open(path);
        Assert.Equal(document.Scans[0].Id, scan.SourceId);

        SceneGeometry geometry = Assert.Single(scene.Geometries);
        Assert.Equal(SceneTopology.Points, geometry.Topology);
        Assert.Empty(geometry.Indices);
        Assert.Equal(3, geometry.Vertices.Count);
        Assert.Equal(new Vector3d(0, 0, 0), geometry.Vertices[0]);
        Assert.Equal(new Vector3d(2, 4, 6), geometry.Vertices[2]);
        Assert.Null(geometry.Normals);

        SceneInstance instance = Assert.Single(scene.Instances);
        Assert.Equal(scan.Id, instance.ElementId);
        Assert.Equal(geometry.Id, instance.GeometryId);
        Assert.Null(instance.Transform);
        Assert.Null(instance.Color);
    }

    [Fact]
    public void ModelKey_Override_And_Validation()
    {
        using var dir = new TempDirectory();
        string path = XyzFile(dir, 1);

        var result = PclSceneReader.Read(path, new PclSceneOptions { ModelKey = "Site#1" });
        Assert.Equal("Site#1", result.Document.Models[0].Key);

        Assert.Throws<ArgumentException>(() => PclSceneReader.Read(path, new PclSceneOptions { ModelKey = "" }));
        Assert.Throws<ArgumentException>(() => PclSceneReader.Read(""));
    }

    [Fact]
    public void Read_Document_Overload_Derives_The_Key_From_The_File_Path()
    {
        using var dir = new TempDirectory();
        string path = XyzFile(dir, 2);

        using var document = PclFile.Open(path);
        var result = PclSceneReader.Read(document);
        Assert.Equal("cloud.xyz", result.Document.Models[0].Key);
        Assert.Null(result.Document.Models[0].Path);
        Assert.Equal(2, result.PointCount);

        var named = PclSceneReader.Read(document, new PclSceneOptions { ModelKey = "k" });
        Assert.Equal("k", named.Document.Models[0].Key);

        Assert.Throws<ArgumentNullException>(() => PclSceneReader.Read((PclDocument)null!));
    }

    [Fact]
    public void Colours_And_Intensities_Are_Reported_Dropped()
    {
        using var dir = new TempDirectory();
        string path = PtsFileWithChannels(dir);

        var result = PclSceneReader.Read(path);

        Assert.Equal(2, result.PointCount);
        Assert.Contains(result.Diagnostics, d => d.Severity == SceneSeverity.Info && d.Code == "colours-dropped");
        Assert.Contains(result.Diagnostics, d => d.Severity == SceneSeverity.Info && d.Code == "intensities-dropped");
        Assert.All(result.Document.Instances, i => Assert.Null(i.Color));
        AssertNoErrors(result.Document);
    }

    [Fact]
    public void MaxPoints_Caps_The_Scene_And_Reports_It()
    {
        using var dir = new TempDirectory();
        string path = XyzFile(dir, 10);

        var capped = PclSceneReader.Read(path, new PclSceneOptions { MaxPoints = 4 });
        Assert.Equal(4, capped.PointCount);
        var diagnostic = Assert.Single(capped.Diagnostics, d => d.Code == "points-capped");
        Assert.Equal(SceneSeverity.Warning, diagnostic.Severity);
        Assert.Equal(4, capped.Document.Geometries[0].Vertices.Count);

        var zero = PclSceneReader.Read(path, new PclSceneOptions { MaxPoints = 0 });
        Assert.Equal(0, zero.PointCount);
        Assert.Equal(0, zero.ScansWithPoints);
        Assert.Empty(zero.Document.Geometries);
        Assert.Empty(zero.Document.Instances);
        // Scans skipped because of the cap are reported by points-capped alone, not as empty scans.
        Assert.Contains(zero.Diagnostics, d => d.Code == "points-capped");
        Assert.DoesNotContain(zero.Diagnostics, d => d.Code == "empty-scan");
        AssertNoErrors(zero.Document);

        Assert.Throws<ArgumentOutOfRangeException>(() => new PclSceneOptions { MaxPoints = -1 });
    }

    [Fact]
    public void A_Scan_That_Yields_No_Points_Is_Reported_Empty()
    {
        using var dir = new TempDirectory();
        string path = dir.Write("empty.ptg", PtgChunk([(0, 0, 0)], pointValid: false));

        var result = PclSceneReader.Read(path);

        Assert.Equal(1, result.ScanCount);
        Assert.Equal(0, result.ScansWithPoints);
        Assert.Equal(0, result.PointCount);
        Assert.Empty(result.Document.Geometries);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == "empty-scan");
        Assert.Equal(SceneSeverity.Info, diagnostic.Severity);
        AssertNoErrors(result.Document);
    }

    [Fact]
    public void SubsampleStep_Thins_The_Points()
    {
        using var dir = new TempDirectory();
        string path = XyzFile(dir, 10);

        var result = PclSceneReader.Read(path, new PclSceneOptions { SubsampleStep = 2 });

        Assert.Equal(5, result.PointCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PclSceneOptions { SubsampleStep = 0 });
    }

    [Fact]
    public void MetresPerUnit_Scales_The_Positions()
    {
        using var dir = new TempDirectory();
        string path = XyzFile(dir, 2);

        var result = PclSceneReader.Read(path, new PclSceneOptions { MetresPerUnit = 0.5 });

        Assert.Equal(0.5, result.MetresPerUnit);
        Assert.Equal(new Vector3d(0.5, 1, 1.5), result.Document.Geometries[0].Vertices[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PclSceneOptions { MetresPerUnit = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new PclSceneOptions { MetresPerUnit = double.NaN });
    }

    [Fact]
    public void MultiScan_Ptg_Index_Becomes_One_Element_Per_Scan()
    {
        using var dir = new TempDirectory();
        string path = PtgIndex(dir);

        var result = PclSceneReader.Read(path);

        Assert.Equal(2, result.ScanCount);
        Assert.Equal(2, result.ScansWithPoints);
        Assert.Equal(3, result.PointCount);
        Assert.Equal("ptg", result.Document.Models[0].SourceFormat);

        SceneElement root = result.Document.Elements[0];
        Assert.Equal(3, result.Document.Elements.Count);
        Assert.Equal(["scan-0", "scan-1"], result.Document.Elements.Skip(1).Select(e => e.SourceId));
        Assert.All(result.Document.Elements.Skip(1), e => Assert.Equal(root.Id, e.ParentId));
        Assert.Equal(2, result.Document.Geometries.Count);
        Assert.Equal(2, result.Document.Instances.Count);

        // Scan properties are text properties of the element's set.
        SceneElement first = result.Document.Elements[1];
        int setId = Assert.Single(first.PropertySetIds);
        ScenePropertySet set = result.Document.PropertySets.Single(s => s.Id == setId);
        Assert.Contains(set.Properties, p => p.Name == "Columns" && p.Value.Text == "2");
        Assert.All(set.Properties, p => Assert.Equal(SceneValueKind.Text, p.Value.Kind));
        AssertNoErrors(result.Document);
    }

    [Fact]
    public void Ptg_Scanner_Pose_Is_Baked_Into_The_Positions()
    {
        using var dir = new TempDirectory();
        double[] matrix =
        [
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            10, 20, 30, 1,
        ];
        string path = dir.Write("posed.ptg", PtgChunk([(1, 0, 0)], matrix));

        var result = PclSceneReader.Read(path);

        Assert.Equal(new Vector3d(11, 20, 30), result.Document.Geometries[0].Vertices[0]);
    }

    [Fact]
    public void Normals_Are_Kept_When_Every_Point_Has_One()
    {
        using var dir = new TempDirectory();
        string path = PlyFileWithNormals(dir);

        var result = PclSceneReader.Read(path);

        SceneGeometry geometry = Assert.Single(result.Document.Geometries);
        Assert.NotNull(geometry.Normals);
        Assert.Equal(2, geometry.Normals!.Count);
        Assert.Equal(new Vector3d(0, 0, 1), geometry.Normals[0]);
        AssertNoErrors(result.Document);
    }

    [Fact]
    public void SceneJson_Roundtrip_Keeps_The_Points()
    {
        using var dir = new TempDirectory();
        string path = XyzFile(dir, 3);
        var result = PclSceneReader.Read(path);

        string scenePath = dir.PathOf("cloud.scene.json");
        SceneJson.Save(result.Document, scenePath);
        SceneDocument read = SceneJson.Load(scenePath);

        AssertNoErrors(read);
        Assert.Equal(result.Document.Geometries[0].Vertices, read.Geometries[0].Vertices);
        Assert.Equal("cloud.xyz", read.Models[0].Key);
    }
}
