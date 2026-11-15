using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace PclIO.Tests;

/// <summary>Keeps private data and proprietary material out of the repository. The files checked are the ones git tracks (or
/// would track), or every file outside build output when git is not available. The patterns are assembled from pieces at
/// run time so that this file does not contain a literal that would match itself. It also checks that the shipped projects
/// reference no package.</summary>
public sealed class RepositoryHygieneTests
{
    private const long MaxFileBytes = 1024 * 1024;

    private static readonly string[] IgnoredFolders = ["bin", "obj", ".git", ".vs", ".idea", "test_files", "TestResults", "_site"];

    // The only binary files the repository may hold are the brand images, and each must really be what its extension says.
    private static readonly Dictionary<string, byte[]> AllowedBinary = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
        [".ico"] = [0x00, 0x00, 0x01, 0x00],
    };

    // Binary scan, model and archive formats must never be committed, in any folder.
    private static readonly string[] ForbiddenExtensions =
        [".e57", ".rcs", ".rcp", ".lgsx", ".lgs", ".fls", ".smf", ".srf", ".c3db", ".c3ptx", ".las", ".ptx", ".zip", ".bin"];

    // What the readers look for at the start of a file; a committed file that begins with one is a scan, whatever its name.
    private static readonly byte[][] ScanSignatures =
    [
        "ASTM-E57"u8.ToArray(),
        "wild::geosystems"u8.ToArray(),
        "ADOCT"u8.ToArray(),
        "LASF"u8.ToArray(),
        "_Tree"u8.ToArray(),
        [0x50, 0x4B, 0x03, 0x04],
        [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1],
    ];

    // Synthetic identifiers of the test fixtures; any other GUID in the repository might come from a private file.
    private static readonly string[] SyntheticGuids =
        ["00000000-0000-0000-0000-000000000001", "12345678-1234-1234-1234-123456789abc"];

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(10);

    private static Regex Rx(string pattern) =>
        new(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeout);

    // Wording that must not appear in any text of the repository or in a commit message. Each pattern is assembled from
    // pieces so that this file does not match itself.
    private static readonly (string Label, Regex Pattern)[] ForbiddenWording =
    [
        // How a vendor format was learned, and the tools used for it.
        ("provenance wording", Rx(
            "reverse" + @"[\s-]+" + "engineer|de" + "compil|disas" + "sembl|ghi" + "dra|il" + "spy|dn" + "spy"
            + @"|\bid" + @"a pro\b|hex" + "-rays|wire" + "shark|rad" + "are|x64" + "dbg|olly" + "dbg|binary" + @"\s+ninja"
            + @"|\bfri" + @"da\b|proc" + @"mon\b|process" + @"\s+monitor")),

        // An assistant, a generator or a tool credited in a text or a commit.
        ("assistant or generator mention", Rx(
            "cla" + "ude|anthr" + "opic|co-auth" + "ored-by|generated " + "with|" + @"\bai[\s-]+assist" + "|chat" + "gpt|open" + @"ai\b")),

        // Sibling projects and other implementations are not referred to.
        ("other project or implementation", Rx(
            @"\b(?:Rvt|Pla|Tdb|Nwc|Dwg|Ifc)" + @"IO\b|\bIO(?:Sc" + "ene|Vie" + @"wer)\b|lib" + "e57|cloud" + "compare|" + @"\bpd" + @"al\b|\bla" + @"spy\b"
            + "|pye" + "57|open" + "3d|las" + "tools|lib" + "las|pot" + "ree")),
    ];

    private static string? TryFindRepoRoot()
    {
        for (string? current = AppContext.BaseDirectory; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (File.Exists(Path.Combine(current, "PclIO.slnx"))) return current;
        }
        return null;
    }

    private static string FindRepoRoot() =>
        TryFindRepoRoot() ?? throw new SkipException("The repository root (the folder with the solution file) was not found above the test binaries.");

    private static string[] Segments(string relativePath) => relativePath.Split('/', '\\');

    private static bool IsIgnored(string relativePath) =>
        Segments(relativePath).Any(s => IgnoredFolders.Contains(s, StringComparer.OrdinalIgnoreCase));

    /// <summary>Repo-relative paths with '/' separators: git's own list when git works, otherwise a scan of the folder.</summary>
    private static List<string> RepositoryFiles(string root)
    {
        var fromGit = TryGitFiles(root);
        var files = fromGit ?? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'));

        // A tracked file that was deleted in the working tree is still listed by git.
        return [.. files.Where(f => !IsIgnored(f) && File.Exists(Path.Combine(root, f))).Order(StringComparer.Ordinal)];
    }

    private static List<string>? TryGitFiles(string root) =>
        RunGit(root, "ls-files", "--cached", "--others", "--exclude-standard", "-z") is { } output
            ? [.. output.Split('\0', StringSplitOptions.RemoveEmptyEntries)]
            : null;

    /// <summary>The standard output of a git command in the repository, or null when git is missing or the command fails.</summary>
    private static string? RunGit(string root, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (string arg in new[] { "-C", root, "-c", "core.quotepath=false" }.Concat(arguments))
            info.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(info);
            if (process == null) return null;
            Task<string> errors = process.StandardError.ReadToEndAsync();
            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(30_000))
            {
                process.Kill();
                return null;
            }
            errors.Wait();
            return process.ExitCode == 0 ? output : null;
        }
        catch (Win32Exception)
        {
            // git is not installed; the caller falls back to scanning the folder or skips.
            return null;
        }
    }

    /// <summary>The text of every file that is not an allowed image: small enough to be read and free of NUL bytes. Binary
    /// files and large files are reported by the test that checks the file types, not here.</summary>
    private static IEnumerable<(string File, string Text)> TextFiles(string root)
    {
        foreach (string file in RepositoryFiles(root))
        {
            string path = Path.Combine(root, file);
            if (AllowedBinary.ContainsKey(Path.GetExtension(file)) || new FileInfo(path).Length > MaxFileBytes) continue;

            byte[] bytes = File.ReadAllBytes(path);
            if (!bytes.Contains((byte)0)) yield return (file, Encoding.UTF8.GetString(bytes));
        }
    }

    [SkippableFact]
    public void Repo_Root_Contains_The_Standard_Files()
    {
        string root = FindRepoRoot();
        string[] required =
        [
            "Directory.Build.props", "Directory.Packages.props", "global.json", ".editorconfig", ".gitattributes", ".gitignore",
            "README.md", "CHANGELOG.md", "LICENSE", "CONTRIBUTING.md", "SECURITY.md", "THIRD-PARTY-NOTICES.md", "CODE_OF_CONDUCT.md",
            "docfx.json", "docs/index.md", "docs/test-fixtures.md",
            ".github/workflows/ci.yml", ".github/workflows/docs.yml", ".github/workflows/release.yml",
            "src/PclIO/icon.png", "src/PclIO.Cli/icon.png",
            "docs/images/pclio-logo.svg", "docs/images/pclio-logo.png", "docs/images/pclio-banner.svg", "docs/images/pclio-banner.png",
        ];

        Assert.All(required, name => Assert.True(File.Exists(Path.Combine(root, name)), $"{name} must exist in the repository"));
    }

    [SkippableFact]
    public void Png_Files_Hold_Only_Image_Chunks()
    {
        // Text chunks and other ancillary chunks can carry an author name or a local path, so only the image itself is allowed.
        string root = FindRepoRoot();
        string[] allowed = ["IHDR", "PLTE", "IDAT", "IEND"];

        foreach (string file in RepositoryFiles(root).Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(root, file));
            long position = 8; // after the signature
            while (position + 12 <= bytes.Length)
            {
                uint length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan((int)position, 4));
                string type = Encoding.ASCII.GetString(bytes, (int)position + 4, 4);
                Assert.True(allowed.Contains(type), $"{file} holds a '{type}' chunk; only {string.Join(", ", allowed)} are allowed");
                position += 12L + length;
            }

            Assert.True(position == bytes.Length, $"{file} is not a well-formed PNG file");
        }
    }

    [SkippableFact]
    public void Gitignore_Excludes_The_Private_Fixture_Folder()
    {
        string root = FindRepoRoot();
        string[] lines = File.ReadAllLines(Path.Combine(root, ".gitignore")).Select(l => l.Trim()).ToArray();

        Assert.Contains(lines, l => l is "test_files" or "test_files/" or "/test_files" or "/test_files/");
    }

    [SkippableFact]
    public void No_Build_Output_Is_Tracked()
    {
        string root = FindRepoRoot();
        var tracked = TryGitFiles(root);
        if (tracked == null) throw new SkipException("git is not available, so the tracked file list cannot be read.");

        Assert.All(tracked.Where(f => File.Exists(Path.Combine(root, f))), f =>
            Assert.False(IsIgnored(f), $"'{f}' is build output or a private folder and must not be tracked"));
    }

    [SkippableFact]
    public void No_Large_Files_Scan_Files_Or_Unlisted_Binaries_Anywhere()
    {
        string root = FindRepoRoot();
        var files = RepositoryFiles(root);
        Assert.NotEmpty(files);

        var problems = new List<string>();
        foreach (string file in files)
        {
            string path = Path.Combine(root, file);
            if (new FileInfo(path).Length > MaxFileBytes)
            {
                problems.Add($"{file}: larger than 1 MiB");
                continue;
            }

            string extension = Path.GetExtension(file);
            if (ForbiddenExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                problems.Add($"{file}: scan, model or archive extension");

            byte[] bytes = File.ReadAllBytes(path);
            if (ScanSignatures.Any(signature => bytes.AsSpan().StartsWith(signature.AsSpan())))
                problems.Add($"{file}: starts with a scan container signature");

            if (AllowedBinary.TryGetValue(extension, out byte[]? magic))
            {
                if (!bytes.AsSpan().StartsWith(magic.AsSpan())) problems.Add($"{file}: content is not a {extension} image");
            }
            else if (bytes.Contains((byte)0))
            {
                problems.Add($"{file}: binary content (only .png and .ico images are allowed)");
            }
        }

        Assert.True(problems.Count == 0, "Files that must not be committed:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [SkippableFact]
    public void No_Personal_Addresses_Machine_Names_Or_Local_Absolute_Paths_In_Text_Files()
    {
        string root = FindRepoRoot();

        // e-mail address, except the project's own GitHub no-reply address and the reserved example domains
        var email = new Regex(@"[A-Za-z0-9._%+\-]+" + "@" + @"[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", RegexOptions.CultureInvariant);
        var allowedEmail = new Regex(
            @"^(?:\d+\+)?mahach666@users\.noreply\.github\.com$|@(?:[A-Za-z0-9\-]+\.)*example\.(?:com|org|net)$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        // drive-letter path such as <letter>:\ or <letter>:/
        var drive = new Regex(@"(?<![A-Za-z0-9])[A-Za-z]:[\\/]", RegexOptions.CultureInvariant);

        // user-profile folders of Linux, macOS and Windows
        string sep = "/";
        var profile = new Regex(
            "(?<![A-Za-z0-9.])" + sep + "(?:" + "ho" + "me|Us" + "ers)" + sep + @"[A-Za-z0-9._\-]+" + "|" + @"\\Us" + @"ers\\[A-Za-z0-9._\-]+",
            RegexOptions.CultureInvariant);

        // Windows default machine names, network share paths and lines that give a scanner serial number a value
        (string Label, Regex Pattern)[] identifiers =
        [
            ("machine name", new Regex(@"\b(?:DESK" + "TOP|LAP" + @"TOP)-[A-Z0-9]{5,}\b", RegexOptions.CultureInvariant)),
            ("network share path", new Regex(@"(?<![\\\w:.])\\\\[\w.\-]+\\[\w$.\-]+", RegexOptions.CultureInvariant)),
            ("serial number value", Rx("ser" + @"ial(?:\s*(?:number|no\.?|#))?\s*[:=#]\s*[""']?(?=[A-Za-z0-9\-]{4,})[A-Za-z\-]*\d")),
        ];

        // Any GUID that is not one of the synthetic ones may be an identifier from a private file.
        var guid = new Regex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.CultureInvariant);

        var problems = new List<string>();
        foreach (var (file, text) in TextFiles(root))
        {
            // Only the kind and the file are reported, never the value that matched.
            foreach (Match m in email.Matches(text))
            {
                if (!allowedEmail.IsMatch(m.Value)) problems.Add($"{file}: e-mail address");
            }
            foreach (Match m in guid.Matches(text))
            {
                if (!SyntheticGuids.Contains(m.Value, StringComparer.OrdinalIgnoreCase)) problems.Add($"{file}: GUID");
            }
            if (drive.IsMatch(text)) problems.Add($"{file}: drive-letter path");
            if (profile.IsMatch(text)) problems.Add($"{file}: user-profile path");
            foreach (var (label, pattern) in identifiers)
            {
                if (pattern.IsMatch(text)) problems.Add($"{file}: {label}");
            }
        }

        Assert.True(problems.Count == 0, "Personal data found:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Distinct()));
    }

    [SkippableFact]
    public void Text_Files_Carry_No_Provenance_Narrative_Tool_Assistant_Or_Project_Mentions()
    {
        string root = FindRepoRoot();

        // Every tracked text file counts: sources, tests, documents, workflows and project files alike.
        var problems = new List<string>();
        foreach (var (file, text) in TextFiles(root))
        {
            foreach (var (label, pattern) in ForbiddenWording)
            {
                if (pattern.IsMatch(text)) problems.Add($"{file}: {label}");
            }
        }

        Assert.True(problems.Count == 0, "Forbidden wording found:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [SkippableFact]
    public void Git_History_Holds_Only_The_Public_Identity_UTC_Dates_And_Clean_Messages()
    {
        string root = FindRepoRoot();

        // Record and field separators keep a multi-line message inside its record: hash, author, committer, dates, message.
        string? log = RunGit(root, "log", "--all", "--format=%h%x1f%an%x1f%ae%x1f%cn%x1f%ce%x1f%ai%x1f%ci%x1f%B%x1e");
        if (log == null) throw new SkipException("The git history is not available, so commit identities and messages were not checked.");

        // The project's own no-reply address, a bot's no-reply address (dependency updates), and the identity that GitHub
        // uses as committer when it merges on the web.
        var own = new Regex(@"^(?:\d+\+)?mahach666@users\.noreply\.github\.com$", RegexOptions.CultureInvariant);
        var bot = new Regex(@"^\d+\+[A-Za-z0-9\-]+\[bot\]@users\.noreply\.github\.com$", RegexOptions.CultureInvariant);

        bool IsAuthor(string name, string address) =>
            (name == "mahach666" && own.IsMatch(address)) || (name.EndsWith("[bot]", StringComparison.Ordinal) && bot.IsMatch(address));

        bool IsCommitter(string name, string address) =>
            IsAuthor(name, address) || (name == "GitHub" && address == "noreply" + "@github.com");

        var problems = new List<string>();
        int commits = 0;
        foreach (string record in log.Split('\u001e', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            commits++;
            string[] f = record.Split('\u001f', 8);
            if (f.Length < 8)
            {
                problems.Add($"commit {commits}: unreadable record");
                continue;
            }

            // Names and addresses are never printed, only which commit and which field is wrong.
            string id = f[0];
            if (!IsAuthor(f[1], f[2])) problems.Add($"{id}: author is not the public identity");
            if (!IsCommitter(f[3], f[4])) problems.Add($"{id}: committer is not the public identity");
            if (!f[5].EndsWith(" +0000", StringComparison.Ordinal)) problems.Add($"{id}: author date is not UTC");
            if (!f[6].EndsWith(" +0000", StringComparison.Ordinal)) problems.Add($"{id}: commit date is not UTC");
            foreach (var (label, pattern) in ForbiddenWording)
            {
                if (pattern.IsMatch(f[7])) problems.Add($"{id}: message has {label}");
            }
        }

        Assert.True(commits > 0, "git reported no commits");
        Assert.True(problems.Count == 0, "History problems:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [SkippableFact]
    public void The_Packages_Have_No_Dependencies_On_Other_Packages()
    {
        string root = FindRepoRoot();

        // Neither shipped project references a package; the only package any of them gets is the build-time one that
        // Directory.Build.props adds with PrivateAssets, which never reaches the consumer.
        foreach (string project in new[] { "src/PclIO/PclIO.csproj", "src/PclIO.Cli/PclIO.Cli.csproj" })
        {
            var references = XDocument.Load(Path.Combine(root, project)).Descendants("PackageReference");
            Assert.Empty(references);
        }

        var shared = XDocument.Load(Path.Combine(root, "Directory.Build.props")).Descendants("PackageReference");
        Assert.All(shared, r => Assert.Equal("all", (string?)r.Attribute("PrivateAssets")));
    }
}
