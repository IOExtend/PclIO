using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace PclIO.Tests;

/// <summary>A missing private fixture must be reported as a skip, never as a pass and never as a failure: this checks the
/// conversion the skippable attributes rely on, and the lookup of fixtures.</summary>
public class SkippableFactTests
{
    private sealed class Recorder : IMessageBus
    {
        public List<IMessageSinkMessage> Messages { get; } = [];
        public bool QueueMessage(IMessageSinkMessage message) { Messages.Add(message); return true; }
        public void Dispose() { }
    }

    private static ITest StubTest()
    {
        var type = typeof(SkippableFactTests);
        var collection = new TestCollection(new TestAssembly(Reflector.Wrap(type.Assembly)), null, "stub collection");
        var method = new TestMethod(new TestClass(collection, Reflector.Wrap(type)), Reflector.Wrap(type.GetMethod(nameof(AMissingFixtureIsASkipExceptionNamingTheVariable))!));
        return new XunitTest(new XunitTestCase(new NullMessageSink(), TestMethodDisplay.Method, TestMethodDisplayOptions.None, method), "stub");
    }

    private static TestFailed Failed(params string[] exceptionTypes) =>
        new(StubTest(), 0m, "", exceptionTypes, exceptionTypes.Select(type => "message of " + type).ToArray(),
            exceptionTypes.Select(_ => "").ToArray(), exceptionTypes.Select((_, index) => index - 1).ToArray());

    [Fact]
    public void ASkipExceptionFailureBecomesASkipWithItsReason()
    {
        var inner = new Recorder();
        var bus = new SkipConvertingBus(inner);
        bus.QueueMessage(Failed(typeof(SkipException).FullName!));
        var skipped = Assert.IsAssignableFrom<ITestSkipped>(Assert.Single(inner.Messages));
        Assert.Equal("message of " + typeof(SkipException).FullName, skipped.Reason);
        var summary = bus.Adjust(new RunSummary { Total = 1, Failed = 1 });
        Assert.Equal((1, 0, 1), (summary.Total, summary.Failed, summary.Skipped));
    }

    [Fact]
    public void ASkipExceptionWrappedByAFixtureFailureBecomesASkip()
    {
        var inner = new Recorder();
        new SkipConvertingBus(inner).QueueMessage(Failed("Xunit.Sdk.TestClassException", typeof(SkipException).FullName!));
        Assert.IsAssignableFrom<ITestSkipped>(Assert.Single(inner.Messages));
    }

    [Theory]
    [InlineData("Xunit.Sdk.EqualException")]
    [InlineData("System.IO.FileNotFoundException")]
    public void AnyOtherFailureStaysAFailure(string exceptionType)
    {
        var inner = new Recorder();
        var bus = new SkipConvertingBus(inner);
        bus.QueueMessage(Failed(exceptionType));
        Assert.IsAssignableFrom<ITestFailed>(Assert.Single(inner.Messages));
        var summary = bus.Adjust(new RunSummary { Total = 1, Failed = 1 });
        Assert.Equal((1, 0), (summary.Failed, summary.Skipped));
    }

    [Fact]
    public void AMissingFixtureIsASkipExceptionNamingTheVariable()
    {
        var exception = Assert.Throws<SkipException>(() => TestFiles.FindByExtension(".no-such-extension"));
        Assert.Contains(TestFiles.EnvironmentVariable, exception.Message);
        Assert.Contains(".no-such-extension", exception.Message);
        Assert.Null(TestFiles.TryFindByExtension(".no-such-extension"));
    }

    [Fact]
    public void TheSkipReasonNamesNoAbsolutePath()
    {
        using var dir = new TempDirectory();
        string message = Assert.Throws<SkipException>(() => TestFiles.FindByExtension(".e57", [dir.Root])).Message;

        // Neither the folder that was searched nor any other place on this machine may be named in the reason.
        foreach (string place in new[] { dir.Root, Path.GetTempPath(), AppContext.BaseDirectory, Environment.CurrentDirectory })
            Assert.DoesNotContain(place, message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), message);
        Assert.DoesNotContain(":\\", message);
    }

    [Fact]
    public void AFixtureIsFoundUpToThreeFolderLevelsDownWhateverTheCaseOfItsExtension()
    {
        using var dir = new TempDirectory();
        dir.Write("a/b/c/deep.e57", []);
        dir.Write("a/b/c/d/too-deep.rcs", []);
        dir.Write("upper.LGSX", []);
        dir.Write("longer.fls1", []);

        Assert.Equal(Path.Combine(dir.Root, "a", "b", "c", "deep.e57"), TestFiles.TryFindByExtension(".e57", [dir.Root]));
        Assert.Equal(Path.Combine(dir.Root, "upper.LGSX"), TestFiles.TryFindByExtension(".lgsx", [dir.Root]));

        // Four levels down is not searched, and an extension that only begins like the wanted one does not match.
        Assert.Null(TestFiles.TryFindByExtension(".rcs", [dir.Root]));
        Assert.Null(TestFiles.TryFindByExtension(".fls", [dir.Root]));
    }

    [Fact]
    public void WhenSeveralFixturesMatchTheFirstInOrdinalPathOrderIsUsed()
    {
        using var dir = new TempDirectory();
        dir.Write("a/first.c3db", []);
        dir.Write("B/second.c3db", []);

        // Ordinal order puts the capital letter before the lower case one; a culture-aware order would not.
        Assert.Equal(Path.Combine(dir.Root, "B", "second.c3db"), TestFiles.TryFindByExtension(".c3db", [dir.Root]));
    }

    [Fact]
    public void TheFirstFolderThatHoldsAFixtureWins()
    {
        using var first = new TempDirectory();
        using var second = new TempDirectory();
        using var third = new TempDirectory();
        second.Write("x.smf", []);
        third.Write("y.smf", []);

        Assert.Equal(second.PathOf("x.smf"), TestFiles.TryFindByExtension(".smf", [first.Root, second.Root, third.Root]));
    }

    [Fact]
    public void TheConfiguredFolderComesFirstAndOneThatDoesNotExistIsIgnored()
    {
        using var dir = new TempDirectory();

        Assert.Equal(dir.Root, TestFiles.Roots(dir.Root).First());
        Assert.DoesNotContain(dir.PathOf("absent"), TestFiles.Roots(dir.PathOf("absent")));
        Assert.DoesNotContain(dir.Root, TestFiles.Roots(""));
        Assert.DoesNotContain(dir.Root, TestFiles.Roots(null));
    }

    [Fact]
    public void TemporaryAndScratchArtifactsBelowTheRootAreNeverFixtures()
    {
        using var dir = new TempDirectory();
        dir.Write("Temp/aaa.e57", []);
        dir.Write("appdata/Local/TMP/bbb.e57", []);
        dir.Write("ccc.tmp.e57", []);
        dir.Write("pc/ddd.e57", []);

        // The Temp folders and the .tmp. infix sort before the real sample in ordinal order; none of them may win.
        Assert.Equal(Path.Combine(dir.Root, "pc", "ddd.e57"), TestFiles.TryFindByExtension(".e57", [dir.Root]));
        Assert.Equal([Path.Combine(dir.Root, "pc", "ddd.e57")], TestFiles.TryFindAllByExtension(".e57", [dir.Root]));

        // A corpus the caller places under a temporary folder still works: only the part below the root is inspected.
        using var nested = new TempDirectory();
        nested.Write("real.e57", []);
        Assert.Equal(nested.PathOf("real.e57"), TestFiles.TryFindByExtension(".e57", [nested.Root]));
    }
}
