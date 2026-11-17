using System.Text;
using System.Text.RegularExpressions;
using PclIO.Internal;

namespace PclIO.Formats.Leica;

/// <summary>Represents a Leica Geosystems scan container (.lgsx / .lgs).</summary>
public sealed class LgsxDocument : PclDocument
{
    // Signature constants the parser needs to recognise the container.
    private static ReadOnlySpan<byte> ContainerMagic => "wild::geosystems"u8;

    // Signature of the point data payload and of a point block inside it.
    internal static ReadOnlySpan<byte> PointDataMagic => "mMPK"u8;
    internal static ReadOnlySpan<byte> BlockMagic => "APRN"u8;

    // Bounds on file-declared sizes so crafted files cannot force large reads.
    private const int MaxTocEntries = 10_000;
    private const int EntryHeaderBytes = 128;
    private const int TailBytes = 4096;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex GuidPattern = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex VersionPattern = new(
        @"20\d\d\.\d+\.\d+[\.r\w]*", RegexOptions.CultureInvariant, RegexTimeout);

    /// <inheritdoc />
    public override PclFileKind Kind => _isLgsLegacy ? PclFileKind.LeicaLgs : PclFileKind.LeicaLgsx;

    /// <summary>Project GUID found in the file tail, or an empty string when the file does not contain one.</summary>
    public string ProjectGuid { get; }

    /// <summary>Software version string found in the file tail, or an empty string when the file does not contain one.</summary>
    public string SoftwareVersion { get; }

    /// <summary>Table of contents entries in file order.</summary>
    public IReadOnlyList<LgsxEntry> Entries => _entries;

    /// <summary>Setups listed in the container, in file order.</summary>
    public IReadOnlyList<LgsxSetup> Setups => _setups;

    /// <inheritdoc />
    public override IReadOnlyList<PclScan> Scans => _scans;

    private readonly List<PclScan> _scans = [];
    private readonly List<LgsxEntry> _entries = [];
    private readonly List<LgsxSetup> _setups = [];
    private readonly bool _isLgsLegacy;

    private LgsxDocument(string path, long fileSize, string guid, string version, bool isLegacy)
        : base(path, fileSize)
    {
        ProjectGuid = guid;
        SoftwareVersion = version;
        _isLgsLegacy = isLegacy;

        if (guid.Length > 0) _metadata["ProjectGuid"] = guid;
        if (version.Length > 0) _metadata["SoftwareVersion"] = version;
        _metadata["Format"] = isLegacy ? "Leica LGS" : "Leica LGSx";
    }

    /// <summary>Opens a Leica container and reads its table of contents.</summary>
    /// <param name="path">Path of the .lgsx or .lgs file.</param>
    /// <param name="options">Read options; currently unused when opening.</param>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is not a valid or complete container.</exception>
    public static LgsxDocument Open(string path, PclReadOptions? options = null)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("Leica LGSx file not found.", path);

        using var fs = File.OpenRead(path);
        long fileLength = fs.Length;

