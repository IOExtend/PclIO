using System.Buffers.Binary;
using System.Globalization;
using PclIO.Internal;

namespace PclIO.Formats.Sbf;

/// <summary>Represents a Simple Binary Format point cloud file (.sbf, .sbf.data).</summary>
/// <remarks>
/// <para>
/// Supported: Binary point cloud records with 32-bit floating point XYZ coordinates and optional
/// 32-bit floating point scalar fields, paired with an INI-style companion .sbf text header file specifying
/// point count, global shift and scale, and scalar field names (such as Intensity, Color, and Normals).
/// </para>
/// <para>
/// Not read (reported with <see cref="InvalidDataException"/> or <see cref="NotSupportedException"/>):
/// malformed magic signatures, negative or invalid scale factors, scalar field counts exceeding 1024,
/// point counts or record sizes exceeding physical file capacity, truncated data streams, or companion
/// files with mismatched point counts.
/// </para>
/// </remarks>
public sealed class SbfDocument : PclDocument
{
    private const int HeaderBytes = 64;
    private const int MaxScalarFields = 1024;
    private const long MaxCompanionFileBytes = 10 * 1024 * 1024;

    /// <inheritdoc />
    public override PclFileKind Kind => PclFileKind.Sbf;

    /// <summary>Total number of points declared in the file.</summary>
    public long TotalPoints => _header.PointCount;

    /// <summary>Number of scalar fields per point record.</summary>
    public int ScalarFieldCount => _header.ScalarFieldCount;

    /// <summary>Names of the scalar fields defined in the companion header or generated defaults.</summary>
    public IReadOnlyList<string> ScalarFields => _header.ScalarFieldNames;

    /// <summary>Global shift vector applied to restore world coordinates.</summary>
    public PclVector3 GlobalShift => _header.GlobalShift;

    /// <summary>Global scale factor applied to coordinates (default 1.0).</summary>
    public double GlobalScale => _header.GlobalScale;

    /// <summary>True when point coordinates and scalar fields are stored in big-endian byte order.</summary>
    public bool IsBigEndian => _header.IsBigEndian;

    /// <summary>Path of the companion text header file, or null if opened from a standalone binary data file.</summary>
    public string? CompanionPath => _header.CompanionPath;

    /// <summary>Path of the binary data file containing point records.</summary>
    public string DataPath => _header.DataPath;

    /// <inheritdoc />
    public override IReadOnlyList<PclScan> Scans => _scans;
    private readonly List<PclScan> _scans = [];

    private readonly SbfHeader _header;

    private SbfDocument(string path, long fileSize, SbfHeader header)
        : base(path, fileSize)
    {
        _header = header;
        _metadata["Format"] = "Simple Binary (SBF)";
        _metadata["Endianness"] = header.IsBigEndian ? "BigEndian" : "LittleEndian";
        _metadata["TotalPoints"] = header.PointCount.ToString(CultureInfo.InvariantCulture);
        _metadata["ScalarFieldCount"] = header.ScalarFieldCount.ToString(CultureInfo.InvariantCulture);
        if (header.ScalarFieldNames.Count > 0)
            _metadata["ScalarFields"] = string.Join(", ", header.ScalarFieldNames);
        _metadata["GlobalShift"] = string.Create(CultureInfo.InvariantCulture, $"{header.GlobalShift.X:F6}, {header.GlobalShift.Y:F6}, {header.GlobalShift.Z:F6}");
        _metadata["GlobalScale"] = header.GlobalScale.ToString(CultureInfo.InvariantCulture);
        if (header.CompanionPath != null)
            _metadata["CompanionFile"] = Path.GetFileName(header.CompanionPath);

        string scanName = ResolveScanName(header.CompanionPath ?? header.DataPath);
        _scans.Add(new SbfScan("0", scanName, header.PointCount, header, this));
    }

    private static string ResolveScanName(string path)
    {
        string filename = Path.GetFileName(path);
        if (filename.EndsWith(".sbf.data", StringComparison.OrdinalIgnoreCase))
            return filename[..^9];
        return Path.GetFileNameWithoutExtension(filename);
    }

