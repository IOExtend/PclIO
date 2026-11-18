using System.Buffers.Binary;
using System.Globalization;
using PclIO.Internal;

namespace PclIO.Formats.Ptg;

/// <summary>Represents a point scan inside a Leica PTG file.</summary>
public sealed class PtgScan : PclScan
{
    private readonly string _filePath;
    private readonly PtgHeader _header;
    private readonly long[] _columnOffsets;
    private readonly long _validPointCount;

    /// <inheritdoc />
    public override string Id { get; }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override long PointCount => _validPointCount;

    /// <inheritdoc />
    public override bool HasColors => _header.HasColors;

    /// <inheritdoc />
    public override bool HasIntensities => _header.HasIntensity;

    /// <summary>Number of rows in the scan grid.</summary>
    public int Rows => _header.NumRows;

    /// <summary>Number of columns in the scan grid.</summary>
    public int Columns => _header.NumCols;

    /// <summary>Total number of cells in the scan grid (Rows * Columns).</summary>
    public long GridSize => (long)_header.NumRows * _header.NumCols;

    internal PtgScan(string filePath, string id, string name, PtgHeader header, long[] columnOffsets, long validPointCount)
    {
        _filePath = filePath;
        _header = header;
        _columnOffsets = columnOffsets;
        _validPointCount = validPointCount;
        Id = id;
        Name = name;
        Transform = header.Transform;

        _properties["Columns"] = header.NumCols.ToString(CultureInfo.InvariantCulture);
        _properties["Rows"] = header.NumRows.ToString(CultureInfo.InvariantCulture);
        _properties["GridSize"] = GridSize.ToString(CultureInfo.InvariantCulture);
        _properties["ValidPoints"] = validPointCount.ToString(CultureInfo.InvariantCulture);
        _properties["CoordinateType"] = header.HasDoubleCoordinates ? "double" : "float";
        _properties["HasIntensity"] = header.HasIntensity ? "true" : "false";
        _properties["HasColors"] = header.HasColors ? "true" : "false";

        if (!string.IsNullOrEmpty(header.Version)) _properties["Version"] = header.Version;
        if (!string.IsNullOrEmpty(header.ScannerModel)) _properties["ScannerModel"] = header.ScannerModel;
        if (!string.IsNullOrEmpty(header.SoftwareName)) _properties["SoftwareName"] = header.SoftwareName;
    }

    internal static PtgScan Create(string filePath, string id, string? defaultName = null)
    {
        using var fs = File.OpenRead(filePath);
        long fileLength = fs.Length;
        if (fileLength < 8)
            throw FormatError.Truncated("PTG file signature");

        var header = PtgHeader.Parse(fs);

        int cols = header.NumCols;
        int rows = header.NumRows;
        long colTableOffset = header.ColumnPositionsOffset;
        long colTableSize = (long)cols * 8;
        if (colTableOffset + colTableSize > fileLength)
            throw FormatError.Truncated("PTG column offset table");

        fs.Position = colTableOffset;
        byte[] colTableBytes = new byte[colTableSize];
        fs.ReadExactly(colTableBytes);

        long[] colOffsets = new long[cols];
        int maskSize = (rows + 7) / 8;
        int recordSize = header.RecordSize;
        byte[] maskBuf = new byte[maskSize];
        long totalValid = 0;

        for (int c = 0; c < cols; c++)
        {
            long off = BinaryPrimitives.ReadInt64LittleEndian(colTableBytes.AsSpan(c * 8, 8));
            if (off < colTableOffset + colTableSize || off > fileLength - maskSize)
                throw FormatError.Corrupted($"PTG column {c} offset {off} is invalid.");

            colOffsets[c] = off;
        }

        for (int c = 0; c < cols; c++)
        {
            fs.Position = colOffsets[c];
            fs.ReadExactly(maskBuf);

            int validInCol = 0;
            for (int r = 0; r < rows; r++)
            {
                int bIdx = r >> 3;
                int bit = 7 - (r & 7);
                if ((maskBuf[bIdx] & (1 << bit)) != 0)
                    validInCol++;
            }

            long dataSize = (long)validInCol * recordSize;
            if (colOffsets[c] + maskSize + dataSize > fileLength)
                throw FormatError.Truncated($"PTG column {c} data");

            totalValid += validInCol;
        }

        string name = !string.IsNullOrWhiteSpace(header.ScanName)
            ? header.ScanName
            : (defaultName ?? Path.GetFileNameWithoutExtension(filePath));

        return new PtgScan(filePath, id, name, header, colOffsets, totalValid);
    }