        byte[] magic = new byte[ContainerMagic.Length];
        if (fs.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false) < magic.Length || !magic.AsSpan().SequenceEqual(ContainerMagic))
            throw FormatError.Corrupted("not a Leica LGSx file, container signature mismatch.");

        bool isLegacy = path.EndsWith(".lgs", StringComparison.OrdinalIgnoreCase);

        // The last 8 bytes hold the offset of the table of contents.
        fs.Seek(-8, SeekOrigin.End);
        byte[] tocOffsetBytes = new byte[8];
        fs.ReadExactly(tocOffsetBytes);
        ulong tocOffset = Bytes.ReadUInt64LE(tocOffsetBytes);

        // The table needs at least its 4-byte entry count and must end before the trailing offset.
        if (tocOffset == 0 || tocOffset > (ulong)(fileLength - 8 - 4))
            throw FormatError.Corrupted("table of contents offset is outside the file.");

        // Metadata is optional; it is looked up by pattern in the file tail and stays empty when no match is found.
        int tailLength = (int)Math.Min(TailBytes, fileLength);
        fs.Seek(fileLength - tailLength, SeekOrigin.Begin);
        byte[] tailBuffer = new byte[tailLength];
        fs.ReadExactly(tailBuffer, 0, tailLength);
        string tailText = Encoding.ASCII.GetString(tailBuffer);

        string guid = GuidPattern.Match(tailText) is { Success: true } guidMatch ? guidMatch.Value : string.Empty;
        string version = VersionPattern.Match(tailText) is { Success: true } verMatch ? verMatch.Value : string.Empty;

        var document = new LgsxDocument(path, fileLength, guid, version, isLegacy);

        // Parse the table of contents.
        fs.Seek((long)tocOffset, SeekOrigin.Begin);
        byte[] countBytes = new byte[4];
        fs.ReadExactly(countBytes);
        uint entryCount = Bytes.ReadUInt32LE(countBytes);

        if (entryCount > MaxTocEntries)
            throw FormatError.Corrupted($"table of contents declares {entryCount} entries; at most {MaxTocEntries} are accepted.");
        if (entryCount == 0) return document;

        long tocTableEnd = (long)tocOffset + 4 + (long)entryCount * 8;
        if (tocTableEnd > fileLength - 8)
            throw FormatError.Truncated("the complete table of contents");

        byte[] tocOffsets = new byte[entryCount * 8];
        fs.ReadExactly(tocOffsets);

        var scanSetups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        LgsxEntry? pointCloudEntry = null;

        for (int i = 0; i < entryCount; i++)
        {
            ulong entryOffset = Bytes.ReadUInt64LE(tocOffsets.AsSpan(i * 8, 8));
            if (entryOffset >= (ulong)fileLength)
            {
                document._warnings.Add($"Table of contents entry {i} points outside the file and was skipped.");
                continue;
            }

            fs.Seek((long)entryOffset, SeekOrigin.Begin);
            byte[] entryHeader = new byte[EntryHeaderBytes];
            int r = fs.Read(entryHeader, 0, entryHeader.Length);
            if (r < 32)
            {
                document._warnings.Add($"Table of contents entry {i} is truncated and was skipped.");
                continue;
            }

            // Fields used: name length, name, payload offset and payload length.
            ushort nlen = Bytes.ReadUInt16LE(entryHeader.AsSpan(10, 2));
            string name;
            ulong payloadOffset = entryOffset;
            ulong payloadLength = 0;
            bool layoutParsed = nlen > 0 && nlen <= 120 && 12 + nlen + 17 <= r;

            if (layoutParsed)
            {
                name = Encoding.ASCII.GetString(entryHeader, 12, nlen).TrimEnd('\0');
                int postNameOff = 12 + nlen;
                payloadOffset = Bytes.ReadUInt64LE(entryHeader.AsSpan(postNameOff + 1, 8));
                payloadLength = Bytes.ReadUInt64LE(entryHeader.AsSpan(postNameOff + 9, 8));
            }
            else
            {
                // The name field is unusable; the entry stays unnamed instead of guessing a name.
                name = $"Entry_{i}";
                document._warnings.Add($"Table of contents entry {i} has an unrecognised layout; its payload location is unknown.");
            }

            bool isPointCloud = layoutParsed && name.Equals("PointCloud", StringComparison.OrdinalIgnoreCase);
            if (layoutParsed && (payloadOffset >= (ulong)fileLength || payloadLength > (ulong)fileLength - payloadOffset))
            {
                // Only the point data entry is dereferenced later, so only it is fatal.
                if (isPointCloud) throw FormatError.Corrupted("point data entry declares a payload outside the file.");
                document._warnings.Add($"Entry '{name}' declares a payload range outside the file.");
            }

            var entry = new LgsxEntry(name, payloadOffset, payloadLength);
            document._entries.Add(entry);

            if (isPointCloud)
            {
                pointCloudEntry = entry;
            }
            else if (name.StartsWith("SETUP-", StringComparison.OrdinalIgnoreCase))
            {
                int lastDash = name.LastIndexOf('-');
                if (lastDash > 0)
                {
                    string setupId = name[(lastDash + 1)..];
                    scanSetups.Add(setupId);
                    document._setups.Add(new LgsxSetup(setupId, name, payloadOffset, payloadLength));
                }
            }
        }

        // The points are assigned to the setups when the container says which part of the point data belongs to
        // each of them; otherwise all points stay in one scan.
        var assignments = pointCloudEntry != null
            ? AssignBlocksToSetups(path, pointCloudEntry, document._setups)
            : null;

        if (assignments != null)
        {
            foreach (var (setup, blockStarts) in assignments)
            {
                document._scans.Add(new LgsxScan(
                    id: setup.Id,
                    name: setup.Name,
                    path: path,
                    pointCloudEntry: pointCloudEntry!,
                    blockStarts: blockStarts));
            }

            document._warnings.Add(
                $"The container lists {document._setups.Count} setups; its points are assigned to {assignments.Count} of them.");
        }
        else
        {
            // One scan for the whole container: the file's assignment of points to setups is not read.
            if (scanSetups.Count > 0 || pointCloudEntry != null)
            {
                document._scans.Add(new LgsxScan(
                    id: "unified-cloud",
                    name: Path.GetFileNameWithoutExtension(path),
                    path: path,
                    pointCloudEntry: pointCloudEntry));
            }

            if (scanSetups.Count > 0)
                document._warnings.Add($"The container lists {scanSetups.Count} setup entries; points are not assigned to them and are returned as one scan.");
        }

        if (document._scans.Count > 0)
        {
            if (pointCloudEntry == null)
                document._warnings.Add("No point data entry was found; scans have no readable points.");
            document._warnings.Add("Scan poses and bounds are not read; points are reported in container coordinates.");
        }

        return document;
    }

    /// <summary>
    /// Assigns the point blocks of the point data to the setups that own them. A setup owns a range of the point data
    /// when the range it declares lies inside the point data; a block belongs to the setup whose range holds it.
    /// Returns null when the container does not assign all of its points to setups, so that the caller can fall
    /// back to a single scan.
    /// </summary>
    private static List<(LgsxSetup Setup, long[] BlockStarts)>? AssignBlocksToSetups(
        string path, LgsxEntry pointCloudEntry, List<LgsxSetup> setups)
    {
        if (setups.Count == 0) return null;

        long payloadStart = (long)pointCloudEntry.Offset;
        long payloadEnd = payloadStart + (long)pointCloudEntry.Size;

        // Only a setup whose range lies inside the point data can own points of it.
        var owners = new List<(LgsxSetup Setup, long Start, long End)>();
        foreach (var setup in setups)
        {
            if (setup.Size == 0) continue;
            long start = (long)setup.Offset;
            long end = start + (long)setup.Size;
            if (start < payloadStart || end > payloadEnd) continue;
            owners.Add((setup, start, end));
        }

        if (owners.Count == 0) return null;

        // Setups are matched by the id after the last dash of their entry name. Two entries whose names end in the
        // same suffix share that id, and both would receive the blocks assigned to it, returning points twice.
        // The assignment is ambiguous, so the points stay together.
        for (int i = 0; i < setups.Count; i++)
            for (int j = i + 1; j < setups.Count; j++)
                if (setups[i].Id.Equals(setups[j].Id, StringComparison.Ordinal))
                    return null;

        // Two setups that claim the same part of the point data would make the assignment ambiguous.
        for (int i = 0; i < owners.Count; i++)
            for (int j = i + 1; j < owners.Count; j++)
                if (owners[i].Start < owners[j].End && owners[j].Start < owners[i].End)
                    return null;

        List<(long Start, long Length)> chain;
        try
        {
            chain = ReadBlockChain(path, pointCloudEntry);
        }
        catch (InvalidDataException)
        {
            // The point data is damaged; the scan reports it when its points are read.
            return null;
        }

        var bySetup = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        foreach (var (blockStart, _) in chain)
        {
            var owner = owners.Find(o => blockStart >= o.Start && blockStart < o.End);
            if (owner.Setup is null) return null; // a block that no setup owns
            if (!bySetup.TryGetValue(owner.Setup.Id, out var starts))
            {
                starts = [];
                bySetup[owner.Setup.Id] = starts;
            }

            starts.Add(blockStart);
        }

        if (bySetup.Count == 0) return null;

        var result = new List<(LgsxSetup, long[])>();
        foreach (var setup in setups)
            if (bySetup.TryGetValue(setup.Id, out var starts))
                result.Add((setup, [.. starts]));
        return result;
    }

    /// <summary>Reads the chain of point blocks of a point data payload: their start offsets and their lengths.</summary>
    /// <exception cref="InvalidDataException">The payload or a block header is malformed.</exception>
    internal static List<(long Start, long Length)> ReadBlockChain(string path, LgsxEntry entry)
    {
        using var fs = File.OpenRead(path);
        return ReadBlockChain(fs, entry);
    }

    /// <summary>Reads the chain of point blocks of a point data payload: their start offsets and their lengths.</summary>
    /// <exception cref="InvalidDataException">The payload or a block header is malformed.</exception>
    internal static List<(long Start, long Length)> ReadBlockChain(FileStream fs, LgsxEntry entry)
    {
        var chain = new List<(long Start, long Length)>();

        long fileLength = fs.Length;
        long payloadOffset = (long)entry.Offset;
        if (payloadOffset > fileLength - 64) throw FormatError.Truncated("the point data header");

        fs.Position = payloadOffset;
        byte[] payloadHeader = new byte[64];
        fs.ReadExactly(payloadHeader);
        if (!payloadHeader.AsSpan(0, 4).SequenceEqual(PointDataMagic))
            throw FormatError.Corrupted("point data signature mismatch.");

        // The first block offset is relative to the payload start.
        uint blockRelOffset = Bytes.ReadUInt32LE(payloadHeader.AsSpan(0x20, 4));
        long curBlockOffset = payloadOffset + blockRelOffset;
        if (curBlockOffset <= payloadOffset || curBlockOffset >= fileLength)
            throw FormatError.Corrupted("first point block offset is outside the file.");

        byte[] magicBuf = new byte[8];
        while (curBlockOffset + 64 < fileLength)
        {
            fs.Position = curBlockOffset;
            fs.ReadExactly(magicBuf);

            // Anything other than a block signature ends the chain.
            if (!magicBuf.AsSpan(0, 4).SequenceEqual(BlockMagic)) break;

            uint chunkLen = Bytes.ReadUInt32LE(magicBuf.AsSpan(4, 4));
            if (chunkLen < 8 + 128 || chunkLen > fileLength - curBlockOffset)
                throw FormatError.Corrupted("point block length is outside the file.");

            chain.Add((curBlockOffset, chunkLen));
            curBlockOffset += chunkLen;
        }

        return chain;
    }
}

