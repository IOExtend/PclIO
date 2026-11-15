using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using PclIO.Export;

namespace PclIO.CommandLine;

/// <summary>
/// Command line interface of PclIO: inspect, sample and export point cloud files.
/// </summary>
public static class Program
{
    /// <summary>Exit code: the command succeeded.</summary>
    public const int ExitOk = 0;

    /// <summary>Exit code: wrong command line (unknown option, missing or extra argument, invalid value, output refused).</summary>
    public const int ExitUsage = 1;

    /// <summary>Exit code: the input is missing, empty or cannot be read, or the output file cannot be written.</summary>
    public const int ExitFileNotFound = 2;

    /// <summary>Exit code: the input is corrupt, truncated or not supported.</summary>
    public const int ExitFormatError = 3;

    /// <summary>Exit code: unexpected internal failure.</summary>
    public const int ExitUnexpected = 4;

    private const long DefaultPointsLimit = 1000;

    private static readonly string[] PointsFormats = ["xyz", "pts", "json"];
    private static readonly string[] ExportFormats = ["las", "xyz", "pts", "json"];

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // JSON has no NaN/Infinity literals; named literals keep the output valid instead of throwing.
    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private const string UsageText = """
        pclio - read, inspect and convert point cloud files

        Usage:
          pclio info <file> [--json]
          pclio scans <file> [--json]
          pclio points <file> [--scan <id>] [--limit <n>] [--step <s>] [--format <xyz|pts|json>] [--no-transform]
          pclio export <input> <output> [--format <las|xyz|pts|json>] [--limit <n>] [--step <s>] [--no-transform] [--force]
          pclio <input> [--json]               same as 'info'
          pclio <input> <output> [options]     same as 'export'
          pclio --help | -h
          pclio --version | -v

        Commands:
          info        Print format, file size, scan count, point count, bounds, coordinate system, metadata and warnings.
          scans       List the scans with point counts, available channels, bounds and translation.
          points      Print points to standard output. At most 1000 points unless --limit is given.
                      With --format xyz or pts the lines are the same as in the exported file.
          export      Write points to a file. The output is written to a temporary file next to the
                      target and moved into place when complete. Export refuses to write to the input
                      path and refuses to replace an existing file unless --force is given.

        Input formats (detected from the file signature, the extension or the folder layout):
          ASTM E57      by signature, any extension
          ReCap scan    by signature (.rcs)
          ReCap project a ZIP archive with the extension .rcp
          Leica scan    by signature; .lgs selects the older container, any other extension .lgsx
          FARO scan     an OLE2 compound file with the extension .fls, or a folder with a FARO scan layout
          PinPoint      a JSON text file with the extension .smf or .srf, or a folder that holds
                        ScanProject.json, a .smf file or a .srf file
          EdgeWise      the extension .c3db or .c3ptx, or a text or tree signature in another file

        Output formats (--format, or the output extension; any other extension needs --format):
          las    ASPRS LAS 1.2, point data record format 2 (XYZ, intensity, RGB)
          xyz    ASCII lines "X Y Z", followed by "R G B" when the point has a colour, otherwise by the intensity when it has one
          pts    ASCII: a line with the point count, then "X Y Z intensity R G B" per point
          json   export: metadata and scans; with --limit also the first n points (--step and --no-transform are rejected)
                 points: one JSON object per line with x, y, z, intensity, r, g, b (null when the point lacks a value)
          las, xyz, pts and the json export skip points with a NaN or infinite coordinate; the count is reported on
          standard error. 'points --format json' writes every point; such coordinates appear as the strings "NaN",
          "Infinity" and "-Infinity".
          No output format stores normals.

        Options:
          --scan <id>       points only: read one scan by 0-based index, id or name (default: all scans)
          --limit <n>       Maximum number of points, an integer >= 1 (points: default 1000; export: default all)
          --step <s>        Subsampling step, an integer >= 1 (default 1)
          --no-transform    Return coordinates as stored, without the scan transform
          --force           export only: replace an existing output file
          --json            info and scans: print JSON
          -h, --help        Show this help and exit
          -v, --version     Show the version and exit

        Exit codes (one line on standard error, no stack trace):
          0  success
          1  wrong command line: unknown option, missing or extra argument, invalid value, output refused
          2  input cannot be read (missing, empty or unreadable) or the output file cannot be written
          3  input is corrupt, truncated or unsupported
          4  unexpected internal failure
        """;

