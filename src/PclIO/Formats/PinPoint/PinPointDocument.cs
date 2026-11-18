using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using PclIO.Internal;

namespace PclIO.Formats.PinPoint;

/// <summary>Represents a PinPoint / PointCab laser scanning project (.srf, .smf, ScanProject.json).</summary>
/// <remarks>
/// Only the project manifest (scan names, ids, poses and, when present, point counts) is read from the JSON.
/// Point data is streamed from the referenced .bin files. Unknown values stay unknown: a scan whose
/// point count is not stored in the manifest and cannot be derived from its channel table reports a count of 0.
/// </remarks>
public sealed class PinPointDocument : PclDocument
{
    // Bounds for data taken from an untrusted manifest. The parsed tree needs several times the file size in memory.
    private const long MaxManifestBytes = 64L * 1024 * 1024;
    private const int MaxScans = 100_000;

    /// <inheritdoc/>
    public override PclFileKind Kind => PclFileKind.PinPoint;

    /// <summary>Project name from the manifest, or the file name when the manifest has none.</summary>
    public string ProjectName { get; }

    /// <inheritdoc/>
    public override IReadOnlyList<PclScan> Scans => _scans;
    private readonly List<PclScan> _scans = [];

    private PinPointDocument(string path, long fileSize, string projectName)
        : base(path, fileSize)
    {
        ProjectName = projectName;
        _metadata["ProjectName"] = projectName;
    }