/// <summary>Table of contents entry inside a Leica scan container.</summary>
/// <param name="Name">Entry name.</param>
/// <param name="Offset">Payload offset in bytes from the start of the file.</param>
/// <param name="Size">Payload length in bytes.</param>
public sealed record LgsxEntry(string Name, ulong Offset, ulong Size);

/// <summary>A setup listed in a Leica scan container.</summary>
/// <param name="Id">Identifier of the setup, the part of the entry name after its last dash.</param>
/// <param name="Name">Name of the container entry that lists the setup.</param>
/// <param name="Offset">Offset of the range the setup declares, in bytes from the start of the file.</param>
/// <param name="Size">Length of the range the setup declares, in bytes.</param>
public sealed record LgsxSetup(string Id, string Name, ulong Offset, ulong Size);

/// <summary>
/// Scan representation in a Leica LGS/LGSx container: the points of one setup, or all points of the container when
/// the container does not assign them to its setups.
/// </summary>
public sealed class LgsxScan : PclScan
{
    // Signature of a compressed frame inside a point block.
    private static ReadOnlySpan<byte> FrameMagic => [0x28, 0xB5, 0x2F, 0xFD];

    // Limits that bound memory and work for sizes declared by the file.
    private const int MaxCompressedFrameBytes = 10_000_000;
    private const int MaxFrameBytes = 16 * 1024 * 1024;
    private const int MaxFramesPerBlock = 64;
    private const long MaxBlockBytes = 128L * 1024 * 1024;

