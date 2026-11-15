using PclIO.Internal;
using Xunit;

namespace PclIO.Tests;

public sealed class OptionsAndPathTests
{
    // ---- PclReadOptions -----------------------------------------------------------------------------------------

    [Fact]
    public void Default_Options_Read_Everything_And_Apply_The_Transform()
    {
        var options = new PclReadOptions();

        Assert.Null(options.MaxPoints);
        Assert.Equal(1, options.SubsampleStep);
        Assert.True(options.ApplyTransform);
        Assert.Equal(options, PclReadOptions.Default);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void A_Negative_Point_Limit_Is_Rejected(long value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PclReadOptions { MaxPoints = value });

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(long.MaxValue)]
    public void A_Point_Limit_Of_Zero_Or_More_Is_Accepted(long value) =>
        Assert.Equal(value, new PclReadOptions { MaxPoints = value }.MaxPoints);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_Subsample_Step_Below_One_Is_Rejected(int value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PclReadOptions { SubsampleStep = value });

    [Fact]
    public void A_Subsample_Step_Of_One_Or_More_Is_Accepted()
    {
        Assert.Equal(1, new PclReadOptions { SubsampleStep = 1 }.SubsampleStep);
        Assert.Equal(int.MaxValue, new PclReadOptions { SubsampleStep = int.MaxValue }.SubsampleStep);
    }

    [Fact]
    public void A_Limit_Applies_To_The_Total_Across_All_Scans()
    {
        using var doc = new TwoScanDocument();

        Assert.Equal(5, doc.ReadAllPoints().Count());
        Assert.Equal(4, doc.ReadAllPoints(new PclReadOptions { MaxPoints = 4 }).Count());
        Assert.Equal(5, doc.ReadAllPoints(new PclReadOptions { MaxPoints = 50 }).Count());
        Assert.Empty(doc.ReadAllPoints(new PclReadOptions { MaxPoints = 0 }));
    }

    private sealed class TwoScanDocument : PclDocument
    {
        private readonly PclScan[] _scans = [new ListScan(3), new ListScan(2)];

        public TwoScanDocument() : base("two", 0)
        {
        }

        public override PclFileKind Kind => PclFileKind.RecapScan;

        public override IReadOnlyList<PclScan> Scans => _scans;
    }

    private sealed class ListScan(int count) : PclScan
    {
        public override string Id => "id";

        public override string Name => "name";

        public override long PointCount => count;

        public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null) =>
            Enumerable.Range(0, count).Select(i => new PclPoint(new PclVector3(i, 0, 0)));
    }

    // ---- SafePath -------------------------------------------------------------------------------------------------

    private static readonly string Base = Path.Combine(Path.GetTempPath(), "pclio-safe-path-base");

    [Theory]
    [InlineData("scan.rcs")]
    [InlineData("sub/scan.rcs")]
    [InlineData("sub\\scan.rcs")]
    [InlineData("a/../b/scan.rcs")]
    [InlineData("./scan.rcs")]
    public void A_Relative_Path_Inside_The_Base_Is_Returned_As_A_Full_Path(string relative)
    {
        string full = SafePath.Combine(Base, relative);

        Assert.True(Path.IsPathRooted(full));
        Assert.StartsWith(Path.GetFullPath(Base) + Path.DirectorySeparatorChar, full, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("..\\x")]
    [InlineData("a/../../x")]
    [InlineData("/etc/x")]
    [InlineData("\\x")]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("//server/share/x")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a:b")]
    [InlineData("a\0b")]
    public void A_Path_That_Is_Empty_Rooted_Escaping_Or_Invalid_Is_Rejected(string untrusted) =>
        Assert.Throws<InvalidDataException>(() => SafePath.Combine(Base, untrusted));

    [Fact]
    public void A_Drive_Qualified_Path_Is_Rejected_As_Not_Relative()
    {
        Assert.Throws<InvalidDataException>(() => SafePath.Combine(Base, Path.GetFullPath(Path.GetTempPath()) + "x.rcs"));
    }

    [Fact]
    public void A_Sibling_Folder_Whose_Name_Starts_Like_The_Base_Is_Not_Inside_It()
    {
        // "base-evil" begins with the text "base" but is a different folder.
        string name = Path.GetFileName(Base);

        Assert.Throws<InvalidDataException>(() => SafePath.Combine(Base, $"../{name}-evil/x.rcs"));
    }

    [Fact]
    public void An_Empty_Base_Directory_Is_An_Argument_Error() =>
        Assert.Throws<ArgumentException>(() => SafePath.Combine("", "x"));
}
