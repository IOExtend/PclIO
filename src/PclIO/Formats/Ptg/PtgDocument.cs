using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PclIO.Internal;

namespace PclIO.Formats.Ptg;

/// <summary>Represents a Leica Cyclone gridded point cloud file or index dataset (.ptg).</summary>
public sealed class PtgDocument : PclDocument
{
    /// <inheritdoc />
    public override PclFileKind Kind => PclFileKind.LeicaPtg;

    /// <inheritdoc />
    public override IReadOnlyList<PclScan> Scans => _scans;
    private readonly List<PclScan> _scans = [];

    private PtgDocument(string path, long fileSize)
        : base(path, fileSize)
    {
    }

    /// <summary>Opens a PTG binary scan file or ASCII index file.</summary>
    /// <param name="path">Path to the .ptg file.</param>
    /// <param name="options">Read options (currently unused during open; accepted for symmetry).</param>
    /// <exception cref="FileNotFoundException">The file or a referenced scan does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is malformed, truncated or contains invalid data.</exception>
    public static PtgDocument Open(string path, PclReadOptions? options = null)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("PTG file not found.", path);

        var doc = new PtgDocument(path, fi.Length);

        using (var fs = File.OpenRead(path))
        {
            Span<byte> head = stackalloc byte[8];
            int read = fs.ReadAtLeast(head, 8, throwOnEndOfStream: false);
            if (read >= 8 &&
                head[0] == 0x50 && head[1] == 0x54 && head[2] == 0x47 && head[3] == 0x00 &&
                BinaryPrimitives.ReadUInt32LittleEndian(head[4..8]) == 0x928FA3C7)
            {
                var scan = PtgScan.Create(path, "scan-0", Path.GetFileNameWithoutExtension(path));
                doc._scans.Add(scan);
                doc._metadata["Format"] = "Leica Cyclone PTG";
                doc._metadata["Scans"] = "1";
                doc._metadata["TotalPoints"] = scan.PointCount.ToString(CultureInfo.InvariantCulture);
                return doc;
            }
        }

        string baseDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        List<string> scanPaths = [];

        using (var reader = new StreamReader(path, Encoding.UTF8))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("PTG index", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("---") || line.StartsWith("#") || line.StartsWith("//"))
                {
                    continue;
                }

                string scanPath = SafePath.Combine(baseDir, line);
                scanPaths.Add(scanPath);
            }
        }

        if (scanPaths.Count == 0)
            throw FormatError.Corrupted("PTG index file contains no scan references.");

        for (int i = 0; i < scanPaths.Count; i++)
        {
            string sp = scanPaths[i];
            if (!File.Exists(sp))
                throw new FileNotFoundException($"Referenced PTG scan file not found: {sp}", sp);

            var scan = PtgScan.Create(sp, $"scan-{i}", Path.GetFileNameWithoutExtension(sp));
            doc._scans.Add(scan);
        }

        doc._metadata["Format"] = "Leica Cyclone PTG";
        doc._metadata["Scans"] = doc._scans.Count.ToString(CultureInfo.InvariantCulture);
        doc._metadata["TotalPoints"] = doc.TotalPointCount.ToString(CultureInfo.InvariantCulture);

        return doc;
    }
}
