using PclIO.Formats.Laz;
using PclIO.Internal;

namespace PclIO.Formats.Ept;

/// <summary>Streams points from an Entwine Point Tile (EPT) octree dataset.</summary>
public sealed class EptScan : PclScan
{
    private const int MaxTileMemory = 64 * 1024 * 1024; // 64 MB max per tile

    /// <inheritdoc />
    public override string Id { get; }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override long PointCount { get; }

    /// <summary>Path to the EPT manifest file.</summary>
    public string FilePath { get; }

    /// <inheritdoc />
    public override bool HasColors { get; }

    /// <inheritdoc />
    public override bool HasIntensities { get; }

    /// <inheritdoc />
    public override bool HasNormals { get; }

    private readonly IReadOnlyList<EptNode> _nodes;
    private readonly IReadOnlyList<EptDimension> _schema;
    private readonly string _dataType;

    internal EptScan(
        string id,
        string name,
        long pointCount,
        PclBounds bounds,
        string filePath,
        IReadOnlyList<EptNode> nodes,
        IReadOnlyList<EptDimension> schema,
        string dataType)
    {
        Id = id;
        Name = name;
        PointCount = pointCount;
        Bounds = bounds;
        FilePath = filePath;
        _nodes = nodes;
        _schema = schema;
        _dataType = dataType.ToLowerInvariant();

        HasColors = schema.Any(d => d.Name is "Red" or "COLOR_0") || _dataType == "laszip";
        HasIntensities = schema.Any(d => d.Name is "Intensity") || _dataType == "laszip";
        HasNormals = schema.Any(d => d.Name is "NormalX" or "Normal");
    }