    // Decoded bytes accepted over all blocks of one read: a fixed allowance plus a multiple of the file size.
    private const long DecodedAllowanceBytes = 1L << 30;
    private const long DecodedBytesPerFileByte = 64;

    // Minimum plane counts at which an optional channel is read.
    private const int ColorFrames = 14;
    private const int IntensityFrames = 16;
    private const int NormalFrames = 19;

    private readonly string _filePath;
    private readonly LgsxEntry? _entry;

    // Start offsets of the blocks that belong to this scan; null when the scan holds all blocks of the container.
    private readonly long[]? _blockStarts;

    private long _pointCount = -1;
    private (bool Colors, bool Intensities, bool Normals)? _channels;

    /// <inheritdoc />
    public override string Id { get; }

    /// <inheritdoc />
    public override string Name { get; }

    /// <summary>
    /// Number of points this scan yields. The container does not store it, so the first access decodes
    /// the whole point data and caches the result; this can be slow for large files.
    /// </summary>
    /// <exception cref="InvalidDataException">The point data is malformed; the exception is thrown by the first access.</exception>
    public override long PointCount => _pointCount >= 0 ? _pointCount : _pointCount = CountPoints();

    /// <summary>True when the first point block carries colour planes.</summary>
    public override bool HasColors => Channels.Colors;

