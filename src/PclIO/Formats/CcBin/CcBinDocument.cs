using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PclIO.Internal;

namespace PclIO.Formats.CcBin;

/// <summary>Represents a CC BIN point cloud file (.bin, CCB2).</summary>
public sealed class CcBinDocument : PclDocument
{
    private const int ArrayFooterPrefixSize = 8;
    private const int ArrayFooterWithMatrixSize = ArrayFooterPrefixSize + 16 * sizeof(float);
    private static readonly byte[] Magic = "CCB2"u8.ToArray();

    /// <inheritdoc />
    public override PclFileKind Kind => PclFileKind.CcBin;

    /// <inheritdoc />
    public override IReadOnlyList<PclScan> Scans => _scans;
    private readonly List<PclScan> _scans = [];

    /// <summary>Format version declared in the file header.</summary>
    public uint Version { get; }

    private CcBinDocument(string path, long fileSize, uint version)
        : base(path, fileSize)
    {
        Version = version;
    }

    /// <summary>Opens a CC BIN point cloud file.</summary>
    /// <param name="path">Path to the .bin file.</param>
    /// <param name="options">Read options (accepted for symmetry).</param>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file signature, version or entity layout is corrupted or truncated.</exception>
    public static CcBinDocument Open(string path, PclReadOptions? options = null)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("CC BIN file not found.", path);
        long fileLength = fi.Length;
        if (fileLength < 8)
            throw FormatError.Truncated("CC BIN file header is smaller than 8 bytes.");

        using var fs = File.OpenRead(path);
        byte[] head = new byte[8];
        fs.ReadExactly(head);

