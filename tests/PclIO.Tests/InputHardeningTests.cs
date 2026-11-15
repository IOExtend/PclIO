using System.Buffers.Binary;
using PclIO.Formats.E57;
using PclIO.Formats.EdgeWise;
using PclIO.Formats.Faro;
using PclIO.Formats.Leica;
using PclIO.Formats.PinPoint;
using PclIO.Formats.Recap;
using PclIO.Internal;
using Xunit;

namespace PclIO.Tests;

/// <summary>Feeds damaged and hostile input to every reader. Whatever the bytes are, a reader must finish within a bounded
/// time and report a malformed file as <see cref="InvalidDataException"/>; it must never hang, overflow the stack, allocate
/// without limit or read outside the folder of the project.</summary>
public sealed class InputHardeningTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    /// <summary>Runs the action on a pool thread and fails the test when it does not finish in time.</summary>
    private static T Bounded<T>(Func<T> action)
    {
        var task = Task.Run(action);
        if (!((IAsyncResult)task).AsyncWaitHandle.WaitOne(Limit))
            throw new TimeoutException($"The reader did not finish within {Limit.TotalSeconds} seconds.");
        return task.GetAwaiter().GetResult();
    }

    private static void Bounded(Action action) => Bounded(() =>
    {
        action();
        return 0;
    });

    private static long OpenAndReadAll(Func<string, PclDocument> open, string path) => Bounded(() =>
    {
        using var doc = open(path);
        return doc.ReadAllPoints().LongCount();
    });

    // ---- Formats under test: a valid file and the reader that opens it ---------------------------------------------

    private sealed record Sample(string FileName, byte[] Bytes, Func<string, PclDocument> Open);

    public static TheoryData<string> FormatNames =>
        ["e57", "rcs", "rcp", "lgsx", "fls", "smf", "c3ptx"];

    private static Sample MakeSample(string name) => name switch
    {
        "e57" => new("sample.e57", SyntheticFixtures.CreateSyntheticE57(), p => E57Document.Open(p)),
        "rcs" => new("sample.rcs", SyntheticFixtures.CreateSyntheticRcsWithPoints(
            [new RcsSamplePoint(1, 2, 3, 4, 5, 6, 7), new RcsSamplePoint(8, 9, 10, 11, 12, 13, 14)], (0, 0, 0)), p => RcsScanDocument.Open(p)),
        "rcp" => new("sample.rcp", SyntheticFixtures.CreateSyntheticRcpZip(), p => RcpProjectDocument.Open(p)),
        "lgsx" => new("sample.lgsx", SyntheticFixtures.CreateSyntheticLgsx(), p => LgsxDocument.Open(p)),
        "fls" => new("sample.fls", SyntheticFixtures.CreateSyntheticFlsContainer(), p => FaroFlsDocument.Open(p)),
        "smf" => new("sample.smf", SyntheticFixtures.CreateSyntheticPinPointSmf(), p => PinPointDocument.Open(p)),
        "c3ptx" => new("sample.c3ptx", SyntheticFixtures.CreateSyntheticC3ptx(new C3Cluster(1, 2, 3, [(1, 2, 3), (4, 5, 6)])), p => EdgeWiseDocument.Open(p)),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static IEnumerable<int> PrefixLengths(int length) => new[]
    {
        0, 1, 3, 4, 7, 8, 11, 12, 16, 31, 32, 47, 48, 49, 100, 511, 512, 513, 1023, 1024, 1025,
        length / 4, length / 2, (length * 3) / 4, length - 1,
    }.Where(n => n >= 0 && n < length).Distinct().Order();

    [Theory]
    [MemberData(nameof(FormatNames))]
    public void Every_Prefix_Of_A_Valid_File_Is_Rejected_Or_Read_Without_Another_Exception(string format)
    {
        var sample = MakeSample(format);
        using var dir = new TempDirectory();

        // The complete file is the control: it must read.
        string whole = dir.Write("whole/" + sample.FileName, sample.Bytes);
        Assert.True(OpenAndReadAll(sample.Open, whole) >= 0);

        foreach (int length in PrefixLengths(sample.Bytes.Length))
        {
            string path = dir.Write($"cut{length}/{sample.FileName}", sample.Bytes[..length]);
            try
            {
                OpenAndReadAll(sample.Open, path);
            }
            catch (InvalidDataException)
            {
                // The expected outcome for a damaged file.
            }
            catch (Exception ex) when (ex is not TimeoutException)
            {
                Assert.Fail($"{format}: a file cut to {length} bytes threw {ex.GetType().Name} instead of InvalidDataException: {ex.Message}");
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(40)]
    public void A_Project_Archive_With_Bytes_Removed_From_Inside_Is_Rejected_Or_Read_Without_Another_Exception(int removed)
    {
        // The end of the archive survives, so its directory still points at data that is no longer where it says.
        byte[] bytes = SyntheticFixtures.CreateSyntheticRcpZip();
        using var dir = new TempDirectory();

        for (int start = 0; start + removed < bytes.Length; start++)
        {
            string path = dir.Write("cut/site.rcp", [.. bytes[..start], .. bytes[(start + removed)..]]);
            try
            {
                OpenAndReadAll(p => RcpProjectDocument.Open(p), path);
            }
            catch (InvalidDataException)
            {
                // The expected outcome for a damaged archive.
            }
            catch (Exception ex) when (ex is not TimeoutException)
            {
                Assert.Fail($"{removed} bytes removed at {start} threw {ex.GetType().Name} instead of InvalidDataException: {ex.Message}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(FormatNames))]
    public void A_Zero_Length_File_Is_Rejected_As_Malformed(string format)
    {
        var sample = MakeSample(format);
        using var dir = new TempDirectory();
        string path = dir.Write(sample.FileName, []);

        Assert.Throws<InvalidDataException>(() => OpenAndReadAll(sample.Open, path));
    }

    [Theory]
    [MemberData(nameof(FormatNames))]
    public void A_File_Full_Of_Zero_Bytes_Is_Rejected_Or_Empty(string format)
    {
        var sample = MakeSample(format);
        using var dir = new TempDirectory();
        string path = dir.Write(sample.FileName, new byte[4096]);

        try
        {
            Assert.Equal(0, OpenAndReadAll(sample.Open, path));
        }
        catch (InvalidDataException)
        {
            // Rejected: also fine.
        }
    }

    [Fact]
    public void A_Zero_Length_Input_Through_PclFile_Is_Not_Supported()
    {
        using var dir = new TempDirectory();
        Assert.Throws<NotSupportedException>(() => PclFile.Open(dir.Write("empty.e57", [])));
    }

    [Fact]
    public void A_Missing_Path_Throws_FileNotFound()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.e57");
        Assert.Throws<FileNotFoundException>(() => PclFile.Open(missing));
    }

    [Fact]
    public void Wrong_Signatures_Are_Rejected_By_The_Format_Readers()
    {
        using var dir = new TempDirectory();
        byte[] junk = [.. "NOT_THE_RIGHT_SIGNATURE"u8, .. new byte[1100]];

        Assert.Throws<InvalidDataException>(() => E57Document.Open(dir.Write("a.e57", junk)));
        Assert.Throws<InvalidDataException>(() => RcsScanDocument.Open(dir.Write("a.rcs", junk)));
        Assert.Throws<InvalidDataException>(() => LgsxDocument.Open(dir.Write("a.lgsx", junk)));
        Assert.Throws<InvalidDataException>(() => FaroFlsDocument.Open(dir.Write("a.fls", junk)));
    }

    // ---- OLE2 containers ---------------------------------------------------------------------------------------

    private static Ole2Storage OpenOle2(byte[] bytes) => Bounded(() => Ole2Storage.Open(new MemoryStream(bytes)));

    private static Ole2Builder SmallBuilder()
    {
        var builder = new Ole2Builder();
        builder.AddStream(0, "Alpha", [1, 2, 3, 4]);
        return builder;
    }

    [Fact]
    public void A_Small_Compound_File_Round_Trips()
    {
        var builder = new Ole2Builder();
        int folder = builder.AddStorage(0, "Folder");
        builder.AddStream(folder, "Inner", new byte[5000]);
        builder.AddStream(0, "Alpha", [1, 2, 3, 4]);

        using var storage = OpenOle2(builder.Build());

        Assert.Equal(["Alpha", "Folder", "Folder/Inner", "Inner"], storage.ListStreams());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, storage.ReadStream("Alpha"));
        Assert.Equal(5000, storage.ReadStream("Folder/Inner").Length);
        Assert.Equal(5000UL, storage.GetStreamLength("Inner"));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(12)]
    public void A_Directory_Chained_Through_Twenty_Thousand_Left_Siblings_Is_Walked_Without_Recursion(int sectorShift)
    {
        byte[] bytes = Ole2Builder.WithLeftChain(20_000, sectorShift).Build();

        using var storage = OpenOle2(bytes);

        Assert.Equal(20_000, storage.ListStreams().Count);
    }

    [Fact]
    public void A_Directory_With_More_Entries_Than_The_Limit_Is_Rejected()
    {
        byte[] bytes = Ole2Builder.WithLeftChain(70_000, 12).Build();

        Assert.Throws<InvalidDataException>(() => OpenOle2(bytes));
    }

    [Fact]
    public void Storages_Nested_Deeper_Than_The_Limit_Are_Rejected()
    {
        var builder = new Ole2Builder();
        int parent = 0;
        for (int i = 0; i < 20; i++) parent = builder.AddStorage(parent, "d" + i);

        Assert.Throws<InvalidDataException>(() => OpenOle2(builder.Build()));
    }

    [Fact]
    public void A_Directory_Name_Longer_Than_The_Format_Allows_Is_Rejected()
    {
        var builder = new Ole2Builder();
        builder.AddStream(0, new string('n', 40), [1]);

        Assert.Throws<InvalidDataException>(() => OpenOle2(builder.Build()));
    }

    [Fact]
    public void A_Directory_Entry_That_Lists_Itself_As_A_Sibling_Does_Not_Loop()
    {
        var builder = SmallBuilder();
        int loop = builder.AddUnlinked("Loop", 2);
        builder.Nodes[loop].Left = loop;
        builder.Nodes[loop].Right = loop;
        builder.Nodes[1].Right = loop;

        using var storage = OpenOle2(builder.Build());

        Assert.Contains("Loop", storage.ListStreams());
    }

    [Fact]
    public void A_Storage_That_Is_Its_Own_Child_Does_Not_Loop()
    {
        var builder = new Ole2Builder();
        int self = builder.AddUnlinked("Self", 1);
        builder.Nodes[self].Child = self;
        builder.Nodes[0].Child = self;

        using var storage = OpenOle2(builder.Build());

        Assert.Equal(["Self"], storage.ListStreams());
    }

    [Fact]
    public void An_Unsupported_Sector_Shift_Is_Rejected()
    {
        byte[] bytes = SmallBuilder().Build();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x1E, 2), 10);

        Assert.Throws<InvalidDataException>(() => OpenOle2(bytes));
    }

    [Fact]
    public void A_Directory_Start_Outside_The_File_Is_Rejected()
    {
        byte[] bytes = SmallBuilder().Build();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x30, 4), 0x00FFFFFF);

        Assert.Throws<InvalidDataException>(() => OpenOle2(bytes));
    }

    [Fact]
    public void More_Declared_Fat_Sectors_Than_The_File_Holds_Are_Rejected()
    {
        byte[] bytes = SmallBuilder().Build();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x2C, 4), 100_000);

        Assert.Throws<InvalidDataException>(() => OpenOle2(bytes));
    }

    [Fact]
    public void A_Chain_Of_Difat_Sectors_That_Points_Back_To_Itself_Is_Rejected()
    {
        byte[] built = SmallBuilder().Build();
        byte[] bytes = [.. built, .. new byte[512]];
        int difSector = (built.Length / 512) - 1;

        // One extra sector whose entries are all free and whose "next" link is the sector itself.
        Span<byte> dif = bytes.AsSpan(built.Length, 512);
        dif.Fill(0xFF);
        BinaryPrimitives.WriteUInt32LittleEndian(dif.Slice(508, 4), (uint)difSector + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x2C, 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x44, 4), (uint)difSector + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x48, 4), 3);

        Assert.Throws<InvalidDataException>(() => OpenOle2(bytes));
    }

    [Fact]
    public void A_Stream_Whose_Fat_Chain_Is_A_Cycle_Is_Rejected_On_Read()
    {
        var builder = SmallBuilder();
        builder.AddStream(0, "Big", new byte[3000]);
        byte[] bytes = builder.Build();

        uint startSector = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)builder.DirectoryEntryOffset(2) + 0x74, 4));
        uint fatSector = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x4C, 4));
        int fatEntry = (int)((1L + fatSector) * 512) + (int)(startSector * 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(fatEntry, 4), startSector);

        using var storage = OpenOle2(bytes);

        Assert.Throws<InvalidDataException>(() => Bounded(() => storage.ReadStream("Big")));
    }

    [Fact]
    public void A_Stream_Size_Larger_Than_The_File_Is_Rejected_On_Read()
    {
        var builder = SmallBuilder();
        builder.AddStream(0, "Big", new byte[3000]);
        byte[] bytes = builder.Build();
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan((int)builder.DirectoryEntryOffset(2) + 0x78, 8), 1UL << 28);

        using var storage = OpenOle2(bytes);

        Assert.Throws<InvalidDataException>(() => storage.ReadStream("Big"));
    }

    [Fact]
    public void A_Compound_File_Cut_Short_Is_Rejected()
    {
        byte[] bytes = SmallBuilder().Build();

        foreach (int length in new[] { 0, 100, 511, 512, 600, bytes.Length / 2, bytes.Length - 1 })
        {
            if (length == 0 || length < bytes.Length)
                Assert.Throws<InvalidDataException>(() => OpenOle2(bytes[..length]));
        }
    }

    // ---- E57 -----------------------------------------------------------------------------------------------

    private static long ReadE57(byte[] file)
    {
        using var dir = new TempDirectory();
        return OpenAndReadAll(p => E57Document.Open(p), dir.Write("crafted.e57", file));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(4UL)]
    [InlineData(512UL)]
    [InlineData(4096UL)]
    public void An_E57_Page_Size_Other_Than_1024_Is_Rejected(ulong pageSize) =>
        Assert.Throws<InvalidDataException>(() => ReadE57(new E57Spec { PageSize = pageSize }.Build()));

    [Fact]
    public void An_E57_Packet_Declaring_The_Maximum_Length_Is_Rejected_Without_Allocating_It() =>
        Assert.Throws<InvalidDataException>(() => ReadE57(new E57Spec { Points = [], RecordCount = 10, RawPackets = E57Spec.RawPacket(1, 16, 0xFFFF) }.Build()));

    [Fact]
    public void An_E57_Packet_Of_Length_One_Cannot_Make_The_Reader_Spin()
    {
        // Stored (length - 1) of zero means a packet of one byte: shorter than its own header.
        var spec = new E57Spec { Points = [], RecordCount = 10, RawPackets = E57Spec.RawPacket(2, 8, 0) };

        Assert.Throws<InvalidDataException>(() => ReadE57(spec.Build()));
    }

    [Fact]
    public void E57_Packets_That_Carry_No_Points_Are_Not_Followed_Forever()
    {
        // Thousands of empty index/idle packets, then nothing: the reader must stop with an error.
        byte[] idle = [.. Enumerable.Range(0, 6000).SelectMany(_ => E57Spec.RawPacket(2, 4))];
        var spec = new E57Spec { Points = [], RecordCount = 10, RawPackets = idle };

        Assert.Throws<InvalidDataException>(() => ReadE57(spec.Build()));
    }

    [Fact]
    public void An_E57_Packet_Of_An_Unknown_Type_Is_Rejected() =>
        Assert.Throws<InvalidDataException>(() => ReadE57(new E57Spec { Points = [], RecordCount = 5, RawPackets = E57Spec.RawPacket(7, 16) }.Build()));

    [Fact]
    public void An_E57_Data_Packet_With_The_Wrong_Number_Of_Streams_Is_Rejected()
    {
        byte[] packet = E57Spec.DataPacket([new byte[4], new byte[4]]);

        Assert.Throws<InvalidDataException>(() => ReadE57(new E57Spec { Points = [], RecordCount = 1, RawPackets = packet }.Build()));
    }

    [Fact]
    public void An_E57_Stream_Longer_Than_Its_Packet_Is_Rejected()
    {
        byte[] packet = E57Spec.DataPacket(E57Spec.DefaultPoints(4));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6, 2), 0xFFF0);

        Assert.Throws<InvalidDataException>(() => ReadE57(new E57Spec { Points = E57Spec.DefaultPoints(4), RawPackets = packet }.Build()));
    }

    [Fact]
    public void An_E57_Float_Stream_That_Is_Not_A_Whole_Number_Of_Values_Is_Rejected()
    {
        byte[] packet = E57Spec.DataPacket([new byte[5], new byte[4], new byte[4], new byte[1], new byte[1], new byte[1], new byte[1]]);

        Assert.Throws<InvalidDataException>(() => ReadE57(new E57Spec { Points = [], RecordCount = 1, RawPackets = packet }.Build()));
    }

    [Fact]
    public void An_E57_Document_Type_Declaration_Is_Rejected()
    {
        // An entity declaration must never be expanded.
        var spec = new E57Spec { XmlPrologue = "<!DOCTYPE e57Root [ <!ENTITY big \"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"> ]>" };

        Assert.Throws<InvalidDataException>(() => ReadE57(spec.Build()));
    }

    [Fact]
    public void An_E57_Page_With_A_Wrong_Checksum_Is_Rejected()
    {
        byte[] file = new E57Spec().Build();
        file[2048 + 100] ^= 0xFF;

        Assert.Throws<InvalidDataException>(() => ReadE57(file));
    }

    [Fact]
    public void An_E57_Header_Page_With_A_Wrong_Checksum_Is_Rejected()
    {
        byte[] file = new E57Spec().Build();
        file[1020] ^= 0xFF;

        Assert.Throws<InvalidDataException>(() => ReadE57(file));
    }

    [Fact]
    public void An_E57_Record_Count_Beyond_Any_Plausible_Size_Is_Rejected() =>
        Assert.Throws<InvalidDataException>(() => ReadE57(new E57Spec { RecordCount = 1L << 45 }.Build()));

    [Fact]
    public void An_E57_Record_Count_Larger_Than_The_Stored_Points_Is_Rejected_At_The_End_Of_The_Data()
    {
        var spec = new E57Spec { Points = E57Spec.DefaultPoints(10), RecordCount = 500 };

        Assert.Throws<InvalidDataException>(() => ReadE57(spec.Build()));
    }

    [Fact]
    public void An_E57_Xml_Length_That_Does_Not_Fit_Is_Rejected()
    {
        Assert.Throws<InvalidDataException>(() => ReadE57(new E57Spec { DeclaredXmlLength = 1UL << 40 }.Build()));
        Assert.Throws<InvalidDataException>(() => ReadE57(new E57Spec { DeclaredXmlLength = 0 }.Build()));
    }

    [Fact]
    public void An_E57_File_Shorter_Than_Its_Declared_Length_Is_Rejected() =>
        Assert.Throws<InvalidDataException>(() => ReadE57(new E57Spec { DeclaredFileLength = 10_000_000 }.Build()));

    [Fact]
    public void An_E57_Section_Offset_Outside_The_File_Is_Rejected()
    {
        // The compressed vector offset sits in the XML text; the file offset digits are replaced by a larger number.
        byte[] file = new E57Spec().Build();
        string text = System.Text.Encoding.Latin1.GetString(file);
        int at = text.IndexOf("fileOffset=\"", StringComparison.Ordinal);
        Assert.True(at > 0);
        int digits = text.IndexOf('"', at + 12) - (at + 12);
        string replacement = new string('9', digits);
        System.Text.Encoding.Latin1.GetBytes(replacement).CopyTo(file, at + 12);
        E57Spec.FixChecksum(file, at);

        Assert.Throws<InvalidDataException>(() => ReadE57(file));
    }

    // ---- Paths that come from file contents ---------------------------------------------------------------------

    [Theory]
    [InlineData("../outside.rcs")]
    [InlineData("..\\outside.rcs")]
    [InlineData("Scans/../../outside.rcs")]
    public void An_Rcp_Scan_Path_That_Leaves_The_Project_Folder_Is_Never_Opened(string scanPath)
    {
        using var dir = new TempDirectory();
        dir.Write("outside.rcs", SyntheticFixtures.CreateSyntheticRcsWithPoints([new RcsSamplePoint(1, 2, 3, 0, 0, 0, 0)], (0, 0, 0)));
        string project = dir.Write("proj/site.rcp", SyntheticFixtures.CreateSyntheticRcpZip(scanPath));

        using var doc = RcpProjectDocument.Open(project);

        Assert.NotEmpty(doc.Warnings);
        Assert.Null(((RcpScan)Assert.Single(doc.Scans)).RcsFilePath);
        Assert.Empty(doc.ReadAllPoints());
    }

    [Fact]
    public void An_Rcp_Scan_Path_That_Is_Absolute_Is_Never_Opened()
    {
        using var dir = new TempDirectory();
        string outside = dir.Write("outside/decoy.rcs", SyntheticFixtures.CreateSyntheticRcsWithPoints([new RcsSamplePoint(1, 2, 3, 0, 0, 0, 0)], (0, 0, 0)));
        string project = dir.Write("proj/site.rcp", SyntheticFixtures.CreateSyntheticRcpZip(outside));

        using var doc = RcpProjectDocument.Open(project);

        Assert.Null(((RcpScan)Assert.Single(doc.Scans)).RcsFilePath);
        Assert.Empty(doc.ReadAllPoints());
    }

    [Fact]
    public void An_Rcp_Scan_Path_Naming_A_Unc_Share_Is_Never_Opened()
    {
        using var dir = new TempDirectory();
        string project = dir.Write("site.rcp", SyntheticFixtures.CreateSyntheticRcpZip("//nowhere.invalid/share/scan.rcs"));

        using var doc = RcpProjectDocument.Open(project);

        Assert.Null(((RcpScan)Assert.Single(doc.Scans)).RcsFilePath);
        Assert.NotEmpty(doc.Warnings);
    }

    [Fact]
    public void An_Rcp_Manifest_With_A_Document_Type_Declaration_Is_Rejected()
    {
        using var dir = new TempDirectory();
        string prologue = "<!DOCTYPE Project [ <!ENTITY big \"aaaaaaaaaaaaaaaaaaaaaaaaaaaa\"> ]>";
        string project = dir.Write("site.rcp", SyntheticFixtures.CreateSyntheticRcpZip(xmlPrologue: prologue));

        Assert.Throws<InvalidDataException>(() => RcpProjectDocument.Open(project));
    }

    [Theory]
    [InlineData("../outside.c3ptx")]
    [InlineData("..\\outside.c3ptx")]
    [InlineData("clusters/../../outside.c3ptx")]
    public void An_EdgeWise_Cluster_Path_That_Leaves_The_Database_Folder_Is_Rejected(string file)
    {
        using var dir = new TempDirectory();
        dir.Write("outside.c3ptx", SyntheticFixtures.CreateSyntheticC3ptx(new C3Cluster(0, 0, 0, [(1, 1, 1)])));
        string db = dir.WriteText("proj/db.c3db", SyntheticFixtures.CreateSyntheticC3db((1, file)));

        Assert.Throws<InvalidDataException>(() => EdgeWiseDocument.Open(db));
    }

    [Fact]
    public void An_EdgeWise_Cluster_Path_That_Is_Absolute_Is_Rejected()
    {
        using var dir = new TempDirectory();
        string outside = dir.Write("outside.c3ptx", SyntheticFixtures.CreateSyntheticC3ptx(new C3Cluster(0, 0, 0, [(1, 1, 1)])));
        string db = dir.WriteText("proj/db.c3db", SyntheticFixtures.CreateSyntheticC3db((1, outside.Replace('\\', '/'))));

        Assert.Throws<InvalidDataException>(() => EdgeWiseDocument.Open(db));
    }

    [Fact]
    public void An_EdgeWise_Database_Line_Longer_Than_The_Limit_Is_Rejected()
    {
        using var dir = new TempDirectory();
        string db = dir.WriteText("long.c3db", new string('x', 2 * 1024 * 1024) + "\n");

        Assert.Throws<InvalidDataException>(() => Bounded(() => EdgeWiseDocument.Open(db)));
    }

    [Fact]
    public void An_EdgeWise_Database_With_Long_Digit_Lines_Is_Parsed_In_Linear_Time()
    {
        string digits = new string('1', 100_000);
        using var dir = new TempDirectory();
        string db = dir.WriteText("digits.c3db", "_Tree ( 10 10 10 ) ( -10 -10 -10 )\n" + string.Join("\n", Enumerable.Repeat(digits, 20)) + "\n");

        using var doc = Bounded(() => EdgeWiseDocument.Open(db));

        Assert.Empty(doc.Scans);
    }

    [Fact]
    public void An_EdgeWise_Node_Count_That_Does_Not_Fit_A_Number_Is_Rejected()
    {
        using var dir = new TempDirectory();
        string db = dir.WriteText("big.c3db", "1 (Count:99999999999999999999999 File: \"a.c3ptx\"\n");

        Assert.Throws<InvalidDataException>(() => EdgeWiseDocument.Open(db));
    }

    [Fact]
    public void An_EdgeWise_Cluster_With_A_Negative_Or_Oversized_Count_Is_Rejected()
    {
        using var dir = new TempDirectory();
        byte[] valid = SyntheticFixtures.CreateSyntheticC3ptx(new C3Cluster(0, 0, 0, [(1, 1, 1)]));

        byte[] negative = (byte[])valid.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(negative.AsSpan(16, 4), -5);
        Assert.Throws<InvalidDataException>(() => EdgeWiseDocument.Open(dir.Write("neg.c3ptx", negative)));

        byte[] huge = (byte[])valid.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(huge.AsSpan(16, 4), int.MaxValue);
        Assert.Throws<InvalidDataException>(() => EdgeWiseDocument.Open(dir.Write("huge.c3ptx", huge)));

        byte[] manyClusters = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(manyClusters.AsSpan(0, 4), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => EdgeWiseDocument.Open(dir.Write("many.c3ptx", manyClusters)));
    }

    [Fact]
    public void An_EdgeWise_Cluster_Centre_That_Is_Not_A_Number_Is_Rejected_On_Read()
    {
        using var dir = new TempDirectory();
        string path = dir.Write("nan.c3ptx", SyntheticFixtures.CreateSyntheticC3ptx(new C3Cluster(float.NaN, 0, 0, [(1, 1, 1)])));

        Assert.Throws<InvalidDataException>(() => OpenAndReadAll(p => EdgeWiseDocument.Open(p), path));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/../../evil")]
    public void A_PinPoint_Scan_Name_That_Leaves_The_Data_Folder_Is_Rejected(string name)
    {
        using var dir = new TempDirectory();
        string manifest = SyntheticFixtures.PinPointManifest(name, "h1", null);
        string path = SyntheticFixtures.WritePinPointProject(dir, null, manifest);

        Assert.Throws<InvalidDataException>(() => PinPointDocument.Open(path));
    }

    [Fact]
    public void A_PinPoint_Data_Object_Hash_That_Leaves_The_Data_Folder_Is_Rejected()
    {
        using var dir = new TempDirectory();
        const string manifest = """
            { "DataObjects": [ { "HashId": "../../evil", "Locations": [ { "Name": "x" } ] } ] }
            """;
        string path = dir.WriteText("project.smf", manifest);

        Assert.Throws<InvalidDataException>(() => PinPointDocument.Open(path));
    }

    [Fact]
    public void A_PinPoint_Channel_With_A_Negative_Or_Oversized_Length_Is_Rejected()
    {
        foreach (long length in new[] { -1L, long.MaxValue, 1L << 40 })
        {
            using var dir = new TempDirectory();
            byte[] bin = SyntheticFixtures.CreateSyntheticPinPointBin([(1, 2, 3)], pointsLengthOverride: length);
            string path = SyntheticFixtures.WritePinPointProject(dir, bin);

            Assert.Throws<InvalidDataException>(() => OpenAndReadAll(p => PinPointDocument.Open(p), path));
        }
    }

    [Fact]
    public void A_PinPoint_Manifest_That_Is_Not_Json_Is_Rejected()
    {
        using var dir = new TempDirectory();
        Assert.Throws<InvalidDataException>(() => PinPointDocument.Open(dir.WriteText("bad.smf", "{ this is not json")));
    }

    [Fact]
    public void A_PinPoint_Manifest_Nested_Far_Deeper_Than_Any_Real_One_Is_Rejected()
    {
        using var dir = new TempDirectory();
        string path = dir.WriteText("deep.smf", new string('[', 5000) + new string(']', 5000));

        Assert.Throws<InvalidDataException>(() => Bounded(() => PinPointDocument.Open(path)));
    }

    // ---- Leica ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_Leica_Point_Block_With_Damaged_Contents_Is_Rejected_On_Read()
    {
        byte[] bytes = SyntheticFixtures.CreateSyntheticLgsx();
        string text = System.Text.Encoding.Latin1.GetString(bytes);
        int block = text.IndexOf("APRN", StringComparison.Ordinal);
        Assert.True(block > 0);

        // Overwrite the block header fields (scale, origin) and what follows with a constant pattern.
        Array.Fill(bytes, (byte)0xA5, block + 0x40, Math.Min(32, bytes.Length - block - 0x40));
        using var dir = new TempDirectory();
        string path = dir.Write("damaged.lgsx", bytes);

        Assert.Throws<InvalidDataException>(() => OpenAndReadAll(p => LgsxDocument.Open(p), path));
    }

    // ---- ReCap project manifest limits --------------------------------------------------------------------------

    private static string WriteRcp(TempDirectory dir, string manifest) =>
        dir.Write("site.rcp", SyntheticFixtures.CreateZip("project.xml", manifest));

    private static string Nested(int levels) =>
        "<Project>" + string.Concat(Enumerable.Repeat("<a>", levels - 1)) + string.Concat(Enumerable.Repeat("</a>", levels - 1)) + "</Project>";

    [Theory]
    [InlineData(65)]
    [InlineData(10_000)]
    [InlineData(200_000)]
    public void An_Rcp_Manifest_Nested_Deeper_Than_The_Limit_Is_Rejected(int levels)
    {
        using var dir = new TempDirectory();
        string path = WriteRcp(dir, Nested(levels));

        Assert.Throws<InvalidDataException>(() => Bounded(() => RcpProjectDocument.Open(path)));
    }

    [Fact]
    public void An_Rcp_Manifest_Nested_Exactly_To_The_Limit_Is_Read()
    {
        using var dir = new TempDirectory();
        string path = WriteRcp(dir, Nested(64));

        using var doc = Bounded(() => RcpProjectDocument.Open(path));

        Assert.Empty(doc.Scans);
    }

    [Fact]
    public void An_Rcp_Manifest_With_More_Elements_Than_The_Limit_Is_Rejected()
    {
        using var dir = new TempDirectory();
        string path = WriteRcp(dir, "<Project>" + string.Concat(Enumerable.Repeat("<a/>", 1_000_001)) + "</Project>");

        Assert.Throws<InvalidDataException>(() => Bounded(() => RcpProjectDocument.Open(path)));
    }

    [Fact]
    public void An_Rcp_Manifest_Lists_At_Most_65536_Scans()
    {
        using var dir = new TempDirectory();
        string atLimit = dir.Write("limit/site.rcp", SyntheticFixtures.CreateZip("project.xml", Scans(65_536)));
        string beyond = dir.Write("beyond/site.rcp", SyntheticFixtures.CreateZip("project.xml", Scans(65_537)));

        using (var doc = Bounded(() => RcpProjectDocument.Open(atLimit)))
            Assert.Equal(65_536, doc.Scans.Count);

        Assert.Throws<InvalidDataException>(() => Bounded(() => RcpProjectDocument.Open(beyond)));

        static string Scans(int count) => "<Project>" + string.Concat(Enumerable.Repeat("<VoxelTreeRunTime/>", count)) + "</Project>";
    }

    [Fact]
    public void An_Rcp_Manifest_Larger_Than_The_Limit_Is_Rejected_Before_It_Is_Read()
    {
        using var dir = new TempDirectory();
        string path = dir.Write("site.rcp", ZipOfSpaces("project.xml", 65));

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => RcpProjectDocument.Open(path));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 16L * 1024 * 1024, $"opening allocated {allocated:N0} bytes");
    }

    private static byte[] ZipOfSpaces(string entryName, int mebibytes)
    {
        using var ms = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = archive.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Optimal).Open())
        {
            byte[] chunk = new byte[1 << 20];
            Array.Fill(chunk, (byte)' ');
            for (int i = 0; i < mebibytes; i++) entry.Write(chunk);
        }
        return ms.ToArray();
    }

    // ---- FARO planes --------------------------------------------------------------------------------------------

    private static byte[] FlsWithDistanceStream(byte[] stream, string? parameters = null)
    {
        var builder = new Ole2Builder();
        int one = builder.AddStorage(builder.AddStorage(0, "Scans"), "1");
        builder.AddStream(one, "Params", System.Text.Encoding.ASCII.GetBytes(parameters ?? SyntheticFixtures.FaroParams()));
        builder.AddStream(one, "Dist0", stream);
        return builder.Build();
    }

    private static byte[] GzipOfSpaces(int mebibytes)
    {
        using var ms = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            byte[] chunk = new byte[1 << 20];
            Array.Fill(chunk, (byte)' ');
            for (int i = 0; i < mebibytes; i++) gzip.Write(chunk);
        }
        return ms.ToArray();
    }

    [Fact]
    public void A_Faro_Plane_That_Inflates_Far_Beyond_The_Grid_Is_Rejected_Without_Holding_It_In_Memory()
    {
        using var dir = new TempDirectory();
        // 64 MiB of blanks that deflate to about 64 KiB: a 4 x 8 grid allows 64 KiB of header and samples.
        string path = dir.Write("big.fls", FlsWithDistanceStream(GzipOfSpaces(64)));

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() =>
        {
            using var doc = FaroFlsDocument.Open(path);
            _ = doc.ReadAllPoints().Count();
        });
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 16L * 1024 * 1024, $"reading allocated {allocated:N0} bytes");
    }

    [Fact]
    public void A_Faro_Plane_Stored_Larger_Than_The_Grid_Allows_Is_Rejected_On_Read()
    {
        using var dir = new TempDirectory();
        string path = dir.Write("big.fls", FlsWithDistanceStream(new byte[200_000]));

        using var doc = FaroFlsDocument.Open(path);

        Assert.Throws<InvalidDataException>(() => doc.ReadAllPoints().Count());
    }

    [Fact]
    public void A_Faro_Grid_That_The_Distance_Stream_Cannot_Fill_Is_Rejected_At_Open()
    {
        using var dir = new TempDirectory();
        string parameters = SyntheticFixtures.FaroParams().Replace("NumRows = 4", "NumRows = 4000").Replace("NumCols = 8", "NumCols = 30000");
        string path = dir.Write("tiny.fls", FlsWithDistanceStream(new byte[100], parameters));

        Assert.Throws<InvalidDataException>(() => FaroFlsDocument.Open(path));
    }

    // ---- PinPoint and EdgeWise ----------------------------------------------------------------------------------

    [Fact]
    public void A_PinPoint_Manifest_String_That_Is_A_Lone_Surrogate_Escape_Is_Rejected()
    {
        using var dir = new TempDirectory();
        // The JSON text holds the six characters \ud800, which are not a valid UTF-16 string.
        string path = dir.WriteText("lone.smf", SyntheticFixtures.PinPointManifest("\\ud800", "h1", null));

        Assert.Throws<InvalidDataException>(() => PinPointDocument.Open(path));
    }

    [Fact]
    public void A_PinPoint_Folder_With_Several_Manifests_Uses_The_First_Smf_In_Ordinal_Order()
    {
        using var dir = new TempDirectory();
        // Ordinal order puts the capital letter before the lower case one.
        dir.WriteText("proj/a.srf", SyntheticFixtures.PinPointManifest("FromSrf", "h1", null));
        dir.WriteText("proj/a.smf", SyntheticFixtures.PinPointManifest("FromLowerSmf", "h2", null));
        dir.WriteText("proj/B.smf", SyntheticFixtures.PinPointManifest("FromUpperSmf", "h3", null));

        using var doc = PinPointDocument.Open(dir.PathOf("proj"));

        Assert.Equal("FromUpperSmf", Assert.Single(doc.Scans).Name);
    }

    [Fact]
    public void A_PinPoint_Folder_Without_An_Smf_Uses_The_First_Srf()
    {
        using var dir = new TempDirectory();
        dir.WriteText("proj/z.srf", SyntheticFixtures.PinPointManifest("FromZ", "h1", null));
        dir.WriteText("proj/y.srf", SyntheticFixtures.PinPointManifest("FromY", "h2", null));

        using var doc = PinPointDocument.Open(dir.PathOf("proj"));

        Assert.Equal("FromY", Assert.Single(doc.Scans).Name);
    }

    [Fact]
    public void An_EdgeWise_Database_Without_A_Tree_Line_Is_Rejected()
    {
        using var dir = new TempDirectory();
        string db = dir.WriteText("garbage.c3db", "this is some text\nthat is not a database\n");

        Assert.Throws<InvalidDataException>(() => EdgeWiseDocument.Open(db));
    }

    [Fact]
    public void An_EdgeWise_Database_With_A_Tree_Line_And_No_Nodes_Is_Valid_And_Empty()
    {
        using var dir = new TempDirectory();
        string db = dir.WriteText("empty.c3db", SyntheticFixtures.CreateSyntheticC3db());

        using var doc = EdgeWiseDocument.Open(db);

        Assert.Empty(doc.Scans);
    }

    // ---- Limits on declared counts and sizes ---------------------------------------------------------------------
    // Each test crafts the smallest file that declares more than a reader accepts and checks that the reader says so
    // (a malformed file) and does so without allocating what the file declares.

    private const long MemoryBudget = 16L * 1024 * 1024;

    /// <summary>Runs the action on this thread, expects <see cref="InvalidDataException"/> and returns it; fails when the
    /// action allocated more than the budget, so a declared size cannot have been allocated.</summary>
    private static InvalidDataException AssertRejectedCheaply(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var exception = Assert.Throws<InvalidDataException>(action);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < MemoryBudget, $"the reader allocated {allocated:N0} bytes before it rejected the file");
        return exception;
    }

    private static void ReadEverything(Func<string, PclDocument> open, string path)
    {
        using var doc = open(path);
        _ = doc.ReadAllPoints().Count();
    }

    // ---- Leica limits ---------------------------------------------------------------------------------------------

    private static byte[] LgsxWithDeclaredEntryCount(uint count)
    {
        byte[] bytes = SyntheticFixtures.CreateSyntheticLgsx();
        ulong tocOffset = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(bytes.Length - 8, 8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)tocOffset, 4), count);
        return bytes;
    }

    [Theory]
    [InlineData(10_001u)]
    [InlineData(uint.MaxValue)]
    public void A_Leica_Table_Of_Contents_With_More_Entries_Than_The_Limit_Is_Rejected(uint count)
    {
        using var dir = new TempDirectory();
        string path = dir.Write("many.lgsx", LgsxWithDeclaredEntryCount(count));

        var exception = AssertRejectedCheaply(() => LgsxDocument.Open(path));

        Assert.Contains("at most", exception.Message);
    }

    [Fact]
    public void A_Leica_Table_Of_Contents_At_The_Entry_Limit_Is_Not_Rejected_For_Its_Count()
    {
        using var dir = new TempDirectory();
        string path = dir.Write("limit.lgsx", LgsxWithDeclaredEntryCount(10_000));

        // The file cannot hold 10000 entries, so it is rejected as truncated; the entry limit itself was not what stopped it.
        var exception = AssertRejectedCheaply(() => LgsxDocument.Open(path));

        Assert.DoesNotContain("at most", exception.Message);
    }

    [Fact]
    public void A_Leica_Block_Reads_At_The_Plane_Limit_And_Is_Rejected_Beyond_It()
    {
        using var dir = new TempDirectory();
        byte[] plane = new byte[5];

        // The block always has the four position planes, so 60 more make 64, the limit.
        string atLimit = dir.Write("limit.lgsx", SyntheticFixtures.CreateSyntheticLgsx(extraPlanes: [.. Enumerable.Repeat(plane, 60)]));
        string beyond = dir.Write("beyond.lgsx", SyntheticFixtures.CreateSyntheticLgsx(extraPlanes: [.. Enumerable.Repeat(plane, 61)]));

        Assert.Equal(SyntheticFixtures.DefaultLeicaPoints.Count, OpenAndReadAll(p => LgsxDocument.Open(p), atLimit));
        var exception = AssertRejectedCheaply(() => ReadEverything(p => LgsxDocument.Open(p), beyond));
        Assert.Contains("more than 64 planes", exception.Message);
    }

    [Fact]
    public void A_Leica_Plane_That_Inflates_Beyond_The_Plane_Limit_Is_Rejected_Without_Allocating_It()
    {
        using var dir = new TempDirectory();

        // One byte over 16 MiB of zeros: the compressed frame is a few hundred bytes, the size it declares is not.
        byte[] bomb = new byte[(16 * 1024 * 1024) + 1];
        string path = dir.Write("bomb.lgsx", SyntheticFixtures.CreateSyntheticLgsx(extraPlanes: [bomb]));
        Assert.True(new FileInfo(path).Length < 64 * 1024, "the crafted container should be small");

        var exception = AssertRejectedCheaply(() => ReadEverything(p => LgsxDocument.Open(p), path));

        Assert.Contains("declares more than", exception.Message);
    }

    [Fact]
    public void A_Leica_Block_Whose_Planes_Together_Inflate_Beyond_The_Block_Limit_Is_Rejected()
    {
        using var dir = new TempDirectory();

        // Each plane is exactly at the plane limit (16 MiB); nine of them are 144 MiB and the block limit is 128 MiB.
        byte[] full = new byte[16 * 1024 * 1024];
        string path = dir.Write("block.lgsx", SyntheticFixtures.CreateSyntheticLgsx(extraPlanes: [.. Enumerable.Repeat(full, 9)]));

        var exception = Assert.Throws<InvalidDataException>(() => ReadEverything(p => LgsxDocument.Open(p), path));

        Assert.Contains("decompresses to more than", exception.Message);
    }

    // ---- ReCap scan limit -----------------------------------------------------------------------------------------

    // Offset of the node table in the supported layout of an .rcs file.
    private const int RcsNodeTableOffset = 98605;

    [Theory]
    [InlineData(500_001UL)]
    [InlineData(1UL << 40)]
    [InlineData(ulong.MaxValue)]
    public void An_Rcs_Node_Table_Declaring_More_Nodes_Than_The_Limit_Is_Rejected_Without_Allocating_For_Them(ulong nodes)
    {
        using var dir = new TempDirectory();
        byte[] bytes = SyntheticFixtures.CreateSyntheticRcs();
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(RcsNodeTableOffset, 8), nodes);
        string path = dir.Write("nodes.rcs", bytes);

        AssertRejectedCheaply(() => ReadEverything(p => RcsScanDocument.Open(p), path));
    }

    [Fact]
    public void An_Rcs_Node_Table_One_Node_Beyond_The_Limit_Is_Rejected_Although_The_File_Could_Hold_It()
    {
        using var dir = new TempDirectory();
        const long nodeSize = 368;
        string path = dir.PathOf("nodes.rcs");
        byte[] header = SyntheticFixtures.CreateSyntheticRcs();
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(RcsNodeTableOffset, 8), 500_001);

        // The file is long enough for the whole table, so only the limit on the node count can reject it (the zero-filled tail costs no data).
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            stream.Write(header);
            stream.SetLength(RcsNodeTableOffset + 8 + (500_001 * nodeSize));
        }

        AssertRejectedCheaply(() => ReadEverything(p => RcsScanDocument.Open(p), path));
    }

    // ---- E57 XML and prototype limits -----------------------------------------------------------------------------

    // The pose element sits three levels below the root, so the nested elements inside it start at depth 4.
    private static string PoseNestedBy(int levels) =>
        "<pose type=\"Structure\">" + string.Concat(Enumerable.Repeat("<a>", levels)) + string.Concat(Enumerable.Repeat("</a>", levels)) + "</pose>";

    [Fact]
    public void An_E57_XML_Section_Nested_Exactly_To_The_Depth_Limit_Is_Read()
    {
        // 61 levels put the deepest element at depth 64, the limit.
        Assert.Equal(250, ReadE57(new E57Spec { PoseXml = PoseNestedBy(61) }.Build()));
    }

    [Theory]
    [InlineData(62)]
    [InlineData(10_000)]
    public void An_E57_XML_Section_Nested_Deeper_Than_The_Limit_Is_Rejected(int levels) =>
        Assert.Throws<InvalidDataException>(() => ReadE57(new E57Spec { PoseXml = PoseNestedBy(levels) }.Build()));

    [Fact]
    public void An_E57_XML_Section_With_More_Elements_Than_The_Limit_Is_Rejected() =>
        Assert.Throws<InvalidDataException>(() => ReadE57(new E57Spec
        {
            PoseXml = "<pose type=\"Structure\">" + string.Concat(Enumerable.Repeat("<a/>", 1_000_001)) + "</pose>",
        }.Build()));

    [Fact]
    public void An_E57_Prototype_With_More_Fields_Than_The_Limit_Cannot_Be_Decoded()
    {
        using var dir = new TempDirectory();
        static string Prototype(int fields) =>
            "<prototype type=\"Structure\">" + string.Concat(Enumerable.Range(0, fields).Select(i => $"<f{i} type=\"Integer\" minimum=\"0\" maximum=\"1\"/>")) + "</prototype>";

        using var atLimit = E57Document.Open(dir.Write("limit.e57", new E57Spec { PrototypeXml = Prototype(1024) }.Build()));
        using var beyond = E57Document.Open(dir.Write("beyond.e57", new E57Spec { PrototypeXml = Prototype(1025) }.Build()));

        // Both open; reading says why the points cannot be decoded. The first lacks the coordinate fields, the second has too many fields.
        Assert.Contains("lacks cartesianX", Assert.Throws<NotSupportedException>(() => atLimit.Scans[0].ReadPoints().ToList()).Message);
        Assert.Contains("too many fields", Assert.Throws<NotSupportedException>(() => beyond.Scans[0].ReadPoints().ToList()).Message);
    }

    // ---- PinPoint limits ------------------------------------------------------------------------------------------

    private static string PinPointManifestWithScans(int scans) =>
        "{\"ScanPositions\":[" + string.Join(',', Enumerable.Range(0, scans).Select(i => $"{{\"Id\":\"h{i}\",\"Name\":\"s{i}\"}}")) + "]}";

    [Fact]
    public void A_PinPoint_Manifest_Lists_At_Most_100000_Scans()
    {
        using var dir = new TempDirectory();
        string atLimit = dir.WriteText("limit/p.smf", PinPointManifestWithScans(100_000));
        string beyond = dir.WriteText("beyond/p.smf", PinPointManifestWithScans(100_001));

        using (var doc = Bounded(() => PinPointDocument.Open(atLimit)))
            Assert.Equal(100_000, doc.Scans.Count);

        Assert.Throws<InvalidDataException>(() => Bounded(() => PinPointDocument.Open(beyond)));
    }

    [Fact]
    public void A_PinPoint_Manifest_Larger_Than_The_Limit_Is_Rejected_Before_It_Is_Read()
    {
        using var dir = new TempDirectory();
        string path = dir.PathOf("huge.smf");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            stream.SetLength((64L * 1024 * 1024) + 1);

        var exception = AssertRejectedCheaply(() => PinPointDocument.Open(path));

        Assert.Contains("unreasonably large", exception.Message);
    }

    // ---- FARO parameter size limit --------------------------------------------------------------------------------

    private const int FaroMetadataLimit = 1 << 20;

    private static string FaroParamsOfLength(int length)
    {
        string text = SyntheticFixtures.FaroParams();
        return text + new string(' ', length - text.Length);
    }

    [Fact]
    public void A_Faro_Parameter_File_Larger_Than_The_Limit_Is_Rejected_Before_It_Is_Read()
    {
        using var dir = new TempDirectory();
        string atLimit = SyntheticFixtures.WriteFaroFolder(dir, FaroParamsOfLength(FaroMetadataLimit));

        using (var doc = FaroFlsDocument.Open(atLimit))
            Assert.Equal(SyntheticFixtures.FaroRows, doc.NumRows);

        using var other = new TempDirectory();
        string beyond = SyntheticFixtures.WriteFaroFolder(other, FaroParamsOfLength(FaroMetadataLimit + 1));

        AssertRejectedCheaply(() => FaroFlsDocument.Open(beyond));
    }

    [Fact]
    public void A_Faro_Parameter_Stream_Larger_Than_The_Limit_Is_Rejected_Before_It_Is_Read()
    {
        using var dir = new TempDirectory();
        string path = dir.Write("big-params.fls", FlsWithDistanceStream(SyntheticFixtures.FaroDistances(2000), FaroParamsOfLength(FaroMetadataLimit + 1)));

        AssertRejectedCheaply(() => FaroFlsDocument.Open(path));
    }
}
