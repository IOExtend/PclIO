using PclIO.Internal;

namespace PclIO.Formats.Copc;

/// <summary>Reads and resolves the COPC octree hierarchy pages.</summary>
internal static class CopcHierarchy
{
    private const int MaxHierarchyPages = 10000;
    private const int MaxPageBytes = 16 * 1024 * 1024; // 16 MB max per page
    private const int EntrySize = 32;

    public static List<CopcNode> ReadHierarchy(Stream stream, CopcInfo info)
    {
        if (info.RootHierarchySize < EntrySize || info.RootHierarchySize % EntrySize != 0)
            throw FormatError.Corrupted($"Invalid root hierarchy page size {info.RootHierarchySize} bytes.");
        if (info.RootHierarchySize > MaxPageBytes)
            throw FormatError.Corrupted($"Root hierarchy page size {info.RootHierarchySize} bytes exceeds safety limit.");

        long fileLength = stream.Length;
        if (info.RootHierarchyOffset + info.RootHierarchySize > (ulong)fileLength)
            throw FormatError.Truncated("COPC root hierarchy page");

        var nodes = new List<CopcNode>();
        var pagesToRead = new Queue<(ulong Offset, int ByteSize)>();
        var visitedPages = new HashSet<ulong>();

        pagesToRead.Enqueue((info.RootHierarchyOffset, (int)info.RootHierarchySize));

        while (pagesToRead.Count > 0)
        {
            var (offset, size) = pagesToRead.Dequeue();
            if (!visitedPages.Add(offset))
                throw FormatError.Corrupted("Cyclic COPC hierarchy page reference detected.");

            if (visitedPages.Count > MaxHierarchyPages)
                throw FormatError.Corrupted($"COPC hierarchy exceeds safety limit of {MaxHierarchyPages} pages.");

            if (offset + (ulong)size > (ulong)fileLength)
                throw FormatError.Truncated("COPC hierarchy page");

            byte[] pageBuffer = new byte[size];
            stream.Position = (long)offset;
            stream.ReadExactly(pageBuffer);

            int entryCount = size / EntrySize;
            for (int e = 0; e < entryCount; e++)
            {
                int baseOff = e * EntrySize;
                int level = Bytes.ReadInt32LE(pageBuffer.AsSpan(baseOff, 4));
                int x = Bytes.ReadInt32LE(pageBuffer.AsSpan(baseOff + 4, 4));
                int y = Bytes.ReadInt32LE(pageBuffer.AsSpan(baseOff + 8, 4));
                int z = Bytes.ReadInt32LE(pageBuffer.AsSpan(baseOff + 12, 4));
                ulong entryOffset = Bytes.ReadUInt64LE(pageBuffer.AsSpan(baseOff + 16, 8));
                int byteSize = Bytes.ReadInt32LE(pageBuffer.AsSpan(baseOff + 24, 4));
                int pointCount = Bytes.ReadInt32LE(pageBuffer.AsSpan(baseOff + 28, 4));

                if (level < 0) continue;

                if (pointCount > 0)
                {
                    if (byteSize <= 0)
                        throw FormatError.Corrupted($"COPC chunk byte size {byteSize} is invalid.");
                    if (entryOffset + (ulong)byteSize > (ulong)fileLength)
                        throw FormatError.Truncated("COPC point chunk data");

                    var key = new CopcVoxelKey(level, x, y, z);
                    var bounds = key.GetBounds(info.Center, info.HalfSize);
                    nodes.Add(new CopcNode(key, bounds, entryOffset, byteSize, pointCount));
                }
                else if (pointCount == -1)
                {
                    if (byteSize < EntrySize || byteSize % EntrySize != 0 || byteSize > MaxPageBytes)
                        throw FormatError.Corrupted($"COPC child hierarchy page byte size {byteSize} is invalid.");
                    if (entryOffset + (ulong)byteSize > (ulong)fileLength)
                        throw FormatError.Truncated("COPC child hierarchy page data");

                    pagesToRead.Enqueue((entryOffset, byteSize));
                }
                else if (pointCount == 0)
                {
                    // Empty entry
                }
                else
                {
                    throw FormatError.Corrupted($"Unexpected point count {pointCount} in COPC hierarchy entry.");
                }
            }
        }

        return nodes;
    }
}
