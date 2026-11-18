using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PclIO.Internal;

namespace PclIO.Formats.Ptg;

/// <summary>Parsed metadata and layout information from a Leica PTG binary scan header.</summary>
internal sealed class PtgHeader
{
    internal static readonly byte[] Tag = [0x50, 0x54, 0x47, 0x00];
    internal const uint MagicNumber = 0x928FA3C7;

    public int NumRows { get; }
    public int NumCols { get; }
    public int Properties { get; }
    public PclTransform Transform { get; }
    public long ColumnPositionsOffset { get; }
    public string? ScanName { get; }
    public string? ScannerModel { get; }
    public string? SoftwareName { get; }
    public string? Version { get; }

    public bool HasFloatCoordinates => (Properties & 0x01) != 0;
    public bool HasDoubleCoordinates => (Properties & 0x02) != 0;
    public bool HasIntensity => (Properties & 0x04) != 0;
    public bool HasColors => (Properties & 0x08) != 0;

    public int RecordSize => (HasFloatCoordinates ? 12 : 24) + (HasIntensity ? 4 : 0) + (HasColors ? 3 : 0);

    internal PtgHeader(
        int numRows,
        int numCols,
        int properties,
        PclTransform transform,
        long columnPositionsOffset,
        string? scanName,
        string? scannerModel,
        string? softwareName,
        string? version)
    {
        NumRows = numRows;
        NumCols = numCols;
        Properties = properties;
        Transform = transform;
        ColumnPositionsOffset = columnPositionsOffset;
        ScanName = scanName;
        ScannerModel = scannerModel;
        SoftwareName = softwareName;
        Version = version;
    }

    public static PtgHeader Parse(Stream stream)
    {
        long origin = stream.Position;
        Span<byte> head = stackalloc byte[8];
        int read = stream.ReadAtLeast(head, 8, throwOnEndOfStream: false);
        if (read < 8)
            throw FormatError.Truncated("PTG 8-byte signature");

        if (!head[..4].SequenceEqual(Tag))
            throw FormatError.Corrupted("Invalid PTG file tag.");

        if (BinaryPrimitives.ReadUInt32LittleEndian(head[4..8]) != MagicNumber)
            throw FormatError.Corrupted("Invalid PTG magic number.");

        const int maxHeaderBytes = 1 << 20; // 1 MB limit
        byte[] buffer = new byte[4096];
        using var ms = new MemoryStream();

        byte[] endMarker = "%%header_end"u8.ToArray();
        int foundIndex = -1;
        int endLineLength = 0;

        while (ms.Length < maxHeaderBytes)
        {
            int n = stream.Read(buffer, 0, buffer.Length);
            if (n == 0) break;
            int prevLen = (int)ms.Length;
            ms.Write(buffer, 0, n);

            byte[] fullBytes = ms.GetBuffer();
            int totalBytes = (int)ms.Length;

            int searchStart = Math.Max(0, prevLen - endMarker.Length);
            int idx = fullBytes.AsSpan(searchStart, totalBytes - searchStart).IndexOf(endMarker);
            if (idx >= 0)
            {
                foundIndex = searchStart + idx;
                int afterTag = foundIndex + endMarker.Length;
                int nlIdx = fullBytes.AsSpan(afterTag, totalBytes - afterTag).IndexOf((byte)'\n');
                if (nlIdx >= 0)
                {
                    endLineLength = afterTag + nlIdx + 1;
                    break;
                }
            }
        }

        if (foundIndex < 0 || endLineLength == 0)
            throw FormatError.Truncated("PTG header end tag '%%header_end'");

        long columnPositionsOffset = origin + 8 + endLineLength;
        byte[] headerBytes = ms.ToArray();
        string headerText = Encoding.ASCII.GetString(headerBytes, 0, foundIndex);

        using var reader = new StringReader(headerText);
        string? line;
        int? numRows = null;
        int? numCols = null;
        int? properties = null;
        List<double>? matrix = null;
        string? scanName = null;
        string? scannerModel = null;
        string? softwareName = null;
        string? version = null;

        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("%%header_begin", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("header_begin", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.StartsWith("%%version", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("version", StringComparison.OrdinalIgnoreCase))
            {
                version = ExtractValue(line);
                continue;
            }

            if (line.StartsWith("NumRows", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("%%NumRows", StringComparison.OrdinalIgnoreCase))
            {
                string val = ExtractValue(line);
                if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int nr))
                    throw FormatError.Corrupted($"Invalid NumRows value '{val}'.");
                numRows = nr;
                continue;
            }

            if (line.StartsWith("NumCols", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("%%NumCols", StringComparison.OrdinalIgnoreCase))
            {
                string val = ExtractValue(line);
                if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int nc))
                    throw FormatError.Corrupted($"Invalid NumCols value '{val}'.");
                numCols = nc;
                continue;
            }

            if (line.StartsWith("%%properties", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("properties", StringComparison.OrdinalIgnoreCase))
            {
                string val = ExtractValue(line);
                int p;
                if (val.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(val[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out p))
                        throw FormatError.Corrupted($"Invalid properties hex value '{val}'.");
                }
                else
                {
                    if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out p))
                        throw FormatError.Corrupted($"Invalid properties value '{val}'.");
                }
                properties = p;
                continue;
            }

