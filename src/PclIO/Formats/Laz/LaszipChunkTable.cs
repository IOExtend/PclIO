using PclIO.Internal;

namespace PclIO.Formats.Laz;

/// <summary>Metadata for a compressed LAZ chunk.</summary>
internal readonly record struct LaszipChunk(long Offset, uint ByteSize, uint PointCount);

/// <summary>Parser and decompressor for the LASzip chunk table.</summary>
internal static class LaszipChunkTable
{
    private const int MaxChunks = 10_000_000;

    public static LaszipChunk[] Read(FileStream fs, long pointDataOffset, uint declaredChunkSize, long pointCount, long fileLength)
    {
        if (pointDataOffset + 8 > fileLength)
            throw FormatError.Truncated("offset to LASzip chunk table");

        fs.Position = pointDataOffset;
        byte[] offsetBytes = new byte[8];
        fs.ReadExactly(offsetBytes);
        long chunkTableOffset = Bytes.ReadInt64LE(offsetBytes);

        if (chunkTableOffset == -1)
        {
            if (fileLength < 8) throw FormatError.Truncated("LASzip chunk table offset");
            fs.Position = fileLength - 8;
            fs.ReadExactly(offsetBytes);
            chunkTableOffset = Bytes.ReadInt64LE(offsetBytes);
        }

        long chunksStart = pointDataOffset + 8;
        if (chunkTableOffset + 8 == chunksStart)
            throw FormatError.Corrupted("compressor was interrupted before writing LAZ chunk table.");

        if (chunkTableOffset < chunksStart || chunkTableOffset > fileLength - 8)
            throw FormatError.Corrupted("LAZ chunk table offset is outside the file.");

        fs.Position = chunkTableOffset;
        byte[] tableHeader = new byte[8];
        fs.ReadExactly(tableHeader);
        uint version = Bytes.ReadUInt32LE(tableHeader.AsSpan(0, 4));
        if (version != 0)
            throw FormatError.Unsupported($"LAZ chunk table version {version} is not supported.");

        uint numberOfChunks = Bytes.ReadUInt32LE(tableHeader.AsSpan(4, 4));
        if (numberOfChunks > MaxChunks)
            throw FormatError.Corrupted($"LAZ file declares {numberOfChunks} chunks; at most {MaxChunks} are accepted.");

        if (numberOfChunks == 0)
            return [];

        int tableDataLen = (int)(fileLength - (chunkTableOffset + 8));
        if (tableDataLen < 4)
            throw FormatError.Truncated("LAZ chunk table stream");

        byte[] tableData = new byte[tableDataLen];
        fs.ReadExactly(tableData);

        var dec = new LaszipArithmeticDecoder(tableData);
        var ic = new LaszipIntegerCompressor(dec, 32, 2);

        uint[] chunkTotals = new uint[numberOfChunks];
        uint[] chunkSizes = new uint[numberOfChunks];

        for (int i = 0; i < numberOfChunks; i++)
        {
            if (declaredChunkSize == uint.MaxValue)
            {
                int predTotal = i > 0 ? (int)chunkTotals[i - 1] : 0;
                chunkTotals[i] = (uint)ic.Decompress(predTotal, 0);
            }
            int predSize = i > 0 ? (int)chunkSizes[i - 1] : 0;
            chunkSizes[i] = (uint)ic.Decompress(predSize, 1);
        }

        var chunks = new LaszipChunk[numberOfChunks];
        long currentOffset = chunksStart;
        long totalPointsAllocated = 0;

        for (int i = 0; i < numberOfChunks; i++)
        {
            uint byteSize = chunkSizes[i];
            if (byteSize == 0 || currentOffset + byteSize > chunkTableOffset)
                throw FormatError.Corrupted($"LAZ chunk {i} reaches outside the point data range.");

            uint chunkPts;
            if (declaredChunkSize == uint.MaxValue)
            {
                chunkPts = chunkTotals[i];
            }
            else if (i < numberOfChunks - 1)
            {
                chunkPts = declaredChunkSize;
            }
            else
            {
                long remaining = pointCount - totalPointsAllocated;
                chunkPts = (uint)Math.Clamp(remaining, 0, declaredChunkSize);
            }

            chunks[i] = new LaszipChunk(currentOffset, byteSize, chunkPts);
            currentOffset += byteSize;
            totalPointsAllocated += chunkPts;
        }

        return chunks;
    }
}
