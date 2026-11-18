using PclIO.Internal;

namespace PclIO.Formats.Las;

/// <summary>Represents an ASPRS LAS point cloud file (.las).</summary>
/// <remarks>
/// <para>
/// Supported: versions 1.0 to 1.4 with point formats 0 to 10, the legacy and the extended (1.4) point counts,
/// scale and offset, and the coordinate system records when present. Positions, intensity, classification and,
/// for the colour formats (2, 3, 5, 7, 8, 10), 16-bit RGB downscaled to 8 bits are read. GPS time, return numbers,
/// scan angles, point source ids, near infrared and waveform packet descriptors are not interpreted; extra bytes
/// past a format's base size are skipped. Extended variable length records (after the point data) are not read.
/// </para>
/// <para>
/// Not supported (reported with <see cref="NotSupportedException"/> when the file is opened): versions outside 1.0
/// to 1.4, point formats above 10, and the colour or waveform formats 6 to 10 in a file that is not 1.4.
/// </para>
/// </remarks>
public sealed class LasDocument : PclDocument
{
    // Bounds for data taken from an untrusted file.
    private const int MinHeaderSize = 227;
    private const int Las14HeaderSize = 375;
    private const int MaxVlrs = 4096;
    private const int MaxVlrDataBytes = 1 << 20;
    private const long MaxVlrTotalBytes = 16L << 20;
    private const int MaxPointRecordLength = 1 << 20;

    /// <inheritdoc />
    public override PclFileKind Kind => PclFileKind.Las;

    /// <summary>LAS version of the file, for example "1.2".</summary>
    public string Version { get; }

    /// <summary>Point data format of the file (0 to 10).</summary>
    public int PointFormat { get; }

    /// <inheritdoc />
    public override IReadOnlyList<PclScan> Scans => _scans;
    private readonly List<PclScan> _scans = [];