    /// <summary>Opens a PinPoint project from a manifest file, a .srf file with its data folder, or a project folder.</summary>
    /// <param name="path">
    /// Manifest (.smf, ScanProject.json), .srf file or project directory. When a directory holds several candidates and no
    /// ScanProject.json, the first .smf file in ordinal file-name order is used, else the first .srf file.
    /// </param>
    /// <param name="options">Currently unused; accepted for symmetry with the other readers.</param>
    /// <exception cref="FileNotFoundException">No manifest was found.</exception>
    /// <exception cref="InvalidDataException">The manifest is not valid JSON, contains an invalid string, is too large, or names unsafe paths.</exception>
    public static PinPointDocument Open(string path, PclReadOptions? options = null)
    {
        string fullPath = Path.GetFullPath(path);
        string rootDir = Directory.Exists(fullPath) ? fullPath : (Path.GetDirectoryName(fullPath) ?? "");

        string manifestPath = path;
        if (Directory.Exists(path))
        {
            manifestPath = Path.Combine(path, "ScanProject.json");
            if (!File.Exists(manifestPath))
                manifestPath = FirstWithExtension(path, ".smf") ?? FirstWithExtension(path, ".srf") ?? manifestPath;
        }
        else if (path.EndsWith(".srf", StringComparison.OrdinalIgnoreCase))
        {
            string sidecarJson = Path.Combine($"{path}.data", "ScanProject.json");
            if (File.Exists(sidecarJson)) manifestPath = sidecarJson;
        }

        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("PinPoint manifest file not found.", manifestPath);

        long manifestSize = new FileInfo(manifestPath).Length;
        if (manifestSize > MaxManifestBytes)
            throw FormatError.Corrupted("PinPoint manifest is unreasonably large");

        JsonDocument jsonDoc;
        try
        {
            // Parsing from the stream avoids a second, UTF-16 copy of the whole file.
            using var manifestStream = File.OpenRead(manifestPath);
            jsonDoc = JsonDocument.Parse(manifestStream);
        }
        catch (JsonException)
        {
            throw FormatError.Corrupted("PinPoint manifest is not valid JSON");
        }

        using (jsonDoc)
        {
            var root = jsonDoc.RootElement;

            string projectName = Path.GetFileNameWithoutExtension(path);
            if (TryProp(root, "ProjectHeader", out var headerElem) && TryProp(headerElem, "projectName", out var nameObj))
            {
                string headerName = ReadText(nameObj);
                if (headerName.Length > 0) projectName = headerName;
            }

            var document = new PinPointDocument(path, manifestSize, projectName);

            if (TryProp(root, "ScanPositions", out var positions) && positions.ValueKind == JsonValueKind.Array)
            {
                // Point clouds folder; the project name comes from the file, so it must stay inside the project folder.
                string projectDataDir = SafePath.Combine(rootDir,
                    Path.Combine($"{projectName}.srf.data", "PointClouds", "Input"));
                if (!Directory.Exists(projectDataDir))
                    projectDataDir = Path.Combine(rootDir, "PointClouds", "Input");

                // The folder is listed at most once, however many scans need the prefix search.
                var allBins = new Lazy<BinIndex>(() => new BinIndex(ListBinFiles(projectDataDir)));

                foreach (var pos in positions.EnumerateArray())
                {
                    var posHeader = TryProp(pos, "ScanPosHeader", out var headerObj) ? headerObj : pos;

                    string name = "";
                    if (TryProp(posHeader, "name", out var n)) name = ReadText(n);
                    else if (TryProp(posHeader, "Name", out var n2)) name = ReadText(n2);

                    string hash = "";
                    if (TryProp(posHeader, "hash", out var h)) hash = ReadText(h);
                    else if (TryProp(posHeader, "Id", out var idElem)) hash = ReadText(idElem);

                    // Sensor pose: 3x4 or 4x4 row-major matrix, optionally wrapped in a "value" object.
                    PclTransform transform = PclTransform.Identity;
                    if (TryProp(posHeader, "sop", out var sopElem))
                    {
                        if (TryProp(sopElem, "value", out var sopInner)) sopElem = sopInner;
                        transform = ParseTransform(sopElem);
                    }

                    string? binPath = FindScanBin(projectDataDir, name, hash, allBins);

                    long pointCount = 0; // unknown unless the manifest or the channel table says otherwise
                    var table = ReadTableOrWarn(document, name, binPath);
                    if (TryCount(pos, "Count", out long c)) pointCount = c;
                    else if (TryCount(posHeader, "Count", out long c2)) pointCount = c2;
                    else if (table != null) pointCount = table.PointCount;

                    if (binPath == null)
                        document._warnings.Add($"Scan '{name}': point data file not found.");

                    document.AddScan(new PinPointScan(hash, name, pointCount, transform, binPath, table));
                }
            }
            else if (TryProp(root, "DataObjects", out var dataObjs) && dataObjs.ValueKind == JsonValueKind.Array)
            {
                string smfDataDir = SafePath.Combine(rootDir, $"{projectName}.smf.data");
                if (!Directory.Exists(smfDataDir)) smfDataDir = rootDir;

                foreach (var obj in dataObjs.EnumerateArray())
                {
                    string objHash = TryProp(obj, "HashId", out var h) ? ReadText(h) : "";
                    string? binPath = null;
                    if (objHash.Length > 0)
                    {
                        string candidate = SafePath.Combine(smfDataDir, $"{objHash}.bin");
                        if (File.Exists(candidate)) binPath = candidate;
                    }

                    if (!TryProp(obj, "Locations", out var locs) || locs.ValueKind != JsonValueKind.Array) continue;

                    foreach (var loc in locs.EnumerateArray())
                    {
                        string locHash = TryProp(loc, "HashId", out var lh) ? ReadText(lh) : "";
                        var transform = TryProp(loc, "Pose", out var poseElem) ? ParseTransform(poseElem) : PclTransform.Identity;

                        // The share of the data file that belongs to one location is not recorded, so the count stays unknown.
                        if (binPath == null)
                            document._warnings.Add($"Location '{locHash}': point data file not found.");
                        var table = ReadTableOrWarn(document, locHash, binPath);

                        document.AddScan(new PinPointScan(locHash, locHash, 0, transform, binPath, table));
                    }
                }
            }

            return document;
        }
    }

