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

/// <summary>One point of a synthetic LAS file: position, normalised intensity, colour and class code.</summary>
public readonly record struct LasSamplePoint(double X, double Y, double Z, float Intensity = 1.0f, PclColor? Color = null, byte Classification = 0);

/// <summary>One point of a synthetic PCD file: position, intensity, colour and normal.</summary>
public readonly record struct PcdSamplePoint(double X, double Y, double Z, float? Intensity = null, PclColor? Color = null, PclVector3? Normal = null);

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
    public static byte[] CreateSyntheticRcpZip(string scanPath = "Scans/scan1.rcs", string scanName = "Station 1", string xmlPrologue = "",
        bool withTransform = true) =>
        CreateZip("project.xml", RcpManifest(scanPath, scanName, xmlPrologue, withTransform));

    public static string RcpManifest(string scanPath, string scanName, string xmlPrologue = "", bool withTransform = true) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        {xmlPrologue}
        <Project ProjectId="synthetic-project-id" Version="2.0">
          <CoordinateSystem>EPSG:3857</CoordinateSystem>
          <!-- The element name below is the one the reader looks for. -->
          <VoxelTreeRunTime ScanId="scan-001" ScanName="{SecurityElement.Escape(scanName)}" TotalPoints="5000">
            <RelativePath>{SecurityElement.Escape(scanPath)}</RelativePath>
            {(withTransform ? """
            <Translation X="10.5" Y="20.5" Z="5.0" />
            <Rotation Yaw="0" Pitch="0" Roll="0" />
            <Scale X="1" Y="1" Z="1" />
            """ : "")}
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
    /// <param name="blockCount">When above zero, splits the points into that many blocks without assigning them to setups.</param>
    /// <param name="damagedBlock">When not negative, only the block with this zero-based index holds damaged frames.</param>
    public static byte[] CreateSyntheticLgsx(IReadOnlyList<LeicaSamplePoint>? points = null, bool withColorAndIntensity = false, bool withPointData = true,
        IReadOnlyList<byte[]>? extraPlanes = null, int setupCount = 1, bool assignPointsToSetups = false, bool overlappingSetupRanges = false,
        bool collidingSetupNames = false, bool damagedSetupRecord = false, bool damagedPointData = false, bool withPointCount = true,
        bool oversizedFrame = false, bool unknownBlock = false, bool unknownFirstBlock = false, bool truncatedFrameList = false, bool fewPlanes = false,
        int blockCount = 0, int damagedBlock = -1)
    {
        points ??= DefaultLeicaPoints;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write("wild::geosystems"u8.ToArray());

        // The groups of points, one per block of the point data.
        var groups = blockCount > 0 ? SplitPoints(points, blockCount)
            : assignPointsToSetups ? SplitPoints(points, setupCount)
            : new List<IReadOnlyList<LeicaSamplePoint>> { points };

        (byte[] payload, List<(long Start, long Length)> blocks) =
            BuildLeicaPayload(groups, withColorAndIntensity, extraPlanes ?? [], damagedPointData, withPointCount, oversizedFrame, unknownBlock,
                unknownFirstBlock, truncatedFrameList, fewPlanes, damagedBlock);

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
        bool unknownFirstBlock = false, bool truncatedFrameList = false, bool fewPlanes = false, int damagedBlock = -1)
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
        int blockIndex = 0;
        foreach (var group in groups)
        {
            // A setup without points has no block of its own.
            if (group.Count == 0) continue;

            var frames = BuildFrames(group, full, extraPlanes);
            if (fewPlanes) frames = [.. frames.Take(3)];
            if (damaged || blockIndex == damagedBlock)
            {
                // A frame whose descriptor is not valid: the header of the block stays readable, the data does not.
                foreach (byte[] broken in frames) broken[4] = 0x5A;
            }

            blockIndex++;

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

    // ---- LAS (.las) ---------------------------------------------------------------------------------------------------
    /// <summary>Base record size of a LAS point format; waveform tails are not interpreted.</summary>
    public static int LasBaseRecordSize(int pointFormat) => pointFormat switch
    {
        0 => 20,
        1 => 28,
        2 => 26,
        3 => 34,
        4 => 57,
        5 => 63,
        6 => 30,
        7 => 36,
        8 => 38,
        9 => 59,
        _ => 67,
    };

    /// <summary>A GeoTIFF variable length record naming an EPSG code, as the LAS exporter writes it.</summary>
    public static (string UserId, ushort RecordId, byte[] Data) LasEpsgVlr(ushort code)
    {
        byte[] data = new byte[24];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8, 2), 1024);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(16, 2), 3072);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(18, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22, 2), code);
        return ("LASF_Projection", 34735, data);
    }

    /// <summary>An OGC WKT coordinate system record (LASF_Projection, record 2112, null-terminated UTF-8).</summary>
    public static (string UserId, ushort RecordId, byte[] Data) LasWktVlr(string wkt) =>
        ("LASF_Projection", 2112, Encoding.UTF8.GetBytes(wkt + "\0"));

    /// <summary>A synthetic LAS file with one scan.</summary>
    /// <param name="points">Points to store; empty writes a header with no records.</param>
    /// <param name="versionMinor">Minor version: 0 to 4 for 1.0 to 1.4.</param>
    /// <param name="pointFormat">Point data format; 0 to 10 carry points, anything else needs empty points.</param>
    /// <param name="scale">Metres per stored unit, on every axis.</param>
    /// <param name="offset">Coordinate offset, or null for the minimum of the points.</param>
    /// <param name="vlrs">Variable length records to store.</param>
    /// <param name="extraBytesPerRecord">Zero bytes appended to every record past the format's base size.</param>
    /// <param name="recordLengthOverride">Record length to declare instead of the written one.</param>
    /// <param name="legacyCountOverride">Legacy point count to declare instead of the written one.</param>
    /// <param name="extendedCountOverride">Extended 1.4 point count to declare instead of the written one.</param>
    public static byte[] CreateSyntheticLas(IReadOnlyList<LasSamplePoint>? points = null, int versionMinor = 2,
        int pointFormat = 2, double scale = 0.001, (double X, double Y, double Z)? offset = null,
        IReadOnlyList<(string UserId, ushort RecordId, byte[] Data)>? vlrs = null, int extraBytesPerRecord = 0,
        int? recordLengthOverride = null, uint? legacyCountOverride = null, ulong? extendedCountOverride = null)
    {
        points ??= [new(1, 2, 3, 0.25f, new PclColor(10, 20, 30), 5), new(-4.5, 5.25, 6.125, 0.5f, null, 2)];
        if ((pointFormat < 0 || pointFormat > 10) && points.Count > 0)
            throw new ArgumentException("Only point formats 0 to 10 can hold points.", nameof(pointFormat));

        bool is14 = versionMinor == 4;
        int headerSize = is14 ? 375 : 227;
        int baseSize = pointFormat is >= 0 and <= 10 ? LasBaseRecordSize(pointFormat) : 0;
        int recordLength = recordLengthOverride ?? (baseSize + extraBytesPerRecord);

        var vlrBytes = new List<byte[]>();
        foreach (var (userId, recordId, data) in vlrs ?? [])
        {
            byte[] header = new byte[54];
            byte[] userBytes = Encoding.ASCII.GetBytes(userId);
            Array.Copy(userBytes, 0, header, 2, Math.Min(userBytes.Length, 16));
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(18, 2), recordId);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20, 4), (uint)data.Length);
            Encoding.ASCII.GetBytes("PclIO test").CopyTo(header, 24);
            vlrBytes.Add([.. header, .. data]);
        }

        uint pointDataOffset = (uint)(headerSize + vlrBytes.Sum(b => b.Length));
        double offsetX = offset?.X ?? (points.Count > 0 ? points.Min(p => p.X) : 0);
        double offsetY = offset?.Y ?? (points.Count > 0 ? points.Min(p => p.Y) : 0);
        double offsetZ = offset?.Z ?? (points.Count > 0 ? points.Min(p => p.Z) : 0);

        byte[] headerBuf = new byte[headerSize];
        "LASF"u8.CopyTo(headerBuf);
        headerBuf[24] = 1;
        headerBuf[25] = (byte)versionMinor;
        Encoding.ASCII.GetBytes("PclIO synthetic").CopyTo(headerBuf, 26);
        Encoding.ASCII.GetBytes("PclIO tests").CopyTo(headerBuf, 58);
        BinaryPrimitives.WriteUInt16LittleEndian(headerBuf.AsSpan(94, 2), (ushort)headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(headerBuf.AsSpan(96, 4), pointDataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(headerBuf.AsSpan(100, 4), (uint)(vlrs?.Count ?? 0));
        headerBuf[104] = (byte)pointFormat;
        BinaryPrimitives.WriteUInt16LittleEndian(headerBuf.AsSpan(105, 2), (ushort)recordLength);

        uint legacyCount = legacyCountOverride ?? (uint)points.Count;
        BinaryPrimitives.WriteUInt32LittleEndian(headerBuf.AsSpan(107, 4), legacyCount);
        BinaryPrimitives.WriteUInt32LittleEndian(headerBuf.AsSpan(111, 4), legacyCount);
        BinaryPrimitives.WriteDoubleLittleEndian(headerBuf.AsSpan(131, 8), scale);
        BinaryPrimitives.WriteDoubleLittleEndian(headerBuf.AsSpan(139, 8), scale);
        BinaryPrimitives.WriteDoubleLittleEndian(headerBuf.AsSpan(147, 8), scale);
        BinaryPrimitives.WriteDoubleLittleEndian(headerBuf.AsSpan(155, 8), offsetX);
        BinaryPrimitives.WriteDoubleLittleEndian(headerBuf.AsSpan(163, 8), offsetY);
        BinaryPrimitives.WriteDoubleLittleEndian(headerBuf.AsSpan(171, 8), offsetZ);

        double minX = points.Count > 0 ? points.Min(p => p.X) : 0;
        double minY = points.Count > 0 ? points.Min(p => p.Y) : 0;
        double minZ = points.Count > 0 ? points.Min(p => p.Z) : 0;
        double maxX = points.Count > 0 ? points.Max(p => p.X) : 0;
        double maxY = points.Count > 0 ? points.Max(p => p.Y) : 0;
        double maxZ = points.Count > 0 ? points.Max(p => p.Z) : 0;
        BinaryPrimitives.WriteDoubleLittleEndian(headerBuf.AsSpan(179, 8), maxX);
        BinaryPrimitives.WriteDoubleLittleEndian(headerBuf.AsSpan(187, 8), minX);
        BinaryPrimitives.WriteDoubleLittleEndian(headerBuf.AsSpan(195, 8), maxY);
        BinaryPrimitives.WriteDoubleLittleEndian(headerBuf.AsSpan(203, 8), minY);
        BinaryPrimitives.WriteDoubleLittleEndian(headerBuf.AsSpan(211, 8), maxZ);
        BinaryPrimitives.WriteDoubleLittleEndian(headerBuf.AsSpan(219, 8), minZ);

        if (is14)
        {
            ulong extended = extendedCountOverride ?? (ulong)points.Count;
            BinaryPrimitives.WriteUInt64LittleEndian(headerBuf.AsSpan(247, 8), extended);
            BinaryPrimitives.WriteUInt64LittleEndian(headerBuf.AsSpan(255, 8), extended);
        }

        using var ms = new MemoryStream();
        ms.Write(headerBuf);
        foreach (byte[] vlr in vlrBytes) ms.Write(vlr);

        bool legacy = pointFormat is >= 0 and <= 5;
        // A declared length below the base size cannot hold the format; records are still written whole so that
        // the file is otherwise valid and the open path rejects the declared length.
        int writtenLength = Math.Max(recordLength, baseSize);
        foreach (var p in points)
        {
            byte[] record = new byte[writtenLength];
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0, 4), (int)Math.Round((p.X - offsetX) / scale));
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4, 4), (int)Math.Round((p.Y - offsetY) / scale));
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(8, 4), (int)Math.Round((p.Z - offsetZ) / scale));
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(12, 2),
                (ushort)Math.Clamp(MathF.Round(p.Intensity * 65535f), 0f, 65535f));

            if (legacy)
            {
                record[14] = 0x09;
                record[15] = (byte)(p.Classification & 0x1F);
            }
            else if (pointFormat is >= 6 and <= 10)
            {
                record[14] = 0x11;
                record[16] = p.Classification;
            }

            if (pointFormat is 2 or 3 or 5)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20, 2), (ushort)((p.Color?.R ?? 255) * 257));
                BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(22, 2), (ushort)((p.Color?.G ?? 255) * 257));
                BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(24, 2), (ushort)((p.Color?.B ?? 255) * 257));
            }
            else if (pointFormat is 7 or 8 or 10)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(30, 2), (ushort)((p.Color?.R ?? 255) * 257));
                BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(32, 2), (ushort)((p.Color?.G ?? 255) * 257));
                BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(34, 2), (ushort)((p.Color?.B ?? 255) * 257));
            }

            ms.Write(record);
        }

        return ms.ToArray();
    }

    // ---- PCD (.pcd) ---------------------------------------------------------------------------------------------------

    /// <summary>Default PCD fields: positions, packed colour, intensity and normals, all 4-byte floats.</summary>
    public static IReadOnlyList<(string Name, int Size, char Type, int Count)> DefaultPcdFields =>
    [
        ("x", 4, 'F', 1), ("y", 4, 'F', 1), ("z", 4, 'F', 1),
        ("rgb", 4, 'F', 1), ("intensity", 4, 'F', 1),
        ("normal_x", 4, 'F', 1), ("normal_y", 4, 'F', 1), ("normal_z", 4, 'F', 1),
    ];

    /// <summary>Default PCD points: three points with mixed channels.</summary>
    public static IReadOnlyList<PcdSamplePoint> DefaultPcdPoints =>
    [
        new(1, 2, 3, 0.25f, new PclColor(10, 20, 30), new PclVector3(0, 0, 1)),
        new(-4.5, 5.25, 6.125, 0.5f),
        new(7, 8, 9, null, new PclColor(255, 255, 255)),
    ];

    /// <summary>A synthetic PCD file with one scan.</summary>
    /// <param name="points">Points to store.</param>
    /// <param name="fields">Field declarations; null uses <see cref="DefaultPcdFields"/>.</param>
    /// <param name="data">Point storage: ascii, binary or binary_compressed.</param>
    /// <param name="viewpoint">VIEWPOINT value; null uses the default.</param>
    /// <param name="widthOverride">WIDTH to declare instead of the point count.</param>
    /// <param name="heightOverride">HEIGHT to declare instead of 1.</param>
    /// <param name="pointsOverride">POINTS to declare instead of the point count.</param>
    /// <param name="withCountLine">False leaves the COUNT line out.</param>
    public static byte[] CreateSyntheticPcd(IReadOnlyList<PcdSamplePoint>? points = null,
        IReadOnlyList<(string Name, int Size, char Type, int Count)>? fields = null, string data = "ascii",
        string? viewpoint = null, long? widthOverride = null, long? heightOverride = null, long? pointsOverride = null,
        bool withCountLine = true)
    {
        points ??= DefaultPcdPoints;
        fields ??= DefaultPcdFields;

        var header = new List<string>
        {
            "# .PCD v.7 - Point Cloud Data file format",
            "VERSION 0.7",
            "FIELDS " + string.Join(' ', fields.Select(f => f.Name)),
            "SIZE " + string.Join(' ', fields.Select(f => f.Size)),
            "TYPE " + string.Join(' ', fields.Select(f => f.Type)),
        };
        if (withCountLine) header.Add("COUNT " + string.Join(' ', fields.Select(f => f.Count)));
        header.AddRange([
            $"WIDTH {widthOverride ?? points.Count}",
            $"HEIGHT {heightOverride ?? 1}",
            $"VIEWPOINT {viewpoint ?? "0 0 0 1 0 0 0"}",
            $"POINTS {pointsOverride ?? points.Count}",
            $"DATA {data}",
        ]);

        byte[] body = data switch
        {
            "ascii" => PcdAsciiBody(points, fields),
            "binary" => PcdBinaryBody(points, fields),
            "binary_compressed" => PcdCompressedBody(points, fields),
            _ => throw new ArgumentException($"Unknown PCD data '{data}'.", nameof(data)),
        };

        byte[] headerBytes = System.Text.Encoding.ASCII.GetBytes(string.Join('\n', header) + "\n");
        return [.. headerBytes, .. body];
    }

    /// <summary>A PCD file from raw header lines and a body.</summary>
    public static byte[] CreatePcdFile(IEnumerable<string> headerLines, byte[] body)
    {
        byte[] headerBytes = System.Text.Encoding.ASCII.GetBytes(string.Join('\n', headerLines) + "\n");
        return [.. headerBytes, .. body];
    }

    private static byte[] PcdAsciiBody(IReadOnlyList<PcdSamplePoint> points,
        IReadOnlyList<(string Name, int Size, char Type, int Count)> fields)
    {
        var sb = new StringBuilder();
        foreach (var p in points)
        {
            var tokens = new List<string>();
            foreach (var field in fields)
                for (int c = 0; c < field.Count; c++)
                    tokens.Add(PcdAsciiValue(p, field, c));
            sb.Append(string.Join(' ', tokens));
            sb.Append('\n');
        }

        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string PcdAsciiValue(PcdSamplePoint p, (string Name, int Size, char Type, int Count) field, int index)
    {
        if (index > 0) return "0";
        // Packed colour travels as the float holding its bits, like PCL writes it; other shapes hold no colour.
        if (field.Name is "rgb" or "rgba")
        {
            if (field is not { Size: 4, Type: 'F' }) return "0";
            uint bits = p.Color is { } c ? (uint)((c.R << 16) | (c.G << 8) | c.B) : 0xFFFFFF;
            return BitConverter.UInt32BitsToSingle(bits).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        double value = PcdFieldValue(p, field);
        if (field.Type == 'F') return double.IsNaN(value) ? "nan" : value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        return ((long)value).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] PcdBinaryBody(IReadOnlyList<PcdSamplePoint> points,
        IReadOnlyList<(string Name, int Size, char Type, int Count)> fields)
    {
        using var ms = new MemoryStream();
        foreach (var p in points)
            foreach (var field in fields)
                for (int c = 0; c < field.Count; c++)
                    ms.Write(PcdFieldBytes(p, field, c));
        return ms.ToArray();
    }

    private static byte[] PcdCompressedBody(IReadOnlyList<PcdSamplePoint> points,
        IReadOnlyList<(string Name, int Size, char Type, int Count)> fields)
    {
        // The payload is one block per field holding every point.
        using var flat = new MemoryStream();
        foreach (var field in fields)
            foreach (var p in points)
                for (int c = 0; c < field.Count; c++)
                    flat.Write(PcdFieldBytes(p, field, c));

        byte[] uncompressed = flat.ToArray();
        byte[] compressed = LzfCompress(uncompressed);

        using var ms = new MemoryStream();
        byte[] sizes = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(sizes.AsSpan(0, 4), (uint)compressed.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(sizes.AsSpan(4, 4), (uint)uncompressed.Length);
        ms.Write(sizes);
        ms.Write(compressed);
        return ms.ToArray();
    }

    private static double PcdFieldValue(PcdSamplePoint p, (string Name, int Size, char Type, int Count) field) => field.Name switch
    {
        "x" => p.X,
        "y" => p.Y,
        "z" => p.Z,
        "intensity" => p.Intensity ?? 0,
        "normal_x" => p.Normal?.X ?? 0,
        "normal_y" => p.Normal?.Y ?? 0,
        "normal_z" => p.Normal?.Z ?? 0,
        _ => 0,
    };

    private static byte[] PcdFieldBytes(PcdSamplePoint p, (string Name, int Size, char Type, int Count) field, int index)
    {
        // Packed colour travels as its four bytes; other shapes hold no colour.
        if (field.Name is "rgb" or "rgba")
        {
            uint bits = p.Color is { } c ? (uint)((c.R << 16) | (c.G << 8) | c.B) : 0xFFFFFF;
            byte[] colour = new byte[field.Size];
            if (field.Size >= 4) BinaryPrimitives.WriteUInt32LittleEndian(colour, bits);
            return colour;
        }

        double value = index == 0 ? PcdFieldValue(p, field) : 0;
        byte[] bytes = new byte[field.Size];
        switch (field.Type)
        {
            case 'I':
                long signed = (long)value;
                for (int i = 0; i < field.Size; i++) bytes[i] = (byte)(signed >> (8 * i));
                break;
            case 'U':
                ulong unsigned = (ulong)(long)value;
                for (int i = 0; i < field.Size; i++) bytes[i] = (byte)(unsigned >> (8 * i));
                break;
            default:
                if (field.Size == 4)
                    BinaryPrimitives.WriteSingleLittleEndian(bytes, (float)value);
                else
                    BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
                break;
        }

        return bytes;
    }

    /// <summary>Test-only LZF compressor for fixtures: greedy matches of at least 3 bytes in an 8 KiB window.</summary>
    public static byte[] LzfCompress(byte[] input)
    {
        var output = new List<byte>(input.Length);
        int i = 0;
        int literals = 0;

        void FlushLiterals(int from, int count)
        {
            while (count > 0)
            {
                int run = Math.Min(32, count);
                output.Add((byte)(run - 1));
                output.AddRange(input.AsSpan(from, run).ToArray());
                from += run;
                count -= run;
            }
        }

        while (i < input.Length)
        {
            int bestLength = 0;
            int bestOffset = 0;
            int start = Math.Max(0, i - 8192);
            for (int j = start; j < i; j++)
            {
                int length = 0;
                while (length < 264 && i + length < input.Length && input[j + length] == input[i + length]) length++;
                if (length >= 3 && length > bestLength)
                {
                    bestLength = length;
                    bestOffset = i - j;
                    if (length == 264) break;
                }
            }

            if (bestLength >= 3)
            {
                FlushLiterals(i - literals, literals);
                literals = 0;
                int coded = bestLength - 2;
                output.Add((byte)((Math.Min(7, coded) << 5) | ((bestOffset - 1) >> 8)));
                if (coded >= 7) output.Add((byte)(coded - 7));
                output.Add((byte)((bestOffset - 1) & 0xFF));
                i += bestLength;
            }
            else
            {
                literals++;
                i++;
                if (literals == 32)
                {
                    FlushLiterals(i - literals, literals);
                    literals = 0;
                }
            }
        }

        FlushLiterals(i - literals, literals);
        return [.. output];
    }
}