    private LasDocument(string path, long fileSize, string version, int pointFormat)
        : base(path, fileSize)
    {
        Version = version;
        PointFormat = pointFormat;
        _metadata["Format"] = "ASPRS LAS";
        _metadata["Version"] = version;
        _metadata["PointFormat"] = pointFormat.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Opens an ASPRS LAS file and reads its header and variable length records.</summary>
    /// <param name="path">Path of the .las file.</param>
    /// <param name="options">Currently unused; accepted for symmetry with the other readers.</param>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is damaged or truncated, or declares sizes outside the file.</exception>
    /// <exception cref="NotSupportedException">The version or the point format is not supported.</exception>
    public static LasDocument Open(string path, PclReadOptions? options = null)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("LAS file not found.", path);

        using var fs = File.OpenRead(path);
        long fileLength = fs.Length;

        if (fileLength < 4) throw FormatError.Truncated("LAS header");
        byte[] signature = new byte[4];
        fs.ReadExactly(signature);
        if (!signature.AsSpan().SequenceEqual("LASF"u8))
            throw FormatError.Corrupted("not a LAS file, signature mismatch.");

        if (fileLength < MinHeaderSize) throw FormatError.Truncated("LAS header");
        fs.Position = 0;
        byte[] header = new byte[MinHeaderSize];
        fs.ReadExactly(header);

        int major = header[24];
        int minor = header[25];
        if (major != 1 || minor > 4)
            throw FormatError.Unsupported($"LAS version {major}.{minor} is not supported; versions 1.0 to 1.4 are read.");

        int headerSize = Bytes.ReadUInt16LE(header.AsSpan(94, 2));
        if (headerSize < MinHeaderSize) throw FormatError.Corrupted($"LAS header size {headerSize} is smaller than the minimum {MinHeaderSize}.");
        if (minor == 4 && headerSize < Las14HeaderSize)
            throw FormatError.Corrupted($"LAS 1.4 header size {headerSize} is smaller than {Las14HeaderSize}.");
        if (headerSize > fileLength) throw FormatError.Truncated("LAS header");

        // The full header is needed for the 1.4 extended counts.
        if (headerSize > header.Length)
        {
            fs.Position = 0;
            header = new byte[headerSize];
            fs.ReadExactly(header);
        }

        uint pointDataOffset = Bytes.ReadUInt32LE(header.AsSpan(96, 4));
        if (pointDataOffset < headerSize || pointDataOffset > fileLength)
            throw FormatError.Corrupted("LAS offset to point data is outside the file.");

        uint vlrCount = Bytes.ReadUInt32LE(header.AsSpan(100, 4));
        if (vlrCount > MaxVlrs)
            throw FormatError.Corrupted($"LAS file declares {vlrCount} variable length records; at most {MaxVlrs} are accepted.");

        // The low six bits hold the point format; bit 6 flags waveform data packets and bit 7 is reserved. The
        // waveform tail is skipped as extra bytes, so a file that sets bit 6 still reads with its base format.
        int pointFormat = header[104] & 0x3F;
        if (pointFormat > 10)
            throw FormatError.Unsupported($"LAS point format {pointFormat} is not supported; point formats 0 to 10 are read.");
        if (pointFormat >= 6 && minor != 4)
            throw FormatError.Unsupported($"LAS point format {pointFormat} needs version 1.4.");

        int recordLength = Bytes.ReadUInt16LE(header.AsSpan(105, 2));
        int baseSize = BaseRecordSize(pointFormat);
        if (recordLength < baseSize)
            throw FormatError.Corrupted($"LAS point record length {recordLength} is smaller than the {baseSize} bytes point format {pointFormat} needs.");
        if (recordLength > MaxPointRecordLength)
            throw FormatError.Corrupted($"LAS point record length {recordLength} is larger than the accepted size.");

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
            throw FormatError.Corrupted("LAS scale is not usable.");

        double offsetX = Bytes.ReadDoubleLE(header.AsSpan(155, 8));
        double offsetY = Bytes.ReadDoubleLE(header.AsSpan(163, 8));
        double offsetZ = Bytes.ReadDoubleLE(header.AsSpan(171, 8));
        if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY) || !double.IsFinite(offsetZ))
            throw FormatError.Corrupted("LAS offset is not usable.");

        var bounds = ReadBounds(header);

        var vlrs = ReadVlrs(fs, headerSize, pointDataOffset, vlrCount);
        string? coordinateSystem = FindCoordinateSystem(vlrs);

        var document = new LasDocument(path, fileLength, $"1.{minor}", pointFormat);
        if (coordinateSystem != null)
        {
            document.CoordinateSystem = coordinateSystem;
            document._metadata["CoordinateSystem"] = coordinateSystem;
        }

        string systemId = ReadFixedAscii(header.AsSpan(26, 32));
        if (systemId.Length > 0) document._metadata["SystemIdentifier"] = systemId;
        string software = ReadFixedAscii(header.AsSpan(58, 32));
        if (software.Length > 0) document._metadata["GeneratingSoftware"] = software;

        string name = Path.GetFileNameWithoutExtension(path);
        document._scans.Add(new LasScan("0", name, pointCount, bounds, path, pointDataOffset,
            pointFormat, recordLength, new PclVector3(scaleX, scaleY, scaleZ), new PclVector3(offsetX, offsetY, offsetZ)));
        return document;
    }

    // Minimum record size of a point format; waveform descriptors are part of the tail and are not interpreted.
    private static int BaseRecordSize(int pointFormat) => pointFormat switch
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

    private static PclBounds ReadBounds(byte[] header)
    {
        double maxX = Bytes.ReadDoubleLE(header.AsSpan(179, 8));
        double minX = Bytes.ReadDoubleLE(header.AsSpan(187, 8));
        double maxY = Bytes.ReadDoubleLE(header.AsSpan(195, 8));
        double minY = Bytes.ReadDoubleLE(header.AsSpan(203, 8));
        double maxZ = Bytes.ReadDoubleLE(header.AsSpan(211, 8));
        double minZ = Bytes.ReadDoubleLE(header.AsSpan(219, 8));

        if (!double.IsFinite(maxX) || !double.IsFinite(minX) || !double.IsFinite(maxY) || !double.IsFinite(minY) ||
            !double.IsFinite(maxZ) || !double.IsFinite(minZ))
            return PclBounds.Empty;

        var bounds = new PclBounds(new PclVector3(minX, minY, minZ), new PclVector3(maxX, maxY, maxZ));
        return bounds.IsEmpty ? PclBounds.Empty : bounds;
    }

    private static string ReadFixedAscii(ReadOnlySpan<byte> field)
    {
        int nullIdx = field.IndexOf((byte)0);
        if (nullIdx >= 0) field = field[..nullIdx];
        return System.Text.Encoding.ASCII.GetString(field).Trim();
    }

    private sealed record Vlr(string UserId, int RecordId, byte[] Data);

    // Reads the variable length records between the header and the point data. Every declared size is checked
    // against the file before anything is allocated for it. Only the coordinate system records are kept; the rest
    // are walked past without holding their payload, so a file with many large records does not exhaust memory.
    private static List<Vlr> ReadVlrs(FileStream fs, int headerSize, uint pointDataOffset, uint vlrCount)
    {
        var vlrs = new List<Vlr>((int)Math.Min(vlrCount, 16));
        long position = headerSize;
        long keptBytes = 0;
        byte[] header = new byte[54];

        for (uint i = 0; i < vlrCount; i++)
        {
            if (position + 54 > pointDataOffset) throw FormatError.Corrupted("LAS variable length record reaches past the point data.");
            fs.Position = position;
            fs.ReadExactly(header);
            position += 54;

            string userId = ReadFixedAscii(header.AsSpan(2, 16));
            int recordId = Bytes.ReadUInt16LE(header.AsSpan(18, 2));
            uint dataLength = Bytes.ReadUInt32LE(header.AsSpan(20, 4));
            if (dataLength > MaxVlrDataBytes)
                throw FormatError.Corrupted($"LAS variable length record holds {dataLength} bytes; at most {MaxVlrDataBytes} are accepted.");
            if (position + dataLength > pointDataOffset) throw FormatError.Corrupted("LAS variable length record reaches past the point data.");

            // A compressed LAZ file shares the LAS signature and header; its point data is not LAS records, so it
            // is rejected here rather than read as points.
            if (userId.Equals("laszip encoded", StringComparison.Ordinal) && recordId == 22205)
                throw FormatError.Unsupported("LAS file is compressed (LAZ); compressed point data is not read.");

            bool isCoordinateSystem = userId.Equals("LASF_Projection", StringComparison.Ordinal) && (recordId == 34735 || recordId == 2112);
            if (isCoordinateSystem)
            {
                keptBytes += dataLength;
                if (keptBytes > MaxVlrTotalBytes)
                    throw FormatError.Corrupted("LAS coordinate system records hold more bytes than are accepted.");
                byte[] data = new byte[dataLength];
                if (dataLength > 0) fs.ReadExactly(data);
                vlrs.Add(new Vlr(userId, recordId, data));
            }

            position += dataLength;
        }

        return vlrs;
    }

    // The coordinate system of the file: an EPSG code from a GeoTIFF key directory when it names one, else the
    // OGC WKT when a WKT record holds one. Anything else leaves the coordinate system unknown.
    private static string? FindCoordinateSystem(List<Vlr> vlrs)
    {
        foreach (var vlr in vlrs)
        {
            if (vlr.RecordId == 34735 && vlr.UserId.Equals("LASF_Projection", StringComparison.Ordinal) && TryReadEpsg(vlr.Data, out ushort code))
                return $"EPSG:{code}";
        }

        foreach (var vlr in vlrs)
        {
            // The OGC coordinate system WKT lives in record 2112 under LASF_Projection; record 2111 is a math
            // transform, not a coordinate system, and is not read as one.
            if (vlr.RecordId == 2112 && vlr.UserId.Equals("LASF_Projection", StringComparison.Ordinal))
            {
                string wkt = System.Text.Encoding.UTF8.GetString(vlr.Data).TrimEnd('\0', ' ', '\r', '\n');
                if (wkt.Length > 0) return wkt;
            }
        }

        return null;
    }

    // A GeoTIFF key directory names an EPSG code through the projected (3072) or the geographic (2048) type key.
    private static bool TryReadEpsg(byte[] data, out ushort code)
    {
        code = 0;
        if (data.Length < 8) return false;
        int keyCount = Bytes.ReadUInt16LE(data.AsSpan(6, 2));
        if (keyCount <= 0 || keyCount > 1024) return false;
        if (8 + (keyCount * 8) > data.Length) return false;

        // The projected reference wins over the geographic one.
        for (int pass = 0; pass < 2; pass++)
        {
            int wanted = pass == 0 ? 3072 : 2048;
            for (int k = 0; k < keyCount; k++)
            {
                var entry = data.AsSpan(8 + (k * 8), 8);
                if (Bytes.ReadUInt16LE(entry[..2]) != wanted) continue;
                int value = Bytes.ReadUInt16LE(entry.Slice(6, 2));
                // 32767 is the user-defined sentinel, not an assigned EPSG code; the definition lives elsewhere.
                if (value is >= 1 and <= ushort.MaxValue && value != 32767)
                {
                    code = (ushort)value;
                    return true;
                }
            }
        }

        return false;
    }
}

