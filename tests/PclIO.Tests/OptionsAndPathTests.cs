using PclIO.Export;
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
        Assert.Equal(5000.0, options.MaxCoordinateMagnitude);
        Assert.Equal(0.128 / 32768.0, options.EdgeWiseCoordinateScale);
        Assert.Equal(1, options.SubsampleStep);
        Assert.True(options.ApplyTransform);
        Assert.False(options.Strict);
        Assert.False(options.ExcludeFilteredPoints);
        Assert.Null(options.Blocks);
        Assert.Equal(1, options.MaxDegreeOfParallelism);
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

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.MinValue)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_Negative_Or_Non_Finite_Magnitude_Limit_Is_Rejected(double value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PclReadOptions { MaxCoordinateMagnitude = value });

    [Fact]
    public void A_Magnitude_Limit_Of_Zero_Or_More_Is_Accepted()
    {
        Assert.Equal(0, new PclReadOptions { MaxCoordinateMagnitude = 0 }.MaxCoordinateMagnitude);
        Assert.Null(new PclReadOptions { MaxCoordinateMagnitude = null }.MaxCoordinateMagnitude);
        Assert.Equal(1e9, new PclReadOptions { MaxCoordinateMagnitude = 1e9 }.MaxCoordinateMagnitude);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_Non_Positive_Or_Non_Finite_EdgeWise_Scale_Is_Rejected(double value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PclReadOptions { EdgeWiseCoordinateScale = value });

    [Fact]
    public void A_Positive_Finite_EdgeWise_Scale_Is_Accepted() =>
        Assert.Equal(0.001, new PclReadOptions { EdgeWiseCoordinateScale = 0.001 }.EdgeWiseCoordinateScale);

    [Theory]
    [InlineData(5)]
    [InlineData(0)]
    [InlineData(-2)]
    public void A_Las_Point_Format_Outside_2_6_7_8_Is_Rejected(int format) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PclLasOptions { PointFormat = format });

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_Las_Scale_That_Is_Not_Positive_And_Finite_Is_Rejected(double scale) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PclLasOptions { Scale = scale });

    [Fact]
    public void Las_Options_Default_To_1_2_Format_2_And_A_Millimetre()
    {
        var options = new PclLasOptions();

        Assert.Equal(LasVersion.V12, options.Version);
        Assert.Equal(2, options.PointFormat);
        Assert.Equal(0.001, options.Scale);
        Assert.Null(options.Offset);
        Assert.False(options.WriteCoordinateSystem);
        Assert.Equal(options, PclLasOptions.Default);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_Negative_Block_Start_Is_Rejected(int value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlockRange { StartBlock = value });

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_Negative_Block_Count_Is_Rejected(int value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlockRange { BlockCount = value });

    [Fact]
    public void A_Block_Range_Of_Zero_Count_And_Null_Count_Is_Accepted()
    {
        Assert.Equal(0, new BlockRange { StartBlock = 2, BlockCount = 0 }.BlockCount);
        Assert.Null(new BlockRange { StartBlock = 2 }.BlockCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_Parallelism_Below_One_Is_Rejected(int value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PclReadOptions { MaxDegreeOfParallelism = value });

    [Fact]
    public void A_Parallelism_Of_One_Or_More_Is_Accepted()
    {
        Assert.Equal(1, new PclReadOptions { MaxDegreeOfParallelism = 1 }.MaxDegreeOfParallelism);
        Assert.Equal(4, new PclReadOptions { MaxDegreeOfParallelism = 4 }.MaxDegreeOfParallelism);
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
