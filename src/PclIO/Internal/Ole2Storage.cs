using System.Buffers.Binary;
using System.Collections;
using System.Text;

namespace PclIO.Internal;

/// <summary>
/// Managed reader for the Microsoft Compound File Binary Format (OLE2), used by single-file FARO .fls scans.
/// All sizes, counts and sector ids come from untrusted input and are checked against the stream length.
/// </summary>
internal sealed class Ole2Storage : IDisposable
{
    internal static readonly byte[] Ole2Magic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSect = 0xFFFFFFFF;
    private const int HeaderSize = 512;
    private const int DirEntrySize = 128;

    // Hard caps so a hostile directory cannot grow the path map without bound.
    private const int MaxDirectoryEntries = 1 << 16;
    private const int MaxDirectoryDepth = 16;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly long _length;
    private readonly int _sectorSize;
    private readonly int _miniSectorSize;
    private readonly uint _miniStreamCutoff;
    private readonly int _sectorCount;
    private uint[] _fat = [];
    private uint[] _miniFat = [];
    private readonly List<Ole2DirEntry> _entries = [];
    private readonly Dictionary<string, int> _entryMap = new(StringComparer.OrdinalIgnoreCase);
    private byte[]? _miniStreamData;

    // ObjectType: 0 = unallocated, 1 = storage, 2 = stream, 5 = root.
    private sealed record Ole2DirEntry(
        string Name,
        byte ObjectType,
        uint StartSector,
        ulong StreamSize,
        int LeftSibling,
        int RightSibling,
        int Child);

