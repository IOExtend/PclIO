using System.Buffers.Binary;
using System.Globalization;
using PclIO.Internal;

namespace PclIO.Formats.CcBin;

/// <summary>Represents a point cloud entity inside a CC BIN file.</summary>
public sealed class CcBinScan : PclScan
{
    private const int BatchRecords = 1024;

    private readonly string _filePath;
    private readonly long _pointOffset;
    private readonly long _pointCount;
    private readonly bool _hasColors;
    private readonly long _colorOffset;
    private readonly byte _colorChannels;
    private readonly bool _hasIntensity;
    private readonly long _intensityOffset;
    private readonly long _intensityCount;
    private readonly double _shiftX;
    private readonly double _shiftY;
    private readonly double _shiftZ;
    private readonly double _scale;

    /// <inheritdoc />
    public override string Id { get; }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override long PointCount => _pointCount;

    /// <inheritdoc />
    public override bool HasColors => _hasColors;

    /// <inheritdoc />
    public override bool HasIntensities => _hasIntensity && _intensityCount > 0;

    /// <inheritdoc />
    /// <remarks>Modern CC entity files store normals using quantized spherical sector indices (ccNormalCompressor)
    /// which lacks a published open specification; under clean-room rules, normals are not decoded.</remarks>
    public override bool HasNormals => false;

    /// <summary>Global shift vector stored with the cloud entity.</summary>
    public PclVector3 GlobalShift => new(_shiftX, _shiftY, _shiftZ);

    /// <summary>Global scale factor stored with the cloud entity.</summary>
    public double GlobalScale => _scale;

    internal CcBinScan(
        string filePath,
        string id,
        string name,
        long pointCount,
        long pointOffset,
        bool hasColors,
        long colorOffset,
        byte colorChannels,
        bool hasIntensity,
        long intensityOffset,
        long intensityCount,
        double shiftX,
        double shiftY,
        double shiftZ,
        double scale,
        PclTransform transform,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        _filePath = filePath;
        Id = id;
        Name = name;
        _pointCount = pointCount;
        _pointOffset = pointOffset;
        _hasColors = hasColors;
        _colorOffset = colorOffset;
        _colorChannels = colorChannels;
        _hasIntensity = hasIntensity && intensityCount > 0;
        _intensityOffset = intensityOffset;
        _intensityCount = intensityCount;
        _shiftX = shiftX;
        _shiftY = shiftY;
        _shiftZ = shiftZ;
        _scale = scale;
        Transform = transform;

        _properties["TotalPoints"] = pointCount.ToString(CultureInfo.InvariantCulture);
        _properties["HasColors"] = hasColors ? "true" : "false";
        _properties["HasIntensity"] = hasIntensity ? "true" : "false";
        _properties["GlobalShift"] = string.Create(CultureInfo.InvariantCulture, $"{shiftX:F6}, {shiftY:F6}, {shiftZ:F6}");
        _properties["GlobalScale"] = scale.ToString(CultureInfo.InvariantCulture);

        if (properties != null)
        {
            foreach (var kvp in properties)
            {
                _properties[kvp.Key] = kvp.Value;
            }
        }
    }

    /// <inheritdoc />
    public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null)
    {
        options ??= PclReadOptions.Default;
        long targetCount = options.MaxPoints ?? _pointCount;
        if (targetCount <= 0 || _pointCount == 0) yield break;
        targetCount = Math.Min(targetCount, _pointCount);
        int step = Math.Max(1, options.SubsampleStep);

        using var fs = File.OpenRead(_filePath);

        byte[] ptBuf = new byte[BatchRecords * 12];
        byte[]? colBuf = _hasColors ? new byte[BatchRecords * _colorChannels] : null;
        byte[]? intBuf = _hasIntensity ? new byte[BatchRecords * 4] : null;

        long yielded = 0;
        long pointIndex = 0;
        long pointsRemaining = _pointCount;

        while (pointsRemaining > 0 && yielded < targetCount)
        {
            int toRead = (int)Math.Min(BatchRecords, pointsRemaining);
            int ptBytesToRead = toRead * 12;

            fs.Position = _pointOffset + (pointIndex * 12);
            int ptRead = fs.ReadAtLeast(ptBuf.AsSpan(0, ptBytesToRead), ptBytesToRead, throwOnEndOfStream: false);
            if (ptRead < ptBytesToRead)
                throw FormatError.Truncated($"CC BIN file ended prematurely while reading point coordinates at index {pointIndex}.");

            if (_hasColors && colBuf != null)
            {
                int colBytesToRead = toRead * _colorChannels;
                fs.Position = _colorOffset + (pointIndex * _colorChannels);
                int colRead = fs.ReadAtLeast(colBuf.AsSpan(0, colBytesToRead), colBytesToRead, throwOnEndOfStream: false);
                if (colRead < colBytesToRead)
                    throw FormatError.Truncated($"CC BIN file ended prematurely while reading colors at index {pointIndex}.");
            }

            if (_hasIntensity && intBuf != null)
            {
                long intAvailable = Math.Max(0, Math.Min(_intensityCount - pointIndex, toRead));
                if (intAvailable > 0)
                {
                    int intBytesToRead = (int)intAvailable * 4;
                    fs.Position = _intensityOffset + (pointIndex * 4);
                    int intRead = fs.ReadAtLeast(intBuf.AsSpan(0, intBytesToRead), intBytesToRead, throwOnEndOfStream: false);
                    if (intRead < intBytesToRead)
                        throw FormatError.Truncated($"CC BIN file ended prematurely while reading intensity at index {pointIndex}.");
                }
            }

            for (int p = 0; p < toRead; p++)
            {
                long currentIdx = pointIndex + p;
                if ((currentIdx % step) != 0) continue;

                int ptByteOffset = p * 12;
                float lx = BinaryPrimitives.ReadSingleLittleEndian(ptBuf.AsSpan(ptByteOffset, 4));
                float ly = BinaryPrimitives.ReadSingleLittleEndian(ptBuf.AsSpan(ptByteOffset + 4, 4));
                float lz = BinaryPrimitives.ReadSingleLittleEndian(ptBuf.AsSpan(ptByteOffset + 8, 4));

                if (!float.IsFinite(lx) || !float.IsFinite(ly) || !float.IsFinite(lz))
                    continue;

                PclColor? color = null;
                if (_hasColors && colBuf != null)
                {
                    int colByteOffset = p * _colorChannels;
                    byte r = colBuf[colByteOffset];
                    byte g = colBuf[colByteOffset + 1];
                    byte b = colBuf[colByteOffset + 2];
                    byte a = _colorChannels >= 4 ? colBuf[colByteOffset + 3] : (byte)255;
                    color = new PclColor(r, g, b, a);
                }

                float? intensity = null;
                if (_hasIntensity && intBuf != null && currentIdx < _intensityCount)
                {
                    int intByteOffset = p * 4;
                    float iVal = BinaryPrimitives.ReadSingleLittleEndian(intBuf.AsSpan(intByteOffset, 4));
                    if (float.IsFinite(iVal))
                    {
                        intensity = iVal;
                    }
                }

                PclVector3 pos = new(lx, ly, lz);
                if (options.ApplyTransform && !Transform.IsIdentity)
                {
                    pos = Transform.TransformPoint(pos);
                }

                yield return new PclPoint(pos, Color: color, Intensity: intensity, Index: (ulong)yielded);
                yielded++;
                if (yielded >= targetCount) yield break;
            }

            pointIndex += toRead;
            pointsRemaining -= toRead;
        }
    }
}
