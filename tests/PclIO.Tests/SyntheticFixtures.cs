using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;

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

/// <summary>One vertex of a synthetic PLY file: position, colour, intensity and normal.</summary>
public readonly record struct PlySamplePoint(double X, double Y, double Z, PclColor? Color = null, float? Intensity = null, PclVector3? Normal = null, byte Alpha = 255);

/// <summary>One point of a synthetic PTS file: position, intensity, colour and normal.</summary>
public readonly record struct PtsSamplePoint(double X, double Y, double Z, float? Intensity = null, PclColor? Color = null, PclVector3? Normal = null, byte Alpha = 255);

/// <summary>One point of a synthetic PTX file: position, intensity and colour.</summary>
public readonly record struct PtxSamplePoint(double X, double Y, double Z, float? Intensity = null, PclColor? Color = null);

/// <summary>One point of a synthetic delimited text file: position, intensity, colour and normal.</summary>
public readonly record struct XyzSamplePoint(double X, double Y, double Z, float? Intensity = null, PclColor? Color = null, PclVector3? Normal = null, byte Alpha = 255);

/// <summary>One scan in a synthetic PTX file: grid dimensions, scanner pose, matrix, and point data.</summary>
public readonly record struct PtxSampleScan(
    int Columns,
    int Rows,
    PclVector3 ScannerPosition,
    PclVector3 ScannerXAxis,
    PclVector3 ScannerYAxis,
    PclVector3 ScannerZAxis,
    double[] Matrix16,
    IReadOnlyList<PtxSamplePoint> Points,
    int ColumnsPerPoint = 7);

/// <summary>One point of a synthetic 3D Tiles .pnts or glTF file: position, colour, intensity, normal and classification.</summary>
public readonly record struct PntsSamplePoint(
    double X, double Y, double Z,
    PclColor? Color = null,
    float? Intensity = null,
    PclVector3? Normal = null,
    byte? Classification = null);

/// <summary>One point of a synthetic Potree dataset: position, colour, intensity, normal and classification.</summary>
public readonly record struct PotreeSamplePoint(
    double X, double Y, double Z,
    PclColor? Color = null,
    float? Intensity = null,
    PclVector3? Normal = null,
    byte? Classification = null);

/// <summary>One point of a synthetic EPT dataset: position, colour, intensity, normal and classification.</summary>
public readonly record struct EptSamplePoint(
    double X, double Y, double Z,
    PclColor? Color = null,
    float? Intensity = null,
    PclVector3? Normal = null,
    byte? Classification = null);

