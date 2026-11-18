using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

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
    // 3D Tiles point cloud (.pnts) and glTF binary (.glb) signatures.
    private static readonly byte[] PntsMagic = "pnts"u8.ToArray();
    private static readonly byte[] GlbMagic = "glTF"u8.ToArray();
    // Leica PTG binary scan signature and magic number.
    private static readonly byte[] PtgTag = [0x50, 0x54, 0x47, 0x00];
    private const uint PtgMagicNumber = 0x928FA3C7;
    // CC BIN entity file signature (CCB2).
    private static readonly byte[] CcBinMagic = "CCB2"u8.ToArray();

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

            if (File.Exists(Path.Combine(path, "tileset.json")))
            {
                return PclFileKind.Tiles3d;
            }

            if (File.Exists(Path.Combine(path, "cloud.js")) ||
                File.Exists(Path.Combine(path, "metadata.json")))
            {
                return PclFileKind.Potree;
            }

            string eptPath = Path.Combine(path, "ept.json");
            if (File.Exists(eptPath))
            {
                using var s = File.OpenRead(eptPath);
                if (IsEptJsonHeader(s, 0, "ept.json"))
                    return PclFileKind.Ept;
            }

            return PclFileKind.Unknown;
        }

        if (!File.Exists(path))
            throw new FileNotFoundException($"Point cloud file not found: {path}", path);

        string filename = Path.GetFileName(path).ToLowerInvariant();
        if (filename == "ept.json")
        {
            using var s = File.OpenRead(path);
            return IsEptJsonHeader(s, 0, "ept.json") ? PclFileKind.Ept : PclFileKind.Unknown;
        }

        string ext = path.EndsWith(".copc.laz", StringComparison.OrdinalIgnoreCase)
            ? ".copc.laz"
            : Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".bin")
        {
            using (var probeStream = File.OpenRead(path))
            {
                Span<byte> probeHead = stackalloc byte[4];
                int probeRead = probeStream.ReadAtLeast(probeHead, 4, throwOnEndOfStream: false);
                if (probeRead >= 4 && probeHead.StartsWith(CcBinMagic))
                    return PclFileKind.CcBin;
            }

            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            for (int depth = 0; depth < 5 && dir != null; depth++)
            {
                if (File.Exists(Path.Combine(dir, "cloud.js")) || File.Exists(Path.Combine(dir, "metadata.json")))
                    return PclFileKind.Potree;
                string? parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
        }

        using var stream = File.OpenRead(path);
        return Detect(stream, ext);
    }

    public static PclFileKind Detect(Stream stream, string? extension = null)
    {
        if (!string.IsNullOrEmpty(extension))
        {
            if (extension.EndsWith(".copc.laz", StringComparison.OrdinalIgnoreCase))
                extension = ".copc.laz";
            else
            {
                if (extension.Contains('/') || extension.Contains('\\') || (extension.Contains('.') && !extension.StartsWith('.')))
                    extension = Path.GetExtension(extension);
                extension = extension.ToLowerInvariant();
            }
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

        if (head.StartsWith(CcBinMagic))
            return PclFileKind.CcBin;

        if (head.Length >= 8 && head.StartsWith(PtgTag) && BinaryPrimitives.ReadUInt32LittleEndian(head[4..8]) == PtgMagicNumber)
            return PclFileKind.LeicaPtg;

        if (extension == ".ptg")
            return IsPtgIndexHeader(stream, origin) ? PclFileKind.LeicaPtg : PclFileKind.Unknown;

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

        // LAS, LAZ and COPC files share the LASF signature; COPC files declare a copc VLR; .laz with valid LAZ headers are Laz, others Las.
        if (head.StartsWith(LasMagic))
        {
            if (IsCopcHeader(stream, origin))
                return PclFileKind.Copc;

            if (extension == ".copc.laz")
                return PclFileKind.Unknown;

            if (extension == ".laz")
                return IsLazHeader(stream, origin) ? PclFileKind.Laz : PclFileKind.Unknown;
            return PclFileKind.Las;
        }

        // PCD files have no signature; a strict header structure in any extension decides.
        if (IsPcdHeader(stream, origin))
            return PclFileKind.Pcd;

        // PLY files start with the magic word; the header structure decides, like PCD.
        if (IsPlyHeader(stream, origin))
            return PclFileKind.Ply;

        // PTX files start with grid dimensions, scanner pose and transformation matrix.
        if (IsPtxHeader(stream, origin, extension))
            return PclFileKind.Ptx;

        // PTS files start with an integer point count, or coordinate lines in a .pts file.
        if (IsPtsHeader(stream, origin, extension))
            return PclFileKind.Pts;

        // SBF files (.sbf text header or .sbf.data binary stream).
        if (IsSbfHeader(stream, origin, extension))
            return PclFileKind.Sbf;

        // XYZ / delimited text files (.xyz, .asc, .txt, .csv) with numeric coordinates or an XYZ header.
        if (IsXyzHeader(stream, origin, extension))
            return PclFileKind.Xyz;

        // 3D Tiles point cloud tiles (.pnts).
        if (head.StartsWith(PntsMagic))
            return IsPntsHeader(stream, origin) ? PclFileKind.Tiles3d : PclFileKind.Unknown;

        // glTF binary point clouds (.glb).
        if (head.StartsWith(GlbMagic))
            return IsGlbPointHeader(stream, origin) ? PclFileKind.Tiles3d : PclFileKind.Unknown;

        // 3D Tiles tileset manifest (tileset.json).
        if (IsTilesetJsonHeader(stream, origin, extension))
            return PclFileKind.Tiles3d;

        // Potree octree point clouds (cloud.js or metadata.json).
        if (IsPotreeJsonHeader(stream, origin, extension))
            return PclFileKind.Potree;

        // Potree hierarchy file (.hrc).
        if (IsPotreeHrcHeader(stream, origin, extension))
            return PclFileKind.Potree;

        // Entwine Point Tile dataset manifest (ept.json).
        if (IsEptJsonHeader(stream, origin, extension))
            return PclFileKind.Ept;

        return PclFileKind.Unknown;
    }

    // True when a LAS 1.4 stream contains a COPC info VLR (user "copc", record 1, length 160).
    private static bool IsCopcHeader(Stream stream, long origin)
    {
        try
        {
            if (stream.Length - origin < 375) return false;
            stream.Position = origin;
            Span<byte> header = stackalloc byte[375];
            stream.ReadExactly(header);

            if (!header[..4].SequenceEqual(LasMagic)) return false;
            int major = header[24];
            int minor = header[25];
            if (major != 1 || minor < 4) return false;

            ushort headerSize = Bytes.ReadUInt16LE(header.Slice(94, 2));
            uint pointDataOffset = Bytes.ReadUInt32LE(header.Slice(96, 4));
            uint vlrCount = Bytes.ReadUInt32LE(header.Slice(100, 4));

            if (headerSize < 375 || headerSize > pointDataOffset || pointDataOffset > stream.Length) return false;
            if (vlrCount is 0 or > 4096) return false;

            stream.Position = origin + headerSize;
            long pos = origin + headerSize;
            byte[] vlrHeader = new byte[54];
            for (uint i = 0; i < vlrCount && i < 64; i++)
            {
                if (pos + 54 > origin + pointDataOffset) break;
                stream.ReadExactly(vlrHeader);
                pos += 54;
                string userId = System.Text.Encoding.ASCII.GetString(vlrHeader.AsSpan(2, 16)).TrimEnd('\0');
                ushort recordId = Bytes.ReadUInt16LE(vlrHeader.AsSpan(18, 2));
                ushort dataLen = Bytes.ReadUInt16LE(vlrHeader.AsSpan(20, 2));
                if (userId.Equals("copc", StringComparison.Ordinal) && recordId == 1 && dataLen == 160)
                    return true;
                pos += dataLen;
                if (pos > origin + pointDataOffset) break;
                stream.Position = pos;
            }

            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            stream.Position = origin;
        }
    }

    // True when a LAS stream contains compressed point records (LAZ): point format flag or LASzip VLR (22204).
    private static bool IsLazHeader(Stream stream, long origin)
    {
        try
        {
            if (stream.Length - origin < 105) return false;
            stream.Position = origin;
            Span<byte> header = stackalloc byte[105];
            stream.ReadExactly(header);

            // Point format record at offset 104; bit 7 is set for LASzip compressed formats.
            byte pointFormat = header[104];
            if ((pointFormat & 0x80) != 0) return true;

            // Check VLRs if pointFormat high bit was not set.
            ushort headerSize = Bytes.ReadUInt16LE(header.Slice(94, 2));
            uint pointDataOffset = Bytes.ReadUInt32LE(header.Slice(96, 4));
            uint vlrCount = Bytes.ReadUInt32LE(header.Slice(100, 4));
            if (headerSize < 227 || headerSize > pointDataOffset || pointDataOffset > stream.Length) return false;
            if (vlrCount is 0 or > 4096) return false;

            stream.Position = origin + headerSize;
            long pos = origin + headerSize;
            byte[] vlrHeader = new byte[54];
            for (uint i = 0; i < vlrCount && i < 64; i++)
            {
                if (pos + 54 > origin + pointDataOffset) break;
                stream.ReadExactly(vlrHeader);
                pos += 54;
                string userId = System.Text.Encoding.ASCII.GetString(vlrHeader.AsSpan(2, 16)).TrimEnd('\0');
                ushort recordId = Bytes.ReadUInt16LE(vlrHeader.AsSpan(18, 2));
                ushort dataLen = Bytes.ReadUInt16LE(vlrHeader.AsSpan(20, 2));
                if (userId.Equals("laszip encoded", StringComparison.Ordinal) && (recordId == 22204 || recordId == 22205))
                    return true;
                pos += dataLen;
                if (pos > origin + pointDataOffset) break;
                stream.Position = pos;
            }

            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            stream.Position = origin;
        }
    }

    // True when the stream starts with a PCD header: the documented entries in order, with or without COUNT.
    // At most the first 256 KiB are inspected; the position is restored.
    private static bool IsPcdHeader(Stream stream, long origin)
    {
        const int maxProbeBytes = 256 * 1024;
        const int maxLineLength = 1 << 20;
        string[] ordered = ["FIELDS", "SIZE", "TYPE", "COUNT", "POINTS"];

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

            int lastOrdered = -1;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int position = 0;
            int lines = 0;
            while (position < got)
            {
                int end = Array.IndexOf(buffer, (byte)'\n', position, got - position);
                if (end < 0) return false;
                int lineLength = end - position;
                if (lineLength > 0 && buffer[end - 1] == (byte)'\r') lineLength--;
                if (lineLength > maxLineLength) return false;

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

                    if (!seen.Add(keyword)) return false;
                    int orderIndex = Array.IndexOf(ordered, keyword);
                    if (orderIndex >= 0)
                    {
                        if (orderIndex <= lastOrdered) return false;
                        lastOrdered = orderIndex;
                    }
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

    // True when the stream starts with a PLY header: the magic word, a format line and end_header in order.
    // At most the first 256 KiB are inspected; the position is restored.
    private static bool IsPlyHeader(Stream stream, long origin)
    {
        const int maxProbeBytes = 256 * 1024;

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

            int position = 0;
            int lines = 0;
            bool first = true;
            bool format = false;
            while (position < got)
            {
                int end = Array.IndexOf(buffer, (byte)'\n', position, got - position);
                if (end < 0) return false;
                int lineLength = end - position;
                if (lineLength > 0 && buffer[end - 1] == (byte)'\r') lineLength--;
                if (lineLength > 4096) return false;

                string line = System.Text.Encoding.ASCII.GetString(buffer, position, lineLength);
                if (first)
                {
                    if (!line.Equals("ply", StringComparison.Ordinal)) return false;
                    first = false;
                }
                else if (line.Length != 0 && !line.StartsWith("comment", StringComparison.Ordinal) &&
                    !line.StartsWith("obj_info", StringComparison.Ordinal))
                {
                    if (!format)
                    {
                        if (!IsPlyFormatLine(line)) return false;
                        format = true;
                    }
                    else if (line.Equals("end_header", StringComparison.Ordinal))
                    {
                        return true;
                    }
                    else if (!IsPlyHeaderLine(line))
                    {
                        return false;
                    }
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

    private static bool IsPlyFormatLine(string line)
    {
        string[] parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return parts is ["format", "ascii" or "binary_little_endian" or "binary_big_endian", _];
    }

    private static bool IsPlyHeaderLine(string line) =>
        line.StartsWith("element ", StringComparison.Ordinal) ||
        line.StartsWith("property ", StringComparison.Ordinal);

    // True when the stream starts with a PTS layout: an integer count line followed by coordinate lines,
    // or coordinate lines when the extension is .pts.
    // At most the first 64 KiB are inspected; the position is restored.
    private static bool IsPtsHeader(Stream stream, long origin, string? extension)
    {
        const int maxProbeBytes = 64 * 1024;
        long length = stream.CanSeek ? stream.Length : long.MaxValue;
        int toRead = (int)Math.Min(maxProbeBytes, Math.Max(0, length - origin));
        if (toRead < 5) return false;

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

            if (got < 5) return false;

            // Reject files containing NUL bytes in the probe region (binary files)
            ReadOnlySpan<byte> headSpan = buffer.AsSpan(0, got);
            if (headSpan.Contains((byte)0)) return false;

            int pos = 0;
            string? firstLine = null;
            string? secondLine = null;
            string? thirdLine = null;

            while (pos < got)
            {
                int end = Array.IndexOf(buffer, (byte)'\n', pos, got - pos);
                if (end < 0)
                {
                    if (firstLine == null) return false;
                    break;
                }

                int lineLen = end - pos;
                if (lineLen > 0 && buffer[end - 1] == (byte)'\r') lineLen--;

                if (lineLen > 4096) return false;

                if (lineLen > 0)
                {
                    string line = System.Text.Encoding.ASCII.GetString(buffer, pos, lineLen).Trim();
                    if (line.Length > 0)
                    {
                        if (firstLine == null) firstLine = line;
                        else if (secondLine == null) secondLine = line;
                        else { thirdLine = line; break; }
                    }
                }

                pos = end + 1;
            }

            if (firstLine == null) return false;

            // Check if firstLine is a single integer count
            if (long.TryParse(firstLine, NumberStyles.None, CultureInfo.InvariantCulture, out long count) && count >= 0)
            {
                // Count header found. If count is 0 and no more lines, it is a valid empty PTS
                if (secondLine == null) return count == 0;

                // secondLine must have numeric tokens (3, 4, 6, 7, 8, 9, 10)
                if (!IsNumericPtsLine(secondLine, out int cols)) return false;
                if (thirdLine != null && (!IsNumericPtsLine(thirdLine, out int cols3) || cols3 != cols)) return false;
                return true;
            }

            // No count header: can only be detected as PTS if extension is .pts
            if (extension == ".pts")
            {
                if (!IsNumericPtsLine(firstLine, out int cols1)) return false;
                if (secondLine != null && (!IsNumericPtsLine(secondLine, out int cols2) || cols2 != cols1)) return false;
                return true;
            }

            return false;
        }
        finally
        {
            stream.Position = origin;
        }
    }

    private static bool IsNumericPtsLine(string line, out int tokenCount)
    {
        tokenCount = 0;
        string[] parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        tokenCount = parts.Length;
        if (tokenCount is not (3 or 4 or 6 or 7 or 8 or 9 or 10)) return false;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                return false;
        }
        return true;
    }

    // True when the stream starts with a PTX layout: grid dimensions (cols, rows), scanner origin and axes,
    // and a 4x4 transformation matrix (10 lines total), optionally followed by point lines.
    // At most the first 64 KiB are inspected; the position is restored.
    private static bool IsPtxHeader(Stream stream, long origin, string? extension)
    {
        const int maxProbeBytes = 64 * 1024;
        long length = stream.CanSeek ? stream.Length : long.MaxValue;
        int toRead = (int)Math.Min(maxProbeBytes, Math.Max(0, length - origin));
        if (toRead < 20) return false;

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

            if (got < 20) return false;

            // Reject files containing NUL bytes in the probe region (binary files)
            ReadOnlySpan<byte> headSpan = buffer.AsSpan(0, got);
            if (headSpan.Contains((byte)0)) return false;

            var lines = new List<string>(11);
            int pos = 0;
            while (pos < got && lines.Count < 11)
            {
                int end = Array.IndexOf(buffer, (byte)'\n', pos, got - pos);
                if (end < 0) break;

                int lineLen = end - pos;
                if (lineLen > 0 && buffer[end - 1] == (byte)'\r') lineLen--;
                if (lineLen > 4096) return false;

                if (lineLen > 0)
                {
                    string line = System.Text.Encoding.ASCII.GetString(buffer, pos, lineLen).Trim();
                    if (line.Length > 0)
                    {
                        lines.Add(line);
                    }
                }

                pos = end + 1;
            }

            if (lines.Count < 10) return false;

            // Lines 0 and 1: Columns and Rows (integers >= 0)
            if (!int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out int cols) || cols < 0)
                return false;
            if (!int.TryParse(lines[1], NumberStyles.None, CultureInfo.InvariantCulture, out int rows) || rows < 0)
                return false;

            // Lines 2..5: 3 floats each (scanner position and orientation axes)
            for (int i = 2; i <= 5; i++)
            {
                if (!IsFloatTokens(lines[i], 3)) return false;
            }

            // Lines 6..9: 4 floats each (transformation matrix)
            for (int i = 6; i <= 9; i++)
            {
                if (!IsFloatTokens(lines[i], 4)) return false;
            }

            // Line 10 (if present): first point line (must have 3, 4, 6 or 7 numeric tokens)
            if (lines.Count > 10)
            {
                if (!IsFloatTokens(lines[10], out int ptCols) || ptCols is not (3 or 4 or 6 or 7))
                    return false;
            }

            return true;
        }
        finally
        {
            stream.Position = origin;
        }
    }

    private static bool IsFloatTokens(string line, int expectedCount)
    {
        string[] parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != expectedCount) return false;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                return false;
        }
        return true;
    }

    private static bool IsFloatTokens(string line, out int count)
    {
        string[] parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        count = parts.Length;
        if (count == 0) return false;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                return false;
        }
        return true;
    }

    // True when the stream starts with an XYZ / delimited text layout:
    // Files with extension .xyz, .asc, .txt, .csv holding numeric coordinate lines or a header containing XYZ columns,
    // or an explicit XYZ column header in any extension.
    // At most the first 64 KiB are inspected; the position is restored.
    private static bool IsXyzHeader(Stream stream, long origin, string? extension)
    {
        const int maxProbeBytes = 64 * 1024;
        long length = stream.CanSeek ? stream.Length : long.MaxValue;
        int toRead = (int)Math.Min(maxProbeBytes, Math.Max(0, length - origin));
        if (toRead < 5) return false;

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

            if (got < 5) return false;

            // Reject binary files (files containing NUL bytes in the probe region)
            ReadOnlySpan<byte> headSpan = buffer.AsSpan(0, got);
            if (headSpan.Contains((byte)0)) return false;

            var lines = new List<string>(5);
            int pos = 0;
            if (headSpan.StartsWith([(byte)0xEF, (byte)0xBB, (byte)0xBF]))
                pos = 3;

            while (pos < got && lines.Count < 5)
            {
                int end = Array.IndexOf(buffer, (byte)'\n', pos, got - pos);
                if (end < 0) break;

                int lineLen = end - pos;
                if (lineLen > 0 && buffer[end - 1] == (byte)'\r') lineLen--;
                if (lineLen > 4096) return false;

                if (lineLen > 0)
                {
                    string line = System.Text.Encoding.UTF8.GetString(buffer, pos, lineLen).Trim();
                    if (line.Length > 0)
                    {
                        if (line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
                        {
                            string stripped = line.TrimStart('#', '/', ' ');
                            if (stripped.Length > 0 && lines.Count == 0)
                            {
                                char delim = DetectXyzDelimiter(stripped);
                                string[] candTokens = SplitXyzLine(stripped, delim);
                                if (IsXyzHeaderTokens(candTokens, out _, out _, out _))
                                {
                                    lines.Add(stripped);
                                }
                            }
                        }
                        else
                        {
                            lines.Add(line);
                        }
                    }
                }

                pos = end + 1;
            }

            if (lines.Count == 0) return false;

            bool isKnownExt = extension is ".xyz" or ".asc" or ".txt" or ".csv";

            // Check if line 0 is a header line containing X, Y, Z columns
            char delim0 = DetectXyzDelimiter(lines[0]);
            string[] tokens0 = SplitXyzLine(lines[0], delim0);

            if (IsXyzHeaderTokens(tokens0, out int xIdx, out int yIdx, out int zIdx))
            {
                // Explicit header found! If there is a next line, verify coordinate tokens parse as numbers
                if (lines.Count > 1)
                {
                    char delim1 = delim0;
                    string[] tokens1 = SplitXyzLine(lines[1], delim1);
                    if (tokens1.Length < tokens0.Length) return false;
                    if (!double.TryParse(tokens1[xIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
                        !double.TryParse(tokens1[yIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
                        !double.TryParse(tokens1[zIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    {
                        return false;
                    }
                }
                return true;
            }

            // No header line: requires a recognised extension (.xyz, .asc, .txt, .csv)
            if (!isKnownExt) return false;

            // Line 0 must be purely numeric coordinates with at least 3 values
            if (tokens0.Length < 3) return false;
            foreach (var tok in tokens0)
            {
                if (!double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    return false;
            }

            // If there are subsequent lines in the probe buffer, verify they also parse as numbers with matching column count
            for (int i = 1; i < lines.Count; i++)
            {
                string[] tokensI = SplitXyzLine(lines[i], delim0);
                if (tokensI.Length != tokens0.Length) return false;
                foreach (var tok in tokensI)
                {
                    if (!double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                        return false;
                }
            }

            return true;
        }
        finally
        {
            stream.Position = origin;
        }
    }

    private static char DetectXyzDelimiter(string line)
    {
        int commas = 0, semicolons = 0, tabs = 0;
        foreach (char c in line)
        {
            if (c == ',') commas++;
            else if (c == ';') semicolons++;
            else if (c == '\t') tabs++;
        }
        if (commas >= 2 && commas >= semicolons && commas >= tabs) return ',';
        if (semicolons >= 2 && semicolons >= commas && semicolons >= tabs) return ';';
        if (tabs >= 2 && tabs >= commas && tabs >= semicolons) return '\t';
        if (commas == 1 && semicolons == 0 && tabs == 0) return ',';
        if (semicolons == 1 && commas == 0 && tabs == 0) return ';';
        if (tabs == 1 && commas == 0 && semicolons == 0) return '\t';
        return ' ';
    }

    private static string[] SplitXyzLine(string line, char delimiter)
    {
        if (delimiter == ' ')
        {
            string[] parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
                parts[i] = parts[i].Trim().Trim('\"', '\'');
            return parts;
        }

        string[] tokens = line.Split(delimiter);
        for (int i = 0; i < tokens.Length; i++)
            tokens[i] = tokens[i].Trim().Trim('\"', '\'');
        return tokens;
    }

    private static bool IsXyzHeaderTokens(string[] tokens, out int xIdx, out int yIdx, out int zIdx)
    {
        xIdx = -1;
        yIdx = -1;
        zIdx = -1;
        if (tokens.Length < 3) return false;

        for (int i = 0; i < tokens.Length; i++)
        {
            string t = tokens[i].Trim('\"', '\'').ToLowerInvariant();
            if (t is "x" or "coord_x" or "coordx" or "pos_x" or "posx" or "easting" or "x_coord") xIdx = i;
            else if (t is "y" or "coord_y" or "coordy" or "pos_y" or "posy" or "northing" or "y_coord") yIdx = i;
            else if (t is "z" or "coord_z" or "coordz" or "pos_z" or "posz" or "elevation" or "height" or "altitude" or "depth" or "z_coord") zIdx = i;
        }

        return xIdx >= 0 && yIdx >= 0 && zIdx >= 0;
    }

    // True when the stream starts with an SBF layout:
    // A companion text header starting with "[SBF]", or a binary stream starting with magic 0x2A, 0x2A ('**')
    // followed by a 64-byte header matching the point record size formula.
    private static bool IsSbfHeader(Stream stream, long origin, string? extension)
    {
        long length = stream.CanSeek ? stream.Length : long.MaxValue;
        if (length - origin < 5) return false;

        Span<byte> head = stackalloc byte[64];
        stream.Position = origin;
        try
        {
            int got = stream.ReadAtLeast(head, Math.Min(64, (int)Math.Min(64, length - origin)), throwOnEndOfStream: false);
            if (got < 5) return false;

            // Check for companion / text header "[SBF]"
            if (head[..got].StartsWith("[SBF]"u8))
                return true;

            // Check for binary data file starting with magic bytes 0x2A, 0x2A ('**')
            if (got >= 12 && head[0] == 0x2A && head[1] == 0x2A)
            {
                // Try Big-Endian
                ulong countBE = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(head[2..10]);
                ushort sfBE = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(head[10..12]);

                if (sfBE <= 1024)
                {
                    long recSizeBE = 12L + 4L * sfBE;
                    if (countBE <= (ulong)((long.MaxValue - 64) / recSizeBE))
                    {
                        long expectedSizeBE = 64L + (long)countBE * recSizeBE;
                        if (stream.CanSeek && length - origin == expectedSizeBE)
                            return true;
                    }
                }

                // Try Little-Endian
                ulong countLE = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(head[2..10]);
                ushort sfLE = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(head[10..12]);

                if (sfLE <= 1024)
                {
                    long recSizeLE = 12L + 4L * sfLE;
                    if (countLE <= (ulong)((long.MaxValue - 64) / recSizeLE))
                    {
                        long expectedSizeLE = 64L + (long)countLE * recSizeLE;
                        if (stream.CanSeek && length - origin == expectedSizeLE)
                            return true;
                    }
                }

                // If extension is .data or .sbf and counts are plausible
                if (extension is ".data" or ".sbf")
                {
                    if (sfBE <= 1024 && (countBE == 0 || (length - origin >= 64)))
                        return true;
                }
            }

            return false;
        }
        finally
        {
            stream.Position = origin;
        }
    }

    // 3D Tiles point cloud (.pnts): 28-byte header starting with "pnts", version 1, and consistent byte lengths.
    private static bool IsPntsHeader(Stream stream, long origin)
    {
        long length = stream.CanSeek ? stream.Length : long.MaxValue;
        if (length - origin < 28) return false;

        Span<byte> header = stackalloc byte[28];
        stream.Position = origin;
        try
        {
            int got = stream.ReadAtLeast(header, 28, throwOnEndOfStream: false);
            if (got < 28) return false;

            if (!header[..4].SequenceEqual(PntsMagic)) return false;

            uint version = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
            if (version != 1) return false;

            uint byteLength = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
            uint ftJsonLen = BinaryPrimitives.ReadUInt32LittleEndian(header[12..16]);
            uint ftBinLen = BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]);
            uint btJsonLen = BinaryPrimitives.ReadUInt32LittleEndian(header[20..24]);
            uint btBinLen = BinaryPrimitives.ReadUInt32LittleEndian(header[24..28]);

            if (byteLength < 28) return false;

            ulong totalCalculated = 28UL + ftJsonLen + ftBinLen + btJsonLen + btBinLen;
            if (totalCalculated != byteLength) return false;

            if (stream.CanSeek && (ulong)(length - origin) < byteLength) return false;

            return true;
        }
        finally
        {
            stream.Position = origin;
        }
    }

    // glTF 2.0 binary (.glb) containing point primitives (mesh primitive mode 0).
    private static bool IsGlbPointHeader(Stream stream, long origin)
    {
        long length = stream.CanSeek ? stream.Length : long.MaxValue;
        if (length - origin < 20) return false;

        Span<byte> header = stackalloc byte[12];
        stream.Position = origin;
        try
        {
            int got = stream.ReadAtLeast(header, 12, throwOnEndOfStream: false);
            if (got < 12) return false;

            if (!header[..4].SequenceEqual(GlbMagic)) return false;

            uint version = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
            if (version != 2) return false;

            uint totalLength = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
            if (totalLength < 20) return false;
            if (stream.CanSeek && (ulong)(length - origin) < totalLength) return false;

            Span<byte> chunkHeader = stackalloc byte[8];
            got = stream.ReadAtLeast(chunkHeader, 8, throwOnEndOfStream: false);
            if (got < 8) return false;

            uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[0..4]);
            uint chunkType = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..8]);
            // 0x4E4F534A is "JSON" in ASCII
            if (chunkType != 0x4E4F534A) return false;
            if (chunkLength == 0 || chunkLength > 64 * 1024 * 1024) return false;

            int toRead = (int)chunkLength;
            byte[] jsonBytes = new byte[toRead];
            got = stream.ReadAtLeast(jsonBytes, toRead, throwOnEndOfStream: false);
            if (got < toRead) return false;

            using var doc = JsonDocument.Parse(jsonBytes);
            var root = doc.RootElement;
            if (!root.TryGetProperty("meshes", out var meshes) || meshes.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var mesh in meshes.EnumerateArray())
            {
                if (mesh.TryGetProperty("primitives", out var prims) && prims.ValueKind == JsonValueKind.Array)
                {
                    foreach (var prim in prims.EnumerateArray())
                    {
                        if (prim.TryGetProperty("mode", out var modeElem) && modeElem.GetInt32() == 0)
                        {
                            if (prim.TryGetProperty("attributes", out var attrs) && attrs.TryGetProperty("POSITION", out _))
                                return true;
                        }
                    }
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            stream.Position = origin;
        }
    }

    // 3D Tiles tileset manifest (JSON containing asset and root objects).
    private static bool IsTilesetJsonHeader(Stream stream, long origin, string? extension)
    {
        if (extension != null && extension != ".json") return false;

        long length = stream.CanSeek ? stream.Length : long.MaxValue;
        if (length - origin < 10) return false;

        int toRead = (int)Math.Min(8192, length - origin);
        byte[] buf = new byte[toRead];
        stream.Position = origin;
        try
        {
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
        finally
        {
            stream.Position = origin;
        }
    }

    // Potree manifest: cloud.js (1.x) or metadata.json (2.x)
    private static bool IsPotreeJsonHeader(Stream stream, long origin, string? extension)
    {
        if (extension != null && extension != ".js" && extension != ".json")
            return false;

        long length = stream.CanSeek ? stream.Length : long.MaxValue;
        if (length - origin < 10) return false;

        int toRead = (int)Math.Min(8192, length - origin);
        byte[] buf = new byte[toRead];
        stream.Position = origin;
        try
        {
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
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            // Potree 1.x (cloud.js)
            if (root.TryGetProperty("octreeDir", out _) &&
                (root.TryGetProperty("pointAttributes", out _) || root.TryGetProperty("hierarchyStepSize", out _) || root.TryGetProperty("spacing", out _)))
            {
                return true;
            }

            // Potree 2.x (metadata.json)
            if (root.TryGetProperty("version", out var verElem) &&
                verElem.GetString() is { } v && v.StartsWith("2.") &&
                root.TryGetProperty("points", out _) &&
                (root.TryGetProperty("hierarchy", out _) || root.TryGetProperty("attributes", out _)))
            {
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            stream.Position = origin;
        }
    }

    // Potree hierarchy file (.hrc): entries of 5 bytes (1 byte mask + 4 bytes point count)
    private static bool IsPotreeHrcHeader(Stream stream, long origin, string? extension)
    {
        if (extension != ".hrc") return false;

        long length = stream.CanSeek ? stream.Length : long.MaxValue;
        if (length - origin < 5 || (length - origin) % 5 != 0) return false;

        int toRead = (int)Math.Min(100, length - origin);
        Span<byte> buf = stackalloc byte[toRead];
        stream.Position = origin;
        try
        {
            int got = stream.ReadAtLeast(buf, toRead, throwOnEndOfStream: false);
            if (got < 5 || got % 5 != 0) return false;

            for (int i = 0; i < got; i += 5)
            {
                uint count = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(i + 1, 4));
                if (count > 500_000_000) return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            stream.Position = origin;
        }
    }

    // Entwine Point Tile manifest (ept.json).
    private static bool IsEptJsonHeader(Stream stream, long origin, string? extension)
    {
        if (extension != null && extension != ".json" && !extension.EndsWith("ept.json", StringComparison.OrdinalIgnoreCase))
            return false;

        long length = stream.CanSeek ? stream.Length : long.MaxValue;
        if (length - origin < 10) return false;

        int toRead = (int)Math.Min(65536, length - origin);
        byte[] buf = new byte[toRead];
        stream.Position = origin;
        try
        {
            int got = stream.ReadAtLeast(buf, toRead, throwOnEndOfStream: false);
            if (got < 10) return false;

            int start = 0;
            if (got >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF)
                start = 3;
            while (start < got && char.IsWhiteSpace((char)buf[start]))
                start++;

            if (start >= got || buf[start] != (byte)'{')
                return false;

            if (length - origin <= toRead)
            {
                using var doc = JsonDocument.Parse(buf.AsMemory(start, got - start));
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;

                return root.TryGetProperty("bounds", out var bElem) && bElem.ValueKind == JsonValueKind.Array && bElem.GetArrayLength() == 6 &&
                       root.TryGetProperty("dataType", out var dtElem) && dtElem.ValueKind == JsonValueKind.String &&
                       dtElem.GetString() is "laszip" or "binary" or "zstandard" &&
                       root.TryGetProperty("schema", out var sElem) && sElem.ValueKind == JsonValueKind.Array;
            }
            else
            {
                string text = Encoding.UTF8.GetString(buf, start, got - start);
                return text.Contains("\"bounds\"") &&
                       text.Contains("\"dataType\"") &&
                       (text.Contains("\"laszip\"") || text.Contains("\"binary\"") || text.Contains("\"zstandard\"")) &&
                       text.Contains("\"schema\"");
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            stream.Position = origin;
        }
    }

    // True when a stream with .ptg extension is an ASCII PTG index file.
    private static bool IsPtgIndexHeader(Stream stream, long origin)
    {
        const int maxProbeBytes = 4096;
        long length = stream.CanSeek ? stream.Length : long.MaxValue;
        int toRead = (int)Math.Min(maxProbeBytes, Math.Max(0, length - origin));
        if (toRead < 4) return false;

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

            if (got < 4) return false;

            // Reject binary files (containing NUL bytes)
            ReadOnlySpan<byte> headSpan = buffer.AsSpan(0, got);
            if (headSpan.Contains((byte)0)) return false;

            using var reader = new StreamReader(new MemoryStream(buffer, 0, got), Encoding.UTF8);
            string? line;
            int lineCount = 0;
            bool hasValidIndicator = false;

            while ((line = reader.ReadLine()) != null && lineCount < 32)
            {
                lineCount++;
                line = line.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("PTG index", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (line.StartsWith("---") || line.StartsWith("#") || line.StartsWith("//"))
                    continue;

                if (line.EndsWith(".ptg", StringComparison.OrdinalIgnoreCase))
                {
                    hasValidIndicator = true;
                }
                else
                {
                    return false;
                }
            }

            return hasValidIndicator;
        }
        catch
        {
            return false;
        }
        finally
        {
            stream.Position = origin;
        }
    }
}
