using PclIO.Internal;

namespace PclIO.Formats.Laz;

/// <summary>Represents a LASzip-compressed ASPRS LAS point cloud file (.laz).</summary>
public sealed class LazDocument : PclDocument
{
    private const int MinHeaderSize = 227;
    private const int Las14HeaderSize = 375;
    private const int MaxVlrs = 4096;
    private const int MaxVlrDataBytes = 1 << 20;

    /// <inheritdoc />
    public override PclFileKind Kind => PclFileKind.Laz;

    /// <summary>LAS version of the underlying point data, for example "1.2".</summary>
    public string Version { get; }

    /// <summary>Underlying point data format (0 to 3).</summary>
    public int PointFormat { get; }

    /// <summary>Declared chunk size in points.</summary>
    public uint ChunkSize { get; }

    /// <inheritdoc />
    public override IReadOnlyList<PclScan> Scans => _scans;
    private readonly List<PclScan> _scans = [];

    private LazDocument(string path, long fileSize, string version, int pointFormat, uint chunkSize)
        : base(path, fileSize)
    {
        Version = version;
        PointFormat = pointFormat;
        ChunkSize = chunkSize;
        _metadata["Format"] = "LASzip compressed ASPRS LAS";
        _metadata["Version"] = version;
        _metadata["PointFormat"] = pointFormat.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _metadata["ChunkSize"] = chunkSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Opens a LASzip compressed LAZ file and reads its header, chunk table and variable length records.</summary>
    /// <param name="path">Path of the .laz file.</param>
    /// <param name="options">Read options (currently reserved for consistency with other readers).</param>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is malformed, truncated, or has corrupted compression streams.</exception>
    /// <exception cref="NotSupportedException">The point format, compressor type, or version is not supported.</exception>
    public static LazDocument Open(string path, PclReadOptions? options = null)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("LAZ file not found.", path);

        using var fs = File.OpenRead(path);
        long fileLength = fs.Length;

        if (fileLength < 4) throw FormatError.Truncated("LAZ header");
        byte[] signature = new byte[4];
        fs.ReadExactly(signature);
        if (!signature.AsSpan().SequenceEqual("LASF"u8))
            throw FormatError.Corrupted("not a LAS/LAZ file, signature mismatch.");

        if (fileLength < MinHeaderSize) throw FormatError.Truncated("LAZ header");
        fs.Position = 0;
        byte[] header = new byte[MinHeaderSize];
        fs.ReadExactly(header);

        int major = header[24];
        int minor = header[25];
        if (major != 1 || minor > 4)
            throw FormatError.Unsupported($"LAS version {major}.{minor} is not supported; versions 1.0 to 1.4 are read.");

        int headerSize = Bytes.ReadUInt16LE(header.AsSpan(94, 2));
        if (headerSize < MinHeaderSize)
            throw FormatError.Corrupted($"LAZ header size {headerSize} is smaller than the minimum {MinHeaderSize}.");
        if (minor == 4 && headerSize < Las14HeaderSize)
            throw FormatError.Corrupted($"LAZ 1.4 header size {headerSize} is smaller than {Las14HeaderSize}.");
        if (headerSize > fileLength) throw FormatError.Truncated("LAZ header");

        if (headerSize > header.Length)
        {
            fs.Position = 0;
            header = new byte[headerSize];
            fs.ReadExactly(header);
        }

        uint pointDataOffset = Bytes.ReadUInt32LE(header.AsSpan(96, 4));
        if (pointDataOffset < headerSize || pointDataOffset > fileLength)
            throw FormatError.Corrupted("LAZ offset to point data is outside the file.");

        uint vlrCount = Bytes.ReadUInt32LE(header.AsSpan(100, 4));
        if (vlrCount > MaxVlrs)
            throw FormatError.Corrupted($"LAZ file declares {vlrCount} variable length records; at most {MaxVlrs} are accepted.");

        int rawPointFormatByte = header[104];
        int pointFormat = rawPointFormatByte & 0x3F;
        if (pointFormat > 3)
            throw FormatError.Unsupported($"LAZ point format {pointFormat} is not currently supported; formats 0 to 3 are read.");

        int recordLength = Bytes.ReadUInt16LE(header.AsSpan(105, 2));
        if (recordLength < 20)
            throw FormatError.Corrupted($"LAZ point record length {recordLength} is smaller than 20 bytes.");

        uint legacyCount = Bytes.ReadUInt32LE(header.AsSpan(107, 4));
        long pointCount = legacyCount;
        if (minor == 4)
        {
            ulong extendedCount = Bytes.ReadUInt64LE(header.AsSpan(247, 8));
            if (extendedCount != 0) pointCount = extendedCount > long.MaxValue ? long.MaxValue : (long)extendedCount;
        }

        double scaleX = Bytes.ReadDoubleLE(header.AsSpan(131, 8));
        double scaleY = Bytes.ReadDoubleLE(header.AsSpan(139, 8));
        double scaleZ = Bytes.ReadDoubleLE(header.AsSpan(147, 8));
        if (!double.IsFinite(scaleX) || scaleX == 0 || !double.IsFinite(scaleY) || scaleY == 0 || !double.IsFinite(scaleZ) || scaleZ == 0)
            throw FormatError.Corrupted("LAZ scale is not usable.");

        double offsetX = Bytes.ReadDoubleLE(header.AsSpan(155, 8));
        double offsetY = Bytes.ReadDoubleLE(header.AsSpan(163, 8));
        double offsetZ = Bytes.ReadDoubleLE(header.AsSpan(171, 8));
        if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY) || !double.IsFinite(offsetZ))
            throw FormatError.Corrupted("LAZ offset is not usable.");

        double minX = Bytes.ReadDoubleLE(header.AsSpan(187, 8));
        double maxX = Bytes.ReadDoubleLE(header.AsSpan(179, 8));
        double minY = Bytes.ReadDoubleLE(header.AsSpan(203, 8));
        double maxY = Bytes.ReadDoubleLE(header.AsSpan(195, 8));
        double minZ = Bytes.ReadDoubleLE(header.AsSpan(219, 8));
        double maxZ = Bytes.ReadDoubleLE(header.AsSpan(211, 8));

        var bounds = (!double.IsFinite(maxX) || !double.IsFinite(minX) ||
                      !double.IsFinite(maxY) || !double.IsFinite(minY) ||
                      !double.IsFinite(maxZ) || !double.IsFinite(minZ))
            ? PclBounds.Empty
            : new PclBounds(new PclVector3(minX, minY, minZ), new PclVector3(maxX, maxY, maxZ));

        // Read VLRs to find the LASzip compression parameters and coordinate system.
        var (laszipVlr, crs) = ReadVlrs(fs, headerSize, pointDataOffset, vlrCount);
        if (laszipVlr == null)
            throw FormatError.Corrupted("LAZ file is missing the LASzip configuration VLR (22204).");

        var lazParams = ParseLaszipVlr(laszipVlr);
        if (lazParams.Compressor is not (1 or 2))
            throw FormatError.Unsupported($"LAZ compressor {lazParams.Compressor} is not supported; only pointwise (1) and pointwise chunked (2) are read.");

        // Read chunk table if chunked.
        LaszipChunk[] chunks;
        if (lazParams.Compressor == 2 && pointCount > 0)
        {
            chunks = LaszipChunkTable.Read(fs, pointDataOffset, lazParams.ChunkSize, pointCount, fileLength);
        }
        else
        {
            chunks = [];
        }

        string versionStr = $"{major}.{minor}";
        var doc = new LazDocument(path, fileLength, versionStr, pointFormat, lazParams.ChunkSize);
        if (crs != null) doc._metadata["CoordinateSystem"] = crs;

        var scan = new LazScan(
            id: "scan_0",
            name: Path.GetFileNameWithoutExtension(path),
            pointCount: pointCount,
            bounds: bounds,
            filePath: path,
            pointDataOffset: pointDataOffset,
            pointFormat: pointFormat,
            recordLength: recordLength,
            scale: new PclVector3(scaleX, scaleY, scaleZ),
            offset: new PclVector3(offsetX, offsetY, offsetZ),
            chunks: chunks);

        doc._scans.Add(scan);
        return doc;
    }

    private sealed record LaszipVlrInfo(ushort Compressor, ushort Coder, uint ChunkSize);

    private static LaszipVlrInfo ParseLaszipVlr(byte[] vlrData)
    {
        if (vlrData.Length < 34)
            throw FormatError.Truncated("LASzip VLR header");

        ushort compressor = Bytes.ReadUInt16LE(vlrData.AsSpan(0, 2));
        ushort coder = Bytes.ReadUInt16LE(vlrData.AsSpan(2, 2));
        uint chunkSize = Bytes.ReadUInt32LE(vlrData.AsSpan(12, 4));

        if (chunkSize == 0) chunkSize = 50000;
        return new LaszipVlrInfo(compressor, coder, chunkSize);
    }

    private static (byte[]? LaszipVlr, string? Crs) ReadVlrs(FileStream fs, int headerSize, uint pointDataOffset, uint vlrCount)
    {
        byte[]? laszipVlr = null;
        string? crs = null;
        long position = headerSize;
        byte[] vlrHeader = new byte[54];

        for (uint i = 0; i < vlrCount; i++)
        {
            if (position + 54 > pointDataOffset)
                throw FormatError.Corrupted("LAZ variable length record reaches past the point data.");

            fs.Position = position;
            fs.ReadExactly(vlrHeader);
            position += 54;

            string userId = ReadFixedAscii(vlrHeader.AsSpan(2, 16));
            ushort recordId = Bytes.ReadUInt16LE(vlrHeader.AsSpan(18, 2));
            ushort dataLength = Bytes.ReadUInt16LE(vlrHeader.AsSpan(20, 2));

            if (position + dataLength > pointDataOffset)
                throw FormatError.Corrupted("LAZ variable length record reaches past the point data.");

            if (userId.Equals("laszip encoded", StringComparison.Ordinal) && (recordId == 22204 || recordId == 22205))
            {
                laszipVlr = new byte[dataLength];
                if (dataLength > 0) fs.ReadExactly(laszipVlr);
            }
            else if (userId.Equals("LASF_Projection", StringComparison.Ordinal) && recordId == 2112)
            {
                byte[] wktBytes = new byte[dataLength];
                if (dataLength > 0) fs.ReadExactly(wktBytes);
                string wkt = System.Text.Encoding.UTF8.GetString(wktBytes).TrimEnd('\0', ' ', '\r', '\n');
                if (wkt.Length > 0) crs = wkt;
            }
            else if (userId.Equals("LASF_Projection", StringComparison.Ordinal) && recordId == 34735 && crs == null)
            {
                byte[] geoData = new byte[dataLength];
                if (dataLength > 0) fs.ReadExactly(geoData);
                if (TryReadEpsg(geoData, out ushort epsg))
                    crs = $"EPSG:{epsg}";
            }

            position += dataLength;
        }

        return (laszipVlr, crs);
    }

    private static bool TryReadEpsg(byte[] data, out ushort code)
    {
        code = 0;
        if (data.Length < 8) return false;
        int keyCount = Bytes.ReadUInt16LE(data.AsSpan(6, 2));
        if (keyCount <= 0 || keyCount > 1024 || 8 + (keyCount * 8) > data.Length) return false;

        for (int pass = 0; pass < 2; pass++)
        {
            int wanted = pass == 0 ? 3072 : 2048;
            for (int k = 0; k < keyCount; k++)
            {
                var entry = data.AsSpan(8 + (k * 8), 8);
                if (Bytes.ReadUInt16LE(entry[..2]) != wanted) continue;
                int value = Bytes.ReadUInt16LE(entry.Slice(6, 2));
                if (value is >= 1 and <= ushort.MaxValue && value != 32767)
                {
                    code = (ushort)value;
                    return true;
                }
            }
        }
        return false;
    }

    private static string ReadFixedAscii(ReadOnlySpan<byte> bytes)
    {
        int length = bytes.IndexOf((byte)0);
        if (length < 0) length = bytes.Length;
        return System.Text.Encoding.ASCII.GetString(bytes[..length]);
    }
}