    /// <summary>True when the first point block carries intensity planes.</summary>
    public override bool HasIntensities => Channels.Intensities;

    /// <summary>True when the first point block carries normal planes.</summary>
    public override bool HasNormals => Channels.Normals;

    private (bool Colors, bool Intensities, bool Normals) Channels => _channels ??= DetectChannels();

    internal LgsxScan(string id, string name, string path, LgsxEntry? pointCloudEntry, long[]? blockStarts = null)
    {
        Id = id;
        Name = name;
        _filePath = path;
        _entry = pointCloudEntry;
        _blockStarts = blockStarts;
    }

    /// <summary>Decoded planes of one point block together with its coordinate frame.</summary>
    private sealed record Block(double ScaleX, double ScaleY, double ScaleZ, double MinX, double MinY, double MinZ, List<byte[]> Frames);

    private long CountPoints() => ReadPoints(new PclReadOptions { ApplyTransform = false }).LongCount();

    private (bool, bool, bool) DetectChannels()
    {
        if (_entry == null) return (false, false, false);

        using var fs = File.OpenRead(_filePath);
        foreach (var block in ReadBlocks(fs))
        {
            int n = block.Frames.Count;
            return (n >= ColorFrames, n >= IntensityFrames, n >= NormalFrames);
        }

        return (false, false, false);
    }