/// <summary>The single scan of an ASPRS LAS file.</summary>
public sealed class LasScan : PclScan
{
    // At most this many point records are held at once; memory use does not depend on the number of points.
    private const int MaxChunkBytes = 64 * 1024;

    /// <inheritdoc />
    public override string Id { get; }

    /// <inheritdoc />
    public override string Name { get; }

    /// <summary>Number of points the header declares (the extended count for 1.4 when it states one).</summary>
    public override long PointCount { get; }

    /// <summary>Path of the LAS file.</summary>
    public string FilePath { get; }

    /// <inheritdoc />
    public override bool HasColors { get; }

    /// <inheritdoc />
    public override bool HasIntensities => true;

    /// <inheritdoc />
    public override bool HasNormals => false;

    private readonly long _pointDataOffset;
    private readonly int _pointFormat;
    private readonly int _recordLength;
    private readonly PclVector3 _scale;
    private readonly PclVector3 _offset;

    internal LasScan(string id, string name, long pointCount, PclBounds bounds, string filePath,
        uint pointDataOffset, int pointFormat, int recordLength, PclVector3 scale, PclVector3 offset)
    {
        Id = id;
        Name = name;
        PointCount = pointCount;
        Bounds = bounds;
        FilePath = filePath;
        _pointDataOffset = pointDataOffset;
        _pointFormat = pointFormat;
        _recordLength = recordLength;
        _scale = scale;
        _offset = offset;
        HasColors = pointFormat is 2 or 3 or 5 or 7 or 8 or 10;
    }

