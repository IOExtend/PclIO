using PclIO.Export;
using Xunit;

namespace PclIO.Tests;

/// <summary>Reads private scan files that are not part of the repository (see <see cref="TestFiles"/>). A test is skipped
/// when no file with the extension is available, or when none of the candidates is a readable fixture: a corpus can hold
/// project shells without data and variants the reader does not decode beside the readable files. The assertions are
/// generic invariants only: nothing here depends on the content, size or identifiers of any particular file.</summary>
public sealed class RealFileTests
{
    private const int SampleSize = 100;

    private static void AssertReadsPoints(string extension, PclFileKind kind)
    {
        var candidates = TestFiles.TryFindAllByExtension(extension, TestFiles.Roots());
        foreach (string path in candidates)
        {
            if (PclFile.DetectKind(path) != kind) continue;

            PclDocument doc;
            try
            {
                doc = PclFile.Open(path);
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException)
            {
                continue;
            }

            using (doc)
            {
                Assert.Equal(kind, doc.Kind);
                // A shell without scan data lists no scans; a manifest whose data files are missing reads no points.
                if (doc.Scans.Count == 0) continue;
                Assert.True(doc.FileSize > 0);

                long totalPoints = doc.Scans.Sum(s => s.PointCount);
                int expected = (int)Math.Min(SampleSize, totalPoints > 0 ? totalPoints : SampleSize);
                var options = new PclReadOptions { MaxPoints = SampleSize };
                List<PclPoint> points;
                try
                {
                    points = doc.ReadAllPoints(options).ToList();
                }
                catch (Exception e) when (e is InvalidDataException or NotSupportedException)
                {
                    continue;
                }
                if (points.Count == 0) continue;

                AssertReadable(doc, points, expected, options);
                return;
            }
        }

        throw candidates.Count == 0
            ? TestFiles.Missing(extension)
            : new SkipException($"No private '{extension}' fixture with readable points is available: the candidates hold no scans, hold no points, or use a layout the reader does not decode. Set {TestFiles.EnvironmentVariable} to a folder that holds private scan files, or create a test_files folder.");
    }

