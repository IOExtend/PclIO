using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PclIO.Internal;

namespace PclIO.Formats.CcBin;

/// <summary>Represents a CC BIN point cloud file (.bin, CCB2).</summary>
public sealed class CcBinDocument : PclDocument
{
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
        if (fs.Read(ccHead) < 43) return false;

        double sx = BinaryPrimitives.ReadDoubleLittleEndian(ccHead.AsSpan(8, 8));
        double sy = BinaryPrimitives.ReadDoubleLittleEndian(ccHead.AsSpan(16, 8));
        double sz = BinaryPrimitives.ReadDoubleLittleEndian(ccHead.AsSpan(24, 8));
        double scale = BinaryPrimitives.ReadDoubleLittleEndian(ccHead.AsSpan(32, 8));
        if (scale == 0 || !double.IsFinite(scale)) scale = 1.0;

        byte[] countBuf = new byte[4];
        if (fs.Read(countBuf) < 4) return false;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(countBuf);
        if (count == 0) return false;

        long ptStart = fs.Position;
        long ptBytes = (long)count * 12;
        if (ptStart + ptBytes > fileLength) return false;

        pointsEndOffset = ptStart + ptBytes;

        // Check for colors right after points
        fs.Position = pointsEndOffset;
        int colorFlag = fs.ReadByte();
        bool hasColors = false;
        long colorOffset = -1;
        byte channels = 3;

        if (colorFlag == 1)
        {
            byte[] colH = new byte[16];
            if (fs.Read(colH) < 16)
                throw new InvalidDataException("CC BIN RGB color header is truncated.");

            uint colNameLen = BinaryPrimitives.ReadUInt32BigEndian(colH.AsSpan(12, 4));
            if (colNameLen > 500 || fs.Position + colNameLen + 9 > fileLength)
                throw new InvalidDataException("CC BIN RGB color name or properties are truncated.");

            fs.Position += colNameLen;
            byte[] colProps = new byte[9];
            if (fs.Read(colProps) < 9)
                throw new InvalidDataException("CC BIN RGB color properties are truncated.");

            channels = colProps[8];
            if (channels is not (3 or 4))
                throw new InvalidDataException($"CC BIN unsupported color channels: {channels}.");

            byte[] colCountBuf = new byte[4];
            if (fs.Read(colCountBuf) < 4)
                throw new InvalidDataException("CC BIN RGB color count is truncated.");

            uint colCount = BinaryPrimitives.ReadUInt32LittleEndian(colCountBuf);
            if (colCount != count)
                throw new InvalidDataException($"CC BIN color count {colCount} does not match point count {count}.");

            long cOffset = fs.Position;
            long cBytes = (long)colCount * channels;
            if (cOffset + cBytes > fileLength)
                throw new InvalidDataException("CC BIN RGB color data is truncated.");

            hasColors = true;
            colorOffset = cOffset;
            fs.Position += cBytes;
        }

        // Matrix 4x4
        var matrix16 = new double[16];
        bool isIdentity = true;
        byte[] matH = new byte[8 + 64];
        if (fs.Read(matH) == 72)
        {
            for (int m = 0; m < 16; m++)
            {
                float val = BinaryPrimitives.ReadSingleLittleEndian(matH.AsSpan(8 + m * 4, 4));
                matrix16[m] = val;
                int row = m / 4;
                int col = m % 4;
                double expected = row == col ? 1.0 : 0.0;
                if (Math.Abs(val - expected) > 1e-6)
                {
                    isIdentity = false;
                }
            }
        }

