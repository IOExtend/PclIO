using System.Globalization;
using Xunit;

namespace PclIO.Tests;

public sealed class ModelAndMathTests
{
    [Fact]
    public void Vector3_Basic_Arithmetic()
    {
        var a = new PclVector3(1.0, 2.0, 3.0);
        var b = new PclVector3(4.0, 5.0, 6.0);

        Assert.Equal(new PclVector3(5.0, 7.0, 9.0), a + b);
        Assert.Equal(new PclVector3(3.0, 3.0, 3.0), b - a);
        Assert.Equal(new PclVector3(2.0, 4.0, 6.0), a * 2.0);

        Assert.True(PclVector3.Zero.IsZero);
        Assert.False(a.IsZero);
    }

    [Fact]
    public void Bounds_Encapsulate_And_Center()
    {
        var bounds = PclBounds.Empty;
        Assert.True(bounds.IsEmpty);
        Assert.False(bounds.Contains(PclVector3.Zero));

        bounds = bounds.Encapsulate(new PclVector3(-10.0, 0.0, 5.0));
        bounds = bounds.Encapsulate(new PclVector3(10.0, 20.0, 15.0));

        Assert.False(bounds.IsEmpty);
        Assert.Equal(new PclVector3(-10.0, 0.0, 5.0), bounds.Min);
        Assert.Equal(new PclVector3(10.0, 20.0, 15.0), bounds.Max);
        Assert.Equal(new PclVector3(20.0, 20.0, 10.0), bounds.Size);
        Assert.Equal(new PclVector3(0.0, 10.0, 10.0), bounds.Center);

        Assert.True(bounds.Contains(new PclVector3(0.0, 5.0, 10.0)));
        Assert.False(bounds.Contains(new PclVector3(100.0, 5.0, 10.0)));
    }

    [Fact]
    public void Bounds_Union_Ignores_Empty_Operands()
    {
        var box = new PclBounds(PclVector3.Zero, PclVector3.One);

        Assert.Equal(box, PclBounds.Empty.Union(box));
        Assert.Equal(box, box.Union(PclBounds.Empty));
        Assert.Equal(new PclVector3(2, 2, 2), box.Union(new PclBounds(new PclVector3(1, 1, 1), new PclVector3(2, 2, 2))).Max);
    }

    [Fact]
    public void Transform_Translation_And_Scale()
    {
        var transform = new PclTransform
        {
            Translation = new PclVector3(10.0, 20.0, 30.0),
            Scale = new PclVector3(2.0, 2.0, 2.0)
        };

        Assert.False(transform.IsIdentity);

        // Scale first, then translation: 1*2 + 10, 2*2 + 20, 3*2 + 30.
        Assert.Equal(new PclVector3(12.0, 24.0, 36.0), transform.TransformPoint(new PclVector3(1.0, 2.0, 3.0)));
    }

    [Fact]
    public void Transform_RowMajor_Matrix4x4()
    {
        double[] m = [
            1, 0, 0, 5,
            0, 1, 0, 6,
            0, 0, 1, 7,
            0, 0, 0, 1
        ];

        var transform = new PclTransform { Matrix4x4 = m };

        Assert.Equal(new PclVector3(6.0, 7.0, 8.0), transform.TransformPoint(new PclVector3(1.0, 1.0, 1.0)));
    }

    [Fact]
    public void Rotation_Values_Are_Stored_But_Never_Applied()
    {
        var transform = new PclTransform { Rotation = new PclVector3(1.0, 2.0, 3.0), Translation = new PclVector3(1, 1, 1) };

        Assert.False(transform.IsIdentity);
        Assert.Equal(new PclVector3(2, 3, 4), transform.TransformPoint(new PclVector3(1, 2, 3)));
    }

    [Fact]
    public void TransformVector_Ignores_Translation_And_Normalizes()
    {
        var transform = new PclTransform { Translation = new PclVector3(100, 100, 100), Scale = new PclVector3(3, 0, 4) };

        var v = transform.TransformVector(new PclVector3(1, 1, 1));

        Assert.Equal(0.6, v.X, 12);
        Assert.Equal(0.0, v.Y, 12);
        Assert.Equal(0.8, v.Z, 12);
    }

    [Fact]
    public void Identity_Transform_Leaves_Points_Alone() =>
        Assert.Equal(new PclVector3(1.5, -2.5, 3.5), PclTransform.Identity.TransformPoint(new PclVector3(1.5, -2.5, 3.5)));

    [Fact]
    public void Point_Coordinates_Accessors()
    {
        var pt = new PclPoint(new PclVector3(12.34, 56.78, 90.12), new PclColor(255, 128, 64), 0.75f);
        Assert.Equal(12.34, pt.X);
        Assert.Equal(56.78, pt.Y);
        Assert.Equal(90.12, pt.Z);
        Assert.Equal(new PclColor(255, 128, 64), pt.Color);
        Assert.Equal((byte)255, pt.Color?.A);
        Assert.Equal(0.75f, pt.Intensity);
    }

    [Fact]
    public void Text_Forms_Do_Not_Depend_On_The_Current_Culture()
    {
        var pt = new PclPoint(new PclVector3(1.5, -2.25, 3.125), new PclColor(1, 2, 3), 0.5f);
        var box = new PclBounds(new PclVector3(0.5, 0.5, 0.5), new PclVector3(1.5, 1.5, 1.5));

        string Format() => string.Join("|", pt.ToString(), box.ToString(), pt.Position.ToString());
        string expected = WithCulture(CultureInfo.InvariantCulture, Format);

        Assert.Contains("1.5000", expected);
        Assert.Equal(expected, WithCulture(new CultureInfo("fr-FR"), Format));
        Assert.Equal(expected, WithCulture(new CultureInfo("de-DE"), Format));
    }

    private sealed class CountedDocument(params long[] counts) : PclDocument("counted", 0)
    {
        public override PclFileKind Kind => PclFileKind.RecapScan;

        public override IReadOnlyList<PclScan> Scans { get; } = [.. counts.Select(c => (PclScan)new CountedScan(c))];

        private sealed class CountedScan(long count) : PclScan
        {
            public override string Id => "id";

            public override string Name => "name";

            public override long PointCount => count;

            public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null) => [];
        }
    }

    [Theory]
    [InlineData(new long[] { }, 0L)]
    [InlineData(new long[] { 5, 7 }, 12L)]
    [InlineData(new long[] { 5, -3, 2 }, 7L)]
    [InlineData(new long[] { long.MaxValue, 1 }, long.MaxValue)]
    [InlineData(new long[] { long.MaxValue, long.MaxValue }, long.MaxValue)]
    [InlineData(new long[] { long.MaxValue - 1, 1 }, long.MaxValue)]
    [InlineData(new long[] { 1, long.MaxValue }, long.MaxValue)]
    public void Total_Point_Count_Adds_Without_Overflow(long[] counts, long expected)
    {
        using var doc = new CountedDocument(counts);

        Assert.Equal(expected, doc.TotalPointCount);
    }

    internal static T WithCulture<T>(CultureInfo culture, Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