/// <summary>One point of a synthetic CC BIN file: position, colour and intensity.</summary>
public readonly record struct CcBinSamplePoint(
    double X, double Y, double Z,
    PclColor? Color = null,
    float? Intensity = null);

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

    // ---- LASzip compressed LAS (.laz) ---------------------------------------------------------------------------------

    /// <summary>Creates a minimal valid LASzip compressed LAZ file with 1 point.</summary>
    public static byte[] CreateSyntheticLaz(
        double x = 10.0, double y = 20.0, double z = 30.0,
        float intensity = 0.5f, PclColor? color = null, byte classification = 1,
        ushort compressor = 2, uint chunkSize = 50000,
        bool corruptChunkTable = false, bool truncateData = false,
        uint chunkTableVersion = 0)
    {
        byte[] hdr = new byte[227];
        "LASF"u8.CopyTo(hdr);
        hdr[24] = 1;
        hdr[25] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(94, 2), 227);

        byte[] vlrPayload =
        [
            (byte)(compressor & 0xFF), (byte)(compressor >> 8), 0, 0, 2, 1, 0, 0, 0, 0, 0, 0,
            (byte)(chunkSize & 0xFF), (byte)((chunkSize >> 8) & 0xFF), (byte)((chunkSize >> 16) & 0xFF), (byte)(chunkSize >> 24),
            255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            2, 0, 6, 0, 20, 0, 2, 0, 8, 0, 6, 0, 2, 0
        ];

        uint offsetToPoints = (uint)(227 + 54 + vlrPayload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(96, 4), offsetToPoints);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(100, 4), 1);
        hdr[104] = 130;
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(105, 2), 26);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(107, 4), 1);

        BinaryPrimitives.WriteDoubleLittleEndian(hdr.AsSpan(131, 8), 0.01);
        BinaryPrimitives.WriteDoubleLittleEndian(hdr.AsSpan(139, 8), 0.01);
        BinaryPrimitives.WriteDoubleLittleEndian(hdr.AsSpan(147, 8), 0.01);
        BinaryPrimitives.WriteDoubleLittleEndian(hdr.AsSpan(155, 8), 0.0);
        BinaryPrimitives.WriteDoubleLittleEndian(hdr.AsSpan(163, 8), 0.0);
        BinaryPrimitives.WriteDoubleLittleEndian(hdr.AsSpan(171, 8), 0.0);
        BinaryPrimitives.WriteDoubleLittleEndian(hdr.AsSpan(179, 8), x);
        BinaryPrimitives.WriteDoubleLittleEndian(hdr.AsSpan(187, 8), x);
        BinaryPrimitives.WriteDoubleLittleEndian(hdr.AsSpan(195, 8), y);
        BinaryPrimitives.WriteDoubleLittleEndian(hdr.AsSpan(203, 8), y);
        BinaryPrimitives.WriteDoubleLittleEndian(hdr.AsSpan(211, 8), z);
        BinaryPrimitives.WriteDoubleLittleEndian(hdr.AsSpan(219, 8), z);

        byte[] vlrHdr = new byte[54];
        Encoding.ASCII.GetBytes("laszip encoded").CopyTo(vlrHdr, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(vlrHdr.AsSpan(18, 2), 22204);
        BinaryPrimitives.WriteUInt16LittleEndian(vlrHdr.AsSpan(20, 2), (ushort)vlrPayload.Length);
        Encoding.ASCII.GetBytes("http://laszip.org").CopyTo(vlrHdr, 22);

        long chunkTableOffset = offsetToPoints + 8 + 26;
        byte[] ptPrefix = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(ptPrefix, chunkTableOffset);

        byte[] pt0 = new byte[26];
        int rawX = (int)Math.Round(x / 0.01);
        int rawY = (int)Math.Round(y / 0.01);
        int rawZ = (int)Math.Round(z / 0.01);
        BinaryPrimitives.WriteInt32LittleEndian(pt0.AsSpan(0, 4), rawX);
        BinaryPrimitives.WriteInt32LittleEndian(pt0.AsSpan(4, 4), rawY);
        BinaryPrimitives.WriteInt32LittleEndian(pt0.AsSpan(8, 4), rawZ);
        BinaryPrimitives.WriteUInt16LittleEndian(pt0.AsSpan(12, 2), (ushort)(intensity * 65535));
        pt0[14] = 1;
        pt0[15] = classification;
        color ??= new PclColor(254, 0, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(pt0.AsSpan(20, 2), (ushort)(color.Value.R * 257));
        BinaryPrimitives.WriteUInt16LittleEndian(pt0.AsSpan(22, 2), (ushort)(color.Value.G * 257));
        BinaryPrimitives.WriteUInt16LittleEndian(pt0.AsSpan(24, 2), (ushort)(color.Value.B * 257));

        byte[] chunkTable;
        if (corruptChunkTable)
        {
            byte[] ctHeader = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(ctHeader.AsSpan(0, 4), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(ctHeader.AsSpan(4, 4), 10);
            chunkTable = ctHeader;
        }
        else
        {
            byte[] ctHeader = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(ctHeader.AsSpan(0, 4), chunkTableVersion);
            BinaryPrimitives.WriteUInt32LittleEndian(ctHeader.AsSpan(4, 4), 1);
            byte[] ctStream = [44, 215, 24, 156, 0, 0, 0, 0];
            chunkTable = [.. ctHeader, .. ctStream];
        }

        byte[] full = [.. hdr, .. vlrHdr, .. vlrPayload, .. ptPrefix, .. pt0, .. chunkTable];
        if (truncateData)
            return full[..(full.Length - 10)];
        return full;
    }

    // ---- COPC (.copc.laz) ---------------------------------------------------------------------------------------------

    /// <summary>Creates a minimal valid Cloud Optimized Point Cloud (COPC) file with octree hierarchy and points.</summary>
    public static byte[] CreateSyntheticCopc(
        double x = 10.0, double y = 20.0, double z = 30.0,
        float intensity = 0.5f, PclColor? color = null, byte classification = 1,
        int pointFormat = 2,
        bool corruptHierarchyOffset = false,
        bool cyclicHierarchy = false,
        bool corruptVlrSize = false,
        bool truncateData = false,
        bool nonFiniteCoordinates = false)
    {
        byte[] hdr = new byte[375];
        "LASF"u8.CopyTo(hdr);
        hdr[24] = 1;
        hdr[25] = 4;
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(94, 2), 375);

        ushort vlrDataLen = (ushort)(corruptVlrSize ? 100 : 160);
        uint offsetToPoints = (uint)(375 + 54 + vlrDataLen);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(96, 4), offsetToPoints);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(100, 4), 1);
        hdr[104] = (byte)(pointFormat | 0x80);
        ushort recordLen = (ushort)(pointFormat == 7 ? 36 : 26);
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(105, 2), recordLen);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(107, 4), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(247, 8), 1);

        BinaryPrimitives.WriteDoubleLittleEndian(hdr.AsSpan(131, 8), 0.01);
        BinaryPrimitives.WriteDoubleLittleEndian(hdr.AsSpan(139, 8), 0.01);
        BinaryPrimitives.WriteDoubleLittleEndian(hdr.AsSpan(147, 8), 0.01);
        BinaryPrimitives.WriteDoubleLittleEndian(hdr.AsSpan(155, 8), 0.0);
        BinaryPrimitives.WriteDoubleLittleEndian(hdr.AsSpan(163, 8), 0.0);
        BinaryPrimitives.WriteDoubleLittleEndian(hdr.AsSpan(171, 8), 0.0);
        BinaryPrimitives.WriteDoubleLittleEndian(hdr.AsSpan(179, 8), nonFiniteCoordinates ? double.NaN : x);
        BinaryPrimitives.WriteDoubleLittleEndian(hdr.AsSpan(187, 8), nonFiniteCoordinates ? double.NaN : x);
        BinaryPrimitives.WriteDoubleLittleEndian(hdr.AsSpan(195, 8), nonFiniteCoordinates ? double.NaN : y);
        BinaryPrimitives.WriteDoubleLittleEndian(hdr.AsSpan(203, 8), nonFiniteCoordinates ? double.NaN : y);
        BinaryPrimitives.WriteDoubleLittleEndian(hdr.AsSpan(211, 8), nonFiniteCoordinates ? double.NaN : z);
        BinaryPrimitives.WriteDoubleLittleEndian(hdr.AsSpan(219, 8), nonFiniteCoordinates ? double.NaN : z);

        // VLR header (54 bytes)
        byte[] vlrHdr = new byte[54];
        Encoding.ASCII.GetBytes("copc").CopyTo(vlrHdr, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(vlrHdr.AsSpan(18, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(vlrHdr.AsSpan(20, 2), vlrDataLen);
        Encoding.ASCII.GetBytes("COPC info VLR").CopyTo(vlrHdr, 22);

        // COPC info payload (160 bytes)
        byte[] vlrPayload = new byte[vlrDataLen];
        if (!corruptVlrSize)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(vlrPayload.AsSpan(0, 8), x);
            BinaryPrimitives.WriteDoubleLittleEndian(vlrPayload.AsSpan(8, 8), y);
            BinaryPrimitives.WriteDoubleLittleEndian(vlrPayload.AsSpan(16, 8), z);
            BinaryPrimitives.WriteDoubleLittleEndian(vlrPayload.AsSpan(24, 8), 50.0);
            BinaryPrimitives.WriteDoubleLittleEndian(vlrPayload.AsSpan(32, 8), 1.0);
            ulong hierOffset = corruptHierarchyOffset ? 999999UL : offsetToPoints;
            BinaryPrimitives.WriteUInt64LittleEndian(vlrPayload.AsSpan(40, 8), hierOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(vlrPayload.AsSpan(48, 8), 32);
        }

        // Hierarchy page (32 bytes per entry)
        byte[] hierPage = new byte[32];
        if (cyclicHierarchy)
        {
            BinaryPrimitives.WriteInt32LittleEndian(hierPage.AsSpan(0, 4), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(hierPage.AsSpan(16, 8), offsetToPoints);
            BinaryPrimitives.WriteInt32LittleEndian(hierPage.AsSpan(24, 4), 32);
            BinaryPrimitives.WriteInt32LittleEndian(hierPage.AsSpan(28, 4), -1);
        }
        else
        {
            ulong chunkOffset = offsetToPoints + 32;
            BinaryPrimitives.WriteInt32LittleEndian(hierPage.AsSpan(0, 4), 0);
            BinaryPrimitives.WriteInt32LittleEndian(hierPage.AsSpan(4, 4), 0);
            BinaryPrimitives.WriteInt32LittleEndian(hierPage.AsSpan(8, 4), 0);
            BinaryPrimitives.WriteInt32LittleEndian(hierPage.AsSpan(12, 4), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(hierPage.AsSpan(16, 8), chunkOffset);
            BinaryPrimitives.WriteInt32LittleEndian(hierPage.AsSpan(24, 4), recordLen);
            BinaryPrimitives.WriteInt32LittleEndian(hierPage.AsSpan(28, 4), 1);
        }

        // Point chunk (recordLen bytes)
        byte[] pt0 = new byte[recordLen];
        int rawX = (int)Math.Round(x / 0.01);
        int rawY = (int)Math.Round(y / 0.01);
        int rawZ = (int)Math.Round(z / 0.01);
        BinaryPrimitives.WriteInt32LittleEndian(pt0.AsSpan(0, 4), rawX);
        BinaryPrimitives.WriteInt32LittleEndian(pt0.AsSpan(4, 4), rawY);
        BinaryPrimitives.WriteInt32LittleEndian(pt0.AsSpan(8, 4), rawZ);
        BinaryPrimitives.WriteUInt16LittleEndian(pt0.AsSpan(12, 2), (ushort)(intensity * 65535));

        color ??= new PclColor(200, 100, 50);
        if (pointFormat == 7)
        {
            pt0[16] = classification;
            BinaryPrimitives.WriteUInt16LittleEndian(pt0.AsSpan(30, 2), (ushort)(color.Value.R * 257));
            BinaryPrimitives.WriteUInt16LittleEndian(pt0.AsSpan(32, 2), (ushort)(color.Value.G * 257));
            BinaryPrimitives.WriteUInt16LittleEndian(pt0.AsSpan(34, 2), (ushort)(color.Value.B * 257));
        }
        else
        {
            pt0[14] = 1;
            pt0[15] = classification;
            BinaryPrimitives.WriteUInt16LittleEndian(pt0.AsSpan(20, 2), (ushort)(color.Value.R * 257));
            BinaryPrimitives.WriteUInt16LittleEndian(pt0.AsSpan(22, 2), (ushort)(color.Value.G * 257));
            BinaryPrimitives.WriteUInt16LittleEndian(pt0.AsSpan(24, 2), (ushort)(color.Value.B * 257));
        }

        byte[] full = [.. hdr, .. vlrHdr, .. vlrPayload, .. hierPage, .. pt0];
        if (truncateData)
            return full[..(full.Length - 10)];
        return full;
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
        // Packed colour travels as the float holding its bits; other shapes hold no colour.
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

    // ---- PLY (.ply) ---------------------------------------------------------------------------------------------------

    /// <summary>Default PLY vertex properties: positions, 8-bit colour, intensity and normals.</summary>
    public static IReadOnlyList<(string Name, string Type)> DefaultPlyProperties =>
    [
        ("x", "float"), ("y", "float"), ("z", "float"),
        ("red", "uchar"), ("green", "uchar"), ("blue", "uchar"),
        ("intensity", "float"), ("nx", "float"), ("ny", "float"), ("nz", "float"),
    ];

    /// <summary>Default PLY vertices: three points with mixed channels.</summary>
    public static IReadOnlyList<PlySamplePoint> DefaultPlyPoints =>
    [
        new(1, 2, 3, new PclColor(10, 20, 30), 0.25f, new PclVector3(0, 0, 1)),
        new(-4.5, 5.25, 6.125, null, 0.5f),
        new(7, 8, 9, new PclColor(255, 255, 255)),
    ];

    /// <summary>A synthetic PLY file with one vertex element.</summary>
    /// <param name="points">Vertices to store.</param>
    /// <param name="properties">Vertex properties; null uses <see cref="DefaultPlyProperties"/>.</param>
    /// <param name="format">Storage: ascii, binary_little_endian or binary_big_endian.</param>
    /// <param name="withFaces">True appends a face element with one triangle after the vertices.</param>
    public static byte[] CreateSyntheticPly(IReadOnlyList<PlySamplePoint>? points = null,
        IReadOnlyList<(string Name, string Type)>? properties = null, string format = "ascii", bool withFaces = false)
    {
        points ??= DefaultPlyPoints;
        properties ??= DefaultPlyProperties;

        var header = new List<string>
        {
            "ply",
            $"format {format} 1.0",
            "comment Created by PclIO tests",
            $"element vertex {points.Count}",
        };
        header.AddRange(properties.Select(p => $"property {p.Type} {p.Name}"));
        if (withFaces)
        {
            header.Add("element face 1");
            header.Add("property list uchar int vertex_indices");
        }

        header.Add("end_header");

        byte[] body = format switch
        {
            "ascii" => PlyAsciiBody(points, properties),
            "binary_little_endian" => PlyBinaryBody(points, properties, littleEndian: true, withFaces),
            "binary_big_endian" => PlyBinaryBody(points, properties, littleEndian: false, withFaces),
            _ => throw new ArgumentException($"Unknown PLY format '{format}'.", nameof(format)),
        };

        byte[] headerBytes = System.Text.Encoding.ASCII.GetBytes(string.Join('\n', header) + "\n");
        return [.. headerBytes, .. body];
    }

    /// <summary>A PLY file from raw header lines and a body.</summary>
    public static byte[] CreatePlyFile(IEnumerable<string> headerLines, byte[] body)
    {
        byte[] headerBytes = System.Text.Encoding.ASCII.GetBytes(string.Join('\n', headerLines) + "\n");
        return [.. headerBytes, .. body];
    }

    private static int PlyTypeSize(string type) => type switch
    {
        "char" or "int8" or "uchar" or "uint8" => 1,
        "short" or "int16" or "ushort" or "uint16" => 2,
        "int" or "int32" or "uint" or "uint32" or "float" or "float32" => 4,
        "double" or "float64" => 8,
        _ => 0,
    };

    private static byte[] PlyAsciiBody(IReadOnlyList<PlySamplePoint> points,
        IReadOnlyList<(string Name, string Type)> properties)
    {
        var sb = new StringBuilder();
        foreach (var p in points)
        {
            sb.Append(string.Join(' ', properties.Select(prop => PlyAsciiValue(p, prop))));
            sb.Append('\n');
        }

        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string PlyAsciiValue(PlySamplePoint p, (string Name, string Type) property)
    {
        double value = PlyPropertyValue(p, property.Name);
        if (property.Type is "float" or "float32" or "double" or "float64")
            return double.IsNaN(value) ? "nan" : value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        return ((long)value).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] PlyBinaryBody(IReadOnlyList<PlySamplePoint> points,
        IReadOnlyList<(string Name, string Type)> properties, bool littleEndian, bool withFaces)
    {
        using var ms = new MemoryStream();
        foreach (var p in points)
            foreach (var prop in properties)
                ms.Write(PlyPropertyBytes(p, prop, littleEndian));

        if (withFaces)
        {
            // One triangle over the first three vertices.
            ms.WriteByte(3);
            foreach (int index in new[] { 0, 1, 2 })
            {
                byte[] indexBytes = new byte[4];
                if (littleEndian)
                    BinaryPrimitives.WriteInt32LittleEndian(indexBytes, index);
                else
                    BinaryPrimitives.WriteInt32BigEndian(indexBytes, index);
                ms.Write(indexBytes);
            }
        }

        return ms.ToArray();
    }

    private static double PlyPropertyValue(PlySamplePoint p, string name) => name switch
    {
        "x" => p.X,
        "y" => p.Y,
        "z" => p.Z,
        "red" => p.Color?.R ?? 255,
        "green" => p.Color?.G ?? 255,
        "blue" => p.Color?.B ?? 255,
        "alpha" => p.Alpha,
        "intensity" => p.Intensity ?? 0,
        "nx" => p.Normal?.X ?? 0,
        "ny" => p.Normal?.Y ?? 0,
        "nz" => p.Normal?.Z ?? 0,
        _ => 0,
    };

    private static byte[] PlyPropertyBytes(PlySamplePoint p, (string Name, string Type) property, bool littleEndian)
    {
        double value = PlyPropertyValue(p, property.Name);
        int size = PlyTypeSize(property.Type);
        byte[] bytes = new byte[size];
        bool signed = property.Type is "char" or "int8" or "short" or "int16" or "int" or "int32";
        if (property.Type is "float" or "float32")
        {
            if (littleEndian)
                BinaryPrimitives.WriteSingleLittleEndian(bytes, (float)value);
            else
                BinaryPrimitives.WriteSingleBigEndian(bytes, (float)value);
        }
        else if (property.Type is "double" or "float64")
        {
            if (littleEndian)
                BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
            else
                BinaryPrimitives.WriteDoubleBigEndian(bytes, value);
        }
        else if (signed)
        {
            long signedValue = (long)value;
            for (int i = 0; i < size; i++)
                bytes[littleEndian ? i : size - 1 - i] = (byte)(signedValue >> (8 * i));
        }
        else
        {
            ulong unsigned = (ulong)(long)value;
            for (int i = 0; i < size; i++)
                bytes[littleEndian ? i : size - 1 - i] = (byte)(unsigned >> (8 * i));
        }

        return bytes;
    }

    public static IReadOnlyList<PtsSamplePoint> DefaultPtsPoints =>
    [
        new(1, 2, 3, 511f, new PclColor(10, 20, 30), new PclVector3(0, 0, 1)),
        new(-4.5, 5.25, 6.125, 1023f, new PclColor(255, 255, 255), new PclVector3(0, 0, 0)),
        new(7, 8, 9, 0f, new PclColor(255, 255, 255), new PclVector3(0, 0, 0)),
    ];

    /// <summary>A synthetic PTS ASCII file.</summary>
    public static byte[] CreateSyntheticPts(
        IReadOnlyList<PtsSamplePoint>? points = null,
        bool withCountHeader = true,
        long? declaredCount = null,
        int columns = 7,
        string? customHeader = null)
    {
        points ??= DefaultPtsPoints;
        long count = declaredCount ?? points.Count;
        var sb = new StringBuilder();
        if (customHeader != null)
        {
            sb.Append(customHeader);
        }
        else if (withCountHeader)
        {
            sb.Append(count.ToString(CultureInfo.InvariantCulture));
            sb.Append('\n');
        }

        foreach (var pt in points)
        {
            string line = columns switch
            {
                3 => string.Create(CultureInfo.InvariantCulture, $"{pt.X:F4} {pt.Y:F4} {pt.Z:F4}"),
                4 => string.Create(CultureInfo.InvariantCulture, $"{pt.X:F4} {pt.Y:F4} {pt.Z:F4} {pt.Intensity ?? 0f:F1}"),
                6 when pt.Normal is { } n && (n.X < 0 || n.Y < 0 || n.Z < 0) =>
                    string.Create(CultureInfo.InvariantCulture, $"{pt.X:F4} {pt.Y:F4} {pt.Z:F4} {n.X:F4} {n.Y:F4} {n.Z:F4}"),
                6 => string.Create(CultureInfo.InvariantCulture, $"{pt.X:F4} {pt.Y:F4} {pt.Z:F4} {pt.Color?.R ?? 255} {pt.Color?.G ?? 255} {pt.Color?.B ?? 255}"),
                7 when pt.Normal is { } n && (n.X < 0 || n.Y < 0 || n.Z < 0) =>
                    string.Create(CultureInfo.InvariantCulture, $"{pt.X:F4} {pt.Y:F4} {pt.Z:F4} {pt.Intensity ?? 0f:F1} {n.X:F4} {n.Y:F4} {n.Z:F4}"),
                7 => string.Create(CultureInfo.InvariantCulture, $"{pt.X:F4} {pt.Y:F4} {pt.Z:F4} {pt.Intensity ?? 0f:F1} {pt.Color?.R ?? 255} {pt.Color?.G ?? 255} {pt.Color?.B ?? 255}"),
                8 => string.Create(CultureInfo.InvariantCulture, $"{pt.X:F4} {pt.Y:F4} {pt.Z:F4} {pt.Intensity ?? 0f:F1} {pt.Color?.R ?? 255} {pt.Color?.G ?? 255} {pt.Color?.B ?? 255} {pt.Alpha}"),
                9 => string.Create(CultureInfo.InvariantCulture, $"{pt.X:F4} {pt.Y:F4} {pt.Z:F4} {pt.Color?.R ?? 255} {pt.Color?.G ?? 255} {pt.Color?.B ?? 255} {pt.Normal?.X ?? 0:F4} {pt.Normal?.Y ?? 0:F4} {pt.Normal?.Z ?? 1:F4}"),
                10 => string.Create(CultureInfo.InvariantCulture, $"{pt.X:F4} {pt.Y:F4} {pt.Z:F4} {pt.Intensity ?? 0f:F1} {pt.Color?.R ?? 255} {pt.Color?.G ?? 255} {pt.Color?.B ?? 255} {pt.Normal?.X ?? 0:F4} {pt.Normal?.Y ?? 0:F4} {pt.Normal?.Z ?? 1:F4}"),
                _ => throw new ArgumentException($"Unsupported column count {columns}", nameof(columns)),
            };
            sb.Append(line);
            sb.Append('\n');
        }

        return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static IReadOnlyList<PtxSamplePoint> DefaultPtxPoints =>
    [
        new(1, 2, 3, 0.5f, new PclColor(10, 20, 30)),
        new(-4.5, 5.25, 6.125, 1.0f, new PclColor(255, 255, 255)),
        new(7, 8, 9, 0f, new PclColor(255, 255, 255)),
    ];

    public static byte[] CreateSyntheticPtx(
        IReadOnlyList<PtxSamplePoint>? points = null,
        int? cols = null,
        int? rows = null,
        int columnsPerPoint = 7,
        PclVector3? scannerPos = null,
        PclVector3? scannerX = null,
        PclVector3? scannerY = null,
        PclVector3? scannerZ = null,
        double[]? matrix = null,
        string? customHeader = null)
    {
        points ??= DefaultPtxPoints;
        int c = cols ?? points.Count;
        int r = rows ?? 1;
        var pos = scannerPos ?? PclVector3.Zero;
        var ax = scannerX ?? new PclVector3(1, 0, 0);
        var ay = scannerY ?? new PclVector3(0, 1, 0);
        var az = scannerZ ?? new PclVector3(0, 0, 1);
        var m = matrix ?? [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

        var scan = new PtxSampleScan(c, r, pos, ax, ay, az, m, points, columnsPerPoint);
        return CreateSyntheticMultiScanPtx([scan], customHeader);
    }

    public static byte[] CreateSyntheticMultiScanPtx(
        IReadOnlyList<PtxSampleScan> scans,
        string? customFirstHeader = null)
    {
        var sb = new StringBuilder();

        for (int s = 0; s < scans.Count; s++)
        {
            var scan = scans[s];

            if (s == 0 && customFirstHeader != null)
            {
                sb.Append(customFirstHeader);
            }
            else
            {
                sb.Append(scan.Columns.ToString(CultureInfo.InvariantCulture)).Append('\n');
                sb.Append(scan.Rows.ToString(CultureInfo.InvariantCulture)).Append('\n');
                sb.Append(string.Create(CultureInfo.InvariantCulture, $"{scan.ScannerPosition.X:F6} {scan.ScannerPosition.Y:F6} {scan.ScannerPosition.Z:F6}")).Append('\n');
                sb.Append(string.Create(CultureInfo.InvariantCulture, $"{scan.ScannerXAxis.X:F6} {scan.ScannerXAxis.Y:F6} {scan.ScannerXAxis.Z:F6}")).Append('\n');
                sb.Append(string.Create(CultureInfo.InvariantCulture, $"{scan.ScannerYAxis.X:F6} {scan.ScannerYAxis.Y:F6} {scan.ScannerYAxis.Z:F6}")).Append('\n');
                sb.Append(string.Create(CultureInfo.InvariantCulture, $"{scan.ScannerZAxis.X:F6} {scan.ScannerZAxis.Y:F6} {scan.ScannerZAxis.Z:F6}")).Append('\n');
                var m = scan.Matrix16;
                sb.Append(string.Create(CultureInfo.InvariantCulture, $"{m[0]:G} {m[1]:G} {m[2]:G} {m[3]:G}")).Append('\n');
                sb.Append(string.Create(CultureInfo.InvariantCulture, $"{m[4]:G} {m[5]:G} {m[6]:G} {m[7]:G}")).Append('\n');
                sb.Append(string.Create(CultureInfo.InvariantCulture, $"{m[8]:G} {m[9]:G} {m[10]:G} {m[11]:G}")).Append('\n');
                sb.Append(string.Create(CultureInfo.InvariantCulture, $"{m[12]:G} {m[13]:G} {m[14]:G} {m[15]:G}")).Append('\n');
            }

            foreach (var pt in scan.Points)
            {
                string line = scan.ColumnsPerPoint switch
                {
                    3 => string.Create(CultureInfo.InvariantCulture, $"{pt.X:F4} {pt.Y:F4} {pt.Z:F4}"),
                    4 => string.Create(CultureInfo.InvariantCulture, $"{pt.X:F4} {pt.Y:F4} {pt.Z:F4} {pt.Intensity ?? 0f:F4}"),
                    6 => string.Create(CultureInfo.InvariantCulture, $"{pt.X:F4} {pt.Y:F4} {pt.Z:F4} {pt.Color?.R ?? 255} {pt.Color?.G ?? 255} {pt.Color?.B ?? 255}"),
                    7 => string.Create(CultureInfo.InvariantCulture, $"{pt.X:F4} {pt.Y:F4} {pt.Z:F4} {pt.Intensity ?? 0f:F4} {pt.Color?.R ?? 255} {pt.Color?.G ?? 255} {pt.Color?.B ?? 255}"),
                    _ => throw new ArgumentException($"Unsupported column count {scan.ColumnsPerPoint}", nameof(scans)),
                };
                sb.Append(line).Append('\n');
            }
        }

        return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static IReadOnlyList<XyzSamplePoint> DefaultXyzPoints =>
    [
        new(1, 2, 3, 511f, new PclColor(10, 20, 30), new PclVector3(0, 0, 1)),
        new(-4.5, 5.25, 6.125, 1023f, new PclColor(255, 255, 255), new PclVector3(0, 0, -1)),
        new(7, 8, 9, 0f, new PclColor(255, 255, 255), new PclVector3(0, 1, 0)),
    ];

    /// <summary>A synthetic delimited text point cloud file (.xyz, .asc, .txt, .csv).</summary>
    public static byte[] CreateSyntheticXyz(
        IReadOnlyList<XyzSamplePoint>? points = null,
        char delimiter = ' ',
        string? header = null,
        int columns = 3,
        IReadOnlyList<string>? comments = null,
        string commentPrefix = "# ")
    {
        points ??= DefaultXyzPoints;
        var sb = new StringBuilder();

        if (comments != null)
        {
            foreach (var comment in comments)
            {
                sb.Append(commentPrefix).Append(comment).Append('\n');
            }
        }

        if (header != null)
        {
            sb.Append(header).Append('\n');
        }

        string d = delimiter.ToString();

        foreach (var pt in points)
        {
            string line = columns switch
            {
                3 => string.Create(CultureInfo.InvariantCulture, $"{pt.X:F4}{d}{pt.Y:F4}{d}{pt.Z:F4}"),
                4 => string.Create(CultureInfo.InvariantCulture, $"{pt.X:F4}{d}{pt.Y:F4}{d}{pt.Z:F4}{d}{pt.Intensity ?? 0f:F1}"),
                6 when pt.Normal is { } n && (n.X < 0 || n.Y < 0 || n.Z < 0) =>
                    string.Create(CultureInfo.InvariantCulture, $"{pt.X:F4}{d}{pt.Y:F4}{d}{pt.Z:F4}{d}{n.X:F4}{d}{n.Y:F4}{d}{n.Z:F4}"),
                6 => string.Create(CultureInfo.InvariantCulture, $"{pt.X:F4}{d}{pt.Y:F4}{d}{pt.Z:F4}{d}{pt.Color?.R ?? 255}{d}{pt.Color?.G ?? 255}{d}{pt.Color?.B ?? 255}"),
                7 when pt.Normal is { } n && (n.X < 0 || n.Y < 0 || n.Z < 0) =>
                    string.Create(CultureInfo.InvariantCulture, $"{pt.X:F4}{d}{pt.Y:F4}{d}{pt.Z:F4}{d}{pt.Intensity ?? 0f:F1}{d}{n.X:F4}{d}{n.Y:F4}{d}{n.Z:F4}"),
                7 => string.Create(CultureInfo.InvariantCulture, $"{pt.X:F4}{d}{pt.Y:F4}{d}{pt.Z:F4}{d}{pt.Intensity ?? 0f:F1}{d}{pt.Color?.R ?? 255}{d}{pt.Color?.G ?? 255}{d}{pt.Color?.B ?? 255}"),
                8 => string.Create(CultureInfo.InvariantCulture, $"{pt.X:F4}{d}{pt.Y:F4}{d}{pt.Z:F4}{d}{pt.Intensity ?? 0f:F1}{d}{pt.Color?.R ?? 255}{d}{pt.Color?.G ?? 255}{d}{pt.Color?.B ?? 255}{d}{pt.Alpha}"),
                9 => string.Create(CultureInfo.InvariantCulture, $"{pt.X:F4}{d}{pt.Y:F4}{d}{pt.Z:F4}{d}{pt.Color?.R ?? 255}{d}{pt.Color?.G ?? 255}{d}{pt.Color?.B ?? 255}{d}{pt.Normal?.X ?? 0:F4}{d}{pt.Normal?.Y ?? 0:F4}{d}{pt.Normal?.Z ?? 1:F4}"),
                10 => string.Create(CultureInfo.InvariantCulture, $"{pt.X:F4}{d}{pt.Y:F4}{d}{pt.Z:F4}{d}{pt.Intensity ?? 0f:F1}{d}{pt.Color?.R ?? 255}{d}{pt.Color?.G ?? 255}{d}{pt.Color?.B ?? 255}{d}{pt.Normal?.X ?? 0:F4}{d}{pt.Normal?.Y ?? 0:F4}{d}{pt.Normal?.Z ?? 1:F4}"),
                _ => throw new ArgumentException($"Unsupported column count {columns}", nameof(columns)),
            };
            sb.Append(line).Append('\n');
        }

        return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
    }

    public readonly record struct SbfSamplePoint(
        double X,
        double Y,
        double Z,
        float[]? ScalarFields = null);

    public static IReadOnlyList<SbfSamplePoint> DefaultSbfPoints =>
    [
        new(1.0, 2.0, 3.0, [100.0f]),
        new(-4.5, 5.25, 6.125, [250.0f]),
        new(7.0, 8.0, 9.0, [50.0f]),
    ];

    /// <summary>Creates a synthetic SBF file pair: a companion header text file (.sbf) and binary data (.sbf.data).</summary>
    public static (byte[] HeaderBytes, byte[] DataBytes) CreateSyntheticSbf(
        IReadOnlyList<SbfSamplePoint>? points = null,
        IReadOnlyList<string>? sfNames = null,
        PclVector3? globalShift = null,
        double globalScale = 1.0,
        bool isBigEndian = true,
        string? customHeaderText = null)
    {
        points ??= DefaultSbfPoints;
        sfNames ??= ["Intensity"];
        var shift = globalShift ?? PclVector3.Zero;

        // Build header text
        string headerStr = customHeaderText ?? string.Create(CultureInfo.InvariantCulture, $"""
            [SBF]
            Points={points.Count}
            GlobalShift={shift.X:F6}, {shift.Y:F6}, {shift.Z:F6}
            GlobalScale={globalScale:F6}
            SFCount={sfNames.Count}
            {string.Join("\n", sfNames.Select((name, idx) => $"SF{idx + 1}={name}"))}
            """);
        byte[] headerBytes = System.Text.Encoding.UTF8.GetBytes(headerStr);

        // Build binary data (64-byte header + point records)
        int sfCount = sfNames.Count;
        int recSize = 12 + 4 * sfCount;
        byte[] dataBytes = new byte[64 + points.Count * recSize];

        dataBytes[0] = 0x2A;
        dataBytes[1] = 0x2A;

        if (isBigEndian)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(dataBytes.AsSpan(2, 8), (ulong)points.Count);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(dataBytes.AsSpan(10, 2), (ushort)sfCount);
        }
        else
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(dataBytes.AsSpan(2, 8), (ulong)points.Count);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(dataBytes.AsSpan(10, 2), (ushort)sfCount);
        }

        for (int i = 0; i < points.Count; i++)
        {
            int offset = 64 + i * recSize;
            var pt = points[i];

            if (isBigEndian)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(dataBytes.AsSpan(offset, 4), (float)pt.X);
                System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(dataBytes.AsSpan(offset + 4, 4), (float)pt.Y);
                System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(dataBytes.AsSpan(offset + 8, 4), (float)pt.Z);
            }
            else
            {
                System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(dataBytes.AsSpan(offset, 4), (float)pt.X);
                System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(dataBytes.AsSpan(offset + 4, 4), (float)pt.Y);
                System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(dataBytes.AsSpan(offset + 8, 4), (float)pt.Z);
            }

            for (int s = 0; s < sfCount; s++)
            {
                float sfVal = (pt.ScalarFields != null && s < pt.ScalarFields.Length) ? pt.ScalarFields[s] : 0f;
                int sfOffset = offset + 12 + s * 4;
                if (isBigEndian)
                    System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(dataBytes.AsSpan(sfOffset, 4), sfVal);
                else
                    System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(dataBytes.AsSpan(sfOffset, 4), sfVal);
            }
        }

        return (headerBytes, dataBytes);
    }

    // ---- 3D Tiles (.pnts, .glb, tileset.json) -------------------------------------------------------------------

    /// <summary>Encodes a 3D unit normal vector using octahedral oct16p encoding.</summary>
    public static (byte b0, byte b1) EncodeOct16p(double nx, double ny, double nz)
    {
        double l1 = Math.Abs(nx) + Math.Abs(ny) + Math.Abs(nz);
        if (l1 > 0.0)
        {
            nx /= l1;
            ny /= l1;
        }
        if (nz < 0.0)
        {
            double oldX = nx;
            double oldY = ny;
            nx = (1.0 - Math.Abs(oldY)) * (oldX >= 0.0 ? 1.0 : -1.0);
            ny = (1.0 - Math.Abs(oldX)) * (oldY >= 0.0 ? 1.0 : -1.0);
        }
        byte bx = (byte)Math.Clamp((int)Math.Round(((nx + 1.0) * 0.5) * 255.0), 0, 255);
        byte by = (byte)Math.Clamp((int)Math.Round(((ny + 1.0) * 0.5) * 255.0), 0, 255);
        return (bx, by);
    }

    /// <summary>Generates a synthetic .pnts file adhering strictly to the OGC 3D Tiles specification.</summary>
    public static byte[] CreateSyntheticPnts(
        IReadOnlyList<PntsSamplePoint> points,
        bool isQuantized = false,
        PclVector3? qOffset = null,
        PclVector3? qScale = null,
        PclVector3? rtcCenter = null,
        string? colorFormat = null,
        PclColor? constantColor = null,
        string? normalFormat = null,
        bool hasIntensity = false,
        bool intensityBinary = true,
        bool hasClassification = false)
    {
        qOffset ??= PclVector3.Zero;
        qScale ??= new PclVector3(100.0, 100.0, 100.0);

        // Auto-detect formats if not specified
        if (colorFormat == null && points.Count > 0 && points.Any(p => p.Color.HasValue))
            colorFormat = "RGB";

        if (normalFormat == null && points.Count > 0 && points.Any(p => p.Normal.HasValue))
            normalFormat = "NORMAL";

        // 1. Calculate Feature Table binary layout
        int ftBinLen = 0;
        int posOffset = 0;
        if (isQuantized)
            ftBinLen += points.Count * 6;
        else
            ftBinLen += points.Count * 12;

        int colOffset = ftBinLen;
        if (colorFormat == "RGB") ftBinLen += points.Count * 3;
        else if (colorFormat == "RGBA") ftBinLen += points.Count * 4;
        else if (colorFormat == "RGB565") ftBinLen += points.Count * 2;

        int normOffset = ftBinLen;
        if (normalFormat == "NORMAL") ftBinLen += points.Count * 12;
        else if (normalFormat == "NORMAL_OCT16P") ftBinLen += points.Count * 2;

        int ftBinPadded = (ftBinLen + 7) & ~7;
        byte[] ftBinBytes = new byte[ftBinPadded];

        // Fill Feature Table binary data
        for (int i = 0; i < points.Count; i++)
        {
            var pt = points[i];
            if (isQuantized)
            {
                int off = posOffset + i * 6;
                ushort qx = (ushort)Math.Clamp((int)Math.Round(((pt.X - qOffset.Value.X) / qScale.Value.X) * 65535.0), 0, 65535);
                ushort qy = (ushort)Math.Clamp((int)Math.Round(((pt.Y - qOffset.Value.Y) / qScale.Value.Y) * 65535.0), 0, 65535);
                ushort qz = (ushort)Math.Clamp((int)Math.Round(((pt.Z - qOffset.Value.Z) / qScale.Value.Z) * 65535.0), 0, 65535);
                BinaryPrimitives.WriteUInt16LittleEndian(ftBinBytes.AsSpan(off, 2), qx);
                BinaryPrimitives.WriteUInt16LittleEndian(ftBinBytes.AsSpan(off + 2, 2), qy);
                BinaryPrimitives.WriteUInt16LittleEndian(ftBinBytes.AsSpan(off + 4, 2), qz);
            }
            else
            {
                int off = posOffset + i * 12;
                BinaryPrimitives.WriteSingleLittleEndian(ftBinBytes.AsSpan(off, 4), (float)pt.X);
                BinaryPrimitives.WriteSingleLittleEndian(ftBinBytes.AsSpan(off + 4, 4), (float)pt.Y);
                BinaryPrimitives.WriteSingleLittleEndian(ftBinBytes.AsSpan(off + 8, 4), (float)pt.Z);
            }

            var c = pt.Color ?? new PclColor(255, 255, 255, 255);
            if (colorFormat == "RGB")
            {
                int off = colOffset + i * 3;
                ftBinBytes[off] = c.R;
                ftBinBytes[off + 1] = c.G;
                ftBinBytes[off + 2] = c.B;
            }
            else if (colorFormat == "RGBA")
            {
                int off = colOffset + i * 4;
                ftBinBytes[off] = c.R;
                ftBinBytes[off + 1] = c.G;
                ftBinBytes[off + 2] = c.B;
                ftBinBytes[off + 3] = c.A;
            }
            else if (colorFormat == "RGB565")
            {
                int off = colOffset + i * 2;
                ushort val = (ushort)(((c.R >> 3) << 11) | ((c.G >> 2) << 5) | (c.B >> 3));
                BinaryPrimitives.WriteUInt16LittleEndian(ftBinBytes.AsSpan(off, 2), val);
            }

            if (normalFormat == "NORMAL")
            {
                int off = normOffset + i * 12;
                var n = pt.Normal ?? new PclVector3(0, 0, 1);
                BinaryPrimitives.WriteSingleLittleEndian(ftBinBytes.AsSpan(off, 4), (float)n.X);
                BinaryPrimitives.WriteSingleLittleEndian(ftBinBytes.AsSpan(off + 4, 4), (float)n.Y);
                BinaryPrimitives.WriteSingleLittleEndian(ftBinBytes.AsSpan(off + 8, 4), (float)n.Z);
            }
            else if (normalFormat == "NORMAL_OCT16P")
            {
                int off = normOffset + i * 2;
                var n = pt.Normal ?? new PclVector3(0, 0, 1);
                var (b0, b1) = EncodeOct16p(n.X, n.Y, n.Z);
                ftBinBytes[off] = b0;
                ftBinBytes[off + 1] = b1;
            }
        }

        // 2. Build Feature Table JSON
        var ftDict = new Dictionary<string, object>
        {
            ["POINTS_LENGTH"] = points.Count
        };

        if (isQuantized)
        {
            ftDict["POSITION_QUANTIZED"] = new Dictionary<string, int> { ["byteOffset"] = posOffset };
            ftDict["QUANTIZED_VOLUME_OFFSET"] = new[] { qOffset.Value.X, qOffset.Value.Y, qOffset.Value.Z };
            ftDict["QUANTIZED_VOLUME_SCALE"] = new[] { qScale.Value.X, qScale.Value.Y, qScale.Value.Z };
        }
        else
        {
            ftDict["POSITION"] = new Dictionary<string, int> { ["byteOffset"] = posOffset };
        }

        if (rtcCenter.HasValue)
        {
            ftDict["RTC_CENTER"] = new[] { rtcCenter.Value.X, rtcCenter.Value.Y, rtcCenter.Value.Z };
        }

        if (colorFormat == "RGB") ftDict["RGB"] = new Dictionary<string, int> { ["byteOffset"] = colOffset };
        else if (colorFormat == "RGBA") ftDict["RGBA"] = new Dictionary<string, int> { ["byteOffset"] = colOffset };
        else if (colorFormat == "RGB565") ftDict["RGB565"] = new Dictionary<string, int> { ["byteOffset"] = colOffset };
        else if (colorFormat == "CONSTANT_RGBA")
        {
            var cc = constantColor ?? new PclColor(200, 100, 50, 255);
            ftDict["CONSTANT_RGBA"] = new int[] { cc.R, cc.G, cc.B, cc.A };
        }

        if (normalFormat == "NORMAL") ftDict["NORMAL"] = new Dictionary<string, int> { ["byteOffset"] = normOffset };
        else if (normalFormat == "NORMAL_OCT16P") ftDict["NORMAL_OCT16P"] = new Dictionary<string, int> { ["byteOffset"] = normOffset };

        string ftJsonRaw = JsonSerializer.Serialize(ftDict);
        byte[] ftJsonRawBytes = Encoding.UTF8.GetBytes(ftJsonRaw);
        int ftJsonPadded = (ftJsonRawBytes.Length + 7) & ~7;
        byte[] ftJsonBytes = new byte[ftJsonPadded];
        Array.Fill(ftJsonBytes, (byte)0x20); // Pad with spaces
        ftJsonRawBytes.CopyTo(ftJsonBytes, 0);

        // 3. Build Batch Table binary and JSON
        int btBinLen = 0;
        int intOffset = 0;
        int classOffset = 0;
        if (hasIntensity && intensityBinary)
        {
            intOffset = btBinLen;
            btBinLen += points.Count * 4;
        }
        if (hasClassification)
        {
            classOffset = btBinLen;
            btBinLen += points.Count * 1;
        }
        int btBinPadded = (btBinLen + 7) & ~7;
        byte[] btBinBytes = new byte[btBinPadded];

        if (hasIntensity && intensityBinary)
        {
            for (int i = 0; i < points.Count; i++)
            {
                float val = points[i].Intensity ?? 1.0f;
                BinaryPrimitives.WriteSingleLittleEndian(btBinBytes.AsSpan(intOffset + i * 4, 4), val);
            }
        }
        if (hasClassification)
        {
            for (int i = 0; i < points.Count; i++)
            {
                byte cls = points[i].Classification ?? 2;
                btBinBytes[classOffset + i] = cls;
            }
        }

        var btDict = new Dictionary<string, object>();
        if (hasIntensity)
        {
            if (intensityBinary)
            {
                btDict["Intensity"] = new Dictionary<string, object>
                {
                    ["byteOffset"] = intOffset,
                    ["componentType"] = "FLOAT",
                    ["type"] = "SCALAR"
                };
            }
            else
            {
                btDict["Intensity"] = points.Select(p => p.Intensity ?? 1.0f).ToArray();
            }
        }
        if (hasClassification)
        {
            btDict["Classification"] = new Dictionary<string, object>
            {
                ["byteOffset"] = classOffset,
                ["componentType"] = "UNSIGNED_BYTE",
                ["type"] = "SCALAR"
            };
        }

        byte[] btJsonBytes;
        if (btDict.Count > 0)
        {
            string btJsonRaw = JsonSerializer.Serialize(btDict);
            byte[] btJsonRawBytes = Encoding.UTF8.GetBytes(btJsonRaw);
            int btJsonPadded = (btJsonRawBytes.Length + 7) & ~7;
            btJsonBytes = new byte[btJsonPadded];
            Array.Fill(btJsonBytes, (byte)0x20);
            btJsonRawBytes.CopyTo(btJsonBytes, 0);
        }
        else
        {
            btJsonBytes = Array.Empty<byte>();
        }

        // 4. Assemble final .pnts byte array
        uint totalLength = (uint)(28 + ftJsonBytes.Length + ftBinBytes.Length + btJsonBytes.Length + btBinBytes.Length);
        byte[] pntsData = new byte[totalLength];

        // Header (28 bytes)
        pntsData[0] = 0x70; pntsData[1] = 0x6E; pntsData[2] = 0x74; pntsData[3] = 0x73; // "pnts"
        BinaryPrimitives.WriteUInt32LittleEndian(pntsData.AsSpan(4, 4), 1); // version
        BinaryPrimitives.WriteUInt32LittleEndian(pntsData.AsSpan(8, 4), totalLength);
        BinaryPrimitives.WriteUInt32LittleEndian(pntsData.AsSpan(12, 4), (uint)ftJsonBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(pntsData.AsSpan(16, 4), (uint)ftBinBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(pntsData.AsSpan(20, 4), (uint)btJsonBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(pntsData.AsSpan(24, 4), (uint)btBinBytes.Length);

        int pos = 28;
        ftJsonBytes.CopyTo(pntsData, pos); pos += ftJsonBytes.Length;
        ftBinBytes.CopyTo(pntsData, pos); pos += ftBinBytes.Length;
        btJsonBytes.CopyTo(pntsData, pos); pos += btJsonBytes.Length;
        btBinBytes.CopyTo(pntsData, pos);

        return pntsData;
    }

    /// <summary>Generates a synthetic glTF 2.0 binary point cloud (.glb) with mode 0 (POINTS).</summary>
    public static byte[] CreateSyntheticGlbPoints(
        IReadOnlyList<PntsSamplePoint> points,
        bool hasColors = false,
        int colorComponentType = 5121, // 5121: UBYTE, 5123: USHORT, 5126: FLOAT
        bool colorHasAlpha = false,
        bool hasNormals = false,
        bool hasIntensity = false,
        double[]? nodeMatrixColMajor = null)
    {
        int count = points.Count;

        // Calculate binary layout
        int posLen = count * 12;
        int colCompCount = colorHasAlpha ? 4 : 3;
        int colElemSize = colorComponentType switch { 5121 => colCompCount, 5123 => colCompCount * 2, 5126 => colCompCount * 4, _ => 3 };
        int colLen = hasColors ? count * colElemSize : 0;
        int colLenPadded = (colLen + 3) & ~3;

        int normLen = hasNormals ? count * 12 : 0;
        int intLen = hasIntensity ? count * 4 : 0;

        int totalBinLen = posLen + (hasColors ? colLenPadded : 0) + normLen + intLen;
        int totalBinPadded = (totalBinLen + 3) & ~3;
        byte[] binData = new byte[totalBinPadded];

        // Fill POS
        for (int i = 0; i < count; i++)
        {
            var pt = points[i];
            BinaryPrimitives.WriteSingleLittleEndian(binData.AsSpan(i * 12, 4), (float)pt.X);
            BinaryPrimitives.WriteSingleLittleEndian(binData.AsSpan(i * 12 + 4, 4), (float)pt.Y);
            BinaryPrimitives.WriteSingleLittleEndian(binData.AsSpan(i * 12 + 8, 4), (float)pt.Z);
        }

        // Fill COLOR
        int curBinOffset = posLen;
        int colOffset = curBinOffset;
        if (hasColors)
        {
            for (int i = 0; i < count; i++)
            {
                var c = points[i].Color ?? new PclColor(255, 255, 255, 255);
                int off = colOffset + i * colElemSize;
                if (colorComponentType == 5121)
                {
                    binData[off] = c.R; binData[off + 1] = c.G; binData[off + 2] = c.B;
                    if (colorHasAlpha) binData[off + 3] = c.A;
                }
                else if (colorComponentType == 5123)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(binData.AsSpan(off, 2), (ushort)(c.R * 65535 / 255));
                    BinaryPrimitives.WriteUInt16LittleEndian(binData.AsSpan(off + 2, 2), (ushort)(c.G * 65535 / 255));
                    BinaryPrimitives.WriteUInt16LittleEndian(binData.AsSpan(off + 4, 2), (ushort)(c.B * 65535 / 255));
                    if (colorHasAlpha) BinaryPrimitives.WriteUInt16LittleEndian(binData.AsSpan(off + 6, 2), (ushort)(c.A * 65535 / 255));
                }
                else if (colorComponentType == 5126)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(binData.AsSpan(off, 4), c.R / 255.0f);
                    BinaryPrimitives.WriteSingleLittleEndian(binData.AsSpan(off + 4, 4), c.G / 255.0f);
                    BinaryPrimitives.WriteSingleLittleEndian(binData.AsSpan(off + 8, 4), c.B / 255.0f);
                    if (colorHasAlpha) BinaryPrimitives.WriteSingleLittleEndian(binData.AsSpan(off + 12, 4), c.A / 255.0f);
                }
            }
            curBinOffset += colLenPadded;
        }

        // Fill NORMAL
        int normOffset = curBinOffset;
        if (hasNormals)
        {
            for (int i = 0; i < count; i++)
            {
                var n = points[i].Normal ?? new PclVector3(0, 0, 1);
                int off = normOffset + i * 12;
                BinaryPrimitives.WriteSingleLittleEndian(binData.AsSpan(off, 4), (float)n.X);
                BinaryPrimitives.WriteSingleLittleEndian(binData.AsSpan(off + 4, 4), (float)n.Y);
                BinaryPrimitives.WriteSingleLittleEndian(binData.AsSpan(off + 8, 4), (float)n.Z);
            }
            curBinOffset += normLen;
        }

        // Fill INTENSITY
        int intOffset = curBinOffset;
        if (hasIntensity)
        {
            for (int i = 0; i < count; i++)
            {
                float val = points[i].Intensity ?? 1.0f;
                int off = intOffset + i * 4;
                BinaryPrimitives.WriteSingleLittleEndian(binData.AsSpan(off, 4), val);
            }
            curBinOffset += intLen;
        }

        // Build Accessors and BufferViews
        var accessors = new List<object>();
        var bufferViews = new List<object>();
        var attributes = new Dictionary<string, int>();

        // 0: POSITION
        attributes["POSITION"] = accessors.Count;
        bufferViews.Add(new { buffer = 0, byteOffset = 0, byteLength = posLen });
        accessors.Add(new
        {
            bufferView = 0,
            byteOffset = 0,
            componentType = 5126,
            count = count,
            type = "VEC3",
            min = new[] { points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z) },
            max = new[] { points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z) }
        });

        if (hasColors)
        {
            int bvIdx = bufferViews.Count;
            attributes["COLOR_0"] = accessors.Count;
            bufferViews.Add(new { buffer = 0, byteOffset = colOffset, byteLength = colLen });
            accessors.Add(new
            {
                bufferView = bvIdx,
                byteOffset = 0,
                componentType = colorComponentType,
                count = count,
                type = colorHasAlpha ? "VEC4" : "VEC3",
                normalized = colorComponentType != 5126
            });
        }

        if (hasNormals)
        {
            int bvIdx = bufferViews.Count;
            attributes["NORMAL"] = accessors.Count;
            bufferViews.Add(new { buffer = 0, byteOffset = normOffset, byteLength = normLen });
            accessors.Add(new
            {
                bufferView = bvIdx,
                byteOffset = 0,
                componentType = 5126,
                count = count,
                type = "VEC3"
            });
        }

        if (hasIntensity)
        {
            int bvIdx = bufferViews.Count;
            attributes["_INTENSITY"] = accessors.Count;
            bufferViews.Add(new { buffer = 0, byteOffset = intOffset, byteLength = intLen });
            accessors.Add(new
            {
                bufferView = bvIdx,
                byteOffset = 0,
                componentType = 5126,
                count = count,
                type = "SCALAR"
            });
        }

        var node = new Dictionary<string, object> { ["mesh"] = 0 };
        if (nodeMatrixColMajor != null && nodeMatrixColMajor.Length == 16)
        {
            node["matrix"] = nodeMatrixColMajor;
        }

        var gltfDict = new
        {
            asset = new { version = "2.0" },
            scenes = new[] { new { nodes = new[] { 0 } } },
            nodes = new[] { node },
            meshes = new[]
            {
                new
                {
                    name = "PointsMesh",
                    primitives = new[]
                    {
                        new
                        {
                            mode = 0, // POINTS
                            attributes = attributes
                        }
                    }
                }
            },
            accessors = accessors,
            bufferViews = bufferViews,
            buffers = new[] { new { byteLength = totalBinPadded } }
        };

        string jsonStr = JsonSerializer.Serialize(gltfDict);
        byte[] jsonRawBytes = Encoding.UTF8.GetBytes(jsonStr);
        int jsonPadded = (jsonRawBytes.Length + 3) & ~3;
        byte[] jsonBytes = new byte[jsonPadded];
        Array.Fill(jsonBytes, (byte)0x20);
        jsonRawBytes.CopyTo(jsonBytes, 0);

        uint totalGlbLength = (uint)(12 + 8 + jsonBytes.Length + 8 + binData.Length);
        byte[] glb = new byte[totalGlbLength];

        // Header (12 bytes)
        glb[0] = 0x67; glb[1] = 0x6C; glb[2] = 0x54; glb[3] = 0x46; // "glTF"
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4, 4), 2); // version 2
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8, 4), totalGlbLength);

        // Chunk 0: JSON
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12, 4), (uint)jsonBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16, 4), 0x4E4F534A); // "JSON"
        jsonBytes.CopyTo(glb, 20);

        // Chunk 1: BIN
        int binChunkHeaderPos = 20 + jsonBytes.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(binChunkHeaderPos, 4), (uint)binData.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(binChunkHeaderPos + 4, 4), 0x004E4942); // "BIN\0"
        binData.CopyTo(glb, binChunkHeaderPos + 8);

        return glb;
    }

    /// <summary>Generates a synthetic tileset.json string.</summary>
    public static string CreateSyntheticTilesetJson(string tileUri, double[]? transformColMajor = null, double geometricError = 10.0)
    {
        var tileDict = new Dictionary<string, object>
        {
            ["geometricError"] = geometricError,
            ["content"] = new Dictionary<string, string> { ["uri"] = tileUri },
            ["boundingVolume"] = new Dictionary<string, double[]>
            {
                ["sphere"] = new double[] { 0, 0, 0, 100 }
            }
        };

        if (transformColMajor != null && transformColMajor.Length == 16)
        {
            tileDict["transform"] = transformColMajor;
        }

        var root = new Dictionary<string, object>
        {
            ["asset"] = new Dictionary<string, string> { ["version"] = "1.0" },
            ["geometricError"] = geometricError,
            ["root"] = tileDict
        };

        return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Encodes a 3D unit normal vector using Potree's Lambert spheremap projection.</summary>
    public static (byte b0, byte b1) EncodeSpheremap(PclVector3 n)
    {
        double len = Math.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
        if (len > 0.0)
            n = new PclVector3(n.X / len, n.Y / len, n.Z / len);
        else
            n = new PclVector3(0, 0, 1);

        double nz = Math.Clamp(n.Z, -1.0, 1.0);
        double g = (1.0 - nz) * 0.5;
        double p = Math.Sqrt(Math.Max(0.0, 1.0 - g));
        double u = 0.0;
        double v = 0.0;
        if (p > 1e-6)
        {
            u = n.X / (2.0 * p);
            v = n.Y / (2.0 * p);
        }
        byte b0 = (byte)Math.Clamp((int)Math.Round(((u + 1.0) * 0.5) * 255.0), 0, 255);
        byte b1 = (byte)Math.Clamp((int)Math.Round(((v + 1.0) * 0.5) * 255.0), 0, 255);
        return (b0, b1);
    }

    /// <summary>Generates a synthetic Potree 1.x dataset (cloud.js, r.bin, r.hrc).</summary>
    public static (string cloudJs, byte[] rBin, byte[] rHrc) CreateSyntheticPotree1x(
        IReadOnlyList<PotreeSamplePoint> points,
        PclVector3 scale,
        PclVector3 offset,
        bool hasColors = true,
        bool hasAlpha = true,
        string normalType = "NORMAL_SPHEREMAPPED",
        bool hasIntensity = true,
        bool hasClassification = true,
        bool floatPositions = false)
    {
        var attrs = new List<string>();
        attrs.Add("POSITION_CARTESIAN");
        if (hasColors)
            attrs.Add(hasAlpha ? "COLOR_PACKED" : "RGB");
        if (!string.IsNullOrEmpty(normalType))
            attrs.Add(normalType);
        if (hasIntensity)
            attrs.Add("INTENSITY");
        if (hasClassification)
            attrs.Add("CLASSIFICATION");

        int posSize = 12;
        int colSize = hasColors ? (hasAlpha ? 4 : 3) : 0;
        int normSize = normalType switch
        {
            "NORMAL_SPHEREMAPPED" or "NORMAL_OCT16" => 2,
            "NORMAL_FLOATS" => 12,
            _ => 0
        };
        int intSize = hasIntensity ? 2 : 0;
        int classSize = hasClassification ? 1 : 0;
        int recordSize = posSize + colSize + normSize + intSize + classSize;

        byte[] rBin = new byte[points.Count * recordSize];
        int writePos = 0;

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;

        foreach (var pt in points)
        {
            minX = Math.Min(minX, pt.X); minY = Math.Min(minY, pt.Y); minZ = Math.Min(minZ, pt.Z);
            maxX = Math.Max(maxX, pt.X); maxY = Math.Max(maxY, pt.Y); maxZ = Math.Max(maxZ, pt.Z);

            if (floatPositions)
            {
                BinaryPrimitives.WriteSingleLittleEndian(rBin.AsSpan(writePos, 4), (float)pt.X);
                BinaryPrimitives.WriteSingleLittleEndian(rBin.AsSpan(writePos + 4, 4), (float)pt.Y);
                BinaryPrimitives.WriteSingleLittleEndian(rBin.AsSpan(writePos + 8, 4), (float)pt.Z);
            }
            else
            {
                int rx = (int)Math.Round((pt.X - offset.X) / scale.X);
                int ry = (int)Math.Round((pt.Y - offset.Y) / scale.Y);
                int rz = (int)Math.Round((pt.Z - offset.Z) / scale.Z);
                BinaryPrimitives.WriteInt32LittleEndian(rBin.AsSpan(writePos, 4), rx);
                BinaryPrimitives.WriteInt32LittleEndian(rBin.AsSpan(writePos + 4, 4), ry);
                BinaryPrimitives.WriteInt32LittleEndian(rBin.AsSpan(writePos + 8, 4), rz);
            }
            writePos += 12;

            if (hasColors)
            {
                var c = pt.Color ?? new PclColor(255, 255, 255, 255);
                rBin[writePos] = c.R;
                rBin[writePos + 1] = c.G;
                rBin[writePos + 2] = c.B;
                if (hasAlpha)
                {
                    rBin[writePos + 3] = c.A;
                    writePos += 4;
                }
                else
                {
                    writePos += 3;
                }
            }

            if (!string.IsNullOrEmpty(normalType))
            {
                var n = pt.Normal ?? new PclVector3(0, 0, 1);
                if (normalType == "NORMAL_SPHEREMAPPED")
                {
                    var (b0, b1) = EncodeSpheremap(n);
                    rBin[writePos] = b0;
                    rBin[writePos + 1] = b1;
                    writePos += 2;
                }
                else if (normalType == "NORMAL_OCT16")
                {
                    var (b0, b1) = EncodeOct16p(n.X, n.Y, n.Z);
                    rBin[writePos] = b0;
                    rBin[writePos + 1] = b1;
                    writePos += 2;
                }
                else if (normalType == "NORMAL_FLOATS")
                {
                    BinaryPrimitives.WriteSingleLittleEndian(rBin.AsSpan(writePos, 4), (float)n.X);
                    BinaryPrimitives.WriteSingleLittleEndian(rBin.AsSpan(writePos + 4, 4), (float)n.Y);
                    BinaryPrimitives.WriteSingleLittleEndian(rBin.AsSpan(writePos + 8, 4), (float)n.Z);
                    writePos += 12;
                }
            }

            if (hasIntensity)
            {
                ushort intVal = (ushort)(pt.Intensity.HasValue ? Math.Clamp((int)pt.Intensity.Value, 0, 65535) : 0);
                BinaryPrimitives.WriteUInt16LittleEndian(rBin.AsSpan(writePos, 2), intVal);
                writePos += 2;
            }

            if (hasClassification)
            {
                rBin[writePos] = pt.Classification ?? 0;
                writePos += 1;
            }
        }

        if (points.Count == 0)
        {
            minX = minY = minZ = 0;
            maxX = maxY = maxZ = 1;
        }

        var manifest = new Dictionary<string, object>
        {
            ["version"] = "1.7",
            ["octreeDir"] = "data",
            ["boundingBox"] = new Dictionary<string, double>
            {
                ["lx"] = offset.X, ["ly"] = offset.Y, ["lz"] = offset.Z,
                ["ux"] = offset.X + 100, ["uy"] = offset.Y + 100, ["uz"] = offset.Z + 100
            },
            ["tightBoundingBox"] = new Dictionary<string, double>
            {
                ["lx"] = minX, ["ly"] = minY, ["lz"] = minZ,
                ["ux"] = maxX, ["uy"] = maxY, ["uz"] = maxZ
            },
            ["pointAttributes"] = attrs,
            ["spacing"] = 0.05,
            ["scale"] = new double[] { scale.X, scale.Y, scale.Z },
            ["hierarchyStepSize"] = 5
        };

        string cloudJs = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });

        // 1 node entry in .hrc: 1 byte mask (0 = no children), 4 bytes point count
        byte[] rHrc = new byte[5];
        rHrc[0] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(rHrc.AsSpan(1, 4), (uint)points.Count);

        return (cloudJs, rBin, rHrc);
    }

    /// <summary>Generates a synthetic Potree 2.x dataset (metadata.json, octree.bin).</summary>
    public static (string metadataJson, byte[] octreeBin) CreateSyntheticPotree2x(
        IReadOnlyList<PotreeSamplePoint> points,
        PclVector3 scale,
        PclVector3 offset,
        bool hasColors = true,
        bool hasAlpha = true,
        bool color16Bit = false,
        bool hasNormals = true,
        bool normalOct16 = true,
        bool hasIntensity = true,
        bool hasClassification = true,
        bool floatPositions = false)
    {
        var attrs = new List<Dictionary<string, object>>();
        attrs.Add(new Dictionary<string, object>
        {
            ["name"] = "position",
            ["size"] = 12,
            ["numElements"] = 3,
            ["elementSize"] = 4,
            ["type"] = floatPositions ? "float" : "int32"
        });

        if (hasColors)
        {
            int elemSize = color16Bit ? 2 : 1;
            int numElem = hasAlpha ? 4 : 3;
            attrs.Add(new Dictionary<string, object>
            {
                ["name"] = hasAlpha ? "rgba" : "rgb",
                ["size"] = numElem * elemSize,
                ["numElements"] = numElem,
                ["elementSize"] = elemSize,
                ["type"] = color16Bit ? "uint16" : "uint8"
            });
        }

        if (hasNormals)
        {
            attrs.Add(new Dictionary<string, object>
            {
                ["name"] = "normal",
                ["size"] = normalOct16 ? 2 : 12,
                ["numElements"] = normalOct16 ? 2 : 3,
                ["elementSize"] = normalOct16 ? 1 : 4,
                ["type"] = normalOct16 ? "uint8" : "float"
            });
        }

        if (hasIntensity)
        {
            attrs.Add(new Dictionary<string, object>
            {
                ["name"] = "intensity",
                ["size"] = 2,
                ["numElements"] = 1,
                ["elementSize"] = 2,
                ["type"] = "uint16"
            });
        }

        if (hasClassification)
        {
            attrs.Add(new Dictionary<string, object>
            {
                ["name"] = "classification",
                ["size"] = 1,
                ["numElements"] = 1,
                ["elementSize"] = 1,
                ["type"] = "uint8"
            });
        }

        int recordSize = attrs.Sum(a => (int)a["size"]);
        byte[] octreeBin = new byte[points.Count * recordSize];
        int writePos = 0;

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;

        foreach (var pt in points)
        {
            minX = Math.Min(minX, pt.X); minY = Math.Min(minY, pt.Y); minZ = Math.Min(minZ, pt.Z);
            maxX = Math.Max(maxX, pt.X); maxY = Math.Max(maxY, pt.Y); maxZ = Math.Max(maxZ, pt.Z);

            if (floatPositions)
            {
                BinaryPrimitives.WriteSingleLittleEndian(octreeBin.AsSpan(writePos, 4), (float)pt.X);
                BinaryPrimitives.WriteSingleLittleEndian(octreeBin.AsSpan(writePos + 4, 4), (float)pt.Y);
                BinaryPrimitives.WriteSingleLittleEndian(octreeBin.AsSpan(writePos + 8, 4), (float)pt.Z);
            }
            else
            {
                int rx = (int)Math.Round((pt.X - offset.X) / scale.X);
                int ry = (int)Math.Round((pt.Y - offset.Y) / scale.Y);
                int rz = (int)Math.Round((pt.Z - offset.Z) / scale.Z);
                BinaryPrimitives.WriteInt32LittleEndian(octreeBin.AsSpan(writePos, 4), rx);
                BinaryPrimitives.WriteInt32LittleEndian(octreeBin.AsSpan(writePos + 4, 4), ry);
                BinaryPrimitives.WriteInt32LittleEndian(octreeBin.AsSpan(writePos + 8, 4), rz);
            }
            writePos += 12;

            if (hasColors)
            {
                var c = pt.Color ?? new PclColor(255, 255, 255, 255);
                if (color16Bit)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(octreeBin.AsSpan(writePos, 2), (ushort)(c.R << 8));
                    BinaryPrimitives.WriteUInt16LittleEndian(octreeBin.AsSpan(writePos + 2, 2), (ushort)(c.G << 8));
                    BinaryPrimitives.WriteUInt16LittleEndian(octreeBin.AsSpan(writePos + 4, 2), (ushort)(c.B << 8));
                    if (hasAlpha)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(octreeBin.AsSpan(writePos + 6, 2), (ushort)(c.A << 8));
                        writePos += 8;
                    }
                    else
                    {
                        writePos += 6;
                    }
                }
                else
                {
                    octreeBin[writePos] = c.R;
                    octreeBin[writePos + 1] = c.G;
                    octreeBin[writePos + 2] = c.B;
                    if (hasAlpha)
                    {
                        octreeBin[writePos + 3] = c.A;
                        writePos += 4;
                    }
                    else
                    {
                        writePos += 3;
                    }
                }
            }

            if (hasNormals)
            {
                var n = pt.Normal ?? new PclVector3(0, 0, 1);
                if (normalOct16)
                {
                    var (b0, b1) = EncodeOct16p(n.X, n.Y, n.Z);
                    octreeBin[writePos] = b0;
                    octreeBin[writePos + 1] = b1;
                    writePos += 2;
                }
                else
                {
                    BinaryPrimitives.WriteSingleLittleEndian(octreeBin.AsSpan(writePos, 4), (float)n.X);
                    BinaryPrimitives.WriteSingleLittleEndian(octreeBin.AsSpan(writePos + 4, 4), (float)n.Y);
                    BinaryPrimitives.WriteSingleLittleEndian(octreeBin.AsSpan(writePos + 8, 4), (float)n.Z);
                    writePos += 12;
                }
            }

            if (hasIntensity)
            {
                ushort intVal = (ushort)(pt.Intensity.HasValue ? Math.Clamp((int)pt.Intensity.Value, 0, 65535) : 0);
                BinaryPrimitives.WriteUInt16LittleEndian(octreeBin.AsSpan(writePos, 2), intVal);
                writePos += 2;
            }

            if (hasClassification)
            {
                octreeBin[writePos] = pt.Classification ?? 0;
                writePos += 1;
            }
        }

        if (points.Count == 0)
        {
            minX = minY = minZ = 0;
            maxX = maxY = maxZ = 1;
        }

        var manifest = new Dictionary<string, object>
        {
            ["version"] = "2.0",
            ["name"] = "SyntheticPotree2",
            ["points"] = points.Count,
            ["boundingBox"] = new Dictionary<string, double[]>
            {
                ["min"] = new double[] { minX, minY, minZ },
                ["max"] = new double[] { maxX, maxY, maxZ }
            },
            ["attributes"] = attrs,
            ["scale"] = new double[] { scale.X, scale.Y, scale.Z },
            ["offset"] = new double[] { offset.X, offset.Y, offset.Z },
            ["hierarchy"] = new Dictionary<string, object>
            {
                ["firstChunkSize"] = 100,
                ["stepSize"] = 4,
                ["depth"] = 1
            }
        };

        string metadataJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        return (metadataJson, octreeBin);
    }

    /// <summary>Generates a synthetic EPT dataset (ept.json, hierarchy 0-0-0-0.json, and data tile 0-0-0-0.{ext}).</summary>
    public static (string manifestJson, string hierarchyJson, byte[] dataBytes, string dataExtension) CreateSyntheticEpt(
        IReadOnlyList<EptSamplePoint> points,
        string dataType = "binary",
        PclBounds? bounds = null,
        int span = 128)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;

        foreach (var pt in points)
        {
            minX = Math.Min(minX, pt.X);
            minY = Math.Min(minY, pt.Y);
            minZ = Math.Min(minZ, pt.Z);
            maxX = Math.Max(maxX, pt.X);
            maxY = Math.Max(maxY, pt.Y);
            maxZ = Math.Max(maxZ, pt.Z);
        }

        if (points.Count == 0 || !double.IsFinite(minX))
        {
            minX = minY = minZ = 0;
            maxX = maxY = maxZ = 100;
        }

        double cubeMinX = Math.Floor(minX);
        double cubeMinY = Math.Floor(minY);
        double cubeMinZ = Math.Floor(minZ);
        double maxSpan = Math.Max(maxX - cubeMinX, Math.Max(maxY - cubeMinY, maxZ - cubeMinZ));
        if (maxSpan <= 0) maxSpan = 1.0;
        double cubeMaxX = cubeMinX + maxSpan;
        double cubeMaxY = cubeMinY + maxSpan;
        double cubeMaxZ = cubeMinZ + maxSpan;

        if (bounds.HasValue)
        {
            cubeMinX = bounds.Value.Min.X;
            cubeMinY = bounds.Value.Min.Y;
            cubeMinZ = bounds.Value.Min.Z;
            cubeMaxX = bounds.Value.Max.X;
            cubeMaxY = bounds.Value.Max.Y;
            cubeMaxZ = bounds.Value.Max.Z;
        }

        var schemaList = new List<Dictionary<string, object>>
        {
            new() { ["name"] = "X", ["type"] = "signed", ["size"] = 4, ["scale"] = 0.001, ["offset"] = 0.0 },
            new() { ["name"] = "Y", ["type"] = "signed", ["size"] = 4, ["scale"] = 0.001, ["offset"] = 0.0 },
            new() { ["name"] = "Z", ["type"] = "signed", ["size"] = 4, ["scale"] = 0.001, ["offset"] = 0.0 },
            new() { ["name"] = "Intensity", ["type"] = "unsigned", ["size"] = 2, ["scale"] = 1.0, ["offset"] = 0.0 },
            new() { ["name"] = "Red", ["type"] = "unsigned", ["size"] = 2, ["scale"] = 1.0, ["offset"] = 0.0 },
            new() { ["name"] = "Green", ["type"] = "unsigned", ["size"] = 2, ["scale"] = 1.0, ["offset"] = 0.0 },
            new() { ["name"] = "Blue", ["type"] = "unsigned", ["size"] = 2, ["scale"] = 1.0, ["offset"] = 0.0 },
            new() { ["name"] = "Classification", ["type"] = "unsigned", ["size"] = 1, ["scale"] = 1.0, ["offset"] = 0.0 },
        };

        var manifestObj = new Dictionary<string, object>
        {
            ["bounds"] = new double[] { cubeMinX, cubeMinY, cubeMinZ, cubeMaxX, cubeMaxY, cubeMaxZ },
            ["boundsConforming"] = new double[] { minX, minY, minZ, maxX, maxY, maxZ },
            ["dataType"] = dataType,
            ["hierarchyType"] = "json",
            ["points"] = points.Count,
            ["schema"] = schemaList,
            ["span"] = span,
            ["version"] = "1.0.0"
        };

        string manifestJson = JsonSerializer.Serialize(manifestObj, new JsonSerializerOptions { WriteIndented = true });

        var hierObj = new Dictionary<string, object>
        {
            ["0-0-0-0"] = points.Count
        };
        string hierarchyJson = JsonSerializer.Serialize(hierObj, new JsonSerializerOptions { WriteIndented = true });

        int recSize = 4 + 4 + 4 + 2 + 2 + 2 + 2 + 1; // 21 bytes
        byte[] rawBin = new byte[points.Count * recSize];
        int offset = 0;
        foreach (var pt in points)
        {
            int ix = (int)Math.Round(pt.X / 0.001);
            int iy = (int)Math.Round(pt.Y / 0.001);
            int iz = (int)Math.Round(pt.Z / 0.001);
            ushort intensity = (ushort)(pt.Intensity.HasValue ? Math.Clamp((int)Math.Round(pt.Intensity.Value * 65535.0), 0, 65535) : 0);
            ushort r = pt.Color.HasValue ? (ushort)(pt.Color.Value.R * 257) : (ushort)0;
            ushort g = pt.Color.HasValue ? (ushort)(pt.Color.Value.G * 257) : (ushort)0;
            ushort b = pt.Color.HasValue ? (ushort)(pt.Color.Value.B * 257) : (ushort)0;
            byte cls = pt.Classification ?? 0;

            BinaryPrimitives.WriteInt32LittleEndian(rawBin.AsSpan(offset, 4), ix);
            BinaryPrimitives.WriteInt32LittleEndian(rawBin.AsSpan(offset + 4, 4), iy);
            BinaryPrimitives.WriteInt32LittleEndian(rawBin.AsSpan(offset + 8, 4), iz);
            BinaryPrimitives.WriteUInt16LittleEndian(rawBin.AsSpan(offset + 12, 2), intensity);
            BinaryPrimitives.WriteUInt16LittleEndian(rawBin.AsSpan(offset + 14, 2), r);
            BinaryPrimitives.WriteUInt16LittleEndian(rawBin.AsSpan(offset + 16, 2), g);
            BinaryPrimitives.WriteUInt16LittleEndian(rawBin.AsSpan(offset + 18, 2), b);
            rawBin[offset + 20] = cls;
            offset += recSize;
        }

        byte[] tileBytes;
        string ext;
        if (dataType == "laszip")
        {
            ext = "laz";
            if (points.Count > 0)
            {
                var pt0 = points[0];
                tileBytes = CreateSyntheticLaz(pt0.X, pt0.Y, pt0.Z, pt0.Intensity ?? 0f, pt0.Color, pt0.Classification ?? 0);
            }
            else
            {
                tileBytes = CreateSyntheticLaz();
            }
        }
        else if (dataType == "zstandard")
        {
            ext = "zst";
            tileBytes = RawZstdFrame.Wrap(rawBin);
        }
        else
        {
            ext = "bin";
            tileBytes = rawBin;
        }

        return (manifestJson, hierarchyJson, tileBytes, ext);
    }

    public record PtgSamplePoint(double X, double Y, double Z, float? Intensity = null, PclColor? Color = null, bool IsValid = true);

    public static readonly IReadOnlyList<PtgSamplePoint> DefaultPtgPoints =
    [
        new(1.0, 2.0, 3.0, 0.5f, new PclColor(10, 20, 30)),
        new(-4.5, 5.25, 6.125, 1.0f, new PclColor(255, 255, 255)),
        new(7.0, 8.0, 9.0, 0.2f, new PclColor(100, 150, 200)),
    ];

    public static byte[] CreateSyntheticPtgBinary(
        int numCols = 3,
        int numRows = 1,
        int properties = 0x01 | 0x04 | 0x08,
        IReadOnlyList<PtgSamplePoint>? points = null,
        double[]? transformMatrix = null,
        string? scanName = "SyntheticScan",
        string? customHeader = null,
        bool corruptMagic = false,
        bool corruptOffsets = false)
    {
        points ??= DefaultPtgPoints;

        using var ms = new MemoryStream();

        byte[] tag = corruptMagic ? [0x58, 0x59, 0x5A, 0x00] : [0x50, 0x54, 0x47, 0x00];
        ms.Write(tag);

        uint magic = corruptMagic ? 0x12345678 : 0x928FA3C7;
        Span<byte> magicBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(magicBytes, magic);
        ms.Write(magicBytes);

        byte[] headerBytes;
        if (customHeader != null)
        {
            headerBytes = Encoding.ASCII.GetBytes(customHeader);
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine("%%header_begin");
            sb.AppendLine("version 1");
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"NumRows {numRows}"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"NumCols {numCols}"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"%%properties {properties}"));
            if (!string.IsNullOrEmpty(scanName))
                sb.AppendLine($"%%scan_name {scanName}");

            if (transformMatrix is { Length: 16 } m)
            {
                sb.AppendLine("%%transform");
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{m[0]} {m[1]} {m[2]} {m[3]}"));
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{m[4]} {m[5]} {m[6]} {m[7]}"));
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{m[8]} {m[9]} {m[10]} {m[11]}"));
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{m[12]} {m[13]} {m[14]} {m[15]}"));
            }

            sb.AppendLine("%%header_end");
            headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        }
        ms.Write(headerBytes);

        int maskSize = (numRows + 7) / 8;
        bool isDouble = (properties & 0x02) != 0;
        bool hasIntensity = (properties & 0x04) != 0;
        bool hasColors = (properties & 0x08) != 0;

        List<byte[]> columnBlocks = [];
        long[] colOffsets = new long[numCols];

        long tableStartOffset = ms.Position;
        long tableSize = (long)numCols * 8;
        long currentDataOffset = tableStartOffset + tableSize;

        int pointIdx = 0;
        Span<byte> recordBuf = stackalloc byte[32];

        for (int c = 0; c < numCols; c++)
        {
            colOffsets[c] = corruptOffsets ? (c == 0 ? 0 : currentDataOffset) : currentDataOffset;

            byte[] mask = new byte[maskSize];
            using var colDataMs = new MemoryStream();

            for (int r = 0; r < numRows; r++)
            {
                var pt = points.Count > 0 ? points[pointIdx % points.Count] : new PtgSamplePoint(0, 0, 0);
                pointIdx++;

                if (pt.IsValid)
                {
                    int bIdx = r >> 3;
                    int bit = 7 - (r & 7);
                    mask[bIdx] |= (byte)(1 << bit);

                    int recPos = 0;

                    if (isDouble)
                    {
                        BinaryPrimitives.WriteDoubleLittleEndian(recordBuf[recPos..], pt.X);
                        BinaryPrimitives.WriteDoubleLittleEndian(recordBuf[(recPos + 8)..], pt.Y);
                        BinaryPrimitives.WriteDoubleLittleEndian(recordBuf[(recPos + 16)..], pt.Z);
                        recPos += 24;
                    }
                    else
                    {
                        BinaryPrimitives.WriteSingleLittleEndian(recordBuf[recPos..], (float)pt.X);
                        BinaryPrimitives.WriteSingleLittleEndian(recordBuf[(recPos + 4)..], (float)pt.Y);
                        BinaryPrimitives.WriteSingleLittleEndian(recordBuf[(recPos + 8)..], (float)pt.Z);
                        recPos += 12;
                    }

                    if (hasIntensity)
                    {
                        BinaryPrimitives.WriteSingleLittleEndian(recordBuf[recPos..], pt.Intensity ?? 0f);
                        recPos += 4;
                    }

                    if (hasColors)
                    {
                        recordBuf[recPos] = pt.Color?.R ?? 255;
                        recordBuf[recPos + 1] = pt.Color?.G ?? 255;
                        recordBuf[recPos + 2] = pt.Color?.B ?? 255;
                        recPos += 3;
                    }

                    colDataMs.Write(recordBuf[..recPos]);
                }
            }

            byte[] colBlock = new byte[maskSize + colDataMs.Length];
            Buffer.BlockCopy(mask, 0, colBlock, 0, maskSize);
            byte[] ptBytes = colDataMs.ToArray();
            Buffer.BlockCopy(ptBytes, 0, colBlock, maskSize, ptBytes.Length);

            columnBlocks.Add(colBlock);
            currentDataOffset += colBlock.Length;
        }

        Span<byte> offsetBuf = stackalloc byte[8];
        for (int c = 0; c < numCols; c++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(offsetBuf, colOffsets[c]);
            ms.Write(offsetBuf);
        }

        foreach (var block in columnBlocks)
        {
            ms.Write(block);
        }

        return ms.ToArray();
    }

    public static byte[] CreateSyntheticPtgIndex(
        IEnumerable<string> scanRelativePaths,
        string? headerLine = "PTG index file")
    {
        var sb = new StringBuilder();
        if (headerLine != null)
        {
            sb.AppendLine(headerLine);
            sb.AppendLine("--------------");
        }
        foreach (var path in scanRelativePaths)
        {
            sb.AppendLine(path);
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    // ---- CC BIN (.bin) ------------------------------------------------------------------------------------------

    /// <summary>Creates a synthetic CC BIN point cloud file (.bin, CCB2).</summary>
    public static byte[] CreateSyntheticCcBin(
        IReadOnlyList<CcBinSamplePoint> points,
        string name = "Cloud",
        double shiftX = 0,
        double shiftY = 0,
        double shiftZ = 0,
        double scale = 1.0,
        bool withColors = true,
        bool withIntensity = true,
        uint version = 47,
        double[]? matrix16 = null,
        IReadOnlyDictionary<string, double>? doubleProperties = null,
        byte[]? rawMagic = null,
        bool truncatePoints = false,
        bool truncateColorBlock = false,
        bool truncateIntensityData = false,
        int? declaredIntensityCount = null,
        IReadOnlyList<uint>? normalCodes = null)
    {
        using var ms = new MemoryStream();

        // 1. Magic signature (4 bytes)
        ms.Write(rawMagic ?? "CCB2"u8.ToArray());

        // 2. Version (uint32 LE)
        Span<byte> u32Buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(u32Buf, version);
        ms.Write(u32Buf);

        WriteCcBinCloudEntity(
            ms,
            points,
            name: name,
            id: 100,
            shiftX: shiftX,
            shiftY: shiftY,
            shiftZ: shiftZ,
            scale: scale,
            withColors: withColors,
            withIntensity: withIntensity,
            version: version,
            normalCodes: normalCodes,
            declaredIntensityCount: declaredIntensityCount,
            matrix16: matrix16,
            doubleProperties: doubleProperties,
            truncatePoints: truncatePoints,
            truncateColorBlock: truncateColorBlock,
            truncateIntensityData: truncateIntensityData);

        return ms.ToArray();
    }

    /// <summary>Creates a synthetic CC BIN file containing multiple adjacent cloud entities.</summary>
    public static byte[] CreateSyntheticCcBinMultiCloud(
        IReadOnlyList<IReadOnlyList<CcBinSamplePoint>> clouds,
        uint version = 47)
    {
        using var ms = new MemoryStream();
        ms.Write("CCB2"u8.ToArray());

        Span<byte> u32Buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(u32Buf, version);
        ms.Write(u32Buf);

        for (int c = 0; c < clouds.Count; c++)
        {
            WriteCcBinCloudEntity(
                ms,
                clouds[c],
                name: $"Cloud_{c + 1}",
                id: (uint)(100 + c),
                version: version);
        }

        return ms.ToArray();
    }

    private static void WriteCcBinCloudEntity(
        Stream ms,
        IReadOnlyList<CcBinSamplePoint> points,
        string name = "Cloud",
        uint id = 100,
        double shiftX = 0,
        double shiftY = 0,
        double shiftZ = 0,
        double scale = 1.0,
        bool withColors = true,
        bool withIntensity = true,
        uint version = 47,
        IReadOnlyList<uint>? normalCodes = null,
        int? declaredIntensityCount = null,
        double[]? matrix16 = null,
        IReadOnlyDictionary<string, double>? doubleProperties = null,
        bool truncatePoints = false,
        bool truncateColorBlock = false,
        bool truncateIntensityData = false)
    {
        Span<byte> u32Buf = stackalloc byte[4];

        // 3. Entity header: f1 (257), f2 (0), id
        BinaryPrimitives.WriteUInt32LittleEndian(u32Buf, 257);
        ms.Write(u32Buf);
        BinaryPrimitives.WriteUInt32LittleEndian(u32Buf, 0);
        ms.Write(u32Buf);
        BinaryPrimitives.WriteUInt32LittleEndian(u32Buf, id);
        ms.Write(u32Buf);

        // Name (UTF-16 BE)
        byte[] nameBytes = Encoding.BigEndianUnicode.GetBytes(name);
        BinaryPrimitives.WriteUInt32BigEndian(u32Buf, (uint)nameBytes.Length);
        ms.Write(u32Buf);
        ms.Write(nameBytes);

        // 4. QVariantMap properties
        BinaryPrimitives.WriteUInt32LittleEndian(u32Buf, 2); // mapType
        ms.Write(u32Buf);
        BinaryPrimitives.WriteUInt32LittleEndian(u32Buf, (uint)(doubleProperties?.Count ?? 0));
        ms.Write(u32Buf);

        if (doubleProperties != null)
        {
            Span<byte> dBuf = stackalloc byte[8];
            foreach (var kvp in doubleProperties)
            {
                byte[] kBytes = Encoding.BigEndianUnicode.GetBytes(kvp.Key);
                BinaryPrimitives.WriteUInt32BigEndian(u32Buf, (uint)kBytes.Length);
                ms.Write(u32Buf);
                ms.Write(kBytes);

                BinaryPrimitives.WriteUInt32BigEndian(u32Buf, 6); // vType = double
                ms.Write(u32Buf);
                ms.WriteByte(0); // isNull = 0

                BinaryPrimitives.WriteUInt64BigEndian(dBuf, BitConverter.DoubleToUInt64Bits(kvp.Value));
                ms.Write(dBuf);
            }
        }

        // 5. Flags (8 bytes), shift (24 bytes), scale (8 bytes), marker (3 bytes)
        ms.WriteByte(0x01);
        ms.WriteByte(0x00);
        ms.WriteByte(withColors ? (byte)1 : (byte)0);
        ms.WriteByte(normalCodes != null ? (byte)1 : (byte)0);
        ms.WriteByte(0x00);
        ms.WriteByte(0x00);
        ms.WriteByte(0x00);
        ms.WriteByte(0x00);

        Span<byte> dblBuf = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(dblBuf, shiftX);
        ms.Write(dblBuf);
        BinaryPrimitives.WriteDoubleLittleEndian(dblBuf, shiftY);
        ms.Write(dblBuf);
        BinaryPrimitives.WriteDoubleLittleEndian(dblBuf, shiftZ);
        ms.Write(dblBuf);
        BinaryPrimitives.WriteDoubleLittleEndian(dblBuf, scale);
        ms.Write(dblBuf);

        ms.WriteByte(0x00);
        ms.WriteByte(0x00);
        ms.WriteByte(0x03);

        // 6. Point count (uint32 LE)
        BinaryPrimitives.WriteUInt32LittleEndian(u32Buf, (uint)points.Count);
        ms.Write(u32Buf);

        // 7. Points data (12 bytes per point)
        int ptCountToWrite = truncatePoints ? points.Count / 2 : points.Count;
        Span<byte> ptBuf = stackalloc byte[12];
        for (int i = 0; i < ptCountToWrite; i++)
        {
            var pt = points[i];
            BinaryPrimitives.WriteSingleLittleEndian(ptBuf.Slice(0, 4), (float)pt.X);
            BinaryPrimitives.WriteSingleLittleEndian(ptBuf.Slice(4, 4), (float)pt.Y);
            BinaryPrimitives.WriteSingleLittleEndian(ptBuf.Slice(8, 4), (float)pt.Z);
            ms.Write(ptBuf);
        }

        if (truncatePoints)
            return;

        // 8. Colors
        if (truncateColorBlock)
        {
            ms.WriteByte(0x01); // color flag declared, but stream ends immediately
            return;
        }

        if (withColors)
        {
            ms.WriteByte(0x01); // color flag

            // Header: f1, f2, id, nameLen, name ("RGB colors"), colType, colFlags, channels, colCount
            BinaryPrimitives.WriteUInt32LittleEndian(u32Buf, 0x10000012);
            ms.Write(u32Buf);
            BinaryPrimitives.WriteUInt32LittleEndian(u32Buf, 0);
            ms.Write(u32Buf);
            BinaryPrimitives.WriteUInt32LittleEndian(u32Buf, 101);
            ms.Write(u32Buf);

            byte[] colNameBytes = Encoding.BigEndianUnicode.GetBytes("RGB colors");
            BinaryPrimitives.WriteUInt32BigEndian(u32Buf, (uint)colNameBytes.Length);
            ms.Write(u32Buf);
            ms.Write(colNameBytes);

            BinaryPrimitives.WriteUInt32LittleEndian(u32Buf, 6);
            ms.Write(u32Buf);
            BinaryPrimitives.WriteUInt32LittleEndian(u32Buf, 0);
            ms.Write(u32Buf);
            ms.WriteByte(3); // channels

            BinaryPrimitives.WriteUInt32LittleEndian(u32Buf, (uint)points.Count);
            ms.Write(u32Buf);

            Span<byte> rgb = stackalloc byte[3];
            for (int i = 0; i < points.Count; i++)
            {
                var pt = points[i];
                rgb[0] = pt.Color?.R ?? 255;
                rgb[1] = pt.Color?.G ?? 255;
                rgb[2] = pt.Color?.B ?? 255;
                ms.Write(rgb);
            }
        }
        else
        {
            ms.WriteByte(0x00);
        }

        int footerSize = version is 41 or 42 ? 8 : 72;
        if (withColors) WriteCcBinArrayFooter(ms, footerSize, matrix16);

        // 9. Optional packed normals.
        ms.WriteByte(normalCodes != null ? (byte)1 : (byte)0);
        if (normalCodes != null)
        {
            byte[] normalsName = Encoding.BigEndianUnicode.GetBytes("Normals");
            Span<byte> header = stackalloc byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(header, 0x40000012);
            BinaryPrimitives.WriteUInt32LittleEndian(header[8..], 102);
            BinaryPrimitives.WriteUInt32BigEndian(header[12..], (uint)normalsName.Length);
            ms.Write(header);
            ms.Write(normalsName);
            ms.Write(new byte[] { 6, 0, 0, 0, 0, 0, 0, 0, 1 });
            BinaryPrimitives.WriteUInt32LittleEndian(u32Buf, (uint)normalCodes.Count);
            ms.Write(u32Buf);
            foreach (uint code in normalCodes)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(u32Buf, code);
                ms.Write(u32Buf);
            }
            WriteCcBinArrayFooter(ms, footerSize);
        }

        // 10. Scalar fields (Intensity)
        if (withIntensity)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(u32Buf, 1); // 1 scalar field
            ms.Write(u32Buf);

            byte[] nameBuf256 = new byte[256];
            Encoding.ASCII.GetBytes("Intensity", nameBuf256.AsSpan());
            ms.Write(nameBuf256);

            ms.WriteByte(0x01); // flag
            int actualIntensityCount = declaredIntensityCount ?? (truncateIntensityData ? 2 : points.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(u32Buf, (uint)actualIntensityCount);
            ms.Write(u32Buf);

            Span<byte> fltBuf = stackalloc byte[4];
            int toWrite = truncateIntensityData ? 1 : actualIntensityCount;
            for (int i = 0; i < toWrite; i++)
            {
                float val = i < points.Count ? (points[i].Intensity ?? 0f) : 0f;
                BinaryPrimitives.WriteSingleLittleEndian(fltBuf, val);
                ms.Write(fltBuf);
            }

            if (truncateIntensityData)
                return;

            // Write 4 trailing bytes with float value 99f to ensure reading past actualIntensityCount would read 99
            BinaryPrimitives.WriteSingleLittleEndian(fltBuf, 99f);
            ms.Write(fltBuf);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(u32Buf, 0);
            ms.Write(u32Buf);
        }
    }

    private static void WriteCcBinArrayFooter(Stream ms, int size, double[]? matrix = null)
    {
        byte[] footer = new byte[size];
        for (int i = 0; i < (size - 8) / 4; i++)
        {
            float value = matrix != null && i < matrix.Length ? (float)matrix[i] : i % 5 == 0 ? 1f : 0f;
            BinaryPrimitives.WriteSingleLittleEndian(footer.AsSpan(8 + i * 4), value);
        }
        ms.Write(footer);
    }
}
