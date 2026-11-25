using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PclIO.Internal;

namespace PclIO.Formats.Ptg;

/// <summary>Parsed metadata and layout information from a Leica PTG binary scan header.</summary>
/// <remarks>
/// The header is a sequence of records: a uint32 name length, the name with a trailing NUL, then a value whose
/// shape depends on the name. Strings carry a uint32 byte length and the bytes with a trailing NUL; counters are
/// a bare uint32; the transform is 16 float64 values. The record <c>%%header_end</c> terminates the header and is
/// immediately followed by the column offset table.
/// </remarks>
internal sealed class PtgHeader
{
    internal static readonly byte[] Tag = [0x50, 0x54, 0x47, 0x00];
    internal const uint MagicNumber = 0x928FA3C7;

    private const int MaxHeaderBytes = 1 << 20; // 1 MB limit
    private const int MaxNameBytes = 256;

    public int NumRows { get; }
    public int NumCols { get; }
    public int Properties { get; }
    public PclTransform Transform { get; }
    public long ColumnPositionsOffset { get; }
    public string? ScanName { get; }
    public string? ScannerName { get; }
    public string? ScannerModel { get; }
    public string? SoftwareName { get; }
    public string? Version { get; }
    public string? CreationDate { get; }
    public string? CreationTime { get; }

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
        string? scannerName,
        string? scannerModel,
        string? softwareName,
        string? version,
        string? creationDate,
        string? creationTime)
    {
        NumRows = numRows;
        NumCols = numCols;
        Properties = properties;
        Transform = transform;
        ColumnPositionsOffset = columnPositionsOffset;
        ScanName = scanName;
        ScannerName = scannerName;
        ScannerModel = scannerModel;
        SoftwareName = softwareName;
        Version = version;
        CreationDate = creationDate;
        CreationTime = creationTime;
    }

    public static PtgHeader Parse(Stream stream)
    {
        Span<byte> head = stackalloc byte[8];
        int read = stream.ReadAtLeast(head, 8, throwOnEndOfStream: false);
        if (read < 8)
            throw FormatError.Truncated("PTG 8-byte signature");

        if (!head[..4].SequenceEqual(Tag))
            throw FormatError.Corrupted("Invalid PTG file tag.");

        if (BinaryPrimitives.ReadUInt32LittleEndian(head[4..8]) != MagicNumber)
            throw FormatError.Corrupted("Invalid PTG magic number.");

        long headerStart = stream.Position;
        int? numRows = null;
        int? numCols = null;
        int? properties = null;
        double[]? matrix = null;
        string? scanName = null;
        string? scannerName = null;
        string? scannerModel = null;
        string? softwareName = null;
        string? version = null;
        string? creationDate = null;
        string? creationTime = null;

        byte[] nameBuf = new byte[MaxNameBytes];
        bool ended = false;
        while (!ended)
        {
            if (stream.Position - headerStart > MaxHeaderBytes)
                throw FormatError.Corrupted("PTG header exceeds the 1 MiB limit.");

            uint nameLength = ReadUInt32(stream);
            if (nameLength < 2 || nameLength > MaxNameBytes)
                throw FormatError.Corrupted($"Invalid PTG header record name length {nameLength}.");

            ReadExactly(stream, nameBuf.AsSpan(0, (int)nameLength), "PTG header record name");
            if (nameBuf[(int)nameLength - 1] != 0)
                throw FormatError.Corrupted("PTG header record name is not NUL-terminated.");
            string key = Encoding.ASCII.GetString(nameBuf, 0, (int)nameLength - 1);

            switch (key)
            {
                case "%%header_begin":
                    break;
                case "%%header_end":
                    ended = true;
                    break;
                case "%%version":
                    uint v = ReadUInt32(stream);
                    if (v != 1)
                        throw FormatError.Unsupported($"PTG header version {v}; only version 1 is described.");
                    version = v.ToString(CultureInfo.InvariantCulture);
                    break;
                case "%%cols":
                    numCols = ReadCount(stream, key);
                    break;
                case "%%rows":
                    numRows = ReadCount(stream, key);
                    break;
                case "%%properties":
                    properties = (int)ReadUInt32(stream);
                    break;
                case "%%transform":
                    matrix = ReadTransform(stream);
                    break;
                case "%%scan_name":
                    scanName = ReadString(stream, key);
                    break;
                case "%%scanner_name":
                    scannerName = ReadString(stream, key);
                    break;
                case "%%scanner_model":
                    scannerModel = ReadString(stream, key);
                    break;
                case "%%sw_name":
                    softwareName = ReadString(stream, key);
                    break;
                case "%%creation_date":
                    creationDate = ReadString(stream, key);
                    break;
                case "%%creation_time":
                    creationTime = ReadString(stream, key);
                    break;
                default:
                    throw FormatError.Unsupported($"PTG header key '{key}'.");
            }
        }

        if (!numRows.HasValue)
            throw FormatError.Corrupted("Missing %%rows in PTG header.");
        if (!numCols.HasValue)
            throw FormatError.Corrupted("Missing %%cols in PTG header.");
        if (!properties.HasValue)
            throw FormatError.Corrupted("Missing %%properties in PTG header.");

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

        // PTG stores a row-vector matrix (translation in the last row); PclTransform applies a column-vector one
        // (translation in the last column), so the matrix is transposed on load.
        PclTransform transform = PclTransform.Identity;
        if (matrix != null)
        {
            double[] m = new double[16];
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    m[r * 4 + c] = matrix[c * 4 + r];
            transform = new PclTransform { Matrix4x4 = m };
        }

        return new PtgHeader(rows, cols, pVal, transform, stream.Position,
            scanName, scannerName, scannerModel, softwareName, version, creationDate, creationTime);
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer, string what)
    {
        try
        {
            stream.ReadExactly(buffer);
        }
        catch (EndOfStreamException)
        {
            throw FormatError.Truncated(what);
        }
    }

    private static uint ReadUInt32(Stream stream)
    {
        Span<byte> buf = stackalloc byte[4];
        ReadExactly(stream, buf, "PTG header uint32");
        return BinaryPrimitives.ReadUInt32LittleEndian(buf);
    }

    private static int ReadCount(Stream stream, string key)
    {
        uint value = ReadUInt32(stream);
        if (value > int.MaxValue)
            throw FormatError.Corrupted($"PTG {key} value {value} exceeds the supported range.");
        return (int)value;
    }

    private static string ReadString(Stream stream, string key)
    {
        uint length = ReadUInt32(stream);
        if (length == 0 || length > MaxHeaderBytes)
            throw FormatError.Corrupted($"Invalid PTG {key} string length {length}.");

        byte[] buf = new byte[length];
        ReadExactly(stream, buf, $"PTG {key} string");
        int textLength = buf[length - 1] == 0 ? (int)length - 1 : (int)length;
        return Encoding.UTF8.GetString(buf, 0, textLength);
    }

    private static double[] ReadTransform(Stream stream)
    {
        byte[] buf = new byte[128];
        ReadExactly(stream, buf, "PTG transform matrix");
        double[] values = new double[16];
        for (int i = 0; i < 16; i++)
        {
            double val = BinaryPrimitives.ReadDoubleLittleEndian(buf.AsSpan(i * 8, 8));
            if (!double.IsFinite(val))
                throw FormatError.Corrupted("Non-finite numeric value in PTG transform.");
            values[i] = val;
        }
        return values;
    }
}