    // Several candidates are possible in one folder; the ordinal order of the file names makes the choice repeatable.
    private static string? FirstWithExtension(string directory, string extension) =>
        Directory.EnumerateFiles(directory)
            .Where(f => f.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .FirstOrDefault();

    private void AddScan(PinPointScan scan)
    {
        if (_scans.Count >= MaxScans)
            throw FormatError.Corrupted("PinPoint manifest lists too many scans");
        _scans.Add(scan);
    }

    // A damaged or unsupported data file must not hide the other scans: record a warning and leave its channels unknown.
    private static ChannelTable? ReadTableOrWarn(PinPointDocument document, string scanName, string? binPath)
    {
        if (binPath == null) return null;
        try
        {
            using var fs = File.OpenRead(binPath);
            var table = ChannelTable.TryRead(fs);
            if (table == null)
                document._warnings.Add($"Scan '{scanName}': the point data file has no recognised channel layout; its points cannot be read.");
            return table;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            document._warnings.Add($"Scan '{scanName}': {ex.Message}");
            return null;
        }
    }

    private static string[] ListBinFiles(string dataDir) =>
        [.. Directory.EnumerateFiles(dataDir, "*.bin", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];

    private static string? FindScanBin(string dataDir, string name, string hash, Lazy<BinIndex> allBins)
    {
        if (name.Length == 0 || !Directory.Exists(dataDir)) return null;

        char sep = Path.DirectorySeparatorChar;
        string candidate = SafePath.Combine(dataDir, $"{name}_{hash}{sep}{name}.bin");
        if (File.Exists(candidate)) return candidate;

        // Prefix match done in code: the name comes from the file and must never act as a search wildcard.
        return allBins.Value.FirstWithPrefix(name);
    }

    /// <summary>Data files ordered by name, so that a prefix search costs a binary search instead of a pass over all files.</summary>
    private sealed class BinIndex
    {
        private readonly (string Name, string Path, int Rank)[] _byName;
        private readonly Dictionary<string, string?> _answers = new(StringComparer.OrdinalIgnoreCase);

        /// <param name="ordinalOrder">The files in ordinal order of their paths; that order decides between several matches.</param>
        public BinIndex(string[] ordinalOrder) =>
            _byName = [.. ordinalOrder
                .Select((path, rank) => (Name: System.IO.Path.GetFileName(path), Path: path, Rank: rank))
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];

        public string? FirstWithPrefix(string prefix)
        {
            if (_answers.TryGetValue(prefix, out string? known)) return known;

            // Names that start with the prefix are contiguous from the first name that is not smaller than it.
            int lo = 0, hi = _byName.Length;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) / 2);
                if (string.Compare(_byName[mid].Name, prefix, StringComparison.OrdinalIgnoreCase) < 0) lo = mid + 1;
                else hi = mid;
            }

            (string Name, string Path, int Rank)? best = null;
            for (int i = lo; i < _byName.Length && _byName[i].Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase); i++)
            {
                if (best is null || _byName[i].Rank < best.Value.Rank) best = _byName[i];
            }