        if (!head.AsSpan(0, 4).SequenceEqual(Magic))
            throw FormatError.Corrupted($"Invalid CC BIN signature in '{path}'; expected 'CCB2'.");

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4, 4));
        if (version is < 20 or > 2000)
            throw FormatError.Corrupted($"Unsupported or invalid CC BIN format version {version} in '{path}'.");

        var doc = new CcBinDocument(path, fileLength, version);
        doc.ScanPointClouds(fs, fileLength);

        if (doc._scans.Count == 0)
            throw FormatError.Corrupted($"CC BIN file '{path}' contains no recognizable point cloud entities.");

        doc._metadata["Format"] = "CC BIN";
        doc._metadata["Version"] = version.ToString(CultureInfo.InvariantCulture);
        doc._metadata["Scans"] = doc._scans.Count.ToString(CultureInfo.InvariantCulture);
        doc._metadata["TotalPoints"] = doc.TotalPointCount.ToString(CultureInfo.InvariantCulture);

        return doc;
    }

    private void ScanPointClouds(FileStream fs, long fileLength)
    {
        const int BlockSize = 65536;
        byte[] buffer = new byte[BlockSize + 512];
        long streamPos = 8;

        while (streamPos < fileLength)
        {
            int toRead = (int)Math.Min(BlockSize + 512, fileLength - streamPos);
            fs.Position = streamPos;
            int bytesRead = fs.Read(buffer, 0, toRead);
            if (bytesRead < 44) break;

            int scanLimit = bytesRead - 44;
            bool found = false;
            for (int i = 0; i <= scanLimit; i++)
            {
                // Entity type f1 == 257 (0x01, 0x01, 0x00, 0x00) and f2 == 0 (0x00, 0x00, 0x00, 0x00)
                if (buffer[i] == 0x01 && buffer[i + 1] == 0x01 && buffer[i + 2] == 0x00 && buffer[i + 3] == 0x00 &&
                    buffer[i + 4] == 0x00 && buffer[i + 5] == 0x00 && buffer[i + 6] == 0x00 && buffer[i + 7] == 0x00)
                {
                    long entityPos = streamPos + i;
                    if (TryParsePointCloud(fs, fileLength, entityPos, _scans.Count, out var scan, out long nextOffset))
                    {
                        _scans.Add(scan);
                        streamPos = Math.Max(entityPos + 8, nextOffset);
                        found = true;
                        break;
                    }
                }
            }

            if (!found)
            {
                streamPos += BlockSize;
            }
        }
    }

    private bool TryParsePointCloud(
        FileStream fs,
        long fileLength,
        long entityPos,
        int scanIndex,
        out CcBinScan scan,
        out long pointsEndOffset)
    {
        scan = default!;
        pointsEndOffset = entityPos + 8;
        if (entityPos + 24 > fileLength) return false;

        fs.Position = entityPos + 8;
        byte[] h = new byte[8];
        if (fs.Read(h) < 8) return false;
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(0, 4));
        uint nameByteLen = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(4, 4));
        if (nameByteLen is 0 or > 2000 || fs.Position + nameByteLen > fileLength) return false;

        byte[] nameBytes = new byte[nameByteLen];
        if (fs.Read(nameBytes) < (int)nameByteLen) return false;
        string name = Encoding.BigEndianUnicode.GetString(nameBytes);

        // QVariantMap properties
        byte[] mapHead = new byte[8];
        if (fs.Read(mapHead) < 8) return false;
        uint mapType = BinaryPrimitives.ReadUInt32LittleEndian(mapHead.AsSpan(0, 4));
        uint propCount = BinaryPrimitives.ReadUInt32LittleEndian(mapHead.AsSpan(4, 4));
        if (mapType != 2 || propCount > 1000) return false;

        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        byte[] kLenBuf = new byte[4];
        byte[] vHead = new byte[5];
        byte[] sLenBuf = new byte[4];

        for (uint p = 0; p < propCount; p++)
        {
            if (fs.Read(kLenBuf) < 4) return false;
            uint kLen = BinaryPrimitives.ReadUInt32BigEndian(kLenBuf);
            if (kLen > 1000 || fs.Position + kLen > fileLength) return false;

            byte[] kBytes = new byte[kLen];
            if (fs.Read(kBytes) < (int)kLen) return false;
            string key = Encoding.BigEndianUnicode.GetString(kBytes);

            if (fs.Read(vHead) < 5) return false;
            uint vType = BinaryPrimitives.ReadUInt32BigEndian(vHead.AsSpan(0, 4));
            byte isNull = vHead[4];

            if (vType == 6) // double
            {
                byte[] dBuf = new byte[8];
                if (fs.Read(dBuf) < 8) return false;
                if (isNull == 0)
                {
                    double dVal = BitConverter.UInt64BitsToDouble(BinaryPrimitives.ReadUInt64BigEndian(dBuf));
                    props[key] = dVal.ToString(CultureInfo.InvariantCulture);
                }
            }
            else if (vType is 2 or 3) // int / uint
            {
                byte[] iBuf = new byte[4];
                if (fs.Read(iBuf) < 4) return false;
                if (isNull == 0)
                {
                    int iVal = BinaryPrimitives.ReadInt32BigEndian(iBuf);
                    props[key] = iVal.ToString(CultureInfo.InvariantCulture);
                }
            }
            else if (vType == 1) // bool
            {
                int bVal = fs.ReadByte();
                if (bVal < 0) return false;
                if (isNull == 0)
                {
                    props[key] = bVal != 0 ? "true" : "false";
                }
            }
            else if (vType == 10) // string
            {
                if (fs.Read(sLenBuf) < 4) return false;
                uint sLen = BinaryPrimitives.ReadUInt32BigEndian(sLenBuf);
                if (sLen > 2000 || fs.Position + sLen > fileLength) return false;
                byte[] sBytes = new byte[sLen];
                if (fs.Read(sBytes) < (int)sLen) return false;
                if (isNull == 0)
                {
                    props[key] = Encoding.BigEndianUnicode.GetString(sBytes);
                }
            }
            else
            {
                return false;
            }
        }

        // 43 bytes: flags (8), shift (24), scale (8), marker (3)
        byte[] ccHead = new byte[43];
        // Confirm the XYZ array before treating an incomplete point block as a damaged scan.
        if (fs.Read(ccHead) < ccHead.Length || ccHead[^1] != 3) return false;

        double sx = BinaryPrimitives.ReadDoubleLittleEndian(ccHead.AsSpan(8, 8));
        double sy = BinaryPrimitives.ReadDoubleLittleEndian(ccHead.AsSpan(16, 8));
        double sz = BinaryPrimitives.ReadDoubleLittleEndian(ccHead.AsSpan(24, 8));
        double scale = BinaryPrimitives.ReadDoubleLittleEndian(ccHead.AsSpan(32, 8));
        if (scale == 0 || !double.IsFinite(scale) || !double.IsFinite(sx) || !double.IsFinite(sy) || !double.IsFinite(sz))
            throw FormatError.Corrupted("CC BIN has an invalid global shift or scale.");

        byte[] countBuf = new byte[4];
        if (fs.Read(countBuf) < countBuf.Length)
            throw FormatError.Truncated($"CC BIN point count for scan {scanIndex}");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(countBuf);
        if (count == 0) return false;

        long ptStart = fs.Position;
        long ptBytes = (long)count * 12;
        if (ptBytes > fileLength - ptStart)
            throw FormatError.Truncated($"CC BIN XYZ coordinates for scan {scanIndex} ({count} declared points)");

        pointsEndOffset = ptStart + ptBytes;

        // Check for colors right after points
        fs.Position = pointsEndOffset;
        bool hasColors = ReadArrayFlag(fs, "color");
        long colorOffset = -1;
        byte channels = 3;

        if (hasColors)
        {
            var (_, colorChannels, colCount) = ReadArrayHeader(fs, "color");
            channels = colorChannels;
            if (channels is not (3 or 4))
                throw new InvalidDataException($"CC BIN unsupported color channels: {channels}.");
            if (colCount != count)
                throw new InvalidDataException($"CC BIN color count {colCount} does not match point count {count}.");

            colorOffset = fs.Position;
            long cBytes = (long)colCount * channels;
            if (cBytes > fileLength - colorOffset)
                throw new InvalidDataException("CC BIN RGB color data is truncated.");
            fs.Position += cBytes;
        }

        double[]? matrix = hasColors ? ReadColorArrayFooter(fs) : null;
        long normalOffset = ReadNormalOffset(fs, count);

        // Scalar fields (probe for Intensity)
        long intensityOffset = -1;
        uint intensityCount = 0;
        Span<byte> countBytes = stackalloc byte[4];
        ReadScalarBytes(fs, countBytes);
        uint fieldCount = BinaryPrimitives.ReadUInt32LittleEndian(countBytes);
        if (fieldCount >= 100)
            throw FormatError.Unsupported("CC BIN has 100 or more scalar fields.");

        Span<byte> fieldHeader = stackalloc byte[261];
        for (uint field = 0; field < fieldCount; field++)
        {
            ReadScalarBytes(fs, fieldHeader);
            uint valueCount = BinaryPrimitives.ReadUInt32LittleEndian(fieldHeader[257..]);
            long dataOffset = fs.Position;
            SkipScalarBytes(fs, (long)valueCount * sizeof(float));

            if (intensityOffset < 0 &&
                Encoding.ASCII.GetString(fieldHeader[..256]).TrimEnd('\0').Equals("Intensity", StringComparison.OrdinalIgnoreCase))
            {
                intensityOffset = dataOffset;
                intensityCount = valueCount;
            }

            if (field + 1 < fieldCount)
                SkipScalarMetadata(fs);
        }

        string scanId = scanIndex.ToString(CultureInfo.InvariantCulture);
        scan = new CcBinScan(
            filePath: fs.Name,
            id: scanId,
            name: name,
            pointCount: count,
            pointOffset: ptStart,
            hasColors: hasColors,
            colorOffset: colorOffset,
            colorChannels: channels,
            normalOffset: normalOffset,
            intensityOffset: intensityOffset,
            intensityCount: intensityCount,
            shiftX: sx,
            shiftY: sy,
            shiftZ: sz,
            scale: scale,
            transform: CreateTransform(scale, new PclVector3(-sx, -sy, -sz), matrix),
            normalTransform: normalOffset >= 0 && (matrix != null || scale < 0)
                ? new PclTransform { Matrix4x4 = matrix, UniformScaleDivisor = Math.Sign(scale) }
                : null,
            properties: props);

        pointsEndOffset = Math.Max(pointsEndOffset, fs.Position);
        return true;
    }

    private bool HasShortArrayFooter => Version is 41 or 42;
    private int ArrayFooterSize => HasShortArrayFooter ? ArrayFooterPrefixSize : ArrayFooterWithMatrixSize;

    private static bool ReadArrayFlag(FileStream fs, string attribute) => fs.ReadByte() switch
    {
        0 => false,
        1 => true,
        -1 => throw FormatError.Truncated($"CC BIN {attribute} array flag"),
        _ => throw FormatError.Corrupted($"CC BIN {attribute} array flag is invalid."),
    };

    private static (ulong Type, byte Components, uint Count) ReadArrayHeader(FileStream fs, string attribute)
    {
        Span<byte> header = stackalloc byte[16];
        if (fs.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length)
            throw FormatError.Truncated($"CC BIN {attribute} array header");

        uint nameLength = BinaryPrimitives.ReadUInt32BigEndian(header[12..]);
        if (nameLength > 500 || (nameLength & 1) != 0)
            throw FormatError.Corrupted($"CC BIN {attribute} array name has an invalid byte length.");
        if (nameLength > fs.Length - fs.Position)
            throw FormatError.Truncated($"CC BIN {attribute} array name");
        fs.Position += nameLength;

        Span<byte> properties = stackalloc byte[9 + sizeof(uint)];
        if (fs.ReadAtLeast(properties, properties.Length, throwOnEndOfStream: false) != properties.Length)
            throw FormatError.Truncated($"CC BIN {attribute} array properties");
        return (BinaryPrimitives.ReadUInt64LittleEndian(header), properties[8],
            BinaryPrimitives.ReadUInt32LittleEndian(properties[9..]));
    }

    private double[]? ReadColorArrayFooter(FileStream fs)
    {
        Span<byte> footer = stackalloc byte[ArrayFooterWithMatrixSize];
        var data = footer[..ArrayFooterSize];
        if (fs.ReadAtLeast(data, data.Length, throwOnEndOfStream: false) != data.Length)
            throw FormatError.Truncated("CC BIN color array footer");
        if (HasShortArrayFooter) return null;

        var matrix = new double[16];
        bool isIdentity = true;
        for (int i = 0; i < matrix.Length; i++)
        {
            float value = BinaryPrimitives.ReadSingleLittleEndian(data[(ArrayFooterPrefixSize + i * sizeof(float))..]);
            if (!float.IsFinite(value))
                throw FormatError.Corrupted("CC BIN has a non-finite coordinate matrix.");
            matrix[i] = value;
            double expected = i % 5 == 0 ? 1.0 : 0.0;
            isIdentity &= Math.Abs(value - expected) <= 1e-6;
        }

        return isIdentity ? null : matrix;
    }

    private long ReadNormalOffset(FileStream fs, uint pointCount)
    {
        if (!ReadArrayFlag(fs, "normal")) return -1;
        if (!HasShortArrayFooter && Version != 50)
            throw FormatError.Unsupported($"CC BIN normals in version {Version}.");

        var (type, components, count) = ReadArrayHeader(fs, "normal");
        if (type != 0x40000012 || components != 1)
            throw FormatError.Unsupported("CC BIN normal array encoding.");
        if (count != pointCount)
            throw FormatError.Corrupted($"CC BIN normal count {count} does not match point count {pointCount}.");

        long offset = fs.Position;
        long bytes = (long)count * sizeof(uint) + ArrayFooterSize;
        if (bytes > fs.Length - offset)
            throw FormatError.Truncated("CC BIN normal array data or footer");
        fs.Position += bytes;
        return offset;
    }

    private static void SkipScalarMetadata(FileStream fs)
    {
        SkipScalarBytes(fs, 52); // Display ranges and flags before the optional palette.
        int hasPalette = fs.ReadByte();
        if (hasPalette < 0)
            throw FormatError.Truncated("CC BIN scalar field metadata");
        if (hasPalette > 1)
            throw FormatError.Corrupted("CC BIN scalar field palette flag is invalid.");

        if (hasPalette == 1)
        {
            Span<byte> word = stackalloc byte[4];
            for (int i = 0; i < 2; i++)
            {
                ReadScalarBytes(fs, word);
                uint byteLength = BinaryPrimitives.ReadUInt32BigEndian(word);
                if (byteLength == uint.MaxValue) continue;
                if (byteLength > 4096 || (byteLength & 1) != 0)
                    throw FormatError.Corrupted("CC BIN scalar field palette string has an invalid byte length.");
                SkipScalarBytes(fs, byteLength);
            }

            SkipScalarBytes(fs, 18); // Palette settings before the step count.
            ReadScalarBytes(fs, word);
            uint steps = BinaryPrimitives.ReadUInt32LittleEndian(word);
            if (steps > 4096)
                throw FormatError.Unsupported("CC BIN scalar field palette exceeds 4096 steps.");
            SkipScalarBytes(fs, steps * 19L); // Position and colour for each palette step.
        }

        SkipScalarBytes(fs, 16);
    }

    private static void ReadScalarBytes(FileStream fs, Span<byte> buffer)
    {
        if (fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) != buffer.Length)
            throw FormatError.Truncated("CC BIN scalar field header or metadata");
    }

    private static void SkipScalarBytes(FileStream fs, long bytes)
    {
        if (bytes > fs.Length - fs.Position)
            throw FormatError.Truncated("CC BIN scalar field data or metadata");
        fs.Position += bytes;
    }

    private static PclTransform CreateTransform(double scale, PclVector3 translation, double[]? matrix)
    {
        // Divide the local coordinates before shifting them back to the original coordinate system.
        double inverse = 1.0 / scale;
        double[]? combined = null;
        if (matrix != null && double.IsFinite(inverse))
        {
            combined = new double[16];
            for (int i = 0; i < 12; i++)
                combined[i] = inverse * matrix[i];
            combined[3] += translation.X;
            combined[7] += translation.Y;
            combined[11] += translation.Z;
            combined[15] = 1.0;
        }

        // The point divided by scale can be finite even when a composed coefficient overflows.
        if (!double.IsFinite(inverse) || (combined != null && combined.Any(static x => !double.IsFinite(x))))
            return new PclTransform { UniformScaleDivisor = scale, Translation = translation, Matrix4x4 = matrix };

        return new PclTransform
        {
            Scale = new PclVector3(inverse, inverse, inverse),
            Translation = translation,
            Matrix4x4 = combined,
        };
    }
}