    /// <inheritdoc />
    public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null)
    {
        options ??= PclReadOptions.Default;
        long targetCount = options.MaxPoints ?? long.MaxValue;
        if (targetCount <= 0 || _validPointCount == 0) yield break;
        int step = Math.Max(1, options.SubsampleStep);

        using var fs = File.OpenRead(_filePath);
        int rows = _header.NumRows;
        int cols = _header.NumCols;
        int maskSize = (rows + 7) / 8;
        int recordSize = _header.RecordSize;
        bool isDouble = _header.HasDoubleCoordinates;
        bool hasIntensity = _header.HasIntensity;
        bool hasColors = _header.HasColors;

        byte[] maskBuffer = new byte[maskSize];
        byte[]? dataBuffer = null;

        long yielded = 0;
        long validPointIndex = 0;

        for (int c = 0; c < cols; c++)
        {
            long colOffset = _columnOffsets[c];
            fs.Position = colOffset;

            int maskRead = fs.ReadAtLeast(maskBuffer, maskSize, throwOnEndOfStream: false);
            if (maskRead < maskSize)
                throw FormatError.Truncated($"PTG column {c} bitmask");

            int validInCol = 0;
            for (int r = 0; r < rows; r++)
            {
                int bIdx = r >> 3;
                int bit = 7 - (r & 7);
                if ((maskBuffer[bIdx] & (1 << bit)) != 0)
                    validInCol++;
            }

            if (validInCol == 0) continue;

            int colDataBytes = validInCol * recordSize;
            if (dataBuffer == null || dataBuffer.Length < colDataBytes)
            {
                dataBuffer = new byte[colDataBytes];
            }

            int dataRead = fs.Read(dataBuffer, 0, colDataBytes);
            if (dataRead < colDataBytes)
                throw FormatError.Truncated($"PTG column {c} point records");

            int recordOffset = 0;

            for (int r = 0; r < rows; r++)
            {
                int bIdx = r >> 3;
                int bit = 7 - (r & 7);
                if ((maskBuffer[bIdx] & (1 << bit)) == 0)
                    continue;

                validPointIndex++;
                if ((validPointIndex - 1) % step != 0)
                {
                    recordOffset += recordSize;
                    continue;
                }

                ParseRecord(dataBuffer, recordOffset, isDouble, hasIntensity, hasColors,
                    out double x, out double y, out double z, out float? intensity, out PclColor? color);
                recordOffset += recordSize;

                if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
                    continue;

                PclVector3 position = new(x, y, z);
                if (options.ApplyTransform && !Transform.IsIdentity)
                {
                    position = Transform.TransformPoint(position);
                }

                yield return new PclPoint(position, Color: color, Intensity: intensity, Index: (ulong)yielded);
                yielded++;
                if (yielded >= targetCount) yield break;
            }
        }
    }

    private static void ParseRecord(
        byte[] data,
        int offset,
        bool isDouble,
        bool hasIntensity,
        bool hasColors,
        out double x,
        out double y,
        out double z,
        out float? intensity,
        out PclColor? color)
    {
        ReadOnlySpan<byte> rec = data.AsSpan(offset);
        int pos = 0;
        if (isDouble)
        {
            x = BinaryPrimitives.ReadDoubleLittleEndian(rec[pos..]);
            y = BinaryPrimitives.ReadDoubleLittleEndian(rec[(pos + 8)..]);
            z = BinaryPrimitives.ReadDoubleLittleEndian(rec[(pos + 16)..]);
            pos += 24;
        }
        else
        {
            x = BinaryPrimitives.ReadSingleLittleEndian(rec[pos..]);
            y = BinaryPrimitives.ReadSingleLittleEndian(rec[(pos + 4)..]);
            z = BinaryPrimitives.ReadSingleLittleEndian(rec[(pos + 8)..]);
            pos += 12;
        }

        intensity = null;
        if (hasIntensity)
        {
            intensity = BinaryPrimitives.ReadSingleLittleEndian(rec[pos..]);
            pos += 4;
        }

        color = null;
        if (hasColors)
        {
            color = new PclColor(rec[pos], rec[pos + 1], rec[pos + 2]);
        }
    }
}