    /// <summary>Streams the points of the file.</summary>
    /// <remarks>
    /// Records are read in chunks, so memory use does not depend on the number of points.
    /// Points whose stored coordinates do not form finite positions are not returned.
    /// <see cref="PclReadOptions.Blocks"/> and <see cref="PclReadOptions.MaxDegreeOfParallelism"/> are ignored:
    /// the point stream has no independent blocks to select or decode in parallel.
    /// </remarks>
    /// <exception cref="InvalidDataException">The point data is truncated.</exception>
    public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null)
    {
        options ??= PclReadOptions.Default;
        long targetCount = options.MaxPoints ?? PointCount;
        if (targetCount <= 0) yield break;
        targetCount = Math.Min(targetCount, PointCount);
        int step = Math.Max(1, options.SubsampleStep);
        bool legacy = _pointFormat <= 5;
        bool colored = HasColors;

        using var fs = File.OpenRead(FilePath);
        long fileLength = fs.Length;

        int recordsPerChunk = Math.Max(1, MaxChunkBytes / _recordLength);
        byte[] chunk = new byte[recordsPerChunk * _recordLength];
        long yielded = 0;

        for (long start = 0; start < targetCount; start += recordsPerChunk)
        {
            int want = (int)Math.Min(recordsPerChunk, targetCount - start);
            long byteOffset = _pointDataOffset + (start * (long)_recordLength);
            if (byteOffset < 0 || want > (fileLength - byteOffset) / _recordLength)
                throw FormatError.Truncated("point records");

            fs.Position = byteOffset;
            fs.ReadExactly(chunk, 0, want * _recordLength);

            for (int i = 0; i < want; i++)
            {
                if ((start + i) % step != 0) continue;
                var record = chunk.AsSpan(i * _recordLength, _recordLength);

                double x = _offset.X + (Bytes.ReadInt32LE(record[..4]) * _scale.X);
                double y = _offset.Y + (Bytes.ReadInt32LE(record.Slice(4, 4)) * _scale.Y);
                double z = _offset.Z + (Bytes.ReadInt32LE(record.Slice(8, 4)) * _scale.Z);
                if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) continue;

                float intensity = Bytes.ReadUInt16LE(record.Slice(12, 2)) / 65535.0f;
                byte classification = legacy
                    ? (byte)(record[15] & 0x1F)
                    : record[16];

                PclColor? color = null;
                if (colored)
                {
                    int at = legacy ? 20 : 30;
                    color = new PclColor(
                        (byte)(Bytes.ReadUInt16LE(record.Slice(at, 2)) / 257),
                        (byte)(Bytes.ReadUInt16LE(record.Slice(at + 2, 2)) / 257),
                        (byte)(Bytes.ReadUInt16LE(record.Slice(at + 4, 2)) / 257));
                }

                var pt = new PclPoint(new PclVector3(x, y, z), Color: color, Intensity: intensity,
                    Classification: classification, Index: (ulong)yielded);

                if (options.ApplyTransform)
                    pt = pt with { Position = Transform.TransformPoint(pt.Position) };

                yield return pt;
                yielded++;
                if (yielded >= targetCount) yield break;
            }
        }
    }
}
