using System.Buffers.Binary;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace PclIO.Tests;

/// <summary>One point record of a synthetic .rcs node: quantised offsets, intensity, colour and an optional packed normal.</summary>
public readonly record struct RcsSamplePoint(ushort X, ushort Y, ushort Z, byte Intensity, byte R, byte G, byte B, sbyte Nx = 0, sbyte Ny = 0, sbyte Nz = 0);

/// <summary>One point of a synthetic Leica block: 10-bit grid coordinates, optional RGB565 colour and 16-bit intensity.</summary>
public readonly record struct LeicaSamplePoint(int X, int Y, int Z, ushort Color565 = 0, ushort Intensity = 0);

/// <summary>One cluster of a synthetic EdgeWise cluster file: a centre and 16-bit offsets from it.</summary>
public readonly record struct C3Cluster(float Cx, float Cy, float Cz, (short Dx, short Dy, short Dz)[] Points);

/// <summary>Generates small synthetic scan files for the tests. Every layout is written from what the readers implement;
/// no byte of any of them comes from a real scan.</summary>
internal static class SyntheticFixtures
{
    private const int RcsNodeTableOffset = 98605;
    private const int RcsNodeSize = 368;

    // ---- ReCap scan (.rcs) --------------------------------------------------------------------------------------

    /// <summary>A header-only scan: the declared point count is set, the node table is empty, so it yields no points.</summary>
    public static byte[] CreateSyntheticRcs(long pointCount = 100, double tx = 1.0, double ty = 2.0, double tz = 3.0)
    {
        byte[] buffer = new byte[RcsNodeTableOffset + 1024];
        WriteRcsHeader(buffer, pointCount, tx, ty, tz);
        return buffer;
    }

