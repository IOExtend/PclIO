using System.Text.Json;
using PclIO.CommandLine;
using Xunit;

namespace PclIO.Tests;

public sealed class CliTests
{
    private sealed record Result(int Exit, string Out, string Err);

    private static Result Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exit = Program.Run(args, stdout, stderr);
        return new Result(exit, stdout.ToString(), stderr.ToString());
    }

    private static string PointsRcs(TempDirectory dir, string name = "points.rcs") =>
        dir.Write(name, SyntheticFixtures.CreateSyntheticRcsWithPoints(
            [new RcsSamplePoint(10000, 20000, 30000, 128, 10, 20, 30), new RcsSamplePoint(0, 0, 0, 255, 1, 2, 3), new RcsSamplePoint(5, 5, 5, 0, 4, 5, 6)],
            (0, 0, 0)));

    private static string MissingPath(string name) => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), name);

    // ---- help, version, usage -----------------------------------------------------------------------------------

    [Fact]
    public void Help_Prints_The_Usage_And_Succeeds()
    {
        var result = Run("--help");

        Assert.Equal(Program.ExitOk, result.Exit);
        Assert.Contains("Usage:", result.Out);
        Assert.Contains("Exit codes", result.Out);
        Assert.Equal("", result.Err);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("help")]
    [InlineData("HELP")]
    public void The_Short_And_Word_Forms_Of_Help_Print_The_Usage(string argument)
    {
        var result = Run(argument);

        Assert.Equal(Program.ExitOk, result.Exit);
        Assert.Equal(Run("--help").Out, result.Out);
        Assert.Equal("", result.Err);
    }

    [Fact]
    public void Help_Text_Is_Plain_Ascii()
    {
        // The text is written to consoles with unknown code pages.
        Assert.All(Run("--help").Out, c => Assert.True(c < 128, $"non-ASCII character U+{(int)c:X4} in the help text"));
    }

    [Fact]
    public void Version_Prints_The_Program_Name_And_A_Version()
    {
        var result = Run("--version");

        Assert.Equal(Program.ExitOk, result.Exit);
        Assert.StartsWith("pclio ", result.Out);
        Assert.True(result.Out.Trim().Length > "pclio ".Length);
    }

    [Theory]
    [InlineData("-v")]
    [InlineData("version")]
    public void The_Short_And_Word_Forms_Of_Version_Print_The_Same_As_The_Long_Form(string argument)
    {
        var result = Run(argument);

        Assert.Equal(Program.ExitOk, result.Exit);
        Assert.Equal(Run("--version").Out, result.Out);
        Assert.Equal("", result.Err);
    }

    [Fact]
    public void No_Arguments_Is_A_Usage_Error_With_One_Line_On_Stderr()
    {
        var result = Run();

        Assert.Equal(Program.ExitUsage, result.Exit);
        Assert.Equal("", result.Out);
        Assert.Single(result.Err.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("error:", result.Err);
    }

    [Theory]
    [InlineData("info")]
    [InlineData("scans")]
    [InlineData("points")]
    [InlineData("export")]
    public void A_Command_Without_Its_Arguments_Is_A_Usage_Error(string command) =>
        Assert.Equal(Program.ExitUsage, Run(command).Exit);

    [Fact]
    public void Unknown_Options_And_Extra_Arguments_Are_Usage_Errors()
    {
        using var dir = new TempDirectory();
        string rcs = PointsRcs(dir);

        Assert.Equal(Program.ExitUsage, Run("info", rcs, "--bogus").Exit);
        Assert.Equal(Program.ExitUsage, Run("info", rcs, "extra").Exit);
        Assert.Equal(Program.ExitUsage, Run("points", rcs, "--limit").Exit);
        Assert.Equal(Program.ExitUsage, Run("points", rcs, "--limit", "5", "--limit", "6").Exit);
    }

    // ---- exit code 2: input missing or unreadable -----------------------------------------------------------------

    [Fact]
    public void A_Missing_Input_Is_Exit_2()
    {
        var result = Run("info", MissingPath("absent.rcs"));

        Assert.Equal(Program.ExitFileNotFound, result.Exit);
        Assert.StartsWith("error:", result.Err);
        Assert.Single(result.Err.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void An_Empty_Input_Is_Exit_2()
    {
        using var dir = new TempDirectory();
        string empty = dir.Write("empty.e57", []);

        Assert.Equal(Program.ExitFileNotFound, Run("info", empty).Exit);
        Assert.Equal(Program.ExitFileNotFound, Run("export", empty, dir.PathOf("out.xyz")).Exit);
        Assert.False(File.Exists(dir.PathOf("out.xyz")));
    }

    // ---- exit code 3: corrupt input -------------------------------------------------------------------------------

    [Theory]
    [InlineData("data.dat")]
    [InlineData("scan.e57")]
    [InlineData("scan.rcs")]
    [InlineData("scan.lgsx")]
    [InlineData("scan.fls")]
    public void Garbage_Input_Is_Exit_3(string name)
    {
        using var dir = new TempDirectory();
        string path = dir.Write(name, [.. "this is not a point cloud file"u8, .. new byte[200]]);

        var result = Run("info", path);

        Assert.Equal(Program.ExitFormatError, result.Exit);
        Assert.Single(result.Err.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.DoesNotContain("   at ", result.Err);
    }

    [Fact]
    public void A_Truncated_File_Is_Exit_3()
    {
        using var dir = new TempDirectory();
        byte[] whole = SyntheticFixtures.CreateSyntheticE57();
        string path = dir.Write("cut.e57", whole[..(whole.Length / 2)]);

        Assert.Equal(Program.ExitFormatError, Run("info", path).Exit);
    }

    // ---- info, scans, points ----------------------------------------------------------------------------------------

    [Fact]
    public void Info_Json_Describes_The_File()
    {
        using var dir = new TempDirectory();
        string path = dir.Write("header.rcs", SyntheticFixtures.CreateSyntheticRcs(pointCount: 777));

        var result = Run("info", path, "--json");

        Assert.Equal(Program.ExitOk, result.Exit);
        using var doc = JsonDocument.Parse(result.Out);
        Assert.Equal("RecapScan", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal(777, doc.RootElement.GetProperty("totalPoints").GetInt64());
        Assert.Equal(1, doc.RootElement.GetProperty("scanCount").GetInt32());
    }

    [Fact]
    public void Info_Without_A_Command_Name_Is_The_Same_As_Info()
    {
        using var dir = new TempDirectory();
        string path = PointsRcs(dir);

        Assert.Equal(Run("info", path).Out, Run(path).Out);
    }

    [Fact]
    public void Scans_Lists_Every_Scan()
    {
        using var dir = new TempDirectory();
        string path = dir.Write("header.rcs", SyntheticFixtures.CreateSyntheticRcs(pointCount: 500));

        var result = Run("scans", path);

        Assert.Equal(Program.ExitOk, result.Exit);
        Assert.Contains("Scans in header.rcs (1):", result.Out);
        Assert.Contains("Points:     500", result.Out);

        using var json = JsonDocument.Parse(Run("scans", path, "--json").Out);
        Assert.Equal(500, json.RootElement[0].GetProperty("pointCount").GetInt64());
    }

    [Fact]
    public void Points_Prints_Xyz_Lines_Applying_The_Limit()
    {
        using var dir = new TempDirectory();
        string path = PointsRcs(dir);

        var all = Run("points", path, "--no-transform");
        var limited = Run("points", path, "--no-transform", "--limit", "2");

        Assert.Equal(Program.ExitOk, all.Exit);
        Assert.Equal(3, all.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(2, limited.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.StartsWith("1.0000 2.0000 3.0000 10 20 30", all.Out);
    }

    [Fact]
    public void Points_In_Pts_Format_Starts_With_The_Number_Of_Lines_That_Follow()
    {
        using var dir = new TempDirectory();
        string path = PointsRcs(dir);

        var result = Run("points", path, "--format", "pts", "--limit", "2");

        string[] lines = result.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(Program.ExitOk, result.Exit);
        Assert.Equal("2", lines[0].Trim());
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void Points_In_Json_Format_Prints_One_Object_Per_Line()
    {
        using var dir = new TempDirectory();
        string path = PointsRcs(dir);

        var result = Run("points", path, "--format", "json", "--no-transform");

        string[] lines = result.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal(1.0, first.RootElement.GetProperty("x").GetDouble(), 9);
        Assert.Equal(10, first.RootElement.GetProperty("r").GetInt32());
    }

    [Fact]
    public void Points_Of_An_Unknown_Scan_Is_A_Usage_Error()
    {
        using var dir = new TempDirectory();
        Assert.Equal(Program.ExitUsage, Run("points", PointsRcs(dir), "--scan", "nope").Exit);
    }

    [Theory]
    [InlineData("--limit", "0")]
    [InlineData("--limit", "-1")]
    [InlineData("--limit", "abc")]
    [InlineData("--limit", "99999999999999999999")]
    [InlineData("--step", "0")]
    [InlineData("--step", "2147483648")]
    [InlineData("--format", "docx")]
    public void Invalid_Option_Values_Are_Usage_Errors(string option, string value)
    {
        using var dir = new TempDirectory();
        string rcs = PointsRcs(dir);

        Assert.Equal(Program.ExitUsage, Run("points", rcs, option, value).Exit);
        Assert.Equal(Program.ExitUsage, Run("export", rcs, dir.PathOf("o.xyz"), option, value).Exit);
        Assert.False(File.Exists(dir.PathOf("o.xyz")));
    }

    [Fact]
    public void A_Negative_Limit_Is_Reported_Not_Mistaken_For_An_Option()
    {
        using var dir = new TempDirectory();
        var result = Run("points", PointsRcs(dir), "--limit", "-5");

        Assert.Equal(Program.ExitUsage, result.Exit);
        Assert.Contains("--limit", result.Err);
    }

    // ---- export ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("las")]
    [InlineData("xyz")]
    [InlineData("pts")]
    [InlineData("json")]
    [InlineData("scene")]
    public void Export_Writes_The_Requested_Format(string format)
    {
        using var dir = new TempDirectory();
        string input = PointsRcs(dir);
        string output = dir.PathOf("out." + format);

        var result = Run("export", input, output);

        Assert.Equal(Program.ExitOk, result.Exit);
        Assert.Contains(format == "json" ? "Exported 0 points" : "Exported 3 points", result.Out);
        Assert.True(new FileInfo(output).Length > 0);
        Assert.Equal(format == "las" ? (byte)'L' : format is "json" or "scene" ? (byte)'{' : (byte)(format == "pts" ? '3' : '1'), File.ReadAllBytes(output)[0]);
        Assert.Equal([Path.GetFileName(output), Path.GetFileName(input)], Directory.GetFiles(dir.Root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Export_Format_Option_Overrides_The_Extension()
    {
        using var dir = new TempDirectory();
        string output = dir.PathOf("out.dat");

        Assert.Equal(Program.ExitOk, Run("export", PointsRcs(dir), output, "--format", "xyz").Exit);
        Assert.StartsWith("1.0000", File.ReadAllText(output));
    }

    [Fact]
    public void Export_Without_A_Known_Extension_Or_Format_Is_A_Usage_Error()
    {
        using var dir = new TempDirectory();

        Assert.Equal(Program.ExitUsage, Run("export", PointsRcs(dir), dir.PathOf("out.dat")).Exit);
        Assert.False(File.Exists(dir.PathOf("out.dat")));
    }

    [Fact]
    public void Export_Using_The_Direct_Form_Works_Like_The_Command()
    {
        using var dir = new TempDirectory();
        string output = dir.PathOf("direct.xyz");

        Assert.Equal(Program.ExitOk, Run(PointsRcs(dir), output).Exit);
        Assert.Equal(3, File.ReadAllLines(output).Length);
    }

    [Fact]
    public void Export_Json_With_A_Limit_Includes_Sample_Points_And_Rejects_Step()
    {
        using var dir = new TempDirectory();
        string input = PointsRcs(dir);
        string output = dir.PathOf("out.json");

        Assert.Equal(Program.ExitOk, Run("export", input, output, "--limit", "2").Exit);
        using (var json = JsonDocument.Parse(File.ReadAllText(output)))
            Assert.Equal(2, json.RootElement.GetProperty("samplePoints").GetArrayLength());

        Assert.Equal(Program.ExitUsage, Run("export", input, dir.PathOf("two.json"), "--step", "2").Exit);
        Assert.Equal(Program.ExitUsage, Run("export", input, dir.PathOf("two.json"), "--no-transform").Exit);
    }

    [Fact]
    public void Export_Scene_Writes_An_Ioscene_Document()
    {
        using var dir = new TempDirectory();
        string input = PointsRcs(dir);
        string output = dir.PathOf("out.scene.json");

        var result = Run("export", input, output);

        Assert.Equal(Program.ExitOk, result.Exit);
        Assert.Contains("Exported 3 points to IOScene JSON", result.Out);
        // Per-point channels that IOScene 0.1.0 does not hold are reported on standard error.
        Assert.Contains("colours-dropped", result.Err);
        Assert.Contains("intensities-dropped", result.Err);
        IOScene.SceneDocument scene = IOScene.SceneJson.Load(output);
        Assert.Equal("ioscene", scene.Kind);
        Assert.Equal(3, scene.Geometries[0].Vertices.Count);
        Assert.Equal("rcs", scene.Models[0].SourceFormat);
    }

    [Fact]
    public void Export_Scene_Format_Option_And_Limit()
    {
        using var dir = new TempDirectory();
        string input = PointsRcs(dir);
        string output = dir.PathOf("out.dat");

        var result = Run("export", input, output, "--format", "scene", "--limit", "2");

        Assert.Equal(Program.ExitOk, result.Exit);
        IOScene.SceneDocument scene = IOScene.SceneJson.Load(output);
        Assert.Equal(2, scene.Geometries[0].Vertices.Count);
        Assert.Contains("points-capped", result.Err);
    }

    [Fact]
    public void Export_Scene_Rejects_No_Transform()
    {
        using var dir = new TempDirectory();

        var result = Run("export", PointsRcs(dir), dir.PathOf("out.scene.json"), "--no-transform");

        Assert.Equal(Program.ExitUsage, result.Exit);
        Assert.False(File.Exists(dir.PathOf("out.scene.json")));
    }

    [Fact]
    public void Export_Refuses_To_Overwrite_The_Input()
    {
        using var dir = new TempDirectory();
        string input = PointsRcs(dir);
        byte[] before = File.ReadAllBytes(input);

        var result = Run("export", input, input, "--format", "xyz", "--force");

        Assert.Equal(Program.ExitUsage, result.Exit);
        Assert.Equal(before, File.ReadAllBytes(input));
    }

    [Fact]
    public void Export_Refuses_The_Input_Under_A_Different_Spelling_Of_Its_Path()
    {
        using var dir = new TempDirectory();
        string input = PointsRcs(dir);
        string alias = Path.Combine(dir.Root, ".", "sub", "..", Path.GetFileName(input));

        Assert.Equal(Program.ExitUsage, Run("export", input, alias, "--format", "xyz", "--force").Exit);
    }

    [SkippableFact]
    public void Export_Refuses_The_Input_Under_The_Verbatim_And_Device_Prefixes()
    {
        if (!OperatingSystem.IsWindows()) throw new SkipException("These path prefixes exist on Windows only.");
        using var dir = new TempDirectory();
        string input = PointsRcs(dir);
        byte[] before = File.ReadAllBytes(input);

        foreach (string prefix in new[] { @"\\?\", @"\\.\" })
        {
            var result = Run("export", input, prefix + input, "--format", "xyz", "--force");

            Assert.Equal(Program.ExitUsage, result.Exit);
            Assert.Contains("same as the input", result.Err);
            Assert.Equal(before, File.ReadAllBytes(input));
        }
    }

    [SkippableFact]
    public void Export_Refuses_An_Output_Name_That_Windows_Cannot_Store_And_Leaves_Nothing_Behind()
    {
        if (!OperatingSystem.IsWindows()) throw new SkipException("These names are invalid on Windows only.");
        using var dir = new TempDirectory();
        string input = PointsRcs(dir);

        foreach (string name in new[] { "out:stream.xyz", "out<name>.xyz" })
        {
            var result = Run("export", input, Path.Combine(dir.Root, name), "--format", "xyz");

            Assert.Equal(Program.ExitUsage, result.Exit);
            Assert.Contains("not valid", result.Err);
        }

        Assert.Equal([input], Directory.GetFileSystemEntries(dir.Root));
    }

    [Fact]
    public void Export_Does_Not_Replace_An_Existing_File_Without_Force()
    {
        using var dir = new TempDirectory();
        string output = dir.WriteText("out.xyz", "precious");

        var result = Run("export", PointsRcs(dir), output);

        Assert.Equal(Program.ExitUsage, result.Exit);
        Assert.Contains("--force", result.Err);
        Assert.Equal("precious", File.ReadAllText(output));
    }

    [Fact]
    public void Export_Replaces_An_Existing_File_With_Force()
    {
        using var dir = new TempDirectory();
        string output = dir.WriteText("out.xyz", "old");

        var result = Run("export", PointsRcs(dir), output, "--force");

        Assert.Equal(Program.ExitOk, result.Exit);
        Assert.StartsWith("1.0000", File.ReadAllText(output));
    }

    [Fact]
    public void Export_To_A_Missing_Directory_Or_A_Directory_Path_Is_A_Usage_Error()
    {
        using var dir = new TempDirectory();
        string input = PointsRcs(dir);

        Assert.Equal(Program.ExitUsage, Run("export", input, Path.Combine(dir.Root, "no-such-folder", "out.xyz")).Exit);

        string folder = dir.PathOf("folder.xyz");
        Directory.CreateDirectory(folder);
        Assert.Equal(Program.ExitUsage, Run("export", input, folder, "--force").Exit);
    }

    /// <summary>A standard output that fails with an exception no command expects, standing in for an internal failure.</summary>
    private sealed class FailingWriter : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value) => throw new InvalidOperationException("simulated internal failure");
    }

    [Fact]
    public void An_Unexpected_Failure_Is_Exit_4_With_One_Line_On_Stderr_And_No_Stack_Trace()
    {
        using var dir = new TempDirectory();
        var stderr = new StringWriter();

        int exit = Program.Run(["info", PointsRcs(dir)], new FailingWriter(), stderr);

        Assert.Equal(Program.ExitUnexpected, exit);
        string line = Assert.Single(stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("error: unexpected failure (InvalidOperationException)", line);
        Assert.DoesNotContain(" at ", line);
    }

    [SkippableFact]
    public void A_Failed_Write_Of_The_Output_Is_Exit_2_And_Leaves_The_Previous_File_And_No_Partial_File()
    {
        using var dir = new TempDirectory();
        string input = PointsRcs(dir);
        string folder = dir.PathOf("out");
        Directory.CreateDirectory(folder);
        string output = Path.Combine(folder, "target.xyz");
        File.WriteAllText(output, "previous");

        Result result;
        if (OperatingSystem.IsWindows())
        {
            // A file that another process holds open cannot be replaced.
            using var held = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.None);
            result = Run("export", input, output, "--force");
        }
        else
        {
            // A folder without write permission cannot take the temporary file. A process that ignores permissions cannot show this.
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                if (TryCreateFile(Path.Combine(folder, "probe")))
                    throw new SkipException("This process is not stopped by folder permissions, so a failed write cannot be provoked.");
                result = Run("export", input, output, "--force");
            }
            finally
            {
                File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        Assert.Equal(Program.ExitFileNotFound, result.Exit);
        Assert.Equal("", result.Out);
        Assert.StartsWith("error:", result.Err);
        Assert.Single(result.Err.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("previous", File.ReadAllText(output));
        Assert.Equal([output], Directory.GetFileSystemEntries(folder));

        static bool TryCreateFile(string path)
        {
            try
            {
                File.WriteAllText(path, "");
                File.Delete(path);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    [Fact]
    public void A_Failed_Export_Leaves_No_Output_And_Keeps_The_Previous_File()
    {
        using var dir = new TempDirectory();

        // The header is valid, but the node table says there are points that are not in the file.
        byte[] bytes = SyntheticFixtures.CreateSyntheticRcsWithPoints([new RcsSamplePoint(1, 2, 3, 0, 0, 0, 0)], (0, 0, 0));
        string input = dir.Write("cut.rcs", bytes[..^8]);
        string output = dir.PathOf("out.las");
        string kept = dir.WriteText("kept.las", "previous");

        var failed = Run("export", input, output, "--format", "las");
        var failedOver = Run("export", input, kept, "--format", "las", "--force");

        Assert.Equal(Program.ExitFormatError, failed.Exit);
        Assert.Equal(Program.ExitFormatError, failedOver.Exit);
        Assert.False(File.Exists(output));
        Assert.Equal("previous", File.ReadAllText(kept));
        Assert.Equal(["cut.rcs", "kept.las"], Directory.GetFileSystemEntries(dir.Root, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Export_Of_A_Missing_Input_Is_Exit_2_And_Writes_Nothing()
    {
        using var dir = new TempDirectory();

        var result = Run("export", MissingPath("absent.e57"), dir.PathOf("out.las"));

        Assert.Equal(Program.ExitFileNotFound, result.Exit);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Root));
    }

    [Fact]
    public void Output_Is_Invariant_Under_The_Current_Culture()
    {
        using var dir = new TempDirectory();
        string rcs = PointsRcs(dir);

        string Output() => Run("points", rcs, "--no-transform").Out;

        string expected = ModelAndMathTests.WithCulture(System.Globalization.CultureInfo.InvariantCulture, Output);
        Assert.Equal(expected, ModelAndMathTests.WithCulture(new System.Globalization.CultureInfo("fr-FR"), Output));
    }

    [Fact]
    public void Run_Leaves_The_Callers_Culture_Unchanged()
    {
        using var dir = new TempDirectory();
        string rcs = PointsRcs(dir);
        var french = new System.Globalization.CultureInfo("fr-FR");

        var after = ModelAndMathTests.WithCulture(french, () =>
        {
            Run("points", rcs);
            Run("export", rcs, dir.PathOf("o.xyz"));
            Run("--bogus-command-line");
            return System.Globalization.CultureInfo.CurrentCulture;
        });

        Assert.Equal("fr-FR", after.Name);
    }

    // ---- output that is the input under another name -------------------------------------------------------------

    private static void CreateFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            throw new SkipException($"This system does not allow symbolic links to be created ({ex.GetType().Name}).");
        }
    }

    /// <summary>A symbolic link, or on Windows a junction when links need a privilege that is not held.</summary>
    private static void CreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            if (!OperatingSystem.IsWindows())
                throw new SkipException($"This system does not allow symbolic links to be created ({ex.GetType().Name}).");
        }

        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using (var process = System.Diagnostics.Process.Start(start)!)
        {
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
        }

        if (!Directory.Exists(link))
            throw new SkipException("This system does not allow symbolic links or junctions to be created.");
    }

    private static void RemoveDirectoryLink(string link)
    {
        // A non-recursive delete removes the link itself and never the folder it leads to.
        if (Directory.Exists(link)) Directory.Delete(link, recursive: false);
    }

    [SkippableFact]
    public void Export_To_A_Path_Without_A_Containing_Folder_Is_A_Usage_Error()
    {
        if (!OperatingSystem.IsWindows()) throw new SkipException("A device name such as NUL is a path without a folder on Windows only.");
        using var dir = new TempDirectory();

        var result = Run("export", PointsRcs(dir), "NUL", "--format", "xyz");

        Assert.Equal(Program.ExitUsage, result.Exit);
    }

    [Theory]
    [InlineData("info")]
    [InlineData("info", "--json")]
    [InlineData("scans")]
    public void A_Count_That_Fails_On_Its_First_Access_Writes_No_Partial_Output(params string[] command)
    {
        // The Leica point count is decoded on first access when the container does not state it; the failure must come
        // before the first line of output.
        byte[] bytes = SyntheticFixtures.CreateSyntheticLgsx(withPointCount: false);
        int block = System.Text.Encoding.Latin1.GetString(bytes).IndexOf("APRN", StringComparison.Ordinal);
        Array.Fill(bytes, (byte)0xA5, block + 0x40, Math.Min(32, bytes.Length - block - 0x40));
        using var dir = new TempDirectory();

        var result = Run([.. command, dir.Write("damaged.lgsx", bytes)]);

        Assert.Equal(Program.ExitFormatError, result.Exit);
        Assert.Equal("", result.Out);
        Assert.StartsWith("error:", result.Err);
    }

    [Fact]
    public void Control_Characters_In_Names_Are_Printed_Escaped_In_Text_Output()
    {
        using var dir = new TempDirectory();
        string path = dir.WriteText("project.smf", SyntheticFixtures.PinPointManifest("Pos\\u001b[31m1", "h1", 5));

        var result = Run("scans", path);

        Assert.Equal(0, result.Exit);
        Assert.DoesNotContain('\u001b', result.Out);
        Assert.Contains("\\u001b", result.Out);
    }

    [SkippableFact]
    public void Export_Refuses_A_Symbolic_Link_To_The_Input_As_Output_In_Either_Direction()
    {
        using var dir = new TempDirectory();
        string input = PointsRcs(dir);
        byte[] before = File.ReadAllBytes(input);
        string link = dir.PathOf("alias.rcs");
        CreateFileLink(link, input);

        var outputIsLink = Run("export", input, link, "--format", "xyz", "--force");
        var inputIsLink = Run("export", link, input, "--format", "xyz", "--force");

        Assert.Equal(Program.ExitUsage, outputIsLink.Exit);
        Assert.Equal(Program.ExitUsage, inputIsLink.Exit);
        Assert.Contains("same as the input", outputIsLink.Err);
        Assert.Equal(before, File.ReadAllBytes(input));
    }

    [SkippableFact]
    public void Export_Refuses_The_Input_Reached_Through_A_Linked_Folder()
    {
        using var dir = new TempDirectory();
        string input = dir.Write("real/points.rcs", File.ReadAllBytes(PointsRcs(dir, "seed.rcs")));
        byte[] before = File.ReadAllBytes(input);
        string link = dir.PathOf("link");
        CreateDirectoryLink(link, dir.PathOf("real"));
        try
        {
            var result = Run("export", input, Path.Combine(link, "points.rcs"), "--format", "xyz", "--force");

            Assert.Equal(Program.ExitUsage, result.Exit);
            Assert.Equal(before, File.ReadAllBytes(input));
        }
        finally
        {
            RemoveDirectoryLink(link);
        }
    }

    [Fact]
    public void Export_Refuses_An_Output_Inside_A_Folder_Input()
    {
        using var dir = new TempDirectory();
        string folder = SyntheticFixtures.WriteFaroFolder(dir);
        string output = Path.Combine(folder, "out.xyz");

        var result = Run("export", folder, output);

        Assert.Equal(Program.ExitUsage, result.Exit);
        Assert.Contains("inside the input directory", result.Err);
        Assert.False(File.Exists(output));
    }

    [SkippableFact]
    public void Export_Refuses_An_Output_Inside_A_Folder_Input_Reached_Through_A_Link()
    {
        using var dir = new TempDirectory();
        string folder = SyntheticFixtures.WriteFaroFolder(dir);
        string link = dir.PathOf("link");
        CreateDirectoryLink(link, folder);
        try
        {
            var result = Run("export", folder, Path.Combine(link, "out.xyz"));

            Assert.Equal(Program.ExitUsage, result.Exit);
            Assert.False(File.Exists(Path.Combine(folder, "out.xyz")));
        }
        finally
        {
            RemoveDirectoryLink(link);
        }
    }

    [Fact]
    public void Export_Of_A_Folder_Input_To_A_Sibling_File_Works()
    {
        using var dir = new TempDirectory();
        string folder = SyntheticFixtures.WriteFaroFolder(dir);
        string output = dir.PathOf("out.xyz");

        var result = Run("export", folder, output);

        Assert.Equal(Program.ExitOk, result.Exit);
        Assert.Equal(SyntheticFixtures.FaroRows * SyntheticFixtures.FaroCols, File.ReadAllLines(output).Length);
    }

    // ---- skipped points -------------------------------------------------------------------------------------------

    /// <summary>A scan whose header scale is huge, so the points whose offset exceeds 1.8 m overflow to infinity when it is applied.</summary>
    private static string NonFiniteRcs(TempDirectory dir)
    {
        byte[] bytes = SyntheticFixtures.CreateSyntheticRcsWithPoints(
            [new RcsSamplePoint(0, 0, 0, 1, 1, 1, 1), new RcsSamplePoint(30000, 0, 0, 2, 2, 2, 2), new RcsSamplePoint(0, 30000, 0, 3, 3, 3, 3), new RcsSamplePoint(0, 0, 0, 4, 4, 4, 4)],
            (0, 0, 0));
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(0x10 + (6 * 8), 8), 1e308);
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(0x10 + (7 * 8), 8), 1e308);
        return dir.Write("huge.rcs", bytes);
    }

    [Theory]
    [InlineData("las")]
    [InlineData("xyz")]
    [InlineData("pts")]
    public void Export_Reports_The_Skipped_Non_Finite_Points_On_Stderr(string format)
    {
        using var dir = new TempDirectory();

        var result = Run("export", NonFiniteRcs(dir), dir.PathOf("out." + format));

        Assert.True(result.Exit == Program.ExitOk, result.Err);
        Assert.Equal("warning: skipped 2 points with a NaN or infinite coordinate\n", result.Err.ReplaceLineEndings("\n"));
        Assert.Contains("Exported 2 points", result.Out);
    }

    [Theory]
    [InlineData("xyz")]
    [InlineData("pts")]
    public void Points_Reports_The_Skipped_Non_Finite_Points_On_Stderr(string format)
    {
        using var dir = new TempDirectory();

        var result = Run("points", NonFiniteRcs(dir), "--format", format);

        Assert.Equal(Program.ExitOk, result.Exit);
        Assert.Equal("warning: skipped 2 points with a NaN or infinite coordinate\n", result.Err.ReplaceLineEndings("\n"));
        Assert.Equal(2, result.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - (format == "pts" ? 1 : 0));
    }

    [Theory]
    [InlineData("las")]
    [InlineData("xyz")]
    [InlineData("pts")]
    [InlineData("json")]
    public void Export_Is_Silent_On_Stderr_When_Nothing_Was_Skipped(string format)
    {
        using var dir = new TempDirectory();

        var result = Run("export", PointsRcs(dir), dir.PathOf("out." + format));

        Assert.Equal(Program.ExitOk, result.Exit);
        Assert.Equal("", result.Err);
    }

    [Theory]
    [InlineData("xyz")]
    [InlineData("pts")]
    [InlineData("json")]
    public void Points_Is_Silent_On_Stderr_When_Nothing_Was_Skipped(string format)
    {
        using var dir = new TempDirectory();

        Assert.Equal("", Run("points", PointsRcs(dir), "--format", format).Err);
    }

    // ---- 'points' and 'export' write the same lines --------------------------------------------------------------

    [Theory]
    [InlineData("xyz", new string[0])]
    [InlineData("pts", new string[0])]
    [InlineData("xyz", new[] { "--no-transform" })]
    [InlineData("pts", new[] { "--no-transform" })]
    [InlineData("xyz", new[] { "--limit", "2" })]
    [InlineData("pts", new[] { "--limit", "2" })]
    [InlineData("xyz", new[] { "--step", "2" })]
    [InlineData("pts", new[] { "--step", "2" })]
    public void Points_Text_Formats_Are_Identical_To_The_Exported_File(string format, string[] options)
    {
        using var dir = new TempDirectory();
        string input = PointsRcs(dir);
        string output = dir.PathOf("out." + format);

        Assert.Equal(Program.ExitOk, Run(["export", input, output, .. options]).Exit);
        var points = Run(["points", input, "--format", format, .. options]);

        Assert.Equal(Program.ExitOk, points.Exit);
        Assert.Equal(File.ReadAllText(output), points.Out);
    }

    [Theory]
    [InlineData("xyz")]
    [InlineData("pts")]
    public void Points_Of_One_Scan_Match_The_Export_Of_A_Document_With_Only_That_Scan(string format)
    {
        using var dir = new TempDirectory();
        string input = PointsRcs(dir);
        string output = dir.PathOf("out." + format);

        Assert.Equal(Program.ExitOk, Run("export", input, output).Exit);
        var byIndex = Run("points", input, "--format", format, "--scan", "0");
        var byName = Run("points", input, "--format", format, "--scan", "points");

        Assert.Equal(File.ReadAllText(output), byIndex.Out);
        Assert.Equal(byIndex.Out, byName.Out);
    }

    [Fact]
    public void Points_Xyz_Lines_Are_Terminated_By_A_Line_Feed_Whatever_The_Platform()
    {
        using var dir = new TempDirectory();

        string text = Run("points", PointsRcs(dir), "--no-transform").Out;

        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("\n", text);
    }
}