    /// <summary>Opens an SBF point cloud file (.sbf or .sbf.data) and inspects its structure and companion file.</summary>
    /// <param name="path">Path of either the companion header file (.sbf) or binary data file (.sbf.data / .data).</param>
    /// <param name="options">Read options (currently unused during open; accepted for symmetry).</param>
    /// <exception cref="FileNotFoundException">The specified file or its companion data file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is malformed, truncated, or has mismatched declared counts.</exception>
    /// <exception cref="NotSupportedException">The file layout or scalar field count is not supported.</exception>
    public static SbfDocument Open(string path, PclReadOptions? options = null)
    {
        if (Directory.Exists(path))
            throw new NotSupportedException($"SBF does not support opening directories: '{path}'.");

        if (!File.Exists(path))
            throw new FileNotFoundException("SBF file not found.", path);

        ResolveFilePaths(path, out string dataPath, out string? companionPath);

        var fiData = new FileInfo(dataPath);
        if (!fiData.Exists)
            throw new FileNotFoundException($"Companion SBF data file not found: '{dataPath}'", dataPath);

        var header = ReadHeaders(dataPath, companionPath, fiData.Length);
        long reportedSize = new FileInfo(path).Length;
        return new SbfDocument(path, reportedSize, header);
    }

    private static void ResolveFilePaths(string path, out string dataPath, out string? companionPath)
    {
        string ext = Path.GetExtension(path);

        if (ext.Equals(".sbf", StringComparison.OrdinalIgnoreCase))
        {
            companionPath = path;
            string cand1 = path + ".data";
            string cand2 = Path.ChangeExtension(path, ".data");

            if (File.Exists(cand1)) dataPath = cand1;
            else if (File.Exists(cand2)) dataPath = cand2;
            else throw new FileNotFoundException($"Companion SBF data file not found for '{path}'. Looked for '{cand1}' and '{cand2}'.", cand1);
        }
        else if (path.EndsWith(".sbf.data", StringComparison.OrdinalIgnoreCase))
        {
            dataPath = path;
            string cand = path[..^5];
            companionPath = File.Exists(cand) ? cand : null;
        }
        else if (ext.Equals(".data", StringComparison.OrdinalIgnoreCase))
        {
            dataPath = path;
            string cand = Path.ChangeExtension(path, ".sbf");
            companionPath = File.Exists(cand) ? cand : null;
        }
        else
        {
            // Inspect first bytes to see if it's text [SBF] or binary 0x2A, 0x2A
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[5];
            int read = fs.ReadAtLeast(head, 5, throwOnEndOfStream: false);

            if (read >= 5 && head.StartsWith("[SBF]"u8))
            {
                companionPath = path;
                string cand1 = path + ".data";
                string cand2 = path + ".sbf.data";
                if (File.Exists(cand1)) dataPath = cand1;
                else if (File.Exists(cand2)) dataPath = cand2;
                else throw new FileNotFoundException($"Companion SBF data file not found for '{path}'.", cand1);
            }
            else
            {
                dataPath = path;
                string cand1 = Path.ChangeExtension(path, ".sbf");
                string cand2 = path + ".sbf";
                if (File.Exists(cand1)) companionPath = cand1;
                else if (File.Exists(cand2)) companionPath = cand2;
                else companionPath = null;
            }
        }
    }

