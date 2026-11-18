using PclIO.Formats.Laz;
using PclIO.Internal;

namespace PclIO.Formats.Copc;

/// <summary>Streams points from a Cloud Optimized Point Cloud (COPC) file.</summary>
public sealed class CopcScan : PclScan
{
    private const int MaxChunkMemory = 64 * 1024 * 1024; // 64 MB max per chunk

    /// <inheritdoc />
    public override string Id { get; }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override long PointCount { get; }

    /// <summary>Path of the COPC file.</summary>
    public string FilePath { get; }

    /// <inheritdoc />
    public override bool HasColors { get; }

    /// <inheritdoc />
    public override bool HasIntensities => true;

    /// <inheritdoc />
    public override bool HasNormals => false;

    private readonly int _pointFormat;
    private readonly int _recordLength;
    private readonly PclVector3 _scale;
    private readonly PclVector3 _offset;
    private readonly IReadOnlyList<CopcNode> _nodes;

    internal CopcScan(
        string id,
        string name,
        long pointCount,
        PclBounds bounds,
        string filePath,
        int pointFormat,
        int recordLength,
        PclVector3 scale,
        PclVector3 offset,
        IReadOnlyList<CopcNode> nodes)
    {
        Id = id;
        Name = name;
        PointCount = pointCount;
        Bounds = bounds;
        FilePath = filePath;
        _pointFormat = pointFormat;
        _recordLength = recordLength;
        _scale = scale;
        _offset = offset;
        _nodes = nodes;
        HasColors = pointFormat is 2 or 3 or 5 or 7 or 8 or 10;
    }

    /// <summary>Streams points across the octree nodes of this scan.</summary>
    public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null)
    {
        return ReadPointsFromNodes(_nodes, options);
    }

    /// <summary>Streams points from the specified octree nodes.</summary>
    internal IEnumerable<PclPoint> ReadPointsFromNodes(IEnumerable<CopcNode> nodes, PclReadOptions? options = null)
    {
        options ??= PclReadOptions.Default;
        long targetCount = options.MaxPoints ?? PointCount;
        if (targetCount <= 0 || PointCount <= 0) yield break;
        targetCount = Math.Min(targetCount, PointCount);
        int step = Math.Max(1, options.SubsampleStep);

        using var fs = File.OpenRead(FilePath);
        long fileLength = fs.Length;
        long yielded = 0;
        long globalPointIndex = 0;

        bool hasColor = HasColors;
        bool isClassicFormat = _pointFormat <= 3;
        bool hasGpsTime = _pointFormat is 1 or 3;
        int extraBytesCount = isClassicFormat ? _recordLength - (20 + (hasGpsTime ? 8 : 0) + (hasColor ? 6 : 0)) : 0;
        if (extraBytesCount < 0) extraBytesCount = 0;

        foreach (var node in nodes)
        {
            if (node.PointCount <= 0) continue;
            if (node.ByteSize > MaxChunkMemory)
                throw FormatError.Corrupted($"COPC chunk byte size {node.ByteSize} exceeds memory limit.");
            if (node.Offset + (ulong)node.ByteSize > (ulong)fileLength)
                throw FormatError.Truncated("COPC point chunk data");

            fs.Position = (long)node.Offset;
            byte[] chunkBuffer = new byte[node.ByteSize];
            fs.ReadExactly(chunkBuffer);

            if (chunkBuffer.Length < _recordLength)
                throw FormatError.Truncated("point 0 in COPC chunk");

            // Point 0 coordinates and attributes
            int rawX0 = Bytes.ReadInt32LE(chunkBuffer.AsSpan(0, 4));
            int rawY0 = Bytes.ReadInt32LE(chunkBuffer.AsSpan(4, 4));
            int rawZ0 = Bytes.ReadInt32LE(chunkBuffer.AsSpan(8, 4));
            ushort rawInt0 = Bytes.ReadUInt16LE(chunkBuffer.AsSpan(12, 2));

            byte rawClass0;
            ushort rawR0 = 0, rawG0 = 0, rawB0 = 0;

            if (isClassicFormat)
            {
                rawClass0 = (byte)(chunkBuffer[15] & 0x1F);
                if (hasColor)
                {
                    int rgbOffset = 20 + (hasGpsTime ? 8 : 0);
                    if (chunkBuffer.Length >= rgbOffset + 6)
                    {
                        rawR0 = Bytes.ReadUInt16LE(chunkBuffer.AsSpan(rgbOffset, 2));
                        rawG0 = Bytes.ReadUInt16LE(chunkBuffer.AsSpan(rgbOffset + 2, 2));
                        rawB0 = Bytes.ReadUInt16LE(chunkBuffer.AsSpan(rgbOffset + 4, 2));
                    }
                }
            }
            else
            {
                // LAS 1.4 point format (6 to 8)
                rawClass0 = chunkBuffer.Length > 16 ? chunkBuffer[16] : (byte)0;
                if (hasColor && chunkBuffer.Length >= 36)
                {
                    rawR0 = Bytes.ReadUInt16LE(chunkBuffer.AsSpan(30, 2));
                    rawG0 = Bytes.ReadUInt16LE(chunkBuffer.AsSpan(32, 2));
                    rawB0 = Bytes.ReadUInt16LE(chunkBuffer.AsSpan(34, 2));
                }
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
                    PclColor? color = hasColor ? new PclColor((byte)(rawR0 / 257), (byte)(rawG0 / 257), (byte)(rawB0 / 257)) : null;

                    var pt = new PclPoint(new PclVector3(x, y, z), Color: color, Intensity: intensity,
                        Classification: rawClass0, Index: (ulong)yielded);
                    if (options.ApplyTransform)
                        pt = pt with { Position = Transform.TransformPoint(pt.Position) };

                    yield return pt;
                    yielded++;
                    if (yielded >= targetCount) yield break;
                }
            }
            globalPointIndex++;

            // Decompress remaining points if classic LASzip compression (formats 0..3)
            if (isClassicFormat && node.PointCount > 1)
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

                for (uint p = 1; p < node.PointCount; p++)
                {
                    ptDecomp.DecompressNext(out int px, out int py, out int pz, out ushort pIntensity,
                        out _, out byte pClassification, out _, out _, out _);

                    if (gpsDecomp != null) gpsDecomp.DecompressNext();

                    ushort pr = 0, pg = 0, pb = 0;
                    if (rgbDecomp != null) rgbDecomp.DecompressNext(out pr, out pg, out pb);

                    if (byteDecomp != null) byteDecomp.DecompressNext(extraBuffer!);

                    if (globalPointIndex % step == 0)
                    {
                        double pxCoord = _offset.X + (px * _scale.X);
                        double pyCoord = _offset.Y + (py * _scale.Y);
                        double pzCoord = _offset.Z + (pz * _scale.Z);
                        if (double.IsFinite(pxCoord) && double.IsFinite(pyCoord) && double.IsFinite(pzCoord))
                        {
                            float intensity = pIntensity / 65535.0f;
                            byte cls = (byte)(pClassification & 0x1F);
                            PclColor? color = hasColor ? new PclColor((byte)(pr / 257), (byte)(pg / 257), (byte)(pb / 257)) : null;

                            var pt = new PclPoint(new PclVector3(pxCoord, pyCoord, pzCoord), Color: color, Intensity: intensity,
                                Classification: cls, Index: (ulong)yielded);
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
