namespace PclIO.Internal;

/// <summary>
/// Format detection limited to the signatures, extensions and directory layouts that the readers in this library accept.
/// Anything else is reported as <see cref="PclFileKind.Unknown"/>.
/// </summary>
internal static class KindDetector
{
    private static readonly byte[] E57Magic = "ASTM-E57"u8.ToArray();
    // Container signatures at the start of the files the readers accept.
    private static readonly byte[] LeicaMagic = "wild::geosystems"u8.ToArray();
    private static readonly byte[] RcsMagic = "ADOCT"u8.ToArray();
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];
    // Leading bytes of the EdgeWise text database.
    private static readonly byte[] EdgeWiseTextMagic = "2\r\n _Tr"u8.ToArray();
    private static readonly byte[] EdgeWiseTreeMagic = "_Tree"u8.ToArray();
    // First bytes of an ASPRS LAS file. LAZ files share the signature but need their own reader, so the .laz
    // extension below stays unknown until that reader exists.
    private static readonly byte[] LasMagic = "LASF"u8.ToArray();

    public static PclFileKind Detect(string path)
    {
        if (Directory.Exists(path))
        {
            // Layouts the FARO and PinPoint readers open.
            if (File.Exists(Path.Combine(path, "Scans", "1", "Params")))
                return PclFileKind.FaroFls;

            if (File.Exists(Path.Combine(path, "ScanProject.json")) ||
                Directory.EnumerateFiles(path, "*.smf").Any() ||
                Directory.EnumerateFiles(path, "*.srf").Any())
            {
                return PclFileKind.PinPoint;
            }

            return PclFileKind.Unknown;
        }

        if (!File.Exists(path))
            throw new FileNotFoundException($"Point cloud file not found: {path}", path);

        string ext = Path.GetExtension(path).ToLowerInvariant();

        using var stream = File.OpenRead(path);
        return Detect(stream, ext);
    }

    public static PclFileKind Detect(Stream stream, string? extension = null)
    {
        if (!string.IsNullOrEmpty(extension))
        {
            if (extension.Contains('/') || extension.Contains('\\') || (extension.Contains('.') && !extension.StartsWith('.')))
                extension = Path.GetExtension(extension);
            extension = extension.ToLowerInvariant();
        }

        if (!stream.CanSeek)
            throw new NotSupportedException("Detection requires a seekable stream.");

        long origin = stream.Position;
        Span<byte> buffer = stackalloc byte[32];
        int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        stream.Position = origin;

        // The extension rule for the cluster file below needs no content, so it also applies to a very short file.
        if (read < 4) return extension is ".c3db" or ".c3ptx" ? PclFileKind.EdgeWise : PclFileKind.Unknown;
        ReadOnlySpan<byte> head = buffer[..read];

        if (head.StartsWith(E57Magic))
            return PclFileKind.E57;

        if (head.StartsWith(LeicaMagic))
            return extension == ".lgs" ? PclFileKind.LeicaLgs : PclFileKind.LeicaLgsx;

        if (head.StartsWith(RcsMagic))
            return PclFileKind.RecapScan;

        // A generic compound file is not enough: the reader only opens .fls scans.
        if (head.StartsWith(Ole2Storage.Ole2Magic))
            return extension == ".fls" ? PclFileKind.FaroFls : PclFileKind.Unknown;

        // The project reader takes any archive that holds an XML manifest, so the extension decides.
        if (head.StartsWith(ZipMagic))
            return extension == ".rcp" ? PclFileKind.RecapProject : PclFileKind.Unknown;

        if (head[0] == (byte)'{' && extension is ".smf" or ".srf")
            return PclFileKind.PinPoint;

        // The cluster file has no signature, so the extension decides; the reader checks the sizes inside the file.
        if (extension is ".c3db" or ".c3ptx")
            return PclFileKind.EdgeWise;

        if (head.StartsWith(EdgeWiseTextMagic) || head.StartsWith(EdgeWiseTreeMagic))
            return PclFileKind.EdgeWise;

        // LAS files are recognised by content; a compressed LAZ file shares the signature and stays unknown.
        if (head.StartsWith(LasMagic))
            return extension == ".laz" ? PclFileKind.Unknown : PclFileKind.Las;

        // PCD files have no signature; a strict header structure in any extension decides.
        if (IsPcdHeader(stream, origin))
            return PclFileKind.Pcd;

        return PclFileKind.Unknown;
    }

    // True when the stream starts with a PCD header: the documented entries in order, with or without COUNT.
    // At most the first 256 KiB are inspected; the position is restored.
    private static bool IsPcdHeader(Stream stream, long origin)
    {
        const int maxProbeBytes = 256 * 1024;
        string[] order = ["VERSION", "FIELDS", "SIZE", "TYPE", "COUNT", "WIDTH", "HEIGHT", "VIEWPOINT", "POINTS", "DATA"];

        long length = stream.CanSeek ? stream.Length : long.MaxValue;
        int toRead = (int)Math.Min(maxProbeBytes, Math.Max(0, length - origin));
        if (toRead < 7) return false;

        byte[] buffer = new byte[toRead];
        stream.Position = origin;
        try
        {
            int got = 0;
            while (got < toRead)
            {
                int n = stream.Read(buffer, got, toRead - got);
                if (n == 0) break;
                got += n;
            }

            int stage = 0;
            int position = 0;
            int lines = 0;
            while (position < got)
            {
                int end = Array.IndexOf(buffer, (byte)'\n', position, got - position);
                if (end < 0) return false;
                int lineLength = end - position;
                if (lineLength > 0 && buffer[end - 1] == (byte)'\r') lineLength--;
                if (lineLength > 4096) return false;

                bool comment = lineLength > 0 && buffer[position] == (byte)'#';
                if (!comment && lineLength > 0)
                {
                    int space = -1;
                    for (int i = position; i < position + lineLength; i++)
                    {
                        if (buffer[i] is (byte)' ' or (byte)'\t')
                        {
                            space = i;
                            break;
                        }
                    }

                    if (space < 0) return false;
                    string keyword = System.Text.Encoding.ASCII.GetString(buffer, position, space - position);

                    while (stage < order.Length && order[stage] == "COUNT" && keyword != "COUNT") stage++;
                    if (stage >= order.Length || keyword != order[stage]) return false;
                    stage++;
                    if (keyword == "DATA") return true;
                }

                position = end + 1;
                if (++lines > 4096) return false;
            }

            return false;
        }
        finally
        {
            stream.Position = origin;
        }
    }
}
