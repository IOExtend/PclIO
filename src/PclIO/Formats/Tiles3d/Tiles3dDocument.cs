using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using PclIO.Internal;

namespace PclIO.Formats.Tiles3d;

/// <summary>Represents an OGC 3D Tiles point cloud dataset (.pnts, .glb, or tileset.json).</summary>
/// <remarks>
/// <para>
/// Supported:
/// <list type="bullet">
/// <item><description>Point Cloud tile files (.pnts, version 1) with float32 coordinates or quantised 16-bit positions (POSITION_QUANTIZED),
/// RGB, RGBA, RGB565, or constant RGBA colors, float32 normals or octahedral oct16p normals (NORMAL_OCT16P),
/// relative-to-center offsets (RTC_CENTER), and batch table scalar properties (Intensity, Classification).</description></item>
/// <item><description>glTF 2.0 binary point clouds (.glb) with mesh primitive mode 0 (POINTS), reading POSITION, COLOR_0, NORMAL,
/// and INTENSITY accessors with standard component types and node transforms.</description></item>
/// <item><description>3D Tiles tileset manifests (tileset.json) or directories containing them, traversing hierarchical tile trees
/// and composing 4x4 transformation matrices for referenced point cloud tiles.</description></item>
/// </list>
/// </para>
/// <para>
/// Not supported (reported with <see cref="InvalidDataException"/> or <see cref="NotSupportedException"/>):
/// invalid magic signatures, unsupported versions, declared byte lengths or point counts exceeding file size or safe limits (512 MB per tile,
/// 500 million points), truncated streams, glTF assets lacking point primitives, or circular tileset hierarchies exceeding depth 32.
/// </para>
/// </remarks>
public sealed class Tiles3dDocument : PclDocument
{
    private const int PntsHeaderSize = 28;
    private const int GlbHeaderSize = 12;
    private const int MaxTileBytes = 512 * 1024 * 1024; // 512 MB safety limit
    private const int MaxPointsLimit = 500_000_000;
    private const int MaxTilesetDepth = 32;

    private static readonly byte[] PntsMagic = "pnts"u8.ToArray();
    private static readonly byte[] GlbMagic = "glTF"u8.ToArray();

    /// <inheritdoc />
    public override PclFileKind Kind => PclFileKind.Tiles3d;

    /// <summary>Total declared point count summed across all scans.</summary>
    public long TotalPoints => TotalPointCount;

    /// <summary>Tileset specification version (e.g. "1.0", "1.1"), or null when opened from a single tile.</summary>
    public string? TilesetVersion { get; }

    /// <summary>Geometric error declared in the tileset manifest, or null when absent.</summary>
    public double? GeometricError { get; }

    /// <inheritdoc />
    public override IReadOnlyList<PclScan> Scans => _scans;
    private readonly List<PclScan> _scans = [];

    private Tiles3dDocument(
        string path,
        long fileSize,
        string formatName,
        IEnumerable<PclScan> scans,
        string? tilesetVersion = null,
        double? geometricError = null,
        IEnumerable<string>? warnings = null)
        : base(path, fileSize)
    {
        TilesetVersion = tilesetVersion;
        GeometricError = geometricError;

        _metadata["Format"] = formatName;
        if (tilesetVersion != null)
            _metadata["TilesetVersion"] = tilesetVersion;
        if (geometricError.HasValue)
            _metadata["GeometricError"] = geometricError.Value.ToString(CultureInfo.InvariantCulture);

        _scans.AddRange(scans);
        _metadata["ScanCount"] = _scans.Count.ToString(CultureInfo.InvariantCulture);
        _metadata["TotalPoints"] = TotalPointCount.ToString(CultureInfo.InvariantCulture);

        if (warnings != null)
        {
            foreach (string warning in warnings)
                _warnings.Add(warning);
        }
    }