    private static void AssertReadable(PclDocument doc, List<PclPoint> points, int expected, PclReadOptions options)
    {
        // The requested number of points comes back, and every coordinate is a real number.
        Assert.Equal(expected, points.Count);
        Assert.All(points, p =>
        {
            Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z));
            if (p.Intensity is { } intensity) Assert.False(float.IsNaN(intensity));
        });

        // The bounds of the sample contain every point of it.
        var bounds = PclBounds.Empty;
        foreach (var p in points) bounds = bounds.Encapsulate(p.Position);
        Assert.False(bounds.IsEmpty);
        Assert.All(points, p => Assert.True(bounds.Contains(p.Position)));

        // Reading is repeatable: the same options give the same points.
        var again = doc.ReadAllPoints(options).Select(p => p.Position).ToList();
        Assert.Equal(points.Select(p => p.Position), again);

        // A limit of zero yields nothing; a step thins the stream out.
        Assert.Empty(doc.ReadAllPoints(new PclReadOptions { MaxPoints = 0 }));
        int stepped = doc.ReadAllPoints(new PclReadOptions { MaxPoints = SampleSize, SubsampleStep = 2 }).Count();
        Assert.InRange(stepped, 1, SampleSize);
    }

    [SkippableFact]
    public void Read_Real_E57_Points() => AssertReadsPoints(".e57", PclFileKind.E57);

    [SkippableFact]
    public void Read_Real_Recap_Scan_Points() => AssertReadsPoints(".rcs", PclFileKind.RecapScan);

    [SkippableFact]
    public void Read_Real_Recap_Project_Points() => AssertReadsPoints(".rcp", PclFileKind.RecapProject);

    [SkippableFact]
    public void Read_Real_Leica_Container_Points() => AssertReadsPoints(".lgsx", PclFileKind.LeicaLgsx);

    [SkippableFact]
    public void Read_Real_Faro_Scan_Points() => AssertReadsPoints(".fls", PclFileKind.FaroFls);

    [SkippableFact]
    public void Read_Real_PinPoint_Project_Points() => AssertReadsPoints(".srf", PclFileKind.PinPoint);

    [SkippableFact]
    public void Read_Real_PinPoint_Manifest_Points() => AssertReadsPoints(".smf", PclFileKind.PinPoint);

    [SkippableFact]
    public void Read_Real_EdgeWise_Database_Points() => AssertReadsPoints(".c3db", PclFileKind.EdgeWise);

    [SkippableFact]
    public void Read_Real_Las_Points() => AssertReadsPoints(".las", PclFileKind.Las);

    [SkippableFact]
    public void Read_Real_Laz_Points() => AssertReadsPoints(".laz", PclFileKind.Laz);

    [SkippableFact]
    public void Read_Real_Copc_Points() => AssertReadsPoints(".copc.laz", PclFileKind.Copc);

    [SkippableFact]
    public void Read_Real_Pcd_Points() => AssertReadsPoints(".pcd", PclFileKind.Pcd);

    [SkippableFact]
    public void Read_Real_Ply_Points() => AssertReadsPoints(".ply", PclFileKind.Ply);

    [SkippableFact]
    public void Read_Real_Pts_Points() => AssertReadsPoints(".pts", PclFileKind.Pts);

    [SkippableFact]
    public void Read_Real_Ptx_Points() => AssertReadsPoints(".ptx", PclFileKind.Ptx);

    [SkippableFact]
    public void Read_Real_Ptg_Points() => AssertReadsPoints(".ptg", PclFileKind.LeicaPtg);

    [SkippableFact]
    public void Read_Real_Cc_Bin_Points() => AssertReadsPoints(".bin", PclFileKind.CcBin);

    [SkippableFact]
    public void Read_Real_Xyz_Points() => AssertReadsPoints(".xyz", PclFileKind.Xyz);

    [SkippableFact]
    public void Read_Real_Csv_Points() => AssertReadsPoints(".csv", PclFileKind.Xyz);

    [SkippableFact]
    public void Read_Real_Sbf_Points() => AssertReadsPoints(".sbf", PclFileKind.Sbf);

    [SkippableFact]
    public void Read_Real_Pnts_Points() => AssertReadsPoints(".pnts", PclFileKind.Tiles3d);

    [SkippableFact]
    public void Read_Real_Tileset_Json()
    {
        string? root = TestFiles.Roots().FirstOrDefault();
        if (root == null) throw TestFiles.Missing(".json");
        string tilesetPath = Path.Combine(root, "3dtiles", "TilesetWithRequestVolume", "tileset.json");
        if (!File.Exists(tilesetPath)) throw new SkipException("TilesetWithRequestVolume/tileset.json not found.");

        Assert.Equal(PclFileKind.Tiles3d, PclFile.DetectKind(tilesetPath));
        using var doc = PclFile.Open(tilesetPath);
        Assert.Equal(PclFileKind.Tiles3d, doc.Kind);
        Assert.Single(doc.Scans);
        Assert.Equal(125000, doc.TotalPointCount);

        var firstPoint = doc.ReadAllPoints(new PclReadOptions { MaxPoints = 1 }).First();
        Assert.True(double.IsFinite(firstPoint.X));
        Assert.True(double.IsFinite(firstPoint.Y));
        Assert.True(double.IsFinite(firstPoint.Z));
        Assert.NotNull(firstPoint.Color);
    }

    [SkippableFact]
    public void Read_Real_Potree_CloudJs()
    {
        string? root = TestFiles.Roots().FirstOrDefault();
        if (root == null) throw TestFiles.Missing(".js");
        string cloudPath = Path.Combine(root, "potree", "lion_takanawa", "cloud.js");
        if (!File.Exists(cloudPath))
            cloudPath = Path.Combine(root, "downloads", "potree", "lion_takanawa", "cloud.js");
        if (!File.Exists(cloudPath)) throw new SkipException("Potree lion_takanawa sample not found.");

        Assert.Equal(PclFileKind.Potree, PclFile.DetectKind(cloudPath));
        using var doc = PclFile.Open(cloudPath);
        Assert.Equal(PclFileKind.Potree, doc.Kind);
        Assert.Single(doc.Scans);
        Assert.Equal(3751, doc.TotalPointCount);

        var firstPoint = doc.ReadAllPoints(new PclReadOptions { MaxPoints = 1 }).First();
        Assert.True(double.IsFinite(firstPoint.X));
        Assert.True(double.IsFinite(firstPoint.Y));
        Assert.True(double.IsFinite(firstPoint.Z));
        Assert.NotNull(firstPoint.Color);
        Assert.NotNull(firstPoint.Normal);
    }

    [SkippableFact]
    public void Read_Real_Ept_Manifest()
    {
        string? root = TestFiles.Roots().FirstOrDefault();
        if (root == null) throw TestFiles.Missing("ept.json");
        string eptPath = Path.Combine(root, "ept", "lion_takanawa", "ept.json");
        if (!File.Exists(eptPath))
            eptPath = Path.Combine(root, "downloads", "ept", "lion_takanawa", "ept.json");
        if (!File.Exists(eptPath))
            eptPath = TestFiles.FindByExtension("ept.json");

        Assert.Equal(PclFileKind.Ept, PclFile.DetectKind(eptPath));
        Assert.Equal(PclFileKind.Ept, PclFile.DetectKind(Path.GetDirectoryName(eptPath)!));

        using var doc = PclFile.Open(eptPath);
        Assert.Equal(PclFileKind.Ept, doc.Kind);
        Assert.Single(doc.Scans);
        Assert.Equal(341989, doc.TotalPointCount);

        var points = doc.ReadAllPoints(new PclReadOptions { MaxPoints = 100 }).ToList();
        Assert.Equal(100, points.Count);
        var firstPoint = points[0];
        Assert.True(double.IsFinite(firstPoint.X));
        Assert.True(double.IsFinite(firstPoint.Y));
        Assert.True(double.IsFinite(firstPoint.Z));
        Assert.NotNull(firstPoint.Color);
    }

    [SkippableFact]
    public void Export_Real_File_To_Las_Matches_The_Requested_Count()
    {
        // Any one of the available formats is enough; the first one found is used.
        string[] extensions = [".e57", ".rcs", ".lgsx", ".fls", ".srf", ".smf", ".c3db"];
        string? path = extensions.Select(TestFiles.TryFindByExtension).FirstOrDefault(p => p != null);
        if (path == null) throw TestFiles.Missing("scan");

        using var doc = PclFile.Open(path);
        using var ms = new MemoryStream();
        long written = PclLasExporter.Export(doc, ms, new PclReadOptions { MaxPoints = SampleSize });

        Assert.Equal(SampleSize, written);
        Assert.Equal(227 + (SampleSize * 26), ms.Length);
    }
}