            return _answers[prefix] = best?.Path;
        }
    }

    private static bool TryProp(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value)) return true;
        value = default;
        return false;
    }

    // Reads a string that is either plain or wrapped as {"value": "..."}; anything else counts as absent.
    private static string ReadText(JsonElement element)
    {
        if (TryProp(element, "value", out var inner)) element = inner;
        if (element.ValueKind != JsonValueKind.String) return "";

        try
        {
            return element.GetString() ?? "";
        }
        catch (InvalidOperationException)
        {
            // An escape sequence that is a lone UTF-16 surrogate cannot be converted to text.
            throw FormatError.Corrupted("PinPoint manifest contains a string that is not valid text");
        }
    }

    private static bool TryCount(JsonElement element, string name, out long count)
    {
        count = 0;
        return TryProp(element, name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt64(out count) && count >= 0;
    }

    private static PclTransform ParseTransform(JsonElement values)
    {
        if (values.ValueKind != JsonValueKind.Array) return PclTransform.Identity;

        var m = new List<double>(16);
        foreach (var v in values.EnumerateArray())
        {
            if (m.Count == 16) break;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)) m.Add(d);
        }
        if (m.Count < 12) return PclTransform.Identity;

        double[] matrix;
        if (m.Count == 16) matrix = [.. m];
        else matrix = [.. m.Take(12), 0, 0, 0, 1];

        return new PclTransform
        {
            Translation = new PclVector3(m[3], m[7], m[11]),
            Matrix4x4 = matrix
        };
    }

    /// <summary>
    /// Channel layout of a point data file, in one of two forms: a table of named channels stored as they are, or a list of
    /// numbered sections with Snappy-compressed channels (see <see cref="PinPointPackedLayout"/>). For the first form an
    /// offset and length describe the channel payload, for the second the compressed stream.
    /// </summary>
    internal sealed class ChannelTable
    {
        // Each channel payload starts with a short fixed tag that is skipped.
        private const int PointsTag = 6;
        private const int OtherTag = 7;
        private const int MaxNameLength = 64;

        public long PointsOffset, PointsLength;
        public long ColorsOffset, ColorsLength;
        public long NormalsOffset, NormalsLength;
        public long IntensitiesOffset, IntensitiesLength;
        public long FilterOffset, FilterLength;

        /// <summary>True when the channels are Snappy streams, with the point count stored in <see cref="PackedPoints"/>.</summary>
        public bool Packed;

        /// <summary>Point count stored in the data section of the packed form.</summary>
        public long PackedPoints;

        /// <summary>Number of 3 x float32 positions in the points channel.</summary>
        public long PointCount => Packed ? PackedPoints : PointsLength / 12;

        /// <summary>Bytes per colour: RGB in the packed form, RGBA in the table form.</summary>
        public int ColorSize => Packed ? 3 : 4;

        public bool HasColors => Packed ? PackedPoints > 0 && ColorsLength > 0 : ColorsLength >= ColorSize;

        public bool HasIntensities => Packed ? PackedPoints > 0 && IntensitiesLength > 0 : IntensitiesLength >= 1;

        public bool HasNormals => Packed ? PackedPoints > 0 && NormalsLength > 0 : NormalsLength >= 12;

        /// <summary>True when the file holds a per-point flags channel (one byte per stored point).</summary>
        public bool HasFilterBits => Packed ? PackedPoints > 0 && FilterLength > 0 : false;

        /// <summary>Reads the table, or returns null when the file has neither layout.</summary>
        public static ChannelTable? TryRead(FileStream fs)
        {
            // Table form: a channel table whose first entry is named "Points" at offset 12.
            Span<byte> head = stackalloc byte[22];
            if (fs.Length < head.Length) return null;
            fs.Position = 0;
            fs.ReadExactly(head);
            if (Bytes.ReadInt32LE(head.Slice(12, 4)) != 6 || !head.Slice(16, 6).SequenceEqual("Points"u8))
                return PinPointPackedLayout.TryRead(fs);

            var table = new ChannelTable();
            Span<byte> entry = stackalloc byte[16];
            Span<byte> nameBuf = stackalloc byte[MaxNameLength];
            fs.Position = 12;

            while (fs.Position + 16 < fs.Length)
            {
                fs.ReadExactly(entry[..4]);
                int nameLen = Bytes.ReadInt32LE(entry[..4]);
                if (nameLen <= 0 || nameLen > MaxNameLength || fs.Position + nameLen + 12 > fs.Length) break;

                fs.ReadExactly(nameBuf[..nameLen]);
                string name = Encoding.ASCII.GetString(nameBuf[..nameLen]);
                fs.ReadExactly(entry[..12]); // 4-byte type (unused) + 8-byte payload length
                long length = BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(4, 8));
                long dataStart = fs.Position;
                if (length < 0 || length > fs.Length - dataStart)
                    throw FormatError.Truncated($"{name} channel data");

                if (name.Equals("Points", StringComparison.OrdinalIgnoreCase))
                    Assign(PointsTag, length, dataStart, out table.PointsOffset, out table.PointsLength);
                else if (name.Equals("Colors", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("RGB", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("RGB8", StringComparison.OrdinalIgnoreCase))
                    Assign(OtherTag, length, dataStart, out table.ColorsOffset, out table.ColorsLength);
                else if (name.Equals("Normals", StringComparison.OrdinalIgnoreCase))
                    Assign(OtherTag, length, dataStart, out table.NormalsOffset, out table.NormalsLength);
                else if (name.StartsWith("Intensities", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("Intensity", StringComparison.OrdinalIgnoreCase))
                    Assign(OtherTag, length, dataStart, out table.IntensitiesOffset, out table.IntensitiesLength);

                fs.Position = dataStart + length;
            }

            return table;
        }

        private static void Assign(int tag, long length, long dataStart, out long offset, out long payloadLength)
        {
            if (length < tag) throw FormatError.Corrupted("channel is shorter than its header tag");
            offset = dataStart + tag;
            payloadLength = length - tag;
        }
    }
}

/// <summary>Scan representation in a PinPoint project.</summary>
public sealed class PinPointScan : PclScan
{
    // Coordinates with a larger magnitude (metres) are dropped; see ReadPoints for what this means.
    private const float MaxCoordinate = 5000.0f;

    private readonly PinPointDocument.ChannelTable? _table;

    /// <inheritdoc/>
    public override string Id { get; }

    /// <inheritdoc/>
    public override string Name { get; }

    /// <summary>Point count from the manifest or the channel table; 0 when it is not recorded.</summary>
    public override long PointCount { get; }

    /// <summary>Path of the point data file, or null when it was not found.</summary>
    public string? BinFilePath { get; }

    /// <summary>True only when the point data file has a colour channel.</summary>
    public override bool HasColors => _table is { HasColors: true };

    /// <summary>True only when the point data file has an intensity channel.</summary>
    public override bool HasIntensities => _table is { HasIntensities: true };

    /// <summary>True only when the point data file has a normals channel.</summary>
    public override bool HasNormals => _table is { HasNormals: true };

    /// <summary>True when the point data file holds a per-point flags channel ("FilterBits", one byte per stored point).</summary>
    public bool HasFilterBits => _table is { HasFilterBits: true };

    internal PinPointScan(string id, string name, long pointCount, PclTransform transform, string? binFilePath,
        PinPointDocument.ChannelTable? table)
    {
        Id = id;
        Name = name;
        PointCount = pointCount;
        Transform = transform;
        BinFilePath = binFilePath;
        _table = table;
    }

    /// <summary>Streams points from the point data file; yields nothing when the file was not found.</summary>
    /// <remarks>
    /// <para>
    /// Points are positions as float32 triples. Points with a NaN or infinite coordinate, or with any coordinate whose
    /// magnitude exceeds 5000 in the file's own unit, are not returned and
    /// are not counted; the remaining points keep consecutive <see cref="PclPoint.Index"/> values. The magnitude limit
    /// is a plausibility rule of this reader, not a property of the file format, so positions in a projected coordinate
    /// system with large offsets are dropped. <see cref="PointCount"/> reports the manifest or channel-table count, so
    /// it can be larger than the number of returned points.
    /// </para>
    /// <para>
    /// A point data file whose layout is neither a table of named channels nor a list of sections with a property table is
    /// not decoded, because the file does not describe its layout. Of the channels of the sectioned layout the positions
    /// ("Points"), colours ("RGB8", three bytes per point), intensities ("Intensities8"), normals ("Normals", three
    /// float32 components per point) and per-point flags ("FilterBits", one byte per point) are read; a non-zero flag
    /// marks the point as filtered. The grid indices stored beside them ("RowColIndex") are not interpreted.
    /// </para>
    /// <para>
    /// By default every stored point that passes the plausibility rule above is returned. With
    /// <see cref="PclReadOptions.ExcludeFilteredPoints"/> the points that the flags mark as filtered are not returned
    /// and are not counted in the running <see cref="PclPoint.Index"/> values. The exact meaning of the individual flag
    /// bits is not confirmed by test files, so any non-zero value counts as filtered.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidDataException">The point data file is damaged.</exception>
    /// <exception cref="NotSupportedException">The point data file has no recognised channel layout, or uses a feature of the
    /// sectioned layout that is not supported.</exception>
    public override IEnumerable<PclPoint> ReadPoints(PclReadOptions? options = null)
    {
        options ??= PclReadOptions.Default;
        if (BinFilePath == null) yield break;

        long targetCount = options.MaxPoints ?? (PointCount > 0 ? PointCount : long.MaxValue);
        int step = Math.Max(1, options.SubsampleStep);

        using var fs = File.OpenRead(BinFilePath);
        var table = PinPointDocument.ChannelTable.TryRead(fs);

        if (table == null) throw FormatError.Unsupported("point data file without a recognised channel layout");

        foreach (var pt in ReadChannels(table, targetCount, step, options)) yield return pt;
    }

    // Points are decoded in batches, so that reading cost does not depend on the number of calls into the streams.
    private const int Batch = 4096;

    private IEnumerable<PclPoint> ReadChannels(PinPointDocument.ChannelTable table, long targetCount, int step, PclReadOptions options)
    {
        if (targetCount <= 0) yield break;
        bool applyTransform = options.ApplyTransform;

        // A point whose flag byte is not zero is marked as filtered; it is only skipped when the caller asks for it.
        bool skipFiltered = options.ExcludeFilteredPoints && table.HasFilterBits;

        int colorSize = table.ColorSize;
        long pointTotal = table.PointCount;
        long colorTotal = !table.HasColors ? 0 : table.Packed ? pointTotal : table.ColorsLength / colorSize;
        long normalTotal = !table.HasNormals ? 0 : table.Packed ? pointTotal : table.NormalsLength / 12;
        long intensityTotal = !table.HasIntensities ? 0 : table.Packed ? pointTotal : table.IntensitiesLength;

        using var points = OpenChannel(table, table.PointsOffset, table.PointsLength, pointTotal > 0);
        using var colors = OpenChannel(table, table.ColorsOffset, table.ColorsLength, colorTotal > 0);
        using var normals = OpenChannel(table, table.NormalsOffset, table.NormalsLength, normalTotal > 0);
        using var intensities = OpenChannel(table, table.IntensitiesOffset, table.IntensitiesLength, intensityTotal > 0);
        using var filters = OpenChannel(table, table.FilterOffset, table.FilterLength, skipFiltered);

        byte[] pointBytes = new byte[Batch * 12];
        byte[] colorBytes = new byte[Batch * colorSize];
        byte[] normalBytes = new byte[Batch * 12];
        byte[] intensityBytes = new byte[Batch];
        byte[] filterBytes = new byte[Batch];

        long yielded = 0;
        long start = 0;
        for (; start < pointTotal && yielded < targetCount; start += Batch)
        {
            int count = (int)Math.Min(Batch, pointTotal - start);
            points!.ReadExactly(pointBytes, 0, count * 12);

            // Optional channels advance in step with the points; a channel shorter than the points simply ends.
            int colorCount = colors == null ? 0 : (int)Math.Clamp(colorTotal - start, 0, count);
            if (colorCount > 0) colors!.ReadExactly(colorBytes, 0, colorCount * colorSize);
            int normalCount = normals == null ? 0 : (int)Math.Clamp(normalTotal - start, 0, count);
            if (normalCount > 0) normals!.ReadExactly(normalBytes, 0, normalCount * 12);
            int intensityCount = intensities == null ? 0 : (int)Math.Clamp(intensityTotal - start, 0, count);
            if (intensityCount > 0) intensities!.ReadExactly(intensityBytes, 0, intensityCount);
            if (filters != null) filters.ReadExactly(filterBytes, 0, count);

            for (int j = 0; j < count && yielded < targetCount; j++)
            {
                if ((start + j) % step != 0) continue;
                if (filters != null && filterBytes[j] != 0) continue;

                var record = pointBytes.AsSpan(j * 12, 12);
                float x = Bytes.ReadSingleLE(record[..4]);
                float y = Bytes.ReadSingleLE(record.Slice(4, 4));
                float z = Bytes.ReadSingleLE(record.Slice(8, 4));
                if (!IsPlausible(x, y, z)) continue;

                PclColor? color = null;
                if (j < colorCount)
                {
                    int c = j * colorSize;
                    color = new PclColor(colorBytes[c], colorBytes[c + 1], colorBytes[c + 2]);
                }

                float? intensity = j < intensityCount ? intensityBytes[j] / 255.0f : null;

                PclVector3? normal = null;
                if (j < normalCount)
                {
                    var n = normalBytes.AsSpan(j * 12, 12);
                    float nx = Bytes.ReadSingleLE(n[..4]);
                    float ny = Bytes.ReadSingleLE(n.Slice(4, 4));
                    float nz = Bytes.ReadSingleLE(n.Slice(8, 4));
                    if (float.IsFinite(nx) && float.IsFinite(ny) && float.IsFinite(nz) &&
                        (nx != 0 || ny != 0 || nz != 0))
                    {
                        var nVec = new PclVector3(nx, ny, nz);
                        normal = applyTransform ? Transform.TransformVector(nVec) : nVec;
                    }
                }

                var pt = new PclPoint(
                    new PclVector3(x, y, z),
                    Color: color,
                    Intensity: intensity,
                    Normal: normal,
                    Index: (ulong)yielded);

                if (applyTransform)
                    pt = pt with { Position = Transform.TransformPoint(pt.Position) };

                yield return pt;
                yielded++;
            }
        }

        // Once every point has been read, a compressed stream must end there: bytes after its last element are an error.
        if (table.Packed && start >= pointTotal)
        {
            foreach (var stream in new[] { points, colors, normals, intensities, filters })
                if (stream != null && stream.ReadByte() >= 0) throw FormatError.Corrupted("a compressed channel holds more data than it declares");
        }
    }

    // A stream over one channel: the bytes of the file at the offset, or their Snappy decoding for the packed form.
    private Stream? OpenChannel(PinPointDocument.ChannelTable table, long offset, long length, bool present)
    {
        if (!present) return null;

        var fs = File.OpenRead(BinFilePath!);
        try
        {
            fs.Position = offset;
            return table.Packed ? new SnappyReader(fs, length) : fs;
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    // Finite coordinates of at most MaxCoordinate in magnitude; everything else is dropped by the callers.
    private static bool IsPlausible(float x, float y, float z) =>
        float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) &&
        Math.Abs(x) <= MaxCoordinate && Math.Abs(y) <= MaxCoordinate && Math.Abs(z) <= MaxCoordinate;
}
