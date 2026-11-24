namespace PclIO.Tests;

/// <summary>Where the tests find private scan files that are not part of the repository. The folder is named by the
/// environment variable PCLIO_TEST_FILES; when it is not set, a folder test_files in the repository root (or in an ancestor of
/// the test binary) is used. A fixture is found by its extension, anywhere up to three folder levels below the root, so no file
/// name is ever written into a test. Temporary and scratch artifacts below the root are never fixtures. A test that needs a
/// file which is not there is reported as skipped with the reason, never passed.</summary>
internal static class TestFiles
{
    public const string EnvironmentVariable = "PCLIO_TEST_FILES";

    private const int MaxDepth = 3;

    /// <summary>Folders searched for fixtures, the one named by the environment variable first.</summary>
    public static IEnumerable<string> Roots() => Roots(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>The same search with the value of the environment variable given, so a test need not change the process environment.</summary>
    public static IEnumerable<string> Roots(string? configured)
    {
        if (configured is { Length: > 0 } && Directory.Exists(configured))
            yield return configured;
        for (var directory = AppContext.BaseDirectory; directory != null; directory = Path.GetDirectoryName(directory))
        {
            string candidate = Path.Combine(directory, "test_files");
            if (Directory.Exists(candidate)) yield return candidate;
        }
    }

    /// <summary>The first file (in ordinal path order) with this extension, or null when there is none.</summary>
    public static string? TryFindByExtension(string extension) => TryFindByExtension(extension, Roots());

    /// <summary>The first file with this extension in the first of the given folders that holds one, or null.</summary>
    public static string? TryFindByExtension(string extension, IEnumerable<string> roots)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = MaxDepth,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        foreach (string root in roots)
        {
            string? found = Directory.EnumerateFiles(root, "*" + extension, options)
                .Where(f => string.Equals(extension, ".copc.laz", StringComparison.OrdinalIgnoreCase)
                    ? f.EndsWith(".copc.laz", StringComparison.OrdinalIgnoreCase)
                    : string.Equals(extension, "ept.json", StringComparison.OrdinalIgnoreCase)
                        ? f.EndsWith("ept.json", StringComparison.OrdinalIgnoreCase)
                        : string.Equals(Path.GetExtension(f), extension, StringComparison.OrdinalIgnoreCase))
                .Where(f => !string.Equals(extension, ".laz", StringComparison.OrdinalIgnoreCase) || !f.EndsWith(".copc.laz", StringComparison.OrdinalIgnoreCase))
                .Where(f => !IsTemporaryArtifact(f, root))
                .Order(StringComparer.Ordinal)
                .FirstOrDefault();
            if (found != null) return found;
        }

        return null;
    }

    /// <summary>Temporary and scratch artifacts are never fixtures: a mirrored Temp folder holds truncated downloads and
    /// crafted fuzz files that a reader is right to reject. Only the part of the path below the search root is inspected, so a
    /// corpus the caller places under a temporary folder still works.</summary>
    private static bool IsTemporaryArtifact(string path, string root)
    {
        if (Path.GetFileName(path).Contains(".tmp.", StringComparison.OrdinalIgnoreCase))
            return true;

        for (var directory = Path.GetDirectoryName(path);
            directory != null && directory.Length > root.Length;
            directory = Path.GetDirectoryName(directory))
        {
            string name = Path.GetFileName(directory);
            if (name.Equals("temp", StringComparison.OrdinalIgnoreCase) || name.Equals("tmp", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>The path of a fixture with this extension, or a <see cref="SkipException"/> when none is available.</summary>
    public static string FindByExtension(string extension) => FindByExtension(extension, Roots());

    /// <summary>The path of a fixture with this extension in the given folders, or a <see cref="SkipException"/> when none is available.</summary>
    public static string FindByExtension(string extension, IEnumerable<string> roots) =>
        TryFindByExtension(extension, roots) ?? throw Missing(extension);

    /// <summary>The skip that names the missing kind of fixture and the variable, without any absolute path.</summary>
    public static SkipException Missing(string extension) =>
        new($"No private '{extension}' fixture is available. Set {EnvironmentVariable} to a folder that holds private scan files, or create a test_files folder.");
}