    private static SbfHeader ReadHeaders(string dataPath, string? companionPath, long dataLength)
    {
        // 1. Read companion text header if present
        long? companionPoints = null;
        int? companionSfCount = null;
        var companionSfNames = new List<string>();
        PclVector3 globalShift = PclVector3.Zero;
        double globalScale = 1.0;

        if (companionPath != null)
        {
            var fiComp = new FileInfo(companionPath);
            if (fiComp.Length > MaxCompanionFileBytes)
                throw FormatError.Corrupted($"Companion SBF header file '{companionPath}' exceeds maximum allowed size.");

            using var reader = new StreamReader(companionPath, System.Text.Encoding.UTF8);
            string? line;
            bool sbfSectionFound = false;

            while ((line = reader.ReadLine()) != null)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
                    continue;

                if (trimmed.Equals("[SBF]", StringComparison.OrdinalIgnoreCase))
                {
                    sbfSectionFound = true;
                    continue;
                }

                if (!sbfSectionFound)
                    continue;

                // Stop if another section begins
                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                    break;

                int eqIdx = trimmed.IndexOf('=');
                if (eqIdx <= 0) continue;

                string key = trimmed[..eqIdx].Trim();
                string val = trimmed[(eqIdx + 1)..].Trim();

                if (key.Equals("Points", StringComparison.OrdinalIgnoreCase))
                {
                    if (!long.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out long pts) || pts < 0)
                        throw FormatError.Corrupted($"Invalid Points value '{val}' in companion file '{companionPath}'.");
                    companionPoints = pts;
                }
                else if (key.Equals("GlobalShift", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length != 3)
                    {
                        parts = val.Split([' ', '\t', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    }
                    if (parts.Length != 3 ||
                        !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double sx) ||
                        !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double sy) ||
                        !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double sz))
                    {
                        throw FormatError.Corrupted($"Invalid GlobalShift value '{val}' in companion file '{companionPath}'.");
                    }
                    globalShift = new PclVector3(sx, sy, sz);
                }
                else if (key.Equals("GlobalScale", StringComparison.OrdinalIgnoreCase))
                {
                    if (!double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double sc) ||
                        !double.IsFinite(sc) || sc <= 0)
                    {
                        throw FormatError.Corrupted($"Invalid GlobalScale value '{val}' in companion file '{companionPath}'.");
                    }
                    globalScale = sc;
                }
                else if (key.Equals("SFCount", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count < 0 || count > MaxScalarFields)
                        throw FormatError.Corrupted($"Invalid SFCount value '{val}' in companion file '{companionPath}'.");
                    companionSfCount = count;
                }
                else if (key.StartsWith("SF", StringComparison.OrdinalIgnoreCase) &&
                         int.TryParse(key.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out int sfIndex) && sfIndex > 0)
                {
                    while (companionSfNames.Count < sfIndex)
                        companionSfNames.Add(string.Empty);
                    companionSfNames[sfIndex - 1] = val;
                }
            }

            if (!sbfSectionFound)
                throw FormatError.Corrupted($"Companion SBF header file '{companionPath}' missing [SBF] section header.");
        }

        // 2. Read binary header (64 bytes)
        if (dataLength < HeaderBytes)
            throw FormatError.Truncated($"SBF data file '{dataPath}' is smaller than the required 64-byte header.");

        byte[] headerBytes = new byte[HeaderBytes];
        using (var fs = File.OpenRead(dataPath))
        {
            int got = fs.ReadAtLeast(headerBytes, HeaderBytes, throwOnEndOfStream: false);
            if (got < HeaderBytes)
                throw FormatError.Truncated($"Failed to read 64-byte header from SBF data file '{dataPath}'.");
        }

        if (headerBytes[0] != 0x2A || headerBytes[1] != 0x2A)
            throw FormatError.Corrupted($"SBF data file '{dataPath}' has invalid magic bytes; expected 0x2A, 0x2A ('**').");

        // Determine Endianness: compare expected sizes
        ulong countBE = BinaryPrimitives.ReadUInt64BigEndian(headerBytes.AsSpan(2, 8));
        ushort sfBE = BinaryPrimitives.ReadUInt16BigEndian(headerBytes.AsSpan(10, 2));

        ulong countLE = BinaryPrimitives.ReadUInt64LittleEndian(headerBytes.AsSpan(2, 8));
        ushort sfLE = BinaryPrimitives.ReadUInt16LittleEndian(headerBytes.AsSpan(10, 2));

        bool isBigEndian = true;
        ulong pointCount = countBE;
        ushort sfCount = sfBE;

        long recSizeBE = 12L + 4L * sfBE;
        bool beMatchesSize = sfBE <= MaxScalarFields &&
            countBE <= (ulong)((long.MaxValue - HeaderBytes) / recSizeBE) &&
            (HeaderBytes + (long)countBE * recSizeBE == dataLength);

        long recSizeLE = 12L + 4L * sfLE;
        bool leMatchesSize = sfLE <= MaxScalarFields &&
            countLE <= (ulong)((long.MaxValue - HeaderBytes) / recSizeLE) &&
            (HeaderBytes + (long)countLE * recSizeLE == dataLength);

        if (!beMatchesSize && leMatchesSize)
        {
            isBigEndian = false;
            pointCount = countLE;
            sfCount = sfLE;
        }
        else
        {
            isBigEndian = true;
            pointCount = countBE;
            sfCount = sfBE;
        }

        if (sfCount > MaxScalarFields)
            throw FormatError.Unsupported($"SBF scalar field count {sfCount} exceeds maximum supported ({MaxScalarFields}).");

        long recordSize = 12L + 4L * sfCount;
        long maxPossiblePoints = (dataLength - HeaderBytes) / recordSize;

        if ((long)pointCount > maxPossiblePoints)
            throw FormatError.Truncated($"SBF data file '{dataPath}' truncated: declares {pointCount} points, but file only has capacity for {maxPossiblePoints} points.");

        long expectedLength = HeaderBytes + (long)pointCount * recordSize;
        if (dataLength < expectedLength)
            throw FormatError.Truncated($"SBF data file '{dataPath}' is truncated; length is {dataLength} bytes, expected {expectedLength} bytes.");
        if (dataLength > expectedLength)
            throw FormatError.Corrupted($"SBF data file '{dataPath}' length {dataLength} bytes exceeds declared length {expectedLength} bytes.");

        // Check companion consistency if present
        if (companionPoints.HasValue && companionPoints.Value != (long)pointCount)
            throw FormatError.Corrupted($"SBF companion file declares {companionPoints.Value} points, but binary data declares {pointCount} points.");

        if (companionSfCount.HasValue && companionSfCount.Value != sfCount)
            throw FormatError.Corrupted($"SBF companion file declares {companionSfCount.Value} scalar fields, but binary data declares {sfCount} fields.");

        // Build scalar field names list
        var scalarNames = new List<string>(sfCount);
        for (int i = 0; i < sfCount; i++)
        {
            string name = (i < companionSfNames.Count && !string.IsNullOrWhiteSpace(companionSfNames[i]))
                ? companionSfNames[i].Trim()
                : $"SF{i + 1}";
            scalarNames.Add(name);
        }

        // Map channels from scalar field names
        int intensityIdx = -1;
        int redIdx = -1, greenIdx = -1, blueIdx = -1, alphaIdx = -1;
        int nxIdx = -1, nyIdx = -1, nzIdx = -1;

        for (int i = 0; i < scalarNames.Count; i++)
        {
            string lower = scalarNames[i].ToLowerInvariant();
            switch (lower)
            {
                case "i" or "intensity" or "scalar_intensity" or "reflectance" or "scalar" or "intensities":
                    if (intensityIdx < 0) intensityIdx = i;
                    break;
                case "r" or "red" or "diffuse_r" or "color_r":
                    if (redIdx < 0) redIdx = i;
                    break;
                case "g" or "green" or "diffuse_g" or "color_g":
                    if (greenIdx < 0) greenIdx = i;
                    break;
                case "b" or "blue" or "diffuse_b" or "color_b":
                    if (blueIdx < 0) blueIdx = i;
                    break;
                case "a" or "alpha" or "diffuse_a" or "color_a":
                    if (alphaIdx < 0) alphaIdx = i;
                    break;
                case "nx" or "norm_x" or "normx" or "normal_x" or "normalx":
                    if (nxIdx < 0) nxIdx = i;
                    break;
                case "ny" or "norm_y" or "normy" or "normal_y" or "normaly":
                    if (nyIdx < 0) nyIdx = i;
                    break;
                case "nz" or "norm_z" or "normz" or "normal_z" or "normalz":
                    if (nzIdx < 0) nzIdx = i;
                    break;
            }
        }

        // If only 1 scalar field exists and was not mapped to color or normals, default to intensity
        if (intensityIdx < 0 && sfCount == 1 && redIdx < 0 && nxIdx < 0)
        {
            intensityIdx = 0;
        }

        bool hasColors = redIdx >= 0 && greenIdx >= 0 && blueIdx >= 0;
        bool hasIntensities = intensityIdx >= 0;
        bool hasNormals = nxIdx >= 0 && nyIdx >= 0 && nzIdx >= 0;

        // Construct transform: P_global = (P_local / scale) - GlobalShift
        // PclTransform applies: P_transformed = P_local * Scale + Translation
        var transform = new PclTransform
        {
            Translation = new PclVector3(-globalShift.X, -globalShift.Y, -globalShift.Z),
            Scale = new PclVector3(1.0 / globalScale, 1.0 / globalScale, 1.0 / globalScale),
        };

        return new SbfHeader(
            DataPath: dataPath,
            CompanionPath: companionPath,
            PointCount: (long)pointCount,
            ScalarFieldCount: sfCount,
            ScalarFieldNames: scalarNames,
            GlobalShift: globalShift,
            GlobalScale: globalScale,
            IsBigEndian: isBigEndian,
            RecordSize: (int)recordSize,
            IntensityIndex: intensityIdx,
            RedIndex: redIdx,
            GreenIndex: greenIdx,
            BlueIndex: blueIdx,
            AlphaIndex: alphaIdx,
            NxIndex: nxIdx,
            NyIndex: nyIdx,
            NzIndex: nzIdx,
            HasColors: hasColors,
            HasIntensities: hasIntensities,
            HasNormals: hasNormals,
            Transform: transform);
    }

    internal sealed record SbfHeader(
        string DataPath,
        string? CompanionPath,
        long PointCount,
        int ScalarFieldCount,
        IReadOnlyList<string> ScalarFieldNames,
        PclVector3 GlobalShift,
        double GlobalScale,
        bool IsBigEndian,
        int RecordSize,
        int IntensityIndex,
        int RedIndex,
        int GreenIndex,
        int BlueIndex,
        int AlphaIndex,
        int NxIndex,
        int NyIndex,
        int NzIndex,
        bool HasColors,
        bool HasIntensities,
        bool HasNormals,
        PclTransform Transform);
}