            if (line.StartsWith("%%transform", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("transform", StringComparison.OrdinalIgnoreCase))
            {
                matrix = [];
                string remaining = ExtractValue(line);
                if (!string.IsNullOrWhiteSpace(remaining))
                {
                    AddDoubles(remaining, matrix);
                }
                while (matrix.Count < 16)
                {
                    string? nextLine = reader.ReadLine();
                    if (nextLine == null)
                        throw FormatError.Truncated("PTG transform matrix; expected 16 values.");
                    AddDoubles(nextLine, matrix);
                }
                if (matrix.Count != 16)
                    throw FormatError.Corrupted($"Invalid PTG transform matrix: expected 16 values, got {matrix.Count}.");
                continue;
            }

            if (line.StartsWith("%%scan_name", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("scan_name", StringComparison.OrdinalIgnoreCase))
            {
                scanName = ExtractValue(line);
                continue;
            }

            if (line.StartsWith("%%scanner_model", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("scanner_model", StringComparison.OrdinalIgnoreCase))
            {
                scannerModel = ExtractValue(line);
                continue;
            }

            if (line.StartsWith("%%sw_name", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("sw_name", StringComparison.OrdinalIgnoreCase))
            {
                softwareName = ExtractValue(line);
                continue;
            }
        }

        if (!numRows.HasValue)
            throw FormatError.Corrupted("Missing NumRows in PTG header.");
        if (!numCols.HasValue)
            throw FormatError.Corrupted("Missing NumCols in PTG header.");
        if (!properties.HasValue)
            throw FormatError.Corrupted("Missing properties in PTG header.");

        int rows = numRows.Value;
        int cols = numCols.Value;
        if (rows <= 0 || cols <= 0)
            throw FormatError.Corrupted("PTG dimensions must be positive.");

        if (rows > 100_000 || cols > 100_000 || (long)rows * cols > 1_000_000_000)
            throw FormatError.Corrupted("PTG dimensions exceed supported limits.");

        int pVal = properties.Value;
        bool hasFloat = (pVal & 0x1) != 0;
        bool hasDouble = (pVal & 0x2) != 0;
        if (hasFloat == hasDouble)
            throw FormatError.Corrupted("PTG properties must specify exactly one of float (0x1) or double (0x2) coordinates.");

        if ((pVal & ~0x0F) != 0)
            throw FormatError.Corrupted($"Unsupported PTG property bits: 0x{pVal:X}.");

        PclTransform transform = matrix != null ? new PclTransform { Matrix4x4 = matrix.ToArray() } : PclTransform.Identity;

        return new PtgHeader(rows, cols, pVal, transform, columnPositionsOffset, scanName, scannerModel, softwareName, version);
    }

    private static string ExtractValue(string line)
    {
        int i = 0;
        while (i < line.Length && !char.IsWhiteSpace(line[i]) && line[i] != ':' && line[i] != '=') i++;
        while (i < line.Length && (char.IsWhiteSpace(line[i]) || line[i] == ':' || line[i] == '=')) i++;
        return i < line.Length ? line[i..].Trim() : string.Empty;
    }

    private static void AddDoubles(string text, List<double> values)
    {
        string[] parts = text.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
                throw FormatError.Corrupted($"Invalid numeric value in PTG transform: '{part}'.");
            if (!double.IsFinite(val))
                throw FormatError.Corrupted("Non-finite numeric value in PTG transform.");
            values.Add(val);
        }
    }
}
