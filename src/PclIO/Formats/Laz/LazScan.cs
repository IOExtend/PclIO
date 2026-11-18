using PclIO.Internal;

namespace PclIO.Formats.Laz;

/// <summary>A scan streaming points from a compressed LAZ point cloud file.</summary>
public sealed class LazScan : PclScan
{
    private const int MaxChunkMemory = 64 * 1024 * 1024; // 64 MB max per compressed chunk

    /// <inheritdoc />
    public override string Id { get; }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override long PointCount { get; }

    /// <summary>Path of the LAZ file.</summary>
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
    private readonly LaszipChunk[] _chunks;

    internal LazScan(string id, string name, long pointCount, PclBounds bounds, string filePath,
        long pointDataOffset, int pointFormat, int recordLength, PclVector3 scale, PclVector3 offset,
        LaszipChunk[] chunks)
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
        _chunks = chunks;
        HasColors = pointFormat is 2 or 3 or 5 or 7 or 8 or 10;
    }

    /// <summary>Streams the points decompressed from the LAZ chunks.</summary>
    public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null)
    {
        options ??= PclReadOptions.Default;
        long targetCount = options.MaxPoints ?? PointCount;
        if (targetCount <= 0 || PointCount <= 0) yield break;
        targetCount = Math.Min(targetCount, PointCount);
        int step = Math.Max(1, options.SubsampleStep);

        bool hasGpsTime = _pointFormat is 1 or 3;
        bool hasColor = HasColors;
        int extraBytesCount = _recordLength - (20 + (hasGpsTime ? 8 : 0) + (hasColor ? 6 : 0));
        if (extraBytesCount < 0) extraBytesCount = 0;

        using var fs = File.OpenRead(FilePath);
        long fileLength = fs.Length;
        long yielded = 0;
        long globalPointIndex = 0;

        // If chunks are present, stream chunk by chunk.
        if (_chunks.Length > 0)
        {
            foreach (var chunk in _chunks)
            {
                if (chunk.PointCount == 0) continue;
                if (chunk.ByteSize > MaxChunkMemory)
                    throw FormatError.Corrupted($"LAZ chunk declares {chunk.ByteSize} bytes, which exceeds the memory limit.");
                if (chunk.Offset + chunk.ByteSize > fileLength)
                    throw FormatError.Truncated("LAZ chunk data");

                fs.Position = chunk.Offset;
                byte[] chunkBuffer = new byte[chunk.ByteSize];
                fs.ReadExactly(chunkBuffer);

                if (chunkBuffer.Length < _recordLength)
                    throw FormatError.Truncated("point 0 in LAZ chunk");

                // Point 0 raw fields
                int rawX0 = Bytes.ReadInt32LE(chunkBuffer.AsSpan(0, 4));
                int rawY0 = Bytes.ReadInt32LE(chunkBuffer.AsSpan(4, 4));
                int rawZ0 = Bytes.ReadInt32LE(chunkBuffer.AsSpan(8, 4));
                ushort rawInt0 = Bytes.ReadUInt16LE(chunkBuffer.AsSpan(12, 2));
                byte rawFlags0 = chunkBuffer[14];
                byte rawClass0 = chunkBuffer[15];

                ushort rawR0 = 0, rawG0 = 0, rawB0 = 0;
                if (hasColor)
                {
                    int rgbOffset = 20 + (hasGpsTime ? 8 : 0);
                    rawR0 = Bytes.ReadUInt16LE(chunkBuffer.AsSpan(rgbOffset, 2));
                    rawG0 = Bytes.ReadUInt16LE(chunkBuffer.AsSpan(rgbOffset + 2, 2));
                    rawB0 = Bytes.ReadUInt16LE(chunkBuffer.AsSpan(rgbOffset + 4, 2));
                }

                // Yield Point 0 if subsample matches
                if (globalPointIndex % step == 0)
                {
                    double x = _offset.X + (rawX0 * _scale.X);
                    double y = _offset.Y + (rawY0 * _scale.Y);
                    double z = _offset.Z + (rawZ0 * _scale.Z);
                    if (double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z))
                    {
                        float intensity = rawInt0 / 65535.0f;
                        byte classification = (byte)(rawClass0 & 0x1F);
                        PclColor? color = hasColor ? new PclColor((byte)(rawR0 / 257), (byte)(rawG0 / 257), (byte)(rawB0 / 257)) : null;

                        var pt = new PclPoint(new PclVector3(x, y, z), Color: color, Intensity: intensity,
                            Classification: classification, Index: (ulong)yielded);
                        if (options.ApplyTransform)
                            pt = pt with { Position = Transform.TransformPoint(pt.Position) };

                        yield return pt;
                        yielded++;
                        if (yielded >= targetCount) yield break;
                    }
                }
                globalPointIndex++;

                // Decompress remaining points in this chunk
                if (chunk.PointCount > 1)
                {
                    var dec = new LaszipArithmeticDecoder(chunkBuffer, _recordLength);
                    var ptDecomp = new LaszipPoint10Decompressor(dec);
                    ptDecomp.Init(chunkBuffer.AsSpan(0, 20));

                    LaszipGpsTime11Decompressor? gpsDecomp = null;
                    if (hasGpsTime)
                    {
                        gpsDecomp = new LaszipGpsTime11Decompressor(dec);
                        long rawGps = Bytes.ReadInt64LE(chunkBuffer.AsSpan(20, 8));
                        gpsDecomp.Init(rawGps);
                    }

                    LaszipRgb12Decompressor? rgbDecomp = null;
                    if (hasColor)
                    {
                        rgbDecomp = new LaszipRgb12Decompressor(dec);
                        int rgbOffset = 20 + (hasGpsTime ? 8 : 0);
                        rgbDecomp.Init(chunkBuffer.AsSpan(rgbOffset, 6));
                    }

                    LaszipByteDecompressor? byteDecomp = null;
                    byte[]? extraBuffer = null;
                    if (extraBytesCount > 0)
                    {
                        byteDecomp = new LaszipByteDecompressor(dec, extraBytesCount);
                        int extraOffset = 20 + (hasGpsTime ? 8 : 0) + (hasColor ? 6 : 0);
                        byteDecomp.Init(chunkBuffer.AsSpan(extraOffset, extraBytesCount));
                        extraBuffer = new byte[extraBytesCount];
                    }

                    for (uint p = 1; p < chunk.PointCount; p++)
                    {
                        ptDecomp.DecompressNext(out int px, out int py, out int pz, out ushort pIntensity,
                            out byte pFlags, out byte pClassification, out _, out _, out _);

                        if (gpsDecomp != null)
                            gpsDecomp.DecompressNext();

                        ushort pr = 0, pg = 0, pb = 0;
                        if (rgbDecomp != null)
                            rgbDecomp.DecompressNext(out pr, out pg, out pb);

                        if (byteDecomp != null)
                            byteDecomp.DecompressNext(extraBuffer!);

                        if (globalPointIndex % step == 0)
                        {
                            double x = _offset.X + (px * _scale.X);
                            double y = _offset.Y + (py * _scale.Y);
                            double z = _offset.Z + (pz * _scale.Z);
                            if (double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z))
                            {
                                float intensity = pIntensity / 65535.0f;
                                byte classification = (byte)(pClassification & 0x1F);
                                PclColor? color = hasColor ? new PclColor((byte)(pr / 257), (byte)(pg / 257), (byte)(pb / 257)) : null;

                                var pt = new PclPoint(new PclVector3(x, y, z), Color: color, Intensity: intensity,
                                    Classification: classification, Index: (ulong)yielded);
                                if (options.ApplyTransform)
                                    pt = pt with { Position = Transform.TransformPoint(pt.Position) };

                                yield return pt;
                                yielded++;
                                if (yielded >= targetCount) yield break;
                            }
                        }
                        globalPointIndex++;
                    }
                }
            }
        }
    }
}