/// <summary>Represents a point scan inside a Simple Binary Format point cloud file.</summary>
public sealed class SbfScan : PclScan
{
    private const int HeaderBytes = 64;
    private const int BatchRecords = 1024;

    private readonly SbfDocument.SbfHeader _header;
    private readonly SbfDocument _document;

    /// <inheritdoc />
    public override string Id { get; }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override long PointCount => _header.PointCount;

    /// <inheritdoc />
    public override bool HasColors => _header.HasColors;

    /// <inheritdoc />
    public override bool HasIntensities => _header.HasIntensities;

    /// <inheritdoc />
    public override bool HasNormals => _header.HasNormals;

    /// <summary>Names of all scalar fields present in this scan.</summary>
    public IReadOnlyList<string> ScalarFields => _header.ScalarFieldNames;

    /// <summary>Global shift vector stored in the companion file.</summary>
    public PclVector3 GlobalShift => _header.GlobalShift;

    /// <summary>Global scale factor stored in the companion file.</summary>
    public double GlobalScale => _header.GlobalScale;

    internal SbfScan(string id, string name, long pointCount, SbfDocument.SbfHeader header, SbfDocument document)
    {
        Id = id;
        Name = name;
        _header = header;
        _document = document;
        Transform = header.Transform;

        _properties["TotalPoints"] = pointCount.ToString(CultureInfo.InvariantCulture);
        _properties["ScalarFieldCount"] = header.ScalarFieldCount.ToString(CultureInfo.InvariantCulture);
        if (header.ScalarFieldNames.Count > 0)
            _properties["ScalarFields"] = string.Join(", ", header.ScalarFieldNames);
        _properties["GlobalShift"] = string.Create(CultureInfo.InvariantCulture, $"{header.GlobalShift.X:F6}, {header.GlobalShift.Y:F6}, {header.GlobalShift.Z:F6}");
        _properties["GlobalScale"] = header.GlobalScale.ToString(CultureInfo.InvariantCulture);
        _properties["Endianness"] = header.IsBigEndian ? "BigEndian" : "LittleEndian";

        for (int i = 0; i < header.ScalarFieldNames.Count; i++)
        {
            _properties[$"SF{i + 1}"] = header.ScalarFieldNames[i];
        }
    }