        // Scalar fields (probe for Intensity)
        bool hasIntensity = false;
        long intensityOffset = -1;
        uint intensityCount = 0;
        int sfFlag = fs.ReadByte();
        if (sfFlag >= 0)
        {
            byte[] sfCountBuf = new byte[4];
            if (fs.Read(sfCountBuf) == 4)
            {
                uint sfCount = BinaryPrimitives.ReadUInt32LittleEndian(sfCountBuf);
                if (sfCount is > 0 and < 100)
                {
                    byte[] sfNameBuf = new byte[256];
                    byte[] sCountBuf = new byte[4];
                    for (int s = 0; s < sfCount; s++)
                    {
                        if (fs.Position + 256 + 5 > fileLength)
                            throw new InvalidDataException("CC BIN scalar field header is truncated.");
                        if (fs.Read(sfNameBuf) < 256)
                            throw new InvalidDataException("CC BIN scalar field header is truncated.");
                        string sfName = Encoding.ASCII.GetString(sfNameBuf).TrimEnd('\0');
                        int sFlag = fs.ReadByte();
                        if (sFlag < 0 || fs.Read(sCountBuf) < 4)
                            throw new InvalidDataException($"CC BIN scalar field '{sfName}' count is truncated.");
                        uint sCount = BinaryPrimitives.ReadUInt32LittleEndian(sCountBuf);
                        long dataOffset = fs.Position;
                        long sBytes = (long)sCount * 4;
                        if (dataOffset + sBytes > fileLength)
                            throw new InvalidDataException($"CC BIN scalar field '{sfName}' data is truncated.");

                        if (!hasIntensity && sfName.Equals("Intensity", StringComparison.OrdinalIgnoreCase))
                        {
                            hasIntensity = true;
                            intensityOffset = dataOffset;
                            intensityCount = sCount;
                        }

                        fs.Position += sBytes;
                        if (s + 1 < sfCount)
                        {
                            fs.Position += 219; // Skip color scale / metadata
                        }
                    }
                }
            }
        }

        // Construct transform:
        // According to Global Shift and Scale mathematics:
        // P_shifted = (P_original + Shift) * Scale
        // Therefore, to recover the original (global) coordinate from stored local coordinate:
        // P_original = P_local / Scale - Shift
        // In PclTransform: P_transformed = P_local * Transform.Scale + Transform.Translation
        double invScale = (scale != 0.0 && !double.IsNaN(scale) && !double.IsInfinity(scale))
            ? (1.0 / scale)
            : 1.0;

        PclTransform transform;
        if (!isIdentity)
        {
            // When a 4x4 matrix M is present, it must be composed with the global shift and scale:
            // P_world = (M * P_local) * invScale - Shift
            // Row i: combined[i, j] = invScale * M[i, j], and combined[i, 3] = invScale * M[i, 3] - Shift[i]
            var combined = new double[16];
            combined[0] = invScale * matrix16[0];
            combined[1] = invScale * matrix16[1];
            combined[2] = invScale * matrix16[2];
            combined[3] = invScale * matrix16[3] - sx;

            combined[4] = invScale * matrix16[4];
            combined[5] = invScale * matrix16[5];
            combined[6] = invScale * matrix16[6];
            combined[7] = invScale * matrix16[7] - sy;

            combined[8] = invScale * matrix16[8];
            combined[9] = invScale * matrix16[9];
            combined[10] = invScale * matrix16[10];
            combined[11] = invScale * matrix16[11] - sz;

            combined[12] = 0.0;
            combined[13] = 0.0;
            combined[14] = 0.0;
            combined[15] = 1.0;

            transform = new PclTransform
            {
                Scale = new PclVector3(invScale, invScale, invScale),
                Translation = new PclVector3(-sx, -sy, -sz),
                Matrix4x4 = combined,
            };
        }
        else
        {
            transform = new PclTransform
            {
                Scale = new PclVector3(invScale, invScale, invScale),
                Translation = new PclVector3(-sx, -sy, -sz),
                Matrix4x4 = null,
            };
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
            hasIntensity: hasIntensity,
            intensityOffset: intensityOffset,
            intensityCount: intensityCount,
            shiftX: sx,
            shiftY: sy,
            shiftZ: sz,
            scale: scale,
            transform: transform,
            properties: props);

        pointsEndOffset = Math.Max(pointsEndOffset, fs.Position);
        return true;
    }
}