    /// <summary>Process entry point.</summary>
    /// <param name="args">Command line arguments.</param>
    /// <returns>The process exit code.</returns>
    public static int Main(string[] args)
    {
        return Run(args, Console.Out, Console.Error);
    }

    /// <summary>Runs the command line with the given arguments and writers.</summary>
    /// <param name="args">Command line arguments.</param>
    /// <param name="stdout">Receives normal output.</param>
    /// <param name="stderr">Receives error messages, one line each.</param>
    /// <returns>An exit code: <see cref="ExitOk"/>, <see cref="ExitUsage"/>, <see cref="ExitFileNotFound"/>, <see cref="ExitFormatError"/> or <see cref="ExitUnexpected"/>.</returns>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        // The culture is only changed for the duration of the call, so callers such as tests keep theirs.
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            return RunCommand(args, stdout, stderr);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private static int RunCommand(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
        {
            stderr.WriteLine("error: no command given; run 'pclio --help'");
            return ExitUsage;
        }

        if (args.Contains("--help") || args.Contains("-h") || args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            stdout.WriteLine(UsageText);
            return ExitOk;
        }

        if (args.Contains("--version") || args.Contains("-v") || args[0].Equals("version", StringComparison.OrdinalIgnoreCase))
        {
            var version = typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(Program).Assembly.GetName().Version?.ToString()
                ?? "0.1.0";
            stdout.WriteLine($"pclio {version}");
            return ExitOk;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "info" => RunInfo("info", args[1..], stdout),
                "scans" => RunScans(args[1..], stdout),
                "points" => RunPoints(args[1..], stdout, stderr),
                "export" => RunExport("export", args[1..], stdout, stderr),
                _ => RunDirect(args, stdout, stderr),
            };
        }
        catch (CliException ex)
        {
            return Fail(stderr, ex.ExitCode, ex.Message);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Fail(stderr, ExitFileNotFound, $"file or directory not found: {ex.Message}");
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException or JsonException or XmlException)
        {
            return Fail(stderr, ExitFormatError, $"input is corrupt, truncated or unsupported: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Also reached for a failed write of the output; the message names the file.
            return Fail(stderr, ExitFileNotFound, $"file cannot be read or written: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Fail(stderr, ExitUnexpected, $"unexpected failure ({ex.GetType().Name}): {ex.Message}");
        }
    }

    // Names and messages come from file contents; control characters would reach the terminal as escape sequences.
    private static string Printable(string text)
    {
        if (!text.Any(char.IsControl)) return text;

        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsControl(c)) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static int Fail(TextWriter stderr, int exitCode, string message)
    {
        stderr.WriteLine($"error: {Printable(message.ReplaceLineEndings(" "))}");
        return exitCode;
    }

    private static int RunInfo(string command, string[] args, TextWriter stdout)
    {
        var parsed = Parse(command, args, [], ["--json"]);
        ExpectPositionals(command, parsed, 1, "<file>");
        string input = ResolveInput(parsed.Positionals[0]);

        using var doc = PclFile.Open(input);

        // Some formats count points on first access and can fail then; do it before any output is written.
        long totalPoints = doc.TotalPointCount;

        if (parsed.Flags.Contains("--json"))
        {
            var bounds = doc.OverallBounds;
            var payload = new
            {
                path = doc.FilePath,
                fileSize = doc.FileSize,
                kind = doc.Kind.ToString(),
                scanCount = doc.Scans.Count,
                totalPoints,
                bounds = bounds.IsEmpty ? null : new
                {
                    min = new[] { bounds.Min.X, bounds.Min.Y, bounds.Min.Z },
                    max = new[] { bounds.Max.X, bounds.Max.Y, bounds.Max.Z },
                    size = new[] { bounds.Size.X, bounds.Size.Y, bounds.Size.Z },
                },
                coordinateSystem = doc.CoordinateSystem,
                metadata = doc.Metadata,
                warnings = doc.Warnings,
            };
            stdout.WriteLine(JsonSerializer.Serialize(payload, IndentedJson));
            return ExitOk;
        }

        stdout.WriteLine($"File:       {doc.FilePath} ({doc.FileSize:N0} bytes)");
        stdout.WriteLine($"Format:     {doc.Kind}");
        stdout.WriteLine($"Scans:      {doc.Scans.Count:N0}");
        stdout.WriteLine($"Points:     {totalPoints:N0}");

        if (doc.OverallBounds.IsEmpty)
        {
            stdout.WriteLine("Bounds:     (empty)");
        }
        else
        {
            var b = doc.OverallBounds;
            stdout.WriteLine($"Bounds Min: [{b.Min.X:F3}, {b.Min.Y:F3}, {b.Min.Z:F3}]");
            stdout.WriteLine($"Bounds Max: [{b.Max.X:F3}, {b.Max.Y:F3}, {b.Max.Z:F3}]");
            stdout.WriteLine($"Extent:     dX={b.Size.X:F3} m, dY={b.Size.Y:F3} m, dZ={b.Size.Z:F3} m");
        }

        if (!string.IsNullOrWhiteSpace(doc.CoordinateSystem))
        {
            stdout.WriteLine($"CoordSys:   {Printable(doc.CoordinateSystem)}");
        }

        if (doc.Metadata.Count > 0)
        {
            stdout.WriteLine("Metadata:");
            foreach (var kvp in doc.Metadata)
            {
                stdout.WriteLine($"  {Printable(kvp.Key)}: {Printable(kvp.Value)}");
            }
        }

        if (doc.Warnings.Count > 0)
        {
            stdout.WriteLine($"Warnings ({doc.Warnings.Count}):");
            foreach (var w in doc.Warnings)
            {
                stdout.WriteLine($"  ! {Printable(w)}");
            }
        }

        return ExitOk;
    }

    private static int RunScans(string[] args, TextWriter stdout)
    {
        var parsed = Parse("scans", args, [], ["--json"]);
        ExpectPositionals("scans", parsed, 1, "<file>");
        string input = ResolveInput(parsed.Positionals[0]);

        using var doc = PclFile.Open(input);

        if (parsed.Flags.Contains("--json"))
        {
            var scansList = doc.Scans.Select((s, index) => new
            {
                index,
                id = s.Id,
                name = s.Name,
                pointCount = s.PointCount,
                hasColors = s.HasColors,
                hasIntensities = s.HasIntensities,
                hasNormals = s.HasNormals,
                bounds = s.Bounds.IsEmpty ? null : new
                {
                    min = new[] { s.Bounds.Min.X, s.Bounds.Min.Y, s.Bounds.Min.Z },
                    max = new[] { s.Bounds.Max.X, s.Bounds.Max.Y, s.Bounds.Max.Z },
                },
                properties = s.Properties,
            });
            stdout.WriteLine(JsonSerializer.Serialize(scansList, IndentedJson));
            return ExitOk;
        }

        // Counts that are decoded on first access can fail; take them before the first line is written.
        foreach (var scan in doc.Scans) _ = scan.PointCount;

        stdout.WriteLine($"Scans in {Printable(Path.GetFileName(doc.FilePath))} ({doc.Scans.Count}):");
        int idx = 0;
        foreach (var scan in doc.Scans)
        {
            stdout.WriteLine($"  [{idx}] \"{Printable(scan.Name)}\" (id: {Printable(scan.Id)})");
            stdout.WriteLine($"      Points:     {scan.PointCount:N0}");
            stdout.WriteLine($"      Channels:   color={scan.HasColors}, intensity={scan.HasIntensities}, normal={scan.HasNormals}");
            if (!scan.Bounds.IsEmpty)
            {
                stdout.WriteLine($"      Bounds:     min=[{scan.Bounds.Min.X:F2}, {scan.Bounds.Min.Y:F2}, {scan.Bounds.Min.Z:F2}], max=[{scan.Bounds.Max.X:F2}, {scan.Bounds.Max.Y:F2}, {scan.Bounds.Max.Z:F2}]");
            }
            if (!scan.Transform.IsIdentity)
            {
                var t = scan.Transform.Translation;
                stdout.WriteLine($"      Translation: [{t.X:F3}, {t.Y:F3}, {t.Z:F3}]");
            }
            idx++;
        }

        return ExitOk;
    }

    private static int RunPoints(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var parsed = Parse("points", args, ["--scan", "--limit", "--step", "--format"], ["--no-transform"]);
        ExpectPositionals("points", parsed, 1, "<file>");
        string format = ParseFormat(parsed, PointsFormats) ?? "xyz";
        var options = BuildOptions(parsed, DefaultPointsLimit);
        string input = ResolveInput(parsed.Positionals[0]);

        using var doc = PclFile.Open(input, options);

        // The exporters take a document, so a single scan is presented as a one-scan view of it.
        PclDocument source = doc;
        if (parsed.Values.TryGetValue("--scan", out string? scanFilter))
        {
            var scan = FindScan(doc, scanFilter) ?? throw Usage($"scan '{scanFilter}' not found");
            source = new SingleScanDocument(doc, scan);
        }

        if (format == "json")
        {
            foreach (var pt in source.ReadAllPoints(options))
            {
                stdout.WriteLine(JsonSerializer.Serialize(new
                {
                    x = pt.X,
                    y = pt.Y,
                    z = pt.Z,
                    intensity = pt.Intensity,
                    r = pt.Color?.R,
                    g = pt.Color?.G,
                    b = pt.Color?.B,
                }, CompactJson));
            }
        }
        else
        {
            // The same writers as 'export', so both commands produce identical lines.
            long skipped;
            if (format == "pts")
                PclPtsExporter.Export(source, stdout, options, out skipped);
            else
                PclXyzExporter.Export(source, stdout, options, out skipped);

            ReportSkipped(stderr, skipped);
        }

        return ExitOk;
    }

    private static int RunExport(string command, string[] args, TextWriter stdout, TextWriter stderr)
    {
        var parsed = Parse(command, args, ["--format", "--limit", "--step"], ["--no-transform", "--force"]);
        ExpectPositionals(command, parsed, 2, "<input> <output>");

        string outputArg = parsed.Positionals[1];
        string format = ParseFormat(parsed, ExportFormats) ?? FormatFromExtension(outputArg);
        var options = BuildOptions(parsed, defaultLimit: null);

        if (format == "json")
        {
            // The JSON exporter builds its own read options and takes an int point limit.
            if (parsed.Values.ContainsKey("--step") || parsed.Flags.Contains("--no-transform"))
                throw Usage("--step and --no-transform are not supported with --format json");
            if (options.MaxPoints > int.MaxValue)
                throw Usage($"--limit for --format json must not exceed {int.MaxValue}");
        }

        string input = ResolveInput(parsed.Positionals[0]);
        string output = FullPath(outputArg, "output");

        // Compare where the paths really lead, so links and junctions cannot hide that the output is the input.
        string inputId = ResolveIdentity(input);
        string outputId = ResolveIdentity(output);
        if (string.Equals(inputId, outputId, PathComparison))
            throw Usage("output path is the same as the input path");
        if (Directory.Exists(input) && IsInside(outputId, inputId))
            throw Usage("output path is inside the input directory");
        if (Directory.Exists(output))
            throw Usage($"output path is a directory: {outputArg}");

        // An invalid name, or on Windows a ':' that would name a data stream, fails only at the write and may leave an entry behind.
        if (Path.GetFileName(output).AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw Usage($"output file name is not valid: {outputArg}");

        // A device name such as NUL has no containing folder, so it cannot be written through a temporary file.
        string? dir = Path.GetDirectoryName(output);
        if (string.IsNullOrEmpty(dir))
            throw Usage($"output must be a regular file path: {outputArg}");
        if (!Directory.Exists(dir))
            throw Usage($"output directory does not exist: {dir}");
        if (File.Exists(output) && !parsed.Flags.Contains("--force"))
            throw Usage($"output file already exists (use --force to replace it): {outputArg}");

        // Spellings that the path comparison above cannot resolve, such as the verbatim and device prefixes and administrative shares.
        if (File.Exists(input) && File.Exists(output) && FileIdentity.IsSameFile(input, output))
            throw Usage("output path is the same as the input path");

        using var doc = PclFile.Open(input, options);

        long count;
        long skipped = 0;
        string label;
        bool force = parsed.Flags.Contains("--force");
        switch (format)
        {
            case "las":
                label = "ASPRS LAS";
                count = WriteThroughTempFile(output, stderr, force, fs =>
                {
                    long n = PclLasExporter.Export(doc, fs, options, out long s);
                    skipped = s;
                    return n;
                });
                break;
            case "json":
                label = "JSON";
                int? jsonLimit = options.MaxPoints.HasValue ? (int)options.MaxPoints.Value : null;
                count = WriteThroughTempFile(output, stderr, force, fs => WriteText(fs, sw =>
                {
                    long n = PclJsonExporter.Export(doc, sw, true, jsonLimit, out long s);
                    skipped = s;
                    return n;
                }));
                break;
            case "pts":
                label = "PTS";
                count = WriteThroughTempFile(output, stderr, force, fs => WriteText(fs, sw =>
                {
                    long n = PclPtsExporter.Export(doc, sw, options, out long s);
                    skipped = s;
                    return n;
                }));
                break;
            default:
                label = "XYZ";
                count = WriteThroughTempFile(output, stderr, force, fs => WriteText(fs, sw =>
                {
                    long n = PclXyzExporter.Export(doc, sw, options, out long s);
                    skipped = s;
                    return n;
                }));
                break;
        }

        ReportSkipped(stderr, skipped);
        stdout.WriteLine($"Exported {count:N0} points to {label} file '{outputArg}'");
        return ExitOk;
    }

    private static int RunDirect(string[] args, TextWriter stdout, TextWriter stderr)
    {
        // 'pclio <input> [--json]' is info; anything else is 'pclio <input> <output> [options]'.
        if (args.Skip(1).All(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase)))
            return RunInfo("info", args, stdout);

        return RunExport("export", args, stdout, stderr);
    }

    private static void ReportSkipped(TextWriter stderr, long skipped)
    {
        if (skipped > 0)
            stderr.WriteLine($"warning: skipped {skipped:N0} points with a NaN or infinite coordinate");
    }

    /// <summary>Returns the path with every link and junction on it (including its parent folders) followed, so aliases compare equal.</summary>
    private static string ResolveIdentity(string fullPath)
    {
        string path = Path.TrimEndingDirectorySeparator(fullPath);
        string? parent = Path.GetDirectoryName(path);
        if (parent is null)
            return path; // a filesystem root is never a link

        path = Path.Combine(ResolveIdentity(parent), Path.GetFileName(path));

        FileSystemInfo? info = Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path)
            : File.Exists(path) ? new FileInfo(path)
            : null;
        var target = info?.ResolveLinkTarget(returnFinalTarget: true);

        // The target may sit below another link; resolving it again ends because a final target is not a link.
        return target is null ? path : ResolveIdentity(target.FullName);
    }