    /// <summary>Streams the points of this scan.</summary>
    /// <remarks>
    /// Positions are decoded from the packed position planes of each block. A code of zero at any slot except the
    /// first of a block is treated as unused padding and is not returned, so a real point that sits exactly at the
    /// block origin is dropped. A block yields as many points as its shortest position plane holds. Returned points
    /// keep consecutive <see cref="PclPoint.Index"/> values.
    /// </remarks>
    /// <param name="options">Read options; null uses defaults.</param>
    /// <exception cref="InvalidDataException">The point data is malformed or declares oversized data.</exception>
    public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null)
    {
        options ??= PclReadOptions.Default;
        if (_entry == null) yield break;

        long targetCount = options.MaxPoints ?? long.MaxValue;
        if (targetCount <= 0) yield break;
        int step = Math.Max(1, options.SubsampleStep);
        long yielded = 0;

        using var fs = File.OpenRead(_filePath);
        foreach (var block in ReadBlocks(fs))
        {
            var frames = block.Frames;
            byte[] p0 = frames[0];
            byte[] p1 = frames[1];
            byte[] p2 = frames[2];
            byte[] p3 = frames[3];

            // Optional channels follow the position planes; normals are the last three planes.
            byte[][]? colPlanes = frames.Count >= ColorFrames ? [frames[12], frames[13]] : null;
            byte[][]? intPlanes = frames.Count >= IntensityFrames ? [frames[14], frames[15]] : null;
            byte[][]? normPlanes = frames.Count >= NormalFrames ? [frames[^3], frames[^2], frames[^1]] : null;

            int numPoints = Math.Min(p0.Length, Math.Min(p1.Length, Math.Min(p2.Length, p3.Length)));

            for (int i = 0; i < numPoints; i++)
            {
                if (i % step != 0) continue;

                uint val = (uint)(p0[i] | (p1[i] << 8) | (p2[i] << 16) | (p3[i] << 24));
                if (val == 0 && i > 0 && p0[i] == 0 && p1[i] == 0) continue;

                // Unpack the three 10-bit coordinates from the packed code.
                uint xm = 0, ym = 0, zm = 0;
                for (int b = 0; b < 10; b++)
                {
                    xm |= ((val >> (b * 3)) & 1) << b;
                    ym |= ((val >> (b * 3 + 1)) & 1) << b;
                    zm |= ((val >> (b * 3 + 2)) & 1) << b;
                }

                double x = block.MinX + (xm * block.ScaleX);
                double y = block.MinY + (ym * block.ScaleY);
                double z = block.MinZ + (zm * block.ScaleZ);

                // 16-bit packed colour; zero means no colour.
                PclColor? color = null;
                if (colPlanes != null && i < colPlanes[0].Length && i < colPlanes[1].Length)
                {
                    ushort cval = (ushort)(colPlanes[0][i] | (colPlanes[1][i] << 8));
                    if (cval != 0)
                    {
                        byte red = (byte)(((cval >> 11) & 0x1F) * 255 / 31);
                        byte green = (byte)(((cval >> 5) & 0x3F) * 255 / 63);
                        byte blue = (byte)((cval & 0x1F) * 255 / 31);
                        color = new PclColor(red, green, blue);
                    }
                }

                // 16-bit intensity; unknown when the plane is absent.
                float? intensity = null;
                if (intPlanes != null && i < intPlanes[0].Length && i < intPlanes[1].Length)
                {
                    ushort ival = (ushort)(intPlanes[0][i] | (intPlanes[1][i] << 8));
                    intensity = ival / 65535.0f;
                }

                // Signed-byte components; all zero means no normal.
                PclVector3? normal = null;
                if (normPlanes != null && i < normPlanes[0].Length && i < normPlanes[1].Length && i < normPlanes[2].Length)
                {
                    sbyte nx = (sbyte)normPlanes[0][i];
                    sbyte ny = (sbyte)normPlanes[1][i];
                    sbyte nz = (sbyte)normPlanes[2][i];
                    if (nx != 0 || ny != 0 || nz != 0)
                    {
                        normal = new PclVector3(nx / 127.0, ny / 127.0, nz / 127.0);
                    }
                }

                var pt = new PclPoint(
                    new PclVector3(x, y, z),
                    Color: color,
                    Intensity: intensity,
                    Normal: normal,
                    Index: (ulong)yielded);

                if (options.ApplyTransform)
                    pt = pt with { Position = Transform.TransformPoint(pt.Position) };

                yield return pt;
                yielded++;
                if (yielded >= targetCount) yield break;
            }
        }
    }

    /// <summary>Walks the chain of point blocks and yields those that carry point planes and belong to this scan.</summary>
    private IEnumerable<Block> ReadBlocks(FileStream fs)
    {
        long fileLength = fs.Length;
        long decodedLimit = DecodedAllowanceBytes + (fileLength * DecodedBytesPerFileByte);
        long decodedBytes = 0;
        byte[] blockHeader = new byte[128];

        foreach (var (blockStart, blockLength) in LgsxDocument.ReadBlockChain(fs, _entry!))
        {
            if (!BelongsToScan(blockStart)) continue;

            // The block header follows the 8-byte block signature and length.
            fs.Position = blockStart + 8;
            fs.ReadExactly(blockHeader);

            double scaleX = Bytes.ReadDoubleLE(blockHeader.AsSpan(0x34, 8));
            double scaleY = Bytes.ReadDoubleLE(blockHeader.AsSpan(0x3C, 8));
            double scaleZ = Bytes.ReadDoubleLE(blockHeader.AsSpan(0x44, 8));
            double minX = Bytes.ReadDoubleLE(blockHeader.AsSpan(0x4C, 8));
            double minY = Bytes.ReadDoubleLE(blockHeader.AsSpan(0x54, 8));
            double minZ = Bytes.ReadDoubleLE(blockHeader.AsSpan(0x5C, 8));

            long blockEnd = blockStart + blockLength;

            // Find the first compressed frame; its 8-byte length prefix precedes it.
            byte[] searchBuf = new byte[(int)Math.Min(1024L, blockLength)];
            fs.Position = blockStart + 8;
            int bytesRead = fs.Read(searchBuf, 0, searchBuf.Length);
            int firstFrame = -1;
            for (int s = 0; s < bytesRead - 4; s++)
            {
                if (searchBuf.AsSpan(s, 4).SequenceEqual(FrameMagic))
                {
                    firstFrame = s;
                    break;
                }
            }

            if (firstFrame >= 8)
            {
                long firstFrameOffset = blockStart + firstFrame;
                var frames = ReadAllFrames(fs, firstFrameOffset, blockEnd);

                // Many small blocks that each expand a lot would otherwise make the file cost unbounded time.
                foreach (byte[] plane in frames) decodedBytes += plane.Length;
                if (decodedBytes > decodedLimit)
                    throw FormatError.Corrupted("the point data decompresses to more than the accepted total size.");

                // Blocks with fewer than four planes carry no positions.
                if (frames.Count >= 4)
                {
                    if (!(double.IsFinite(scaleX) && scaleX > 0 && double.IsFinite(scaleY) && scaleY > 0 &&
                          double.IsFinite(scaleZ) && scaleZ > 0 &&
                          double.IsFinite(minX) && double.IsFinite(minY) && double.IsFinite(minZ)))
                        throw FormatError.Corrupted("point block has an invalid coordinate scale or origin.");

                    yield return new Block(scaleX, scaleY, scaleZ, minX, minY, minZ, frames);
                }
            }
        }
    }

    // A scan without its own blocks holds all of them; otherwise only the blocks the container assigned to it.
    private bool BelongsToScan(long blockStart) =>
        _blockStarts == null || Array.BinarySearch(_blockStarts, blockStart) >= 0;

    private static List<byte[]> ReadAllFrames(FileStream fs, long offset, long maxOffset)
    {
        var list = new List<byte[]>();
        long limit = Math.Min(maxOffset, fs.Length);
        if (offset < 0 || offset + 8 >= limit) return list;

        fs.Position = offset;
        byte[] lenBuf = new byte[8];
        long totalBytes = 0;

        while (fs.Position + 8 <= limit)
        {
            fs.ReadExactly(lenBuf);

            // A zero or out-of-range length marks the end of the frame list.
            ulong fLen = Bytes.ReadUInt64LE(lenBuf);
            if (fLen == 0 || fLen > MaxCompressedFrameBytes || (long)fLen > limit - fs.Position) break;

            byte[] fData = new byte[(int)fLen];
            fs.ReadExactly(fData);

            if (!fData.AsSpan().StartsWith(FrameMagic)) break;

            if (list.Count >= MaxFramesPerBlock)
                throw FormatError.Corrupted($"point block has more than {MaxFramesPerBlock} planes.");

            // The size cap is checked against the frame's declared size before any allocation.
            byte[] plane = ZstdDecoder.Decompress(fData, MaxFrameBytes);

            totalBytes += plane.Length;
            if (totalBytes > MaxBlockBytes)
                throw FormatError.Corrupted($"point block decompresses to more than {MaxBlockBytes} bytes.");

            list.Add(plane);
        }

        return list;
    }
}