    /// <inheritdoc />
    public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null)
    {
        options ??= PclReadOptions.Default;
        long targetCount = options.MaxPoints ?? PointCount;
        if (targetCount <= 0 || PointCount == 0) yield break;
        targetCount = Math.Min(targetCount, PointCount);
        int step = Math.Max(1, options.SubsampleStep);

        using var fs = File.OpenRead(_header.DataPath);
        fs.Position = HeaderBytes;

        int recSize = _header.RecordSize;
        int sfCount = _header.ScalarFieldCount;

        byte[] rawBuffer = new byte[BatchRecords * recSize];
        float[] sfBuffer = new float[sfCount];

        long pointIndex = 0;
        long yielded = 0;
        long pointsRemaining = PointCount;

        while (pointsRemaining > 0)
        {
            int toReadPoints = (int)Math.Min(BatchRecords, pointsRemaining);
            int bytesToRead = toReadPoints * recSize;

            int got = fs.ReadAtLeast(rawBuffer.AsSpan(0, bytesToRead), bytesToRead, throwOnEndOfStream: false);
            if (got < bytesToRead)
                throw FormatError.Truncated($"SBF data file '{_header.DataPath}' ended prematurely while reading point records.");

            for (int p = 0; p < toReadPoints; p++)
            {
                pointIndex++;
                if ((pointIndex - 1) % step != 0) continue;

                int offset = p * recSize;
                if (!TryParsePointRecord(rawBuffer.AsSpan(offset, recSize), _header, sfBuffer, out PclPoint pt))
                    continue;

                pt = pt with { Index = (ulong)yielded };
                if (options.ApplyTransform && !Transform.IsIdentity)
                {
                    pt = pt with { Position = Transform.TransformPoint(pt.Position) };
                    if (pt.Normal is { } n)
                        pt = pt with { Normal = Transform.TransformVector(n) };
                }

                yield return pt;
                yielded++;
                if (yielded >= targetCount) yield break;
            }

            pointsRemaining -= toReadPoints;
        }
    }

    private static bool TryParsePointRecord(ReadOnlySpan<byte> record, SbfDocument.SbfHeader header, float[] sfBuffer, out PclPoint point)
    {
        point = default;
        bool isBE = header.IsBigEndian;

        float x = isBE
            ? BinaryPrimitives.ReadSingleBigEndian(record[..4])
            : BinaryPrimitives.ReadSingleLittleEndian(record[..4]);

        float y = isBE
            ? BinaryPrimitives.ReadSingleBigEndian(record.Slice(4, 4))
            : BinaryPrimitives.ReadSingleLittleEndian(record.Slice(4, 4));

        float z = isBE
            ? BinaryPrimitives.ReadSingleBigEndian(record.Slice(8, 4))
            : BinaryPrimitives.ReadSingleLittleEndian(record.Slice(8, 4));

        // Skip non-finite coordinates
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            return false;

        // Read scalar fields
        int sfCount = header.ScalarFieldCount;
        for (int s = 0; s < sfCount; s++)
        {
            int sfOffset = 12 + s * 4;
            sfBuffer[s] = isBE
                ? BinaryPrimitives.ReadSingleBigEndian(record.Slice(sfOffset, 4))
                : BinaryPrimitives.ReadSingleLittleEndian(record.Slice(sfOffset, 4));
        }

        float? intensity = null;
        if (header.HasIntensities)
        {
            intensity = sfBuffer[header.IntensityIndex];
        }

        PclColor? color = null;
        if (header.HasColors)
        {
            byte r = ClampColor(sfBuffer[header.RedIndex]);
            byte g = ClampColor(sfBuffer[header.GreenIndex]);
            byte b = ClampColor(sfBuffer[header.BlueIndex]);
            byte a = header.AlphaIndex >= 0 ? ClampColor(sfBuffer[header.AlphaIndex]) : (byte)255;
            color = new PclColor(r, g, b, a);
        }

        PclVector3? normal = null;
        if (header.HasNormals)
        {
            float nx = sfBuffer[header.NxIndex];
            float ny = sfBuffer[header.NyIndex];
            float nz = sfBuffer[header.NzIndex];
            if (float.IsFinite(nx) && float.IsFinite(ny) && float.IsFinite(nz))
                normal = new PclVector3(nx, ny, nz);
        }

        point = new PclPoint(
            Position: new PclVector3(x, y, z),
            Color: color,
            Intensity: intensity,
            Normal: normal);

        return true;
    }

    private static byte ClampColor(float value)
    {
        if (float.IsNaN(value) || value <= 0f) return 0;
        if (value >= 255f) return 255;
        return (byte)Math.Round(value);
    }
}