    /// <inheritdoc />
    public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null)
    {
        return ReadPointsFromNodes(_nodes, options);
    }

    /// <summary>Streams points from the specified octree nodes.</summary>
    internal IEnumerable<PclPoint> ReadPointsFromNodes(IEnumerable<EptNode> nodes, PclReadOptions? options = null)
    {
        options ??= PclReadOptions.Default;
        long targetCount = options.MaxPoints ?? PointCount;
        if (targetCount <= 0 || PointCount <= 0) yield break;
        targetCount = Math.Min(targetCount, PointCount);
        int step = Math.Max(1, options.SubsampleStep);

        long yielded = 0;
        long globalIndex = 0;

        // Resolve schema layout offsets for binary / zstandard streams
        int recordSize = _schema.Sum(d => d.Size);
        int xOff = -1, yOff = -1, zOff = -1;
        int intOff = -1, classOff = -1;
        int redOff = -1, greenOff = -1, blueOff = -1;
        int nxOff = -1, nyOff = -1, nzOff = -1;

        EptDimension? xDim = null, yDim = null, zDim = null;
        EptDimension? intDim = null, classDim = null;
        EptDimension? redDim = null, greenDim = null, blueDim = null;
        EptDimension? nxDim = null, nyDim = null, nzDim = null;

        int curOff = 0;
        foreach (var dim in _schema)
        {
            string name = dim.Name.ToLowerInvariant();
            if (name is "x" or "pos_x") { xOff = curOff; xDim = dim; }
            else if (name is "y" or "pos_y") { yOff = curOff; yDim = dim; }
            else if (name is "z" or "pos_z") { zOff = curOff; zDim = dim; }
            else if (name is "intensity") { intOff = curOff; intDim = dim; }
            else if (name is "classification") { classOff = curOff; classDim = dim; }
            else if (name is "red") { redOff = curOff; redDim = dim; }
            else if (name is "green") { greenOff = curOff; greenDim = dim; }
            else if (name is "blue") { blueOff = curOff; blueDim = dim; }
            else if (name is "normalx") { nxOff = curOff; nxDim = dim; }
            else if (name is "normaly") { nyOff = curOff; nyDim = dim; }
            else if (name is "normalz") { nzOff = curOff; nzDim = dim; }

            curOff += dim.Size;
        }

        foreach (var node in nodes)
        {
            if (node.DataPath == null || !File.Exists(node.DataPath)) continue;

            if (_dataType == "laszip")
            {
                using var lazDoc = LazDocument.Open(node.DataPath);
                foreach (var p in lazDoc.ReadAllPoints())
                {
                    if (globalIndex % step == 0)
                    {
                        var pt = p with { Index = (ulong)yielded };
                        if (options.ApplyTransform)
                            pt = pt with { Position = Transform.TransformPoint(pt.Position) };

                        yield return pt;
                        yielded++;
                        if (yielded >= targetCount) yield break;
                    }
                    globalIndex++;
                }
            }
            else
            {
                // Binary or Zstandard point stream
                byte[] dataBytes;
                if (_dataType == "zstandard")
                {
                    byte[] zstBytes = File.ReadAllBytes(node.DataPath);
                    if (zstBytes.Length > MaxTileMemory)
                        throw FormatError.Corrupted($"EPT zstandard tile '{node.DataPath}' exceeds memory limit.");
                    dataBytes = ZstdDecoder.Decompress(zstBytes, MaxTileMemory);
                }
                else
                {
                    var fi = new FileInfo(node.DataPath);
                    if (fi.Length > MaxTileMemory)
                        throw FormatError.Corrupted($"EPT binary tile '{node.DataPath}' exceeds memory limit.");
                    dataBytes = File.ReadAllBytes(node.DataPath);
                }

                if (recordSize <= 0) continue;
                if (dataBytes.Length % recordSize != 0)
                    throw FormatError.Truncated($"EPT tile '{node.DataPath}' size {dataBytes.Length} is not a multiple of point record size {recordSize}.");

                int tilePoints = dataBytes.Length / recordSize;
                for (int i = 0; i < tilePoints; i++)
                {
                    int recStart = i * recordSize;
                    var rec = dataBytes.AsSpan(recStart, recordSize);

                    if (xDim == null || yDim == null || zDim == null) continue;

                    double x = ReadDimValue(rec.Slice(xOff, xDim.Size), xDim);
                    double y = ReadDimValue(rec.Slice(yOff, yDim.Size), yDim);
                    double z = ReadDimValue(rec.Slice(zOff, zDim.Size), zDim);

                    if (double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z))
                    {
                        if (globalIndex % step == 0)
                        {
                            float? intensity = null;
                            if (intDim != null)
                            {
                                double rawInt = ReadDimValue(rec.Slice(intOff, intDim.Size), intDim);
                                intensity = (float)(rawInt / (intDim.Size == 1 ? 255.0 : 65535.0));
                            }

                            byte? classification = null;
                            if (classDim != null)
                            {
                                classification = (byte)ReadDimValue(rec.Slice(classOff, classDim.Size), classDim);
                            }

                            PclColor? color = null;
                            if (redDim != null && greenDim != null && blueDim != null)
                            {
                                double rVal = ReadDimValue(rec.Slice(redOff, redDim.Size), redDim);
                                double gVal = ReadDimValue(rec.Slice(greenOff, greenDim.Size), greenDim);
                                double bVal = ReadDimValue(rec.Slice(blueOff, blueDim.Size), blueDim);
                                byte r = (byte)(redDim.Size == 2 ? Math.Clamp(rVal / 257.0, 0, 255) : Math.Clamp(rVal, 0, 255));
                                byte g = (byte)(greenDim.Size == 2 ? Math.Clamp(gVal / 257.0, 0, 255) : Math.Clamp(gVal, 0, 255));
                                byte b = (byte)(blueDim.Size == 2 ? Math.Clamp(bVal / 257.0, 0, 255) : Math.Clamp(bVal, 0, 255));
                                color = new PclColor(r, g, b);
                            }

                            PclVector3? normal = null;
                            if (nxDim != null && nyDim != null && nzDim != null)
                            {
                                double nx = ReadDimValue(rec.Slice(nxOff, nxDim.Size), nxDim);
                                double ny = ReadDimValue(rec.Slice(nyOff, nyDim.Size), nyDim);
                                double nz = ReadDimValue(rec.Slice(nzOff, nzDim.Size), nzDim);
                                normal = new PclVector3(nx, ny, nz);
                            }

                            var pt = new PclPoint(new PclVector3(x, y, z), Color: color, Intensity: intensity,
                                Normal: normal, Classification: classification, Index: (ulong)yielded);
                            if (options.ApplyTransform)
                                pt = pt with { Position = Transform.TransformPoint(pt.Position) };

                            yield return pt;
                            yielded++;
                            if (yielded >= targetCount) yield break;
                        }
                    }
                    globalIndex++;
                }
            }
        }
    }

    private static double ReadDimValue(ReadOnlySpan<byte> span, EptDimension dim)
    {
        double raw = dim.Type switch
        {
            "signed" => dim.Size switch
            {
                1 => (sbyte)span[0],
                2 => Bytes.ReadInt16LE(span),
                4 => Bytes.ReadInt32LE(span),
                8 => Bytes.ReadInt64LE(span),
                _ => 0
            },
            "unsigned" => dim.Size switch
            {
                1 => span[0],
                2 => Bytes.ReadUInt16LE(span),
                4 => Bytes.ReadUInt32LE(span),
                8 => Bytes.ReadUInt64LE(span),
                _ => 0
            },
            "float" => dim.Size switch
            {
                4 => Bytes.ReadSingleLE(span),
                8 => Bytes.ReadDoubleLE(span),
                _ => 0
            },
            _ => 0
        };

        return (raw * dim.Scale) + dim.Offset;
    }
}