    private Ole2Storage(Stream stream, bool leaveOpen, long length, int sectorSize, int miniSectorSize, uint miniStreamCutoff, int sectorCount)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _length = length;
        _sectorSize = sectorSize;
        _miniSectorSize = miniSectorSize;
        _miniStreamCutoff = miniStreamCutoff;
        _sectorCount = sectorCount;
    }

    /// <summary>Returns true when the file starts with the compound-file signature. I/O errors propagate.</summary>
    public static bool IsOle2File(string path)
    {
        if (!File.Exists(path)) return false;
        using var fs = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[8];
        if (fs.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false) < magic.Length) return false;
        return magic.SequenceEqual(Ole2Magic);
    }

    /// <summary>Opens a compound file from disk.</summary>
    public static Ole2Storage Open(string path)
    {
        var fs = File.OpenRead(path);
        try
        {
            return Open(fs, leaveOpen: false);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    /// <summary>Opens a compound file from a seekable stream whose compound data starts at position 0.</summary>
    public static Ole2Storage Open(Stream stream, bool leaveOpen = false)
    {
        if (!stream.CanSeek) throw new ArgumentException("The stream must be seekable.", nameof(stream));

        long length = stream.Length;
        stream.Position = 0;
        Span<byte> header = stackalloc byte[HeaderSize];
        if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < HeaderSize)
            throw FormatError.Truncated("a 512-byte OLE2 header");

        if (!header[..8].SequenceEqual(Ole2Magic))
            throw FormatError.Corrupted("OLE2 signature mismatch.");

        ushort sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(0x1E, 2));
        ushort miniSectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(0x20, 2));
        if (sectorShift is not (9 or 12))
            throw FormatError.Corrupted($"unsupported OLE2 sector shift {sectorShift}.");
        if (miniSectorShift != 6)
            throw FormatError.Corrupted($"unsupported OLE2 mini sector shift {miniSectorShift}.");

        int sectorSize = 1 << sectorShift;
        int miniSectorSize = 1 << miniSectorShift;

        uint numFatSectors = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0x2C, 4));
        uint firstDirSector = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0x30, 4));
        uint miniStreamCutoff = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0x38, 4));
        uint firstMiniFatSector = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0x3C, 4));
        uint numMiniFatSectors = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0x40, 4));
        uint firstDifSector = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0x44, 4));
        uint numDifSectors = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0x48, 4));

        // Sector 0 starts after the first sectorSize bytes (the header occupies that slot).
        long sectors = length > sectorSize ? (length - sectorSize + sectorSize - 1) / sectorSize : 0;
        if (sectors > int.MaxValue)
            throw FormatError.Unsupported("OLE2 file has too many sectors.");

        if (numFatSectors > sectors || numMiniFatSectors > sectors || numDifSectors > sectors)
            throw FormatError.Corrupted("OLE2 header declares more sectors than the file contains.");

        var storage = new Ole2Storage(stream, leaveOpen, length, sectorSize, miniSectorSize, miniStreamCutoff, (int)sectors);
        storage.LoadFat(header, numFatSectors, firstDifSector, numDifSectors);
        storage.LoadMiniFat(firstMiniFatSector, numMiniFatSectors);
        storage.LoadDirectory(firstDirSector);
        return storage;
    }

    private void ReadSectorInto(uint sectorId, Span<byte> destination)
    {
        if (sectorId >= (uint)_sectorCount)
            throw FormatError.Corrupted($"sector id {sectorId} lies outside the file.");

        long offset = (1L + sectorId) * _sectorSize;
        if (offset + destination.Length > _length)
            throw FormatError.Truncated($"sector {sectorId}");

        _stream.Position = offset;
        _stream.ReadExactly(destination);
    }

    private void LoadFat(ReadOnlySpan<byte> header, uint numFatSectors, uint firstDifSector, uint numDifSectors)
    {
        var fatSectorIds = new List<uint>();
        for (int i = 0; i < 109 && fatSectorIds.Count < numFatSectors; i++)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0x4C + (i * 4), 4));
            if (id != FreeSect && id != EndOfChain) fatSectorIds.Add(id);
        }

        // Further FAT sector ids live in a chain of DIFAT sectors; each ends with the id of the next one.
        if (fatSectorIds.Count < numFatSectors && numDifSectors > 0)
        {
            var seen = new BitArray(_sectorCount);
            byte[] difBuf = new byte[_sectorSize];
            int entriesPerDif = (_sectorSize / 4) - 1;
            uint cur = firstDifSector;

            for (uint d = 0; d < numDifSectors && cur != EndOfChain && cur != FreeSect && fatSectorIds.Count < numFatSectors; d++)
            {
                ReadSectorInto(cur, difBuf);
                if (seen[(int)cur]) throw FormatError.Corrupted("OLE2 DIFAT chain contains a cycle.");
                seen[(int)cur] = true;

                for (int i = 0; i < entriesPerDif && fatSectorIds.Count < numFatSectors; i++)
                {
                    uint id = BinaryPrimitives.ReadUInt32LittleEndian(difBuf.AsSpan(i * 4, 4));
                    if (id != FreeSect && id != EndOfChain) fatSectorIds.Add(id);
                }

                cur = BinaryPrimitives.ReadUInt32LittleEndian(difBuf.AsSpan(entriesPerDif * 4, 4));
            }
        }

        if (fatSectorIds.Count < numFatSectors)
            throw FormatError.Corrupted("OLE2 FAT sector list is shorter than declared.");

        int intsPerSector = _sectorSize / 4;
        var fat = new uint[fatSectorIds.Count * intsPerSector];
        byte[] buf = new byte[_sectorSize];
        for (int s = 0; s < fatSectorIds.Count; s++)
        {
            ReadSectorInto(fatSectorIds[s], buf);
            for (int i = 0; i < intsPerSector; i++)
                fat[(s * intsPerSector) + i] = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i * 4, 4));
        }

        _fat = fat;
    }

    private void LoadMiniFat(uint firstMiniFatSector, uint numMiniFatSectors)
    {
        if (numMiniFatSectors == 0) return;

        byte[] bytes = ReadSectorChain(firstMiniFatSector, (long)numMiniFatSectors * _sectorSize, exact: true);
        var miniFat = new uint[bytes.Length / 4];
        for (int i = 0; i < miniFat.Length; i++)
            miniFat[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4, 4));
        _miniFat = miniFat;
    }

    private void LoadDirectory(uint firstDirSector)
    {
        byte[] dirBytes = ReadSectorChain(firstDirSector, (long)MaxDirectoryEntries * DirEntrySize, exact: false);
        int entryCount = dirBytes.Length / DirEntrySize;

        // Keep every record (even unallocated ones) so list indices equal directory indices.
        for (int i = 0; i < entryCount; i++)
        {
            ReadOnlySpan<byte> e = dirBytes.AsSpan(i * DirEntrySize, DirEntrySize);
            byte objType = e[0x42];
            if (objType == 0)
            {
                _entries.Add(new Ole2DirEntry("", 0, 0, 0, -1, -1, -1));
                continue;
            }

            ushort nameLen = BinaryPrimitives.ReadUInt16LittleEndian(e.Slice(0x40, 2));
            if (nameLen > 64)
                throw FormatError.Corrupted("OLE2 directory entry name is too long.");

            int chars = Math.Max(0, (nameLen / 2) - 1);
            string name = Encoding.Unicode.GetString(e.Slice(0, chars * 2));

            int left = ToIndex(BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(0x44, 4)));
            int right = ToIndex(BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(0x48, 4)));
            int child = ToIndex(BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(0x4C, 4)));
            uint startSector = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(0x74, 4));
            ulong streamSize = BinaryPrimitives.ReadUInt64LittleEndian(e.Slice(0x78, 8));

            _entries.Add(new Ole2DirEntry(name, objType, startSector, streamSize, left, right, child));
        }

        if (_entries.Count == 0 || _entries[0].ObjectType != 5)
            throw FormatError.Corrupted("OLE2 directory has no root entry.");

        // The root entry owns the mini stream that holds all small streams.
        Ole2DirEntry root = _entries[0];
        if (root.StreamSize > 0)
            _miniStreamData = ReadSectorChain(root.StartSector, CheckedSize(root.StreamSize), exact: true);

        BuildHierarchy(root.Child);
    }

    // "No stream" (0xFFFFFFFF) and any id that cannot be a list index map to -1.
    private static int ToIndex(uint value) => value <= int.MaxValue ? (int)value : -1;

    // Iterative in-order walk (left, node, children, right); depth no longer depends on the stack.
    private void BuildHierarchy(int rootChild)
    {
        var visited = new HashSet<int>();
        var stack = new Stack<(int Index, string Path, int Depth, bool Emit)>();
        stack.Push((rootChild, "", 0, false));

        while (stack.Count > 0)
        {
            var (index, path, depth, emit) = stack.Pop();

            if (!emit)
            {
                if (index < 0 || index >= _entries.Count || _entries[index].ObjectType == 0 || !visited.Add(index)) continue;
                var entry = _entries[index];
                stack.Push((index, path, depth, true));
                stack.Push((entry.LeftSibling, path, depth, false));
                continue;
            }

            var node = _entries[index];
            string fullPath = path.Length == 0 ? node.Name : $"{path}/{node.Name}";
            if (node.Name.Length > 0)
            {
                _entryMap[fullPath] = index;
                _entryMap.TryAdd(node.Name, index);
            }

            stack.Push((node.RightSibling, path, depth, false));
            if (node.ObjectType == 1 && node.Child >= 0)
            {
                if (depth + 1 > MaxDirectoryDepth)
                    throw FormatError.Corrupted("OLE2 directory is nested too deeply.");
                stack.Push((node.Child, fullPath, depth + 1, false));
            }
        }
    }

    private static int CheckedSize(ulong size)
    {
        if (size > (ulong)Array.MaxLength)
            throw FormatError.Unsupported("OLE2 stream is larger than the supported maximum.");
        return (int)size;
    }

    // Yields the sector ids of a FAT chain; throws on out-of-range ids and cycles.
    private IEnumerable<uint> Chain(uint startSector)
    {
        var seen = new BitArray(_sectorCount);
        uint cur = startSector;
        while (cur != EndOfChain)
        {
            if (cur >= (uint)_sectorCount)
                throw FormatError.Corrupted($"sector id {cur} lies outside the file.");
            if (seen[(int)cur])
                throw FormatError.Corrupted("OLE2 sector chain contains a cycle.");
            seen[(int)cur] = true;

            yield return cur;

            if (cur >= (uint)_fat.Length)
                throw FormatError.Corrupted($"sector {cur} is not covered by the FAT.");
            cur = _fat[cur];
        }
    }

    // exact: read length bytes (the chain may be longer); otherwise read to the end of the chain, at most length bytes.
    private byte[] ReadSectorChain(uint startSector, long length, bool exact)
    {
        if (exact)
        {
            if (length > _length)
                throw FormatError.Corrupted("OLE2 stream size exceeds the file size.");
            if (length > Array.MaxLength)
                throw FormatError.Unsupported("OLE2 stream is larger than the supported maximum.");

            byte[] result = new byte[length];
            int filled = 0;
            if (length > 0)
            {
                foreach (uint sector in Chain(startSector))
                {
                    int chunk = (int)Math.Min(_sectorSize, length - filled);
                    ReadSectorInto(sector, result.AsSpan(filled, chunk));
                    filled += chunk;
                    if (filled == length) break;
                }
            }

            if (filled < length) throw FormatError.Truncated($"{length} bytes in an OLE2 sector chain");
            return result;
        }

        var ms = new MemoryStream();
        byte[] buf = new byte[_sectorSize];
        foreach (uint sector in Chain(startSector))
        {
            if (ms.Length + _sectorSize > length)
                throw FormatError.Corrupted("OLE2 sector chain is longer than allowed.");
            ReadSectorInto(sector, buf);
            ms.Write(buf, 0, buf.Length);
        }
        return ms.ToArray();
    }

    private byte[] ReadMiniStream(uint startSector, int size)
    {
        byte[] mini = _miniStreamData ?? throw FormatError.Corrupted("OLE2 file has small streams but no mini stream.");
        if (size > mini.Length)
            throw FormatError.Corrupted("OLE2 small stream is larger than the mini stream.");

        int miniSectorCount = (mini.Length + _miniSectorSize - 1) / _miniSectorSize;
        var seen = new BitArray(miniSectorCount);
        byte[] result = new byte[size];
        int filled = 0;
        uint cur = startSector;

        while (filled < size)
        {
            if (cur >= (uint)miniSectorCount)
                throw FormatError.Corrupted($"mini sector id {cur} lies outside the mini stream.");
            if (seen[(int)cur])
                throw FormatError.Corrupted("OLE2 mini sector chain contains a cycle.");
            seen[(int)cur] = true;

            long offset = (long)cur * _miniSectorSize;
            int chunk = Math.Min(_miniSectorSize, size - filled);
            if (offset + chunk > mini.Length)
                throw FormatError.Truncated("a mini stream sector");

            mini.AsSpan((int)offset, chunk).CopyTo(result.AsSpan(filled));
            filled += chunk;

            if (filled < size)
            {
                if (cur >= (uint)_miniFat.Length)
                    throw FormatError.Corrupted($"mini sector {cur} is not covered by the mini FAT.");
                cur = _miniFat[cur];
            }
        }

        return result;
    }

    /// <summary>Returns true when a stream or storage exists at the given '/' or '\' separated path.</summary>
    public bool StreamExists(string path) => _entryMap.ContainsKey(NormalizePath(path));

    /// <summary>Lists every known path, sorted ordinally so output is deterministic.</summary>
    public IReadOnlyList<string> ListStreams() => [.. _entryMap.Keys.Order(StringComparer.Ordinal)];

    /// <summary>Returns the declared length of the stream at the given path.</summary>
    public ulong GetStreamLength(string path) => _entries[Find(path)].StreamSize;

    /// <summary>Reads the whole stream at the given path into memory.</summary>
    public byte[] ReadStream(string path)
    {
        var entry = _entries[Find(path)];
        if (entry.StreamSize == 0) return [];

        int size = CheckedSize(entry.StreamSize);
        if (entry.StreamSize < _miniStreamCutoff)
            return ReadMiniStream(entry.StartSector, size);

        return ReadSectorChain(entry.StartSector, size, exact: true);
    }

    private int Find(string path)
    {
        if (!_entryMap.TryGetValue(NormalizePath(path), out int idx))
            throw new FileNotFoundException($"Stream not found in OLE2 storage: {path}");
        return idx;
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').Trim('/');

    public void Dispose()
    {
        if (!_leaveOpen) _stream.Dispose();
    }
}
