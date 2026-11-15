using PclIO.Export;
using Xunit;

namespace PclIO.Tests;

/// <summary>Reads private scan files that are not part of the repository (see <see cref="TestFiles"/>). A test is skipped
/// when no file with the extension is available. The assertions are generic invariants only: nothing here depends on the
/// content, size or identifiers of any particular file.</summary>
public sealed class RealFileTests
{
    private const int SampleSize = 100;

    private static void AssertReadsPoints(string extension, PclFileKind kind)
    {
        string path = TestFiles.FindByExtension(extension);

        Assert.Equal(kind, PclFile.DetectKind(path));

        using var doc = PclFile.Open(path);
        Assert.Equal(kind, doc.Kind);
        Assert.True(doc.Scans.Count > 0, "the document lists at least one scan");
        Assert.True(doc.FileSize > 0);

        var options = new PclReadOptions { MaxPoints = SampleSize };
        var points = doc.ReadAllPoints(options).ToList();

        // The requested number of points comes back, and every coordinate is a real number.
        Assert.Equal(SampleSize, points.Count);
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