    /// <summary>Opens a 3D Tiles point cloud document from a file (.pnts, .glb, tileset.json) or a directory containing tileset.json.</summary>
    /// <param name="path">Path of the file or directory to open.</param>
    /// <param name="options">Read options (currently unused during open; accepted for symmetry).</param>
    /// <exception cref="FileNotFoundException">The path does not exist, or a required tileset file is missing.</exception>
    /// <exception cref="InvalidDataException">The file header, JSON table, or binary stream is malformed or truncated.</exception>
    /// <exception cref="NotSupportedException">The tile version or data encoding is not supported.</exception>
    public static Tiles3dDocument Open(string path, PclReadOptions? options = null)
    {
        if (Directory.Exists(path))
        {
            string tilesetPath = Path.Combine(path, "tileset.json");
            if (!File.Exists(tilesetPath))
                throw new FileNotFoundException($"3D Tiles directory does not contain 'tileset.json': '{path}'.", tilesetPath);
            return OpenTileset(tilesetPath);
        }

        if (!File.Exists(path))
            throw new FileNotFoundException($"3D Tiles file not found: '{path}'.", path);

        string ext = Path.GetExtension(path);
        string filename = Path.GetFileName(path);

        // Check if file is a tileset JSON
        if (string.Equals(filename, "tileset.json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase))
        {
            if (IsTilesetJsonFile(path))
                return OpenTileset(path);
        }

        using var stream = File.OpenRead(path);
        if (stream.Length < 4)
            throw FormatError.Truncated("at least 4 magic bytes");

        Span<byte> magic = stackalloc byte[4];
        stream.ReadExactly(magic);
        stream.Position = 0;

        if (magic.SequenceEqual(PntsMagic))
            return OpenPntsFile(path);

        if (magic.SequenceEqual(GlbMagic))
            return OpenGlbFile(path);

        // Try tileset JSON fallback
        if (IsTilesetJsonFile(path))
            return OpenTileset(path);

        throw FormatError.Corrupted($"Unrecognized 3D Tiles signature in '{path}'.");
    }

    private static bool IsTilesetJsonFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < 10) return false;
            int toRead = (int)Math.Min(8192, stream.Length);
            byte[] buf = new byte[toRead];
            int got = stream.ReadAtLeast(buf, toRead, throwOnEndOfStream: false);
            if (got < 10) return false;

            int start = 0;
            if (got >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF)
                start = 3;
            while (start < got && char.IsWhiteSpace((char)buf[start]))
                start++;

            if (start >= got || buf[start] != (byte)'{')
                return false;

            using var doc = JsonDocument.Parse(buf.AsMemory(start, got - start));
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("asset", out _) &&
                   root.TryGetProperty("root", out _);
        }
        catch
        {
            return false;
        }
    }

    private static Tiles3dDocument OpenPntsFile(string path)
    {
        var fi = new FileInfo(path);
        PntsScan scan = PntsScan.FromFile(path, "0", Path.GetFileNameWithoutExtension(path), PclTransform.Identity);
        return new Tiles3dDocument(
            path,
            fi.Length,
            "3D Tiles Point Cloud (PNTS)",
            [scan]);
    }

    private static Tiles3dDocument OpenGlbFile(string path)
    {
        var fi = new FileInfo(path);
        List<GlbScan> scans = GlbScan.FromFile(path);
        if (scans.Count == 0)
            throw FormatError.Corrupted($"glTF file '{path}' does not contain any point primitives (mode 0).");

        return new Tiles3dDocument(
            path,
            fi.Length,
            "3D Tiles Point Cloud (GLB)",
            scans);
    }

    private static Tiles3dDocument OpenTileset(string path)
    {
        var fi = new FileInfo(path);
        byte[] jsonBytes = File.ReadAllBytes(path);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonBytes);
        }
        catch (JsonException ex)
        {
            throw FormatError.Corrupted($"Invalid JSON in tileset '{path}': {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw FormatError.Corrupted("Tileset root must be a JSON object.");

            if (!root.TryGetProperty("asset", out var assetElem) || !root.TryGetProperty("root", out var rootTileElem))
                throw FormatError.Corrupted("Tileset root must contain 'asset' and 'root' elements.");

            string? version = null;
            if (assetElem.TryGetProperty("version", out var versionElem))
                version = versionElem.GetString();

            double? geometricError = null;
            if (root.TryGetProperty("geometricError", out var geElem) && geElem.TryGetDouble(out double geVal))
                geometricError = geVal;

            string baseDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            var scans = new List<PclScan>();
            var warnings = new List<string>();

            double[] identity16 = [
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, 0,
                0, 0, 0, 1
            ];

            CollectTileScans(rootTileElem, baseDir, identity16, scans, warnings, depth: 0);

            return new Tiles3dDocument(
                path,
                fi.Length,
                "3D Tiles Tileset (JSON)",
                scans,
                tilesetVersion: version,
                geometricError: geometricError,
                warnings: warnings);
        }
    }

    private static void CollectTileScans(
        JsonElement tileElem,
        string baseDir,
        double[] parentTransformColMajor,
        List<PclScan> scans,
        List<string> warnings,
        int depth)
    {
        if (depth > MaxTilesetDepth)
        {
            warnings.Add($"Tileset hierarchy exceeded maximum depth of {MaxTilesetDepth}; branch truncated.");
            return;
        }

        double[] currentTransformColMajor = parentTransformColMajor;
        if (tileElem.TryGetProperty("transform", out var transformElem) && transformElem.ValueKind == JsonValueKind.Array)
        {
            if (transformElem.GetArrayLength() == 16)
            {
                double[] localMat = new double[16];
                int idx = 0;
                foreach (var item in transformElem.EnumerateArray())
                {
                    localMat[idx++] = item.GetDouble();
                }
                currentTransformColMajor = MultiplyColumnMajor4x4(parentTransformColMajor, localMat);
            }
        }

        // Bounding volume if present
        PclBounds tileBounds = ParseBoundingVolume(tileElem);

        // Check for content
        if (tileElem.TryGetProperty("content", out var contentElem) && contentElem.ValueKind == JsonValueKind.Object)
        {
            ProcessTileContent(contentElem, baseDir, currentTransformColMajor, tileBounds, scans, warnings, depth);
        }
        else if (tileElem.TryGetProperty("contents", out var contentsElem) && contentsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in contentsElem.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                    ProcessTileContent(item, baseDir, currentTransformColMajor, tileBounds, scans, warnings, depth);
            }
        }

        // Process children
        if (tileElem.TryGetProperty("children", out var childrenElem) && childrenElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in childrenElem.EnumerateArray())
            {
                if (child.ValueKind == JsonValueKind.Object)
                    CollectTileScans(child, baseDir, currentTransformColMajor, scans, warnings, depth + 1);
            }
        }
    }

    private static void ProcessTileContent(
        JsonElement contentElem,
        string baseDir,
        double[] transformColMajor,
        PclBounds declaredBounds,
        List<PclScan> scans,
        List<string> warnings,
        int depth)
    {
        string? uri = null;
        if (contentElem.TryGetProperty("uri", out var uriElem))
            uri = uriElem.GetString();
        else if (contentElem.TryGetProperty("url", out var urlElem))
            uri = urlElem.GetString();

        if (string.IsNullOrEmpty(uri)) return;

        // Skip non-point content (e.g. b3dm, i3dm, cmpt)
        string ext = Path.GetExtension(uri).ToLowerInvariant();
        if (ext is ".b3dm" or ".i3dm" or ".cmpt")
            return;

        string targetPath = Path.GetFullPath(Path.Combine(baseDir, uri.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(targetPath))
        {
            warnings.Add($"Referenced tile content file not found: '{uri}'");
            return;
        }

        // Convert column-major 4x4 matrix to row-major for PclTransform
        double[] rowMajor = ColumnMajorToRowMajor(transformColMajor);
        var pclTransform = new PclTransform { Matrix4x4 = rowMajor };

        if (ext == ".pnts" || IsPntsSignature(targetPath))
        {
            string scanId = scans.Count.ToString(CultureInfo.InvariantCulture);
            string scanName = uri;
            var scan = PntsScan.FromFile(targetPath, scanId, scanName, pclTransform, declaredBounds.IsEmpty ? null : declaredBounds);
            scans.Add(scan);
        }
        else if (ext == ".glb" || IsGlbSignature(targetPath))
        {
            var glbScans = GlbScan.FromFile(targetPath, pclTransform, scanIdPrefix: scans.Count.ToString(CultureInfo.InvariantCulture));
            scans.AddRange(glbScans);
        }
        else if (ext == ".json")
        {
            // External tileset
            try
            {
                byte[] nestedBytes = File.ReadAllBytes(targetPath);
                using var nestedDoc = JsonDocument.Parse(nestedBytes);
                if (nestedDoc.RootElement.TryGetProperty("root", out var nestedRoot))
                {
                    string nestedBaseDir = Path.GetDirectoryName(targetPath) ?? baseDir;
                    CollectTileScans(nestedRoot, nestedBaseDir, transformColMajor, scans, warnings, depth + 1);
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to load external tileset '{uri}': {ex.Message}");
            }
        }
    }

    private static bool IsPntsSignature(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 4) return false;
            Span<byte> magic = stackalloc byte[4];
            fs.ReadExactly(magic);
            return magic.SequenceEqual(PntsMagic);
        }
        catch { return false; }
    }

    private static bool IsGlbSignature(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 4) return false;
            Span<byte> magic = stackalloc byte[4];
            fs.ReadExactly(magic);
            return magic.SequenceEqual(GlbMagic);
        }
        catch { return false; }
    }

    private static PclBounds ParseBoundingVolume(JsonElement tileElem)
    {
        if (!tileElem.TryGetProperty("boundingVolume", out var bvElem) || bvElem.ValueKind != JsonValueKind.Object)
            return PclBounds.Empty;

        // Box: 12 numbers: center (3), half-axes (9)
        if (bvElem.TryGetProperty("box", out var boxElem) && boxElem.ValueKind == JsonValueKind.Array && boxElem.GetArrayLength() == 12)
        {
            double[] b = new double[12];
            int i = 0;
            foreach (var item in boxElem.EnumerateArray()) b[i++] = item.GetDouble();

            double cx = b[0], cy = b[1], cz = b[2];
            double rx = Math.Abs(b[3]) + Math.Abs(b[6]) + Math.Abs(b[9]);
            double ry = Math.Abs(b[4]) + Math.Abs(b[7]) + Math.Abs(b[10]);
            double rz = Math.Abs(b[5]) + Math.Abs(b[8]) + Math.Abs(b[11]);
            return new PclBounds(new PclVector3(cx - rx, cy - ry, cz - rz), new PclVector3(cx + rx, cy + ry, cz + rz));
        }

        // Sphere: 4 numbers: center (3), radius (1)
        if (bvElem.TryGetProperty("sphere", out var sphereElem) && sphereElem.ValueKind == JsonValueKind.Array && sphereElem.GetArrayLength() == 4)
        {
            double[] s = new double[4];
            int i = 0;
            foreach (var item in sphereElem.EnumerateArray()) s[i++] = item.GetDouble();
            double cx = s[0], cy = s[1], cz = s[2], radius = s[3];
            return new PclBounds(new PclVector3(cx - radius, cy - radius, cz - radius), new PclVector3(cx + radius, cy + radius, cz + radius));
        }

        return PclBounds.Empty;
    }

    internal static double[] MultiplyColumnMajor4x4(double[] a, double[] b)
    {
        double[] result = new double[16];
        for (int c = 0; c < 4; c++)
        {
            for (int r = 0; r < 4; r++)
            {
                double sum = 0;
                for (int k = 0; k < 4; k++)
                {
                    sum += a[k * 4 + r] * b[c * 4 + k];
                }
                result[c * 4 + r] = sum;
            }
        }
        return result;
    }

    internal static double[] ColumnMajorToRowMajor(double[] colMajor)
    {
        double[] rowMajor = new double[16];
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                rowMajor[r * 4 + c] = colMajor[c * 4 + r];
            }
        }
        return rowMajor;
    }

    internal static PclTransform CombineRtcWithTransform(PclTransform baseTransform, PclVector3 rtcCenter)
    {
        if (baseTransform.Matrix4x4 is { Length: 16 } m)
        {
            double[] combined = (double[])m.Clone();
            // In row-major, col 3 has indices 3, 7, 11, 15:
            // m'[r*4 + 3] = m[r*4 + 0]*Rx + m[r*4 + 1]*Ry + m[r*4 + 2]*Rz + m[r*4 + 3]
            combined[3] = m[0] * rtcCenter.X + m[1] * rtcCenter.Y + m[2] * rtcCenter.Z + m[3];
            combined[7] = m[4] * rtcCenter.X + m[5] * rtcCenter.Y + m[6] * rtcCenter.Z + m[7];
            combined[11] = m[8] * rtcCenter.X + m[9] * rtcCenter.Y + m[10] * rtcCenter.Z + m[11];
            combined[15] = m[12] * rtcCenter.X + m[13] * rtcCenter.Y + m[14] * rtcCenter.Z + m[15];
            return new PclTransform { Matrix4x4 = combined };
        }

        return new PclTransform
        {
            Translation = new PclVector3(
                baseTransform.Translation.X + rtcCenter.X,
                baseTransform.Translation.Y + rtcCenter.Y,
                baseTransform.Translation.Z + rtcCenter.Z)
        };
    }
}