    /// <summary>A scan with one node table entry and its point records at the end of the file.</summary>
    /// <param name="points">Point records; the position is <paramref name="nodeMin"/> plus 0.0001 times each offset.</param>
    /// <param name="nodeMin">Tight minimum of the node.</param>
    /// <param name="withNormals">Whether the packed normal plane follows the records.</param>
    /// <param name="withColor">Whether the records hold an intensity and a colour; without them they hold the position only.</param>
    /// <param name="declaredPayloadBytes">Overrides the payload size the node declares; the records stay as written.</param>
    /// <param name="tx">Translation of the scan.</param>
    /// <param name="ty">Translation of the scan.</param>
    /// <param name="tz">Translation of the scan.</param>
    public static byte[] CreateSyntheticRcsWithPoints(IReadOnlyList<RcsSamplePoint> points, (double X, double Y, double Z) nodeMin,
        bool withNormals = false, double tx = 0.0, double ty = 0.0, double tz = 0.0, bool withColor = true, uint? declaredPayloadBytes = null)
    {
        int recordBytes = withColor ? 16 : 10;
        int recordBytesTotal = recordBytes + (withNormals ? 4 : 0);
        int recordBytes_ = points.Count * recordBytesTotal;
        byte[] buffer = new byte[RcsNodeTableOffset + 8 + RcsNodeSize + recordBytes_];
        WriteRcsHeader(buffer, points.Count, tx, ty, tz);

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(RcsNodeTableOffset, 8), 1);
        Span<byte> node = buffer.AsSpan(RcsNodeTableOffset + 8, RcsNodeSize);
        double[] min = [nodeMin.X, nodeMin.Y, nodeMin.Z];
        for (int i = 0; i < 3; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(node.Slice(0x30 + (i * 8), 8), min[i]);
            BinaryPrimitives.WriteDoubleLittleEndian(node.Slice(0x48 + (i * 8), 8), min[i] + 7.0);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(node.Slice(0x64, 4), (uint)points.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(node.Slice(0x68, 4), declaredPayloadBytes ?? (uint)recordBytes_);

        int payload = buffer.Length - recordBytes_;
        int normals = payload + (points.Count * recordBytes);
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            Span<byte> record = buffer.AsSpan(payload + (i * recordBytes), recordBytes);
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(4, 2), p.X);
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(6, 2), p.Y);
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(8, 2), p.Z);
            if (withColor)
            {
                record[12] = p.Intensity;
                record[13] = p.R;
                record[14] = p.G;
                record[15] = p.B;
            }

            if (withNormals)
            {
                buffer[normals + (i * 4)] = (byte)p.Nx;
                buffer[normals + (i * 4) + 1] = (byte)p.Ny;
                buffer[normals + (i * 4) + 2] = (byte)p.Nz;
            }
        }

        return buffer;
    }

    private static void WriteRcsHeader(byte[] buffer, long pointCount, double tx, double ty, double tz)
    {
        // Container signature, then version 3.2.
        "ADOCT"u8.CopyTo(buffer.AsSpan(0, 5));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8, 4), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12, 4), 2);

        // Translation, rotation, scale.
        double[] values = [tx, ty, tz, 0, 0, 0, 1, 1, 1];
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(0x10 + (i * 8), 8), values[i]);

        // Octree bounds, then local bounds: min -10, max 10 on every axis.
        double[] bounds = [-10, -10, -10, 10, 10, 10];
        for (int i = 0; i < bounds.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(0x70 + (i * 8), 8), bounds[i]);
            BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(0xA0 + (i * 8), 8), bounds[i]);
        }

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(0xD8, 8), (ulong)pointCount);
        "00000000-0000-0000-0000-000000000001"u8.CopyTo(buffer.AsSpan(0xE3, 36));
        "LOCAL_GRID_SYSTEM"u8.CopyTo(buffer.AsSpan(0x1B9, 17));
    }

    // ---- ReCap project (.rcp) -------------------------------------------------------------------------------------

    /// <summary>A project archive with one scan node whose file is named by <paramref name="scanPath"/>.</summary>
    public static byte[] CreateSyntheticRcpZip(string scanPath = "Scans/scan1.rcs", string scanName = "Station 1", string xmlPrologue = "") =>
        CreateZip("project.xml", RcpManifest(scanPath, scanName, xmlPrologue));

    public static string RcpManifest(string scanPath, string scanName, string xmlPrologue = "") => $"""
        <?xml version="1.0" encoding="utf-8"?>
        {xmlPrologue}
        <Project ProjectId="synthetic-project-id" Version="2.0">
          <CoordinateSystem>EPSG:3857</CoordinateSystem>
          <!-- The element name below is the one the reader looks for. -->
          <VoxelTreeRunTime ScanId="scan-001" ScanName="{SecurityElement.Escape(scanName)}" TotalPoints="5000">
            <RelativePath>{SecurityElement.Escape(scanPath)}</RelativePath>
            <Translation X="10.5" Y="20.5" Z="5.0" />
            <Rotation Yaw="0" Pitch="0" Roll="0" />
            <Scale X="1" Y="1" Z="1" />
          </VoxelTreeRunTime>
        </Project>
        """;

    public static byte[] CreateZip(string entryName, string text)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry(entryName).Open(), new UTF8Encoding(false));
            writer.Write(text);
        }
        return ms.ToArray();
    }

    // ---- E57 ------------------------------------------------------------------------------------------------------

    public static byte[] CreateSyntheticE57(string guid = "synthetic-e57-guid") => new E57Spec { Guid = guid }.Build();

    // ---- Leica container (.lgsx) --------------------------------------------------------------------------------

    /// <summary>A container with setup entries, one point data entry and one point block.</summary>
    /// <param name="points">Points of the block; the default is five points, the first one at the block origin.</param>
    /// <param name="withColorAndIntensity">Writes 16 planes (colour and intensity present) instead of the 4 position planes.</param>
    /// <param name="withPointData">False leaves the point data entry out.</param>
    /// <param name="extraPlanes">Raw plane contents appended to the block after its position (and colour and intensity) planes.</param>
    /// <param name="setupCount">Number of setup entries written before the point data entry.</param>
    /// <param name="assignPointsToSetups">
    /// True gives every setup its own point block and declares the range of that block in the point data, so that
    /// the container assigns its points to its setups. False leaves the setup entries without a range.
    /// </param>
    /// <param name="overlappingSetupRanges">True lets every setup declare the range of the first block.</param>
    /// <param name="collidingSetupNames">True names the second setup so its id after the last dash equals the first's.</param>
    /// <param name="damagedSetupRecord">True writes a setup record whose translation is not a number.</param>
    /// <param name="damagedPointData">True writes point blocks whose compressed frames cannot be decoded.</param>
    /// <param name="withPointCount">False leaves the number of points out of the header of the point data.</param>
    /// <param name="oversizedFrame">True writes a frame whose declared length is above the accepted size.</param>
    /// <param name="unknownBlock">True writes a block after the point blocks that the reader does not know.</param>
    /// <param name="unknownFirstBlock">True writes a chunk the reader does not know before the point blocks.</param>
    /// <param name="truncatedFrameList">True ends the frame list of a block with a length prefix that declares more bytes than the block holds.</param>
    /// <param name="fewPlanes">True writes the blocks with fewer than four planes, so they carry no positions.</param>
    public static byte[] CreateSyntheticLgsx(IReadOnlyList<LeicaSamplePoint>? points = null, bool withColorAndIntensity = false, bool withPointData = true,
        IReadOnlyList<byte[]>? extraPlanes = null, int setupCount = 1, bool assignPointsToSetups = false, bool overlappingSetupRanges = false,
        bool collidingSetupNames = false, bool damagedSetupRecord = false, bool damagedPointData = false, bool withPointCount = true,
        bool oversizedFrame = false, bool unknownBlock = false, bool unknownFirstBlock = false, bool truncatedFrameList = false, bool fewPlanes = false)
    {
        points ??= DefaultLeicaPoints;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write("wild::geosystems"u8.ToArray());

        // The groups of points, one per block of the point data.
        var groups = assignPointsToSetups
            ? SplitPoints(points, setupCount)
            : new List<IReadOnlyList<LeicaSamplePoint>> { points };

        (byte[] payload, List<(long Start, long Length)> blocks) =
            BuildLeicaPayload(groups, withColorAndIntensity, extraPlanes ?? [], damagedPointData, withPointCount, oversizedFrame, unknownBlock,
                unknownFirstBlock, truncatedFrameList, fewPlanes);

        // The point data follows the container entries and their records, so its start is known before they are written.
        var setupNames = Enumerable.Range(0, setupCount).Select(i => "SETUP-" + (3 + i)).ToList();
        if (collidingSetupNames && setupCount >= 2)
            setupNames[1] = "SETUP-A-" + setupNames[0]["SETUP-".Length..];

        var entryOffsets = new long[setupCount];
        var recordOffsets = new long[setupCount];
        long position = ms.Position;
        for (int i = 0; i < setupCount; i++)
        {
            entryOffsets[i] = position;
            position += EntrySize(setupNames[i]);
            recordOffsets[i] = position;
            position += assignPointsToSetups ? SetupRecordSize : 0;
        }

        long payloadStart = position + EntrySize("PointCloud");

        // Container entries as the reader expects them.
        var setupEntries = new List<long>();
        for (int i = 0; i < setupCount; i++)
        {
            setupEntries.Add(entryOffsets[i]);

            // A setup owns the block it declares; without a record the setup has no points of its own.
            bool hasRecord = assignPointsToSetups;
            WriteEntry(bw, setupNames[i], hasRecord ? (ulong)recordOffsets[i] : 0, hasRecord ? (ulong)SetupRecordSize : 0);
            if (!hasRecord) continue;

            bool hasBlock = i < blocks.Count;
            int blockIndex = overlappingSetupRanges ? 0 : i;
            WriteSetupRecord(bw, i, hasBlock ? payloadStart + blocks[blockIndex].Start : 0,
                hasBlock ? (ulong)blocks[blockIndex].Length : 0,
                hasBlock ? groups[blockIndex].Count : 0,
                damaged: damagedSetupRecord);
        }

        long pointEntry = ms.Position;
        if (withPointData) WriteEntry(bw, "PointCloud", (ulong)payloadStart, (ulong)payload.Length);
        bw.Write(payload);

        long tocOffset = ms.Position;
        bw.Write((uint)(setupCount + (withPointData ? 1 : 0)));
        foreach (long setupEntry in setupEntries) bw.Write((ulong)setupEntry);
        if (withPointData) bw.Write((ulong)pointEntry);

        bw.Write(Encoding.ASCII.GetBytes("build 2024.1.0 {12345678-1234-1234-1234-123456789abc}"));
        bw.Write((ulong)tocOffset);
        return ms.ToArray();
    }

    // The size of the record of a setup.
    public const int SetupRecordSize = 96;

    // Writes the record of a setup: the translation and the bounds of the setup, then the range of its points
    // and the number of them.
    private static void WriteSetupRecord(BinaryWriter bw, int setupIndex, long pointOffset, ulong pointLength,
        long pointCount, bool damaged)
    {
        double[] translation = damaged
            ? [double.NaN, 2 + setupIndex, 3 + setupIndex]
            : [1 + setupIndex, 2 + setupIndex, 3 + setupIndex];

        double[] bounds =
        [
            LeicaOriginX, LeicaOriginY, LeicaOriginZ,
            LeicaOriginX + (1023 * LeicaScale), LeicaOriginY + (1023 * LeicaScale), LeicaOriginZ + (1023 * LeicaScale),
        ];

        foreach (double value in translation.Concat(bounds))
            bw.Write(value);

        bw.Write((ulong)pointOffset);
        bw.Write(pointLength);
        bw.Write((ulong)pointCount);
    }

    // Distributes the points over the setups; a setup without points gets no block.
    private static List<IReadOnlyList<LeicaSamplePoint>> SplitPoints(IReadOnlyList<LeicaSamplePoint> points, int setupCount)
    {
        var groups = new List<IReadOnlyList<LeicaSamplePoint>>();
        if (setupCount <= 0) return groups;

        int perGroup = points.Count / setupCount;
        int extra = points.Count % setupCount;
        int index = 0;
        for (int g = 0; g < setupCount; g++)
        {
            int take = perGroup + (g < extra ? 1 : 0);
            groups.Add([.. points.Skip(index).Take(take)]);
            index += take;
        }

        return groups;
    }

    public static readonly IReadOnlyList<LeicaSamplePoint> DefaultLeicaPoints =
    [
        new(0, 0, 0, 0xF800, 0xFFFF),
        new(1, 2, 3, 0x07E0, 0x8000),
        new(1023, 1023, 1023, 0x001F, 0x0001),
        new(10, 20, 30, 0xFFFF, 0),
        new(512, 256, 128, 0x0820, 0x4000),
    ];

    public const double LeicaScale = 0.001;
    public const double LeicaOriginX = 100.0;
    public const double LeicaOriginY = 200.0;
    public const double LeicaOriginZ = 300.0;

    // The bytes a container entry of the given name takes.
    private static int EntrySize(string name) => 29 + name.Length;

    private static void WriteEntry(BinaryWriter bw, string name, ulong payloadOffset, ulong payloadLength)
    {
        bw.Write(new byte[10]);
        bw.Write((ushort)name.Length);
        bw.Write(Encoding.ASCII.GetBytes(name));
        bw.Write((byte)0);
        bw.Write(payloadOffset);
        bw.Write(payloadLength);
    }

    // Builds the point data payload: a header, then one block per group of points.
    private static (byte[] Payload, List<(long Start, long Length)> Blocks) BuildLeicaPayload(
        IReadOnlyList<IReadOnlyList<LeicaSamplePoint>> groups, bool full, IReadOnlyList<byte[]> extraPlanes, bool damaged = false,
        bool withPointCount = true, bool oversizedFrame = false, bool unknownBlock = false,
        bool unknownFirstBlock = false, bool truncatedFrameList = false, bool fewPlanes = false)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // Payload header: signature, the offset of the first block and the number of points.
        byte[] header = new byte[64];
        "mMPK"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x20, 4), 64);
        if (withPointCount)
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x28, 8), (ulong)groups.Sum(g => g.Count));
        bw.Write(header);

        // A chunk that is not a point block before the point blocks: the chain stays empty.
        if (unknownFirstBlock)
        {
            bw.Write("XPRB"u8.ToArray());
            bw.Write((uint)(8 + 128));
            bw.Write(new byte[128]);
        }

        var blocks = new List<(long Start, long Length)>();
        foreach (var group in groups)
        {
            // A setup without points has no block of its own.
            if (group.Count == 0) continue;

            var frames = BuildFrames(group, full, extraPlanes);
            if (fewPlanes) frames = [.. frames.Take(3)];
            if (damaged)
            {
                // A frame whose descriptor is not valid: the header of the block stays readable, the data does not.
                foreach (byte[] broken in frames) broken[4] = 0x5A;
            }

            // A frame whose declared length is above the accepted size is skipped by the reader.
            const ulong OversizedLength = 20_000_000;
            int blockLength = 8 + 128 + frames.Sum(f => 8 + f.Length) + (oversizedFrame ? 8 : 0) + (truncatedFrameList ? 8 : 0);
            long start = ms.Position;

            bw.Write("APRN"u8.ToArray());
            bw.Write((uint)blockLength);
            byte[] blockHeader = new byte[128];
            double[] frame = [LeicaScale, LeicaScale, LeicaScale, LeicaOriginX, LeicaOriginY, LeicaOriginZ];
            for (int i = 0; i < frame.Length; i++)
                BinaryPrimitives.WriteDoubleLittleEndian(blockHeader.AsSpan(0x34 + (i * 8), 8), frame[i]);
            bw.Write(blockHeader);
            foreach (byte[] f in frames)
            {
                bw.Write((ulong)f.Length);
                bw.Write(f);
            }

            if (oversizedFrame) bw.Write(OversizedLength);

            // A length prefix that declares more bytes than the block holds: the frame list is cut short.
            if (truncatedFrameList) bw.Write((ulong)1000);

            blocks.Add((start, blockLength));
        }

        // A chunk that is not a point block: the reader stops the chain there.
        if (unknownBlock)
        {
            bw.Write("XPRB"u8.ToArray());
            bw.Write((uint)(8 + 128));
            bw.Write(new byte[128]);
        }

        return (ms.ToArray(), blocks);
    }

    // The compressed planes of one point block: the four position planes and the optional ones.
    private static List<byte[]> BuildFrames(IReadOnlyList<LeicaSamplePoint> points, bool full, IReadOnlyList<byte[]> extraPlanes)
    {
        int planeCount = full ? 16 : 4;
        int n = points.Count;
        var planes = new byte[planeCount][];
        for (int i = 0; i < planeCount; i++) planes[i] = new byte[n];

        for (int i = 0; i < n; i++)
        {
            // Interleave the three 10-bit coordinates into one 30-bit code, spread over four byte planes.
            uint code = 0;
            for (int b = 0; b < 10; b++)
            {
                code |= (uint)((points[i].X >> b) & 1) << (b * 3);
                code |= (uint)((points[i].Y >> b) & 1) << ((b * 3) + 1);
                code |= (uint)((points[i].Z >> b) & 1) << ((b * 3) + 2);
            }
            for (int p = 0; p < 4; p++) planes[p][i] = (byte)(code >> (8 * p));

            if (full)
            {
                planes[12][i] = (byte)points[i].Color565;
                planes[13][i] = (byte)(points[i].Color565 >> 8);
                planes[14][i] = (byte)points[i].Intensity;
                planes[15][i] = (byte)(points[i].Intensity >> 8);
            }
        }

        var frames = new List<byte[]>();
        foreach (byte[] plane in planes.Concat(extraPlanes)) frames.Add(RawZstdFrame.Wrap(plane));
        return frames;
    }

    // ---- PinPoint -------------------------------------------------------------------------------------------------

    public static byte[] CreateSyntheticPinPointSmf() => Encoding.UTF8.GetBytes(PinPointManifest("Pos1", "h1", 42000));

    public static string PinPointManifest(string name, string hash, long? count) => $$"""
        {
          "ScanPositions": [
            {
              "Id": "{{hash}}",
              "Name": "{{name}}",
              {{(count.HasValue ? $"\"Count\": {count}," : "")}}
              "sop": [1, 0, 0, 5.0, 0, 1, 0, 10.0, 0, 0, 1, 15.0, 0, 0, 0, 1]
            }
          ]
        }
        """;

    /// <summary>A point data file that starts with a table of named channels. Colour is RGBA bytes, normals are three floats.</summary>
    public static byte[] CreateSyntheticPinPointBin(IReadOnlyList<(float X, float Y, float Z)> points, byte[]? rgba = null,
        byte[]? intensities = null, float[]? normals = null, long? pointsLengthOverride = null)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(new byte[12]);

        byte[] positions = new byte[points.Count * 12];
        for (int i = 0; i < points.Count; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(positions.AsSpan(i * 12, 4), points[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(positions.AsSpan((i * 12) + 4, 4), points[i].Y);
            BinaryPrimitives.WriteSingleLittleEndian(positions.AsSpan((i * 12) + 8, 4), points[i].Z);
        }

        WriteChannel(bw, "Points", 6, positions, pointsLengthOverride);
        if (rgba != null) WriteChannel(bw, "Colors", 7, rgba);
        if (normals != null)
        {
            byte[] raw = new byte[normals.Length * 4];
            for (int i = 0; i < normals.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(i * 4, 4), normals[i]);
            WriteChannel(bw, "Normals", 7, raw);
        }
        if (intensities != null) WriteChannel(bw, "Intensities", 7, intensities);
        return ms.ToArray();
    }

    private static void WriteChannel(BinaryWriter bw, string name, int tag, byte[] payload, long? lengthOverride = null)
    {
        bw.Write(name.Length);
        bw.Write(Encoding.ASCII.GetBytes(name));
        bw.Write(0);
        bw.Write(lengthOverride ?? (tag + payload.Length));
        bw.Write(new byte[tag]);
        bw.Write(payload);
    }

    /// <summary>Writes a project manifest and, when given, its point data file into a temporary folder; returns the manifest path.</summary>
    public static string WritePinPointProject(TempDirectory dir, byte[]? bin, string? manifest = null)
    {
        string path = dir.WriteText("project.smf", manifest ?? PinPointManifest("Pos1", "h1", null));
        Directory.CreateDirectory(dir.PathOf(Path.Combine("PointClouds", "Input")));
        if (bin != null) dir.Write(Path.Combine("PointClouds", "Input", "Pos1_h1", "Pos1.bin"), bin);
        return path;
    }

    // ---- EdgeWise -------------------------------------------------------------------------------------------------

    /// <summary>A synthetic cluster file holding the given clusters.</summary>
    public static byte[] CreateSyntheticC3ptx(params C3Cluster[] clusters)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((uint)clusters.Length);
        foreach (var c in clusters)
        {
            bw.Write(c.Cx);
            bw.Write(c.Cy);
            bw.Write(c.Cz);
            bw.Write(c.Points.Length);
            foreach (var (dx, dy, dz) in c.Points)
            {
                bw.Write(dx);
                bw.Write(dy);
                bw.Write(dz);
            }
        }
        return ms.ToArray();
    }

    /// <summary>A database text with a bounds line and one node line per (count, file) pair.</summary>
    public static string CreateSyntheticC3db(params (long Count, string File)[] nodes)
    {
        var sb = new StringBuilder();
        sb.Append("_Tree ( 10 10 10 ) ( -10 -10 -10 )\n");
        for (int i = 0; i < nodes.Length; i++)
            sb.Append($"{i + 1} (Count:{nodes[i].Count} File: \"{nodes[i].File}\"\n");
        return sb.ToString();
    }

    // ---- FARO -------------------------------------------------------------------------------------------------------

    public const int FaroRows = 4;
    public const int FaroCols = 8;

    public static string FaroParams(string extra = "") => $"""
        Name "ScannerModel" Value "TestModel"
        Name "ScannerSerNo" Value "SN-0001"
        NumRows = {FaroRows}
        NumCols = {FaroCols}
        RowStartAngle = -1.0
        RowEndAngle = 1.0
        ColEndAngle = 6.28
        {extra}
        """;

    /// <summary>Little-endian uint16 distances in millimetres for the whole grid.</summary>
    /// <param name="millimetres">The distance of every cell that holds a return.</param>
    /// <param name="invalidCells">
    /// Number of cells at the start of the grid that hold no return: the first half of them hold the value 0 and
    /// the second half the value 0xFFFF.
    /// </param>
    public static byte[] FaroDistances(ushort millimetres, int invalidCells = 0)
    {
        byte[] data = new byte[FaroRows * FaroCols * 2];
        for (int i = 0; i < FaroRows * FaroCols; i++)
        {
            ushort value = millimetres;
            if (i < invalidCells) value = i < (invalidCells / 2) ? (ushort)0 : (ushort)0xFFFF;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(i * 2, 2), value);
        }

        return data;
    }

    /// <summary>A synthetic scan folder with a parameter file and one data stream.</summary>
    /// <param name="dir">Folder to write into.</param>
    /// <param name="parameters">Content of the parameter file.</param>
    /// <param name="millimetres">The distance of the cells that hold a return.</param>
    /// <param name="invalidCells">Number of cells at the start of the grid that hold no return.</param>
    /// <param name="intensity">The stored intensity of every cell.</param>
    /// <param name="recordBytes">Bytes that one sample takes.</param>
    /// <param name="extraBytes">Bytes after the samples, so that the sizes do not fit the grid.</param>
    /// <param name="samples">Number of samples that are written; fewer than the grid leaves the stream short.</param>
    public static string WriteFaroFolder(TempDirectory dir, string? parameters = null, ushort millimetres = 2000, int invalidCells = 0,
        ushort intensity = 1024, int recordBytes = 8, int extraBytes = 0, int samples = FaroRows * FaroCols)
    {
        dir.WriteText(Path.Combine("scan", "Scans", "1", "Params"), parameters ?? FaroParams());
        byte[] records = new byte[(samples * recordBytes) + extraBytes];
        byte[] distances = FaroDistances(millimetres, invalidCells);
        for (int i = 0; i < samples; i++)
        {
            int off = i * recordBytes;
            Array.Copy(distances, i * 2, records, off, 2);
            // A record smaller than four bytes holds the distance only; there is no room for the intensity.
            if (recordBytes >= 4)
                BinaryPrimitives.WriteUInt16LittleEndian(records.AsSpan(off + 2, 2), intensity);
        }

        dir.Write(Path.Combine("scan", "Scans", "1", "ScanDataStream0"), records);
        return dir.PathOf("scan");
    }

    /// <summary>A single-file container (.fls) with the parameter, distance and grey streams of one scan.</summary>
    public static byte[] CreateSyntheticFlsContainer(string? parameters = null, ushort millimetres = 2000, bool withGrey = true,
        int invalidCells = 0)
    {
        var builder = new Ole2Builder();
        int scans = builder.AddStorage(0, "Scans");
        int one = builder.AddStorage(scans, "1");
        builder.AddStream(one, "Params", Encoding.ASCII.GetBytes(parameters ?? FaroParams()));
        builder.AddStream(one, "Dist0", FaroDistances(millimetres, invalidCells));
        if (withGrey) builder.AddStream(one, "Grey0", Enumerable.Range(0, FaroRows * FaroCols).Select(i => (byte)(i * 8)).ToArray());
        return builder.Build();
    }
}
