using System.Buffers.Binary;
using PclIO.Internal;

namespace PclIO.Formats.E57;

/// <summary>
/// Reads the logical byte stream of an E57 file. The file is split into fixed 1024-byte pages;
/// each page ends with a 4-byte CRC-32C of the preceding 1020 bytes, which is checked before the page is used.
/// </summary>
internal sealed class E57StreamReader : IDisposable
{
    /// <summary>Physical page size fixed by the E57 format.</summary>
    public const int FixedPageSize = 1024;

    private const int ChecksumSize = 4;
    private const int PayloadPerPage = FixedPageSize - ChecksumSize;

    private readonly FileStream _file;
    private readonly ulong _pageCount;
    private readonly byte[] _page = new byte[FixedPageSize];
    private long _pageNumber = -1;

    /// <param name="path">File to read.</param>
    /// <param name="pageSize">Page size from the file header; only the fixed size is accepted.</param>
    public E57StreamReader(string path, ulong pageSize)
    {
        if (pageSize != FixedPageSize)
            throw FormatError.Corrupted($"E57 page size must be {FixedPageSize} bytes, found {pageSize}.");

        _file = File.OpenRead(path);
        _pageCount = (ulong)_file.Length / FixedPageSize;
    }

    /// <summary>Converts a physical file offset into an offset in the logical (checksum-free) stream.</summary>
    public ulong ToLogicalOffset(ulong physicalOffset)
    {
        ulong inPage = physicalOffset % FixedPageSize;
        if (inPage >= PayloadPerPage)
            throw FormatError.Corrupted("E57 offset points into a page checksum.");
        return physicalOffset / FixedPageSize * PayloadPerPage + inPage;
    }

    /// <summary>Reads logical bytes starting at a physical file offset.</summary>
    public byte[] ReadLogicalBytes(ulong physicalOffset, ulong logicalLength) =>
        ReadLogical(ToLogicalOffset(physicalOffset), logicalLength);

    /// <summary>Reads exactly <paramref name="length"/> bytes of the logical stream, verifying every page it touches.</summary>
    public byte[] ReadLogical(ulong logicalOffset, ulong length)
    {
        if (length == 0) return [];

        // Bounding by the real file size also bounds the allocation below.
        ulong logicalSize = _pageCount * PayloadPerPage;
        if (logicalOffset >= logicalSize || length > logicalSize - logicalOffset)
            throw FormatError.Truncated($"{length} bytes at logical offset {logicalOffset}");
        if (length > (ulong)Array.MaxLength)
            throw FormatError.Corrupted("E57 read length is too large.");

        var result = new byte[(int)length];
        int done = 0;
        ulong position = logicalOffset;
        while (done < result.Length)
        {
            int inPage = (int)(position % PayloadPerPage);
            byte[] page = LoadPage(position / PayloadPerPage);
            int count = Math.Min(result.Length - done, PayloadPerPage - inPage);
            Buffer.BlockCopy(page, inPage, result, done, count);
            done += count;
            position += (ulong)count;
        }

        return result;
    }

    private byte[] LoadPage(ulong pageNumber)
    {
        if ((long)pageNumber == _pageNumber) return _page;
        if (pageNumber >= _pageCount) throw FormatError.Truncated($"E57 page {pageNumber}");

        _pageNumber = -1;
        _file.Position = (long)(pageNumber * FixedPageSize);
        try
        {
            _file.ReadExactly(_page);
        }
        catch (EndOfStreamException)
        {
            throw FormatError.Truncated($"E57 page {pageNumber}");
        }

        // The checksum is stored big-endian after the payload.
        uint stored = BinaryPrimitives.ReadUInt32BigEndian(_page.AsSpan(PayloadPerPage, ChecksumSize));
        if (Crc32C.Compute(_page.AsSpan(0, PayloadPerPage)) != stored)
            throw FormatError.Corrupted($"E57 page {pageNumber} fails its checksum.");

        _pageNumber = (long)pageNumber;
        return _page;
    }

    public void Dispose() => _file.Dispose();

    /// <summary>CRC-32C (Castagnoli, reflected polynomial 0x82F63B78), the checksum E57 uses for its pages.</summary>
    private static class Crc32C
    {
        private static readonly uint[] Table = CreateTable();

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint entry = i;
                for (int bit = 0; bit < 8; bit++)
                    entry = (entry & 1) != 0 ? (entry >> 1) ^ 0x82F63B78u : entry >> 1;
                table[i] = entry;
            }
            return table;
        }

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data)
                crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
            return ~crc;
        }
    }
}