    private static bool IsInside(string path, string directory)
    {
        string prefix = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    private static long WriteText(Stream stream, Func<TextWriter, long> write)
    {
        using var writer = new StreamWriter(stream, Utf8NoBom, bufferSize: 65536, leaveOpen: true) { NewLine = "\n" };
        long count = write(writer);
        writer.Flush();
        return count;
    }

    /// <summary>Writes to a temporary file in the target folder and moves it into place, so a failure never leaves a partial output.</summary>
    private static long WriteThroughTempFile(string output, TextWriter stderr, bool overwrite, Func<Stream, long> write)
    {
        string dir = Path.GetDirectoryName(output) ?? throw Usage($"output must be a regular file path: {output}");
        string temp = Path.Combine(dir, $".{Path.GetFileName(output)}.{Path.GetRandomFileName()}.tmp");
        bool moved = false;
        try
        {
            long count;
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                count = write(fs);
            }

            File.Move(temp, output, overwrite);
            moved = true;
            return count;
        }
        finally
        {
            if (!moved)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    stderr.WriteLine($"warning: could not remove temporary file '{temp}': {ex.Message.ReplaceLineEndings(" ")}");
                }
            }
        }
    }

    private static PclScan? FindScan(PclDocument doc, string query)
    {
        if (int.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out int idx) && idx < doc.Scans.Count)
            return doc.Scans[idx];

        return doc.Scans.FirstOrDefault(s => s.Id.Equals(query, StringComparison.OrdinalIgnoreCase)
                                          || s.Name.Equals(query, StringComparison.OrdinalIgnoreCase));
    }

    private static PclReadOptions BuildOptions(ParsedArgs parsed, long? defaultLimit) => new()
    {
        MaxPoints = ParseCount(parsed, "--limit", long.MaxValue) ?? defaultLimit,
        SubsampleStep = (int)(ParseCount(parsed, "--step", int.MaxValue) ?? 1),
        ApplyTransform = !parsed.Flags.Contains("--no-transform"),
    };

    private static long? ParseCount(ParsedArgs parsed, string option, long max)
    {
        if (!parsed.Values.TryGetValue(option, out string? text))
            return null;

        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value) || value < 1 || value > max)
            throw Usage($"invalid value '{text}' for {option}: expected an integer from 1 to {max}");

        return value;
    }

    private static string? ParseFormat(ParsedArgs parsed, string[] allowed)
    {
        if (!parsed.Values.TryGetValue("--format", out string? text))
            return null;

        string format = text.ToLowerInvariant();
        if (!allowed.Contains(format))
            throw Usage($"unknown format '{text}' for --format; expected one of: {string.Join(", ", allowed)}");

        return format;
    }

    private static string FormatFromExtension(string outputPath)
    {
        string ext = Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant();
        if (!ExportFormats.Contains(ext))
            throw Usage($"cannot infer the output format from '{outputPath}'; use --format <{string.Join('|', ExportFormats)}>");

        return ext;
    }

    /// <summary>Returns the full path of an existing, non-empty input file or an existing input directory.</summary>
    private static string ResolveInput(string path)
    {
        string full = FullPath(path, "input");
        if (File.Exists(full))
        {
            // A link reports its own size on some systems, so the emptiness is judged on the file it leads to.
            var info = new FileInfo(full);
            var target = info.ResolveLinkTarget(returnFinalTarget: true) as FileInfo ?? info;
            if (target.Length == 0)
                throw new CliException(ExitFileNotFound, $"input file is empty: {path}");
        }
        else if (!Directory.Exists(full))
        {
            throw new CliException(ExitFileNotFound, $"input not found: {path}");
        }

        return full;
    }

    private static string FullPath(string path, string role)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Usage($"invalid {role} path '{path}': {ex.Message}");
        }
    }

    private static CliException Usage(string message) => new(ExitUsage, message);

    private static void ExpectPositionals(string command, ParsedArgs parsed, int expected, string syntax)
    {
        if (parsed.Positionals.Count < expected)
            throw Usage($"'{command}' needs {syntax}");
        if (parsed.Positionals.Count > expected)
            throw Usage($"'{command}' got an unexpected argument: {parsed.Positionals[expected]}");
    }

    /// <summary>Splits arguments into positionals, flags and options with values; anything else that starts with '-' is rejected.</summary>
    private static ParsedArgs Parse(string command, string[] args, string[] valueOptions, string[] flagOptions)
    {
        var parsed = new ParsedArgs();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.Length < 2 || arg[0] != '-')
            {
                parsed.Positionals.Add(arg);
                continue;
            }

            string? flag = flagOptions.FirstOrDefault(o => o.Equals(arg, StringComparison.OrdinalIgnoreCase));
            if (flag != null)
            {
                if (!parsed.Flags.Add(flag))
                    throw Usage($"option '{flag}' given more than once");
                continue;
            }

            string? option = valueOptions.FirstOrDefault(o => o.Equals(arg, StringComparison.OrdinalIgnoreCase));
            if (option == null)
                throw Usage($"unknown option '{arg}' for '{command}'");

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw Usage($"option '{option}' needs a value");

            if (!parsed.Values.TryAdd(option, args[++i]))
                throw Usage($"option '{option}' given more than once");
        }

        return parsed;
    }

    private sealed class ParsedArgs
    {
        public List<string> Positionals { get; } = [];

        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A view of one scan of an open document; it owns nothing and is never disposed separately.</summary>
    private sealed class SingleScanDocument(PclDocument source, PclScan scan) : PclDocument(source.FilePath, source.FileSize)
    {
        public override PclFileKind Kind => source.Kind;

        public override IReadOnlyList<PclScan> Scans { get; } = [scan];
    }

    private sealed class CliException(int exitCode, string message) : Exception(message)
    {
        public int ExitCode { get; } = exitCode;
    }
}
