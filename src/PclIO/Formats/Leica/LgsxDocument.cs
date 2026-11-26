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

    // Size of the record of a setup: the translation and the bounds of the setup as six doubles, then the offset
    // and the length of the range of the point data that belongs to it and the number of its points.
    private const int SetupRecordSize = 96;

    // Offset of the number of points of the point data in the header of the payload; zero when it is not stored.
    private const int PayloadPointCountOffset = 0x28;

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

    internal bool HasUnsupportedLegacyPoints { get; private set; }
    internal const string UnsupportedLegacyPointsMessage =
        "The Leica LGS point encoding or data mask is unsupported. Convert the project to LGSx to read its points.";

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

    /// <summary>
    /// Adds a warning to the document. The same message is added only once, so that reading the same scan twice
    /// does not repeat it. Reads of different scans may add warnings at the same time, so the list is guarded.
    /// </summary>
    internal void AddWarning(string message)
    {
        lock (_warnings)
        {
            if (!_warnings.Contains(message, StringComparer.Ordinal)) _warnings.Add(message);
        }
    }

    /// <summary>
    /// Returns the declared point count of a supported native point section, or null when unavailable.
    /// This reads metadata without decoding the points.
    /// </summary>
    public long? GetEncodedPointCount()
    {
        if (_scans.FirstOrDefault() is LegacyLgsScan legacy) return legacy.PointCount;
        var entry = Entries.FirstOrDefault(e => e.Name.Equals("PointCloud", StringComparison.OrdinalIgnoreCase));
        if (entry == null || HasUnsupportedLegacyPoints) return null;
        using var stream = File.OpenRead(FilePath);
        var chain = ReadBlockChain(stream, entry);
        if (chain.Count == 0) return null;

        Span<byte> header = stackalloc byte[16];
        long total = 0;
        foreach (var (start, _) in chain)
        {
            stream.Position = start + 12;
            stream.ReadExactly(header);
            if (Bytes.ReadUInt32LE(header) != LgsxScan.Morton96Version) return null;
            ulong count = Bytes.ReadUInt64LE(header[8..]);
            if (count is 0 or > LgsxScan.MaxFrameBytes)
                throw FormatError.Corrupted("point block declares an invalid point count.");
            total = checked(total + (long)count);
        }

        return total;
    }

    // Both containers share the leading signature. Published files distinguish their format in the footer.
    internal static PclFileKind? DetectFooterKind(Stream stream)
    {
        ReadOnlySpan<byte> signature = "Leica Project File "u8;
        Span<byte> footer = stackalloc byte[signature.Length + 1 + sizeof(uint) + sizeof(ulong)];
        long origin = stream.Position;
        if (stream.Length - origin < ContainerMagic.Length + footer.Length) return null;
        try
        {
            stream.Position = stream.Length - footer.Length;
            if (stream.ReadAtLeast(footer, footer.Length, throwOnEndOfStream: false) != footer.Length ||
                !footer.StartsWith(signature))
                return null;

            return footer[signature.Length] switch
            {
                (byte)'*' => PclFileKind.LeicaLgs,
                (byte)'X' => PclFileKind.LeicaLgsx,
                _ => null,
            };
        }
        finally
        {
            stream.Position = origin;
        }
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

        PclFileKind? footerKind = DetectFooterKind(fs);
        bool isLegacy = footerKind == PclFileKind.LeicaLgs ||
            (footerKind == null && path.EndsWith(".lgs", StringComparison.OrdinalIgnoreCase));

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

                    var setup = new LgsxSetup(setupId, name, payloadOffset, payloadLength);

                    // Only the LGSx setup record layout is known.
                    var record = footerKind != PclFileKind.LeicaLgs ? ReadSetupRecord(fs, setup) : null;
                    if (record != null)
                    {
                        setup = setup with
                        {
                            Translation = record.Translation,
                            Bounds = record.Bounds,
                            PointOffset = record.PointOffset,
                            PointLength = record.PointLength,
                            PointCount = record.PointCount,
                        };
                    }

                    document._setups.Add(setup);
                }
            }
        }

        // The points are assigned to the setups when the container says which part of the point data belongs to
        // each of them; otherwise all points stay in one scan.
        if (footerKind == PclFileKind.LeicaLgs &&
            pointCloudEntry is { Size: >= 4 } && !HasReadablePointHeader(fs, pointCloudEntry))
        {
            if (LegacyLgsScan.TryOpen(fs, pointCloudEntry) is { } scan)
            {
                document._scans.Add(scan);
                return document;
            }
            document.HasUnsupportedLegacyPoints = true;
        }
        var assignments = pointCloudEntry != null && !document.HasUnsupportedLegacyPoints
            ? AssignBlocksToSetups(path, pointCloudEntry, document._setups)
            : null;

        // The number of points is read from the container when it states it; otherwise it is counted by decoding.
        long? declaredCount = pointCloudEntry != null && !document.HasUnsupportedLegacyPoints
            ? ReadDeclaredPointCount(fs, pointCloudEntry)
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
                    blockStarts: blockStarts,
                    transform: setup.Translation is { } translation ? new PclTransform { Translation = translation } : null,
                    bounds: setup.Bounds,
                    declaredPointCount: setup.PointCount >= 0 ? setup.PointCount : null,
                    document: document));
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
                    pointCloudEntry: pointCloudEntry,
                    declaredPointCount: declaredCount,
                    document: document));
            }

            if (scanSetups.Count > 0 && !document.HasUnsupportedLegacyPoints)
                document._warnings.Add($"The container lists {scanSetups.Count} setup entries; points are not assigned to them and are returned as one scan.");
        }

        if (document.HasUnsupportedLegacyPoints)
            document._warnings.Add(UnsupportedLegacyPointsMessage);
        else if (document._scans.Count > 0)
        {
            if (pointCloudEntry == null)
                document._warnings.Add("No point data entry was found; scans have no readable points.");

            if (assignments != null)
                document._warnings.Add($"The poses and the bounds of {assignments.Count} setups are read and applied.");
            else
                document._warnings.Add("Scan poses and bounds are not read; points are reported in container coordinates.");
        }

        return document;
    }

    /// <summary>
    /// The record of a setup: the translation of the setup, the bounds of its points, the range of the point
    /// blocks it owns inside the point data and the number of its points.
    /// </summary>
    private sealed record SetupRecord(PclVector3 Translation, PclBounds Bounds, ulong PointOffset, ulong PointLength, long PointCount);

    /// <summary>
    /// Reads the record of a setup from the payload of its container entry. The record holds the translation of the
    /// setup and the bounds of its points as six doubles, then the offset and the length of the range of the point
    /// blocks it owns and the number of its points. A record that is too short or holds values that are not usable is
    /// left unread, so that the setup keeps an unknown pose instead of a wrong one.
    /// </summary>
    private static SetupRecord? ReadSetupRecord(FileStream fs, LgsxSetup setup)
    {
        if (setup.Size < SetupRecordSize) return null;
        if (setup.Offset >= (ulong)fs.Length || setup.Size > (ulong)fs.Length - setup.Offset) return null;

        fs.Position = (long)setup.Offset;
        byte[] data = new byte[SetupRecordSize];
        fs.ReadExactly(data);

        double tx = Bytes.ReadDoubleLE(data.AsSpan(0, 8));
        double ty = Bytes.ReadDoubleLE(data.AsSpan(8, 8));
        double tz = Bytes.ReadDoubleLE(data.AsSpan(16, 8));
        double minX = Bytes.ReadDoubleLE(data.AsSpan(24, 8));
        double minY = Bytes.ReadDoubleLE(data.AsSpan(32, 8));
        double minZ = Bytes.ReadDoubleLE(data.AsSpan(40, 8));
        double maxX = Bytes.ReadDoubleLE(data.AsSpan(48, 8));
        double maxY = Bytes.ReadDoubleLE(data.AsSpan(56, 8));
        double maxZ = Bytes.ReadDoubleLE(data.AsSpan(64, 8));

        if (!double.IsFinite(tx) || !double.IsFinite(ty) || !double.IsFinite(tz) ||
            !double.IsFinite(minX) || !double.IsFinite(minY) || !double.IsFinite(minZ) ||
            !double.IsFinite(maxX) || !double.IsFinite(maxY) || !double.IsFinite(maxZ))
            return null;

        var bounds = new PclBounds(new PclVector3(minX, minY, minZ), new PclVector3(maxX, maxY, maxZ));
        if (bounds.IsEmpty) return null;

        ulong pointOffset = Bytes.ReadUInt64LE(data.AsSpan(72, 8));
        ulong pointLength = Bytes.ReadUInt64LE(data.AsSpan(80, 8));
        ulong pointCount = Bytes.ReadUInt64LE(data.AsSpan(88, 8));

        return new SetupRecord(
            new PclVector3(tx, ty, tz),
            bounds,
            pointOffset,
            pointLength,
            pointCount > long.MaxValue ? -1 : (long)pointCount);
    }

    private static bool HasReadablePointHeader(FileStream fs, LgsxEntry entry)
    {
        Span<byte> header = stackalloc byte[4];
        fs.Position = (long)entry.Offset;
        fs.ReadExactly(header);
        return header.SequenceEqual(PointDataMagic);
    }

    /// <summary>
    /// Reads the number of points that the header of the point data declares, or null when it declares none.
    /// A damaged header yields no count, so that the points are counted by decoding them.
    /// </summary>
    private static long? ReadDeclaredPointCount(FileStream fs, LgsxEntry entry)
    {
        long payloadOffset = (long)entry.Offset;
        if (payloadOffset < 0 || payloadOffset > fs.Length - 64) return null;

        fs.Position = payloadOffset;
        byte[] header = new byte[64];
        fs.ReadExactly(header);
        if (!header.AsSpan(0, 4).SequenceEqual(PointDataMagic)) return null;

        ulong count = Bytes.ReadUInt64LE(header.AsSpan(PayloadPointCountOffset, 8));
        if (count == 0 || count > long.MaxValue) return null;
        return (long)count;
    }

    /// <summary>
    /// Assigns the point blocks of the point data to the setups that own them. A setup owns the range of the point
    /// data that its record declares; a block belongs to the setup whose range holds it. Returns null when the
    /// container does not assign all of its points to setups, so that the caller can fall back to a single scan.
    /// </summary>
    private static List<(LgsxSetup Setup, long[] BlockStarts)>? AssignBlocksToSetups(
        string path, LgsxEntry pointCloudEntry, List<LgsxSetup> setups)
    {
        if (setups.Count == 0) return null;

        long payloadStart = (long)pointCloudEntry.Offset;
        long payloadEnd = payloadStart + (long)pointCloudEntry.Size;

        // Only a setup whose record declares a range inside the point data can own points of it.
        var owners = new List<(LgsxSetup Setup, long Start, long End)>();
        foreach (var setup in setups)
        {
            if (setup.PointLength == 0) continue;
            long start = (long)setup.PointOffset;
            long end = start + (long)setup.PointLength;
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
/// <param name="Offset">Offset of the record of the setup, in bytes from the start of the file.</param>
/// <param name="Size">Length of the record of the setup, in bytes.</param>
public sealed record LgsxSetup(string Id, string Name, ulong Offset, ulong Size)
{
    /// <summary>Translation of the setup, or null when its record does not hold one.</summary>
    public PclVector3? Translation { get; init; }

    /// <summary>Bounds of the points of the setup, or null when its record does not hold them.</summary>
    public PclBounds? Bounds { get; init; }

    /// <summary>Offset of the range of the point data that belongs to the setup, in bytes from the start of the file.</summary>
    public ulong PointOffset { get; init; }

    /// <summary>Length of the range of the point data that belongs to the setup, in bytes.</summary>
    public ulong PointLength { get; init; }

    /// <summary>Number of points of the setup, or -1 when the container does not state it.</summary>
    public long PointCount { get; init; } = -1;
}

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
    internal const int MaxFrameBytes = 16 * 1024 * 1024;
    private const int MaxFramesPerBlock = 64;
    private const long MaxBlockBytes = 128L * 1024 * 1024;

    // Decoded bytes accepted over all blocks of one read: a fixed allowance plus a multiple of the file size.
    private const long DecodedAllowanceBytes = 1L << 30;
    private const long DecodedBytesPerFileByte = 64;

    // Minimum plane counts at which an optional channel is read.
    private const int ColorFrames = 14;
    private const int Morton96Planes = 12;
    internal const uint Morton96Version = 22;
    private const int IntensityFrames = 16;
    private const int NormalFrames = 19;

    private readonly string _filePath;
    private readonly LgsxEntry? _entry;
    private readonly LgsxDocument? _document;

    // Start offsets of the blocks that belong to this scan; null when the scan holds all blocks of the container.
    private readonly long[]? _blockStarts;

    // Number of points the container states for this scan; null when it states none.
    private readonly long? _declaredPointCount;

    private long _pointCount = -1;
    private (bool Colors, bool Intensities, bool Normals)? _channels;

    /// <summary>
    /// What one read did not return. Each enumeration keeps its own state, so interleaved or parallel reads of the
    /// same scan do not mix their counts; the state is reported when the read is done.
    /// </summary>
    private sealed class ReadState
    {
        public long DroppedPoints;
        public long SkippedStreams;
        public long BlocksRead;
        public long UnusableBlocks;
        public long IncompleteBlocks;
        public bool ChainEndedEarly;
        public long ChainBlocks;
    }

    /// <inheritdoc />
    public override string Id { get; }

    /// <inheritdoc />
    public override string Name { get; }

    /// <summary>
    /// Number of points this scan yields. The number the container states is used when it states one; otherwise the
    /// first access decodes the whole point data and caches the result, which can be slow for large files.
    /// </summary>
    /// <exception cref="InvalidDataException">The point data is malformed; the exception is thrown by the first access.</exception>
    /// <exception cref="NotSupportedException">The container uses an unsupported legacy point encoding.</exception>
    public override long PointCount =>
        _declaredPointCount ?? (_pointCount >= 0 ? _pointCount : _pointCount = CountPoints());

    /// <summary>True when the colour encoding of the first point block is supported.</summary>
    public override bool HasColors => Channels.Colors;

    /// <summary>True when the first point block carries intensity planes.</summary>
    public override bool HasIntensities => Channels.Intensities;

    /// <summary>True when the normal encoding of the first point block is supported.</summary>
    public override bool HasNormals => Channels.Normals;

    private (bool Colors, bool Intensities, bool Normals) Channels => _channels ??= DetectChannels();

    internal LgsxScan(string id, string name, string path, LgsxEntry? pointCloudEntry, long[]? blockStarts = null,
        PclTransform? transform = null, PclBounds? bounds = null, long? declaredPointCount = null, LgsxDocument? document = null)
    {
        Id = id;
        Name = name;
        _filePath = path;
        _entry = pointCloudEntry;
        _blockStarts = blockStarts;
        _declaredPointCount = declaredPointCount;
        _document = document;
        if (transform != null) Transform = transform;
        if (bounds != null) Bounds = bounds.Value;
    }

    /// <summary>Decoded planes of one point block together with its coordinate frame.</summary>
    private sealed record Block(double ScaleX, double ScaleY, double ScaleZ, double MinX, double MinY, double MinZ,
        List<byte[]> Frames, NativeLayout? Layout);

    private sealed record NativeLayout(int PointCount, int Intensity, int Color);

    private long CountPoints() => ReadPoints(new PclReadOptions { ApplyTransform = false }).LongCount();

    private (bool, bool, bool) DetectChannels()
    {
        if (_entry == null || _document?.HasUnsupportedLegacyPoints == true) return (false, false, false);

        using var fs = File.OpenRead(_filePath);
        foreach (var (start, length) in LgsxDocument.ReadBlockChain(fs, _entry))
        {
            if (!BelongsToScan(start)) continue;
            var (block, _, _, _, _) = DecodeBlock(fs, start, length);
            if (block == null) continue;
            if (block.Layout is { } layout) return (layout.Color >= 0, layout.Intensity >= 0, false);
            int n = block.Frames.Count;
            return (n >= ColorFrames, n >= IntensityFrames, n >= NormalFrames);
        }

        return (false, false, false);
    }

    /// <summary>Streams the points of this scan.</summary>
    /// <remarks>
    /// <para>
    /// Version 22 blocks store differential 96-bit Morton positions. A zero delta repeats the previous position.
    /// The legacy four-plane layout treats zero codes after the first slot as padding.
    /// Returned points keep consecutive <see cref="PclPoint.Index"/> values.
    /// </para>
    /// <para>
    /// With <see cref="PclReadOptions.Blocks"/> only the blocks of the scan in that range are read; without it all
    /// of them are. With <see cref="PclReadOptions.MaxDegreeOfParallelism"/> above one the blocks decode in parallel
    /// and the points still come out in file order; only the blocks of the scan are ever held decoded at once in the
    /// sequential read, while the parallel read holds the decoded points of the selected blocks.
    /// </para>
    /// <para>
    /// Data that the reader cannot use is counted and reported in the warnings of the document: the points that were
    /// dropped as padding, the compressed streams that were skipped because they are too large and the point data
    /// that ends at a block the reader does not know. With <see cref="PclReadOptions.Strict"/> the read fails with
    /// <see cref="InvalidDataException"/> instead of the warning.
    /// </para>
    /// </remarks>
    /// <param name="options">Read options; null uses defaults.</param>
    /// <exception cref="InvalidDataException">The point data is malformed, declares oversized data or is incomplete in strict mode.</exception>
    /// <exception cref="NotSupportedException">The container uses an unsupported legacy point encoding.</exception>
    public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null)
    {
        options ??= PclReadOptions.Default;
        if (_entry == null) yield break;

        long targetCount = options.MaxPoints ?? long.MaxValue;
        if (targetCount <= 0) yield break;
        if (_document?.HasUnsupportedLegacyPoints == true)
            throw FormatError.Unsupported(LgsxDocument.UnsupportedLegacyPointsMessage);
        int step = Math.Max(1, options.SubsampleStep);
        long yielded = 0;

        // The counters describe this read alone; they start at zero every time.
        var state = new ReadState();

        if (options.MaxDegreeOfParallelism > 1)
        {
            foreach (var pt in ReadPointsParallelOrdered(options, step, targetCount, state)) yield return pt;
            yield break;
        }

        using var fs = File.OpenRead(_filePath);
        var chain = ScanBlockChain(fs);
        SetChainEnd(fs, state, chain);
        var selected = ApplyBlockRange(chain.ScanChain, options.Blocks);
        long decodedLimit = DecodedAllowanceBytes + (fs.Length * DecodedBytesPerFileByte);
        long decodedBytes = 0;

        foreach (var (blockStart, blockLength) in selected)
        {
            var (block, added, skippedStreams, unusable, incomplete) = DecodeBlock(fs, blockStart, blockLength);
            state.SkippedStreams += skippedStreams;
            state.UnusableBlocks += unusable;
            state.IncompleteBlocks += incomplete;

            // Every decoded block counts toward the total, including one that carries no positions: many small
            // blocks that each expand a lot would otherwise make the file cost unbounded time.
            decodedBytes += added;
            if (decodedBytes > decodedLimit)
                throw FormatError.Corrupted("the point data decompresses to more than the accepted total size.");

            if (block == null) continue;
            state.BlocksRead++;

            var points = UnpackBlock(block, step, options.ApplyTransform, Transform, (ulong)yielded, out long dropped);
            state.DroppedPoints += dropped;

            foreach (var pt in points)
            {
                yield return pt;
                yielded++;
                if (yielded >= targetCount) yield break;
            }
        }

        // The whole point data was read, so what it did not hold can be reported.
        ReportIncompleteData(options.Strict, state);
    }

    /// <summary>Decodes the selected blocks in parallel and yields their points in file order.</summary>
    /// <remarks>
    /// Every block decodes on its own stream, so blocks never share a file position. The whole selection is
    /// decoded even when <see cref="PclReadOptions.MaxPoints"/> stops the reading early; <paramref name="targetCount"/>
    /// only caps how many of the decoded points are yielded. Warnings and the strict failure behave as in the
    /// sequential read, except that a truncated read (fewer points yielded than decoded) reports nothing.
    /// </remarks>
    private IEnumerable<PclPoint> ReadPointsParallelOrdered(PclReadOptions options, int step, long targetCount, ReadState state)
    {
        List<PclPoint>?[] perBlock;
        using (var fs = File.OpenRead(_filePath))
        {
            var chain = ScanBlockChain(fs);
            SetChainEnd(fs, state, chain);
            var selected = ApplyBlockRange(chain.ScanChain, options.Blocks);
            long decodedLimit = DecodedAllowanceBytes + (fs.Length * DecodedBytesPerFileByte);

            perBlock = new List<PclPoint>?[selected.Count];
            long dropped = 0, skipped = 0, blocks = 0, decodedTotal = 0, unusable = 0, incomplete = 0;
            try
            {
                Parallel.For(0, selected.Count,
                    new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism },
                    () => File.OpenRead(_filePath),
                    (i, _, stream) =>
                    {
                        var (blockStart, blockLength) = selected[i];
                        var (block, added, skippedStreams, u, inc) = DecodeBlock(stream, blockStart, blockLength);
                        Interlocked.Add(ref skipped, skippedStreams);
                        Interlocked.Add(ref unusable, u);
                        Interlocked.Add(ref incomplete, inc);

                        // Every decoded block counts toward the total, including one that carries no positions.
                        if (Interlocked.Add(ref decodedTotal, added) > decodedLimit)
                            throw FormatError.Corrupted("the point data decompresses to more than the accepted total size.");

                        if (block == null)
                        {
                            perBlock[i] = [];
                        }
                        else
                        {
                            perBlock[i] = UnpackBlock(block, step, options.ApplyTransform, Transform, 0, out long d);
                            Interlocked.Add(ref dropped, d);
                            Interlocked.Increment(ref blocks);
                        }

                        return stream;
                    },
                    static stream => stream.Dispose());
            }
            catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
                throw;
            }

            state.DroppedPoints = dropped;
            state.SkippedStreams = skipped;
            state.BlocksRead = blocks;
            state.UnusableBlocks = unusable;
            state.IncompleteBlocks = incomplete;
        }

        long yielded = 0;
        foreach (var list in perBlock)
        {
            if (list == null) continue;
            foreach (var pt in list)
            {
                if (yielded >= targetCount) yield break;
                yield return pt with { Index = (ulong)yielded };
                yielded++;
            }
        }

        if (yielded >= targetCount) yield break;

        // The whole selection was yielded, so what it did not hold can be reported.
        ReportIncompleteData(options.Strict, state);
    }

    /// <summary>Chain of point blocks for this scan: the full payload chain and the scan's own blocks in file order.</summary>
    private (List<(long Start, long Length)> FullChain, List<(long Start, long Length)> ScanChain, long PayloadEnd) ScanBlockChain(FileStream fs)
    {
        var full = LgsxDocument.ReadBlockChain(fs, _entry!);
        long payloadEnd = (long)_entry!.Offset + (long)_entry.Size;
        var scan = new List<(long Start, long Length)>(full.Count);
        foreach (var item in full)
            if (BelongsToScan(item.Start)) scan.Add(item);
        return (full, scan, payloadEnd);
    }

    /// <summary>Slices a scan chain to a block range in file order; a range past the last block yields nothing.</summary>
    private static List<(long Start, long Length)> ApplyBlockRange(List<(long Start, long Length)> scanChain, BlockRange? range)
    {
        if (range is null) return scanChain;
        int start = range.Value.StartBlock;
        if (start < 0) start = 0;
        if (start >= scanChain.Count) return [];
        int count = range.Value.BlockCount ?? (scanChain.Count - start);
        if (count <= 0) return [];
        count = Math.Min(count, scanChain.Count - start);
        return scanChain.GetRange(start, count);
    }

    /// <summary>
    /// Records whether the point data ends at a block the reader does not know, and how many blocks the payload
    /// holds. The gap belongs to the payload, so only the unified scan or the scan that owns the last block reports
    /// it. An empty chain means the very first chunk is unknown; when a full block could have fit there, that is the
    /// same condition.
    /// </summary>
    private void SetChainEnd(FileStream fs, ReadState state,
        (List<(long Start, long Length)> FullChain, List<(long Start, long Length)> ScanChain, long PayloadEnd) chain)
    {
        state.ChainBlocks = chain.FullChain.Count;

        if (_blockStarts != null && (chain.FullChain.Count == 0 || !BelongsToScan(chain.FullChain[^1].Start)))
            return;

        if (chain.FullChain.Count > 0)
        {
            long lastEnd = chain.FullChain[^1].Start + chain.FullChain[^1].Length;
            if (chain.PayloadEnd - lastEnd >= 8 + 128)
            {
                // Version 22 payloads end with a JSON tree descriptor after the last point block.
                ReadOnlySpan<byte> descriptor = "{\n\t\"tree-header-info\""u8;
                Span<byte> prefix = stackalloc byte[descriptor.Length];
                fs.Position = lastEnd;
                fs.ReadExactly(prefix);
                state.ChainEndedEarly = !prefix.SequenceEqual(descriptor);
            }
        }
        else
        {
            fs.Position = (long)_entry!.Offset;
            byte[] payloadHeader = new byte[64];
            fs.ReadExactly(payloadHeader);
            long firstBlock = (long)_entry.Offset + Bytes.ReadUInt32LE(payloadHeader.AsSpan(0x20, 4));
            if (chain.PayloadEnd - firstBlock >= 8 + 128) state.ChainEndedEarly = true;
        }
    }

    /// <summary>
    /// Reports the data that the point data did not hold: the points that were dropped as padding, the compressed
    /// streams that were skipped, the point blocks that carry no positions or whose frame list is cut short, and the
    /// point data that ends at a block the reader does not know. In strict mode the read fails instead.
    /// </summary>
    private void ReportIncompleteData(bool strict, ReadState state)
    {
        var problems = new List<string>();

        if (state.DroppedPoints > 0)
            problems.Add($"{state.DroppedPoints} points were dropped because their slot holds no point");

        if (state.SkippedStreams > 0)
            problems.Add($"{state.SkippedStreams} compressed streams were skipped because they are larger than the accepted size");

        if (state.UnusableBlocks > 0)
            problems.Add($"{state.UnusableBlocks} point blocks were skipped because they hold fewer than four planes");

        if (state.IncompleteBlocks > 0)
            problems.Add($"{state.IncompleteBlocks} point blocks have a frame list that ends before the block does");

        if (state.ChainEndedEarly)
            problems.Add($"the point data ends after {state.ChainBlocks} blocks at a block that is not a point block");

        if (problems.Count == 0) return;

        string message = "The point data is incomplete: " + string.Join("; ", problems) + ".";
        if (strict) throw FormatError.Corrupted(message);

        _document?.AddWarning(message);
    }

    // A scan without its own blocks holds all of them; otherwise only the blocks the container assigned to it.
    private bool BelongsToScan(long blockStart) =>
        _blockStarts == null || Array.BinarySearch(_blockStarts, blockStart) >= 0;

    /// <summary>Decodes one point block on its own stream without touching shared read state.</summary>
    /// <returns>
    /// The block with its planes, the decoded bytes, the skipped streams, and whether the block is unusable (fewer
    /// than four planes) or its frame list is cut short; the block is null when it holds no positions.
    /// </returns>
    /// <exception cref="InvalidDataException">The block header or its planes are malformed or oversized.</exception>
    private static (Block? Block, long DecodedBytes, long SkippedStreams, long Unusable, long Incomplete) DecodeBlock(FileStream fs, long blockStart, long blockLength)
    {
        if (blockStart < 0 || blockLength < 8 + 128 || blockStart + blockLength > fs.Length)
            throw FormatError.Corrupted("point block range is outside the file.");

        byte[] blockHeader = new byte[128];
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

        if (firstFrame < 8) return (null, 0, 0, 1, 0);

        long firstFrameOffset = blockStart + firstFrame;
        var (frames, decodedBytes, skippedStreams, incomplete) = ReadAllFramesLocal(fs, firstFrameOffset, blockEnd);

        // Blocks with fewer than four planes carry no positions.
        if (frames.Count < 4) return (null, decodedBytes, skippedStreams, 1, incomplete);

        if (!(double.IsFinite(scaleX) && scaleX > 0 && double.IsFinite(scaleY) && scaleY > 0 &&
              double.IsFinite(scaleZ) && scaleZ > 0 &&
              double.IsFinite(minX) && double.IsFinite(minY) && double.IsFinite(minZ)))
            throw FormatError.Corrupted("point block has an invalid coordinate scale or origin.");

        NativeLayout? layout = Bytes.ReadUInt32LE(blockHeader.AsSpan(4, 4)) == Morton96Version
            ? ReadNativeLayout(searchBuf.AsSpan(0, firstFrame - 8), frames)
            : null;

        return (new Block(scaleX, scaleY, scaleZ, minX, minY, minZ, frames, layout), decodedBytes, skippedStreams, 0, incomplete);
    }

    private static NativeLayout ReadNativeLayout(ReadOnlySpan<byte> header, List<byte[]> frames)
    {
        const int attributesOffset = 160;
        if (header.Length < attributesOffset + 20)
            throw FormatError.Truncated("the point block attributes");

        uint count = Bytes.ReadUInt32LE(header[attributesOffset..]);
        ulong points = Bytes.ReadUInt64LE(header[12..]);
        if (count == 0 || points == 0 || points > MaxFrameBytes ||
            frames.Count < Morton96Planes)
            throw FormatError.Corrupted("point block has invalid Morton position planes.");

        int offset = attributesOffset + 4;
        int intensity = -1;
        bool red = false, green = false, blue = false;
        int widerPlanes = 0, twoBytePlanes = 0, planeCount = 0;

        for (uint i = 0; i < count; i++)
        {
            // POSITION occupies twelve byte planes. Subsequent attributes prefix their descriptor with
            // the sample width expressed as a power of two.
            int width = Morton96Planes;
            if (i > 0)
            {
                if (offset >= header.Length || header[offset] > 2)
                    throw FormatError.Unsupported("unknown point block attribute size.");
                width = 1 << header[offset++];
            }

            int descriptorSize = i == 0 ? 8 : 7;
            if (offset > header.Length - descriptorSize)
                throw FormatError.Truncated("a point block attribute");
            offset += descriptorSize;
            int nameLength = Bytes.ReadUInt16LE(header[(offset - 2)..]);
            if (nameLength > header.Length - offset)
                throw FormatError.Truncated("a point block attribute name");

            var name = header.Slice(offset, nameLength);
            if (i == 0 && !name.SequenceEqual("POSITION"u8))
                throw FormatError.Corrupted("the first block attribute is not POSITION.");
            // The planes are ordered by sample width, with POSITION and four-byte attributes before U16.
            if (width > 2) widerPlanes += width;
            if (width == 2)
            {
                if (name.SequenceEqual("INTENSITY"u8)) intensity = twoBytePlanes;
                twoBytePlanes += width;
            }
            if (width == 1)
            {
                red |= name.SequenceEqual("R"u8);
                green |= name.SequenceEqual("G"u8);
                blue |= name.SequenceEqual("B"u8);
            }

            offset += nameLength;
            planeCount += width;
            if (planeCount > frames.Count) throw FormatError.Truncated("a point block attribute plane");
        }

        if (frames.Take(planeCount).Any(plane => (ulong)plane.Length != points))
            throw FormatError.Corrupted("point block attribute length does not match the point count.");

        // RGB is a group of three planes after the individual one-byte attributes.
        int color = red && green && blue ? planeCount - 3 : -1;
        return new NativeLayout((int)points, intensity >= 0 ? widerPlanes + intensity : -1, color);
    }

    private static List<PclPoint> UnpackNativeBlock(Block block, NativeLayout layout, int step, bool applyTransform,
        PclTransform transform, ulong baseIndex)
    {
        var frames = block.Frames;
        var points = new List<PclPoint>(1 + (layout.PointCount - 1) / step);
        UInt128 morton = 0;
        ushort intensity = 0;
        int green = 0;
        byte[]? greenDeltas = layout.Color >= 0 ? frames[layout.Color] : null;
        for (int i = 0; i < layout.PointCount; i++)
        {
            // Each pair is stored in reverse order. An unpaired final sample keeps its slot.
            int slot = i ^ 1;
            if (slot >= layout.PointCount) slot = i;
            UInt128 delta = 0;
            for (int b = Morton96Planes - 1; b >= 0; b--) delta = (delta << 8) | frames[b][slot];
            morton += delta;
            if (morton >> 96 != 0)
                throw FormatError.Corrupted("point block Morton position exceeds 96 bits.");

            if (layout.Intensity >= 0)
            {
                int code = frames[layout.Intensity][slot] | (frames[layout.Intensity + 1][slot] << 8);
                intensity = unchecked((ushort)(intensity + code));
            }

            if (greenDeltas != null) green = (green + greenDeltas[slot]) & 255;
            if (i % step != 0) continue;

            // Green is accumulated across points. Blue and red are byte differences from the current green.
            PclColor? color = greenDeltas == null ? null : new PclColor(
                unchecked((byte)(green + frames[layout.Color + 2][slot])),
                (byte)green,
                unchecked((byte)(green + frames[layout.Color + 1][slot])));

            var position = new PclVector3(
                block.MinX + (UnpackMortonAxis(morton) * block.ScaleX),
                block.MinY + (UnpackMortonAxis(morton >> 1) * block.ScaleY),
                block.MinZ + (UnpackMortonAxis(morton >> 2) * block.ScaleZ));
            if (applyTransform) position = transform.TransformPoint(position);
            points.Add(new PclPoint(position,
                Color: color,
                Intensity: layout.Intensity >= 0 ? intensity / 65535.0f : null,
                Index: baseIndex + (ulong)points.Count));
        }

        return points;
    }

    private static long UnpackMortonAxis(UInt128 code)
    {
        // Skip 63 interleaved bits at a time to retain the stride of three for the upper coordinate bits.
        uint coordinate = CompactMortonAxis((ulong)code) | CompactMortonAxis((ulong)(code >> 63)) << 21;
        return (long)coordinate - (1L << 31);
    }

    private static uint CompactMortonAxis(ulong code)
    {
        code &= 0x1249249249249249UL;
        code = (code ^ (code >> 2)) & 0x10C30C30C30C30C3UL;
        code = (code ^ (code >> 4)) & 0x100F00F00F00F00FUL;
        code = (code ^ (code >> 8)) & 0x1F0000FF0000FFUL;
        code = (code ^ (code >> 16)) & 0x1F00000000FFFFUL;
        return (uint)((code ^ (code >> 32)) & 0x1FFFFFUL);
    }

    /// <summary>Unpacks the planes of one block into points, dropping padding slots. The first returned point gets <paramref name="baseIndex"/> and the rest keep consecutive values; <paramref name="dropped"/> counts dropped slots.</summary>
    private static List<PclPoint> UnpackBlock(Block block, int step, bool applyTransform, PclTransform transform, ulong baseIndex, out long dropped)
    {
        dropped = 0;
        if (block.Layout is { } layout)
            return UnpackNativeBlock(block, layout, step, applyTransform, transform, baseIndex);

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
        var points = new List<PclPoint>(Math.Max(0, (numPoints + step - 1) / step));
        ulong yielded = baseIndex;

        for (int i = 0; i < numPoints; i++)
        {
            if (i % step != 0) continue;

            uint val = (uint)(p0[i] | (p1[i] << 8) | (p2[i] << 16) | (p3[i] << 24));
            if (val == 0 && i > 0 && p0[i] == 0 && p1[i] == 0)
            {
                // The slot holds no point: it is padding or a point at the origin of the block.
                dropped++;
                continue;
            }

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
                Index: yielded);

            if (applyTransform)
                pt = pt with { Position = transform.TransformPoint(pt.Position) };

            points.Add(pt);
            yielded++;
        }

        return points;
    }

    /// <summary>Reads the frames of one block without touching shared read state.</summary>
    private static (List<byte[]> Frames, long DecodedBytes, long SkippedStreams, long Incomplete) ReadAllFramesLocal(FileStream fs, long offset, long maxOffset)
    {
        var list = new List<byte[]>();
        long limit = Math.Min(maxOffset, fs.Length);
        if (offset < 0 || offset + 8 >= limit) return (list, 0, 0, 0);

        fs.Position = offset;
        byte[] lenBuf = new byte[8];
        long totalBytes = 0;
        long skipped = 0;
        long incomplete = 0;

        while (fs.Position + 8 <= limit)
        {
            fs.ReadExactly(lenBuf);

            // A zero length marks the end of the frame list.
            ulong fLen = Bytes.ReadUInt64LE(lenBuf);
            if (fLen == 0) break;

            // A stream that is larger than the accepted size is skipped: the rest of the block is lost with it.
            if (fLen > MaxCompressedFrameBytes)
            {
                skipped++;
                break;
            }

            // A frame list that ends before the block does leaves the rest of the block unread.
            if ((long)fLen > limit - fs.Position)
            {
                incomplete = 1;
                break;
            }

            byte[] fData = new byte[(int)fLen];
            fs.ReadExactly(fData);

            if (!fData.AsSpan().StartsWith(FrameMagic))
            {
                incomplete = 1;
                break;
            }

            if (list.Count >= MaxFramesPerBlock)
                throw FormatError.Corrupted($"point block has more than {MaxFramesPerBlock} planes.");

            // The size cap is checked against the frame's declared size before any allocation.
            byte[] plane = ZstdDecoder.Decompress(fData, MaxFrameBytes);

            totalBytes += plane.Length;
            if (totalBytes > MaxBlockBytes)
                throw FormatError.Corrupted($"point block decompresses to more than {MaxBlockBytes} bytes.");

            list.Add(plane);
        }

        return (list, totalBytes, skipped, incomplete);
    }

}
