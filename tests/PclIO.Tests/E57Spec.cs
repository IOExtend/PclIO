using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PclIO.Tests;

/// <summary>One record of the synthetic E57 point stream: float coordinates and byte colour and intensity.</summary>
public readonly record struct E57SamplePoint(float X, float Y, float Z, byte R, byte G, byte B, byte Intensity);

/// <summary>Describes and writes a small, valid E57 file: 1024-byte pages that each end with a CRC-32C, one scan whose points
/// are stored in plain bit-packed data packets. Properties let a test damage one aspect of the file.</summary>
public sealed class E57Spec
{
    private const int PagePayload = 1020;

    public const string DefaultPose = """
        <pose type="Structure">
          <rotation type="Structure"><w type="Float">1</w><x type="Float">0</x><y type="Float">0</y><z type="Float">0</z></rotation>
          <translation type="Structure"><x type="Float">1.5</x><y type="Float">2.5</y><z type="Float">3.5</z></translation>
        </pose>
        """;

    public const string DefaultPrototype = """
        <prototype type="Structure">
          <cartesianX type="Float" precision="single"/>
          <cartesianY type="Float" precision="single"/>
          <cartesianZ type="Float" precision="single"/>
          <colorRed type="Integer" minimum="0" maximum="255"/>
          <colorGreen type="Integer" minimum="0" maximum="255"/>
          <colorBlue type="Integer" minimum="0" maximum="255"/>
          <intensity type="Integer" minimum="0" maximum="255"/>
        </prototype>
        """;

    public string Guid { get; init; } = "synthetic-e57-guid";

    public IReadOnlyList<E57SamplePoint> Points { get; init; } = DefaultPoints(250);

    public int PointsPerPacket { get; init; } = 100;

    /// <summary>Bytes written instead of the generated data packets.</summary>
    public byte[]? RawPackets { get; init; }

    /// <summary>Record count written to the XML instead of the number of points.</summary>
    public long? RecordCount { get; init; }

    public string PoseXml { get; init; } = DefaultPose;

    public string PrototypeXml { get; init; } = DefaultPrototype;

    /// <summary>Elements placed inside the codecs vector; empty for plain bit-packing.</summary>
    public string CodecsXml { get; init; } = "";

    public ulong PageSize { get; init; } = 1024;

    /// <summary>Text inserted between the XML declaration and the root element, for example a document type declaration.</summary>
    public string XmlPrologue { get; init; } = "";

    public ulong? DeclaredXmlLength { get; init; }

    public ulong? DeclaredFileLength { get; init; }

    /// <summary>Deterministic points: coordinates grow with the index so every record is distinguishable.</summary>
    public static IReadOnlyList<E57SamplePoint> DefaultPoints(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new E57SamplePoint(i * 0.5f, -i, i * 0.25f, (byte)i, (byte)(i * 3), (byte)(255 - (i % 256)), (byte)(i * 7)))];

    /// <summary>A data packet holding seven streams: X, Y, Z as float32 and red, green, blue, intensity as bytes.</summary>
    public static byte[] DataPacket(IReadOnlyList<E57SamplePoint> points)
    {
        var streams = new byte[7][];
        for (int s = 0; s < 3; s++) streams[s] = new byte[points.Count * 4];
        for (int s = 3; s < 7; s++) streams[s] = new byte[points.Count];

        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            BinaryPrimitives.WriteSingleLittleEndian(streams[0].AsSpan(i * 4, 4), p.X);
            BinaryPrimitives.WriteSingleLittleEndian(streams[1].AsSpan(i * 4, 4), p.Y);
            BinaryPrimitives.WriteSingleLittleEndian(streams[2].AsSpan(i * 4, 4), p.Z);
            streams[3][i] = p.R;
            streams[4][i] = p.G;
            streams[5][i] = p.B;
            streams[6][i] = p.Intensity;
        }

        return DataPacket(streams);
    }

    /// <summary>A data packet around arbitrary stream contents, padded to a multiple of four bytes.</summary>
    public static byte[] DataPacket(IReadOnlyList<byte[]> streams)
    {
        int length = 6 + (2 * streams.Count) + streams.Sum(s => s.Length);
        length = (length + 3) / 4 * 4;

        byte[] packet = new byte[length];
        packet[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), (ushort)(length - 1));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4, 2), (ushort)streams.Count);
        int offset = 6 + (2 * streams.Count);
        for (int s = 0; s < streams.Count; s++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6 + (2 * s), 2), (ushort)streams[s].Length);
            streams[s].CopyTo(packet, offset);
            offset += streams[s].Length;
        }

        return packet;
    }

    /// <summary>A packet header only: type byte, flags and the stored (length - 1) field, with a body of zeros.</summary>
    public static byte[] RawPacket(byte type, int length, ushort? lengthMinusOne = null)
    {
        byte[] packet = new byte[length];
        packet[0] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), lengthMinusOne ?? (ushort)(length - 1));
        return packet;
    }

    public byte[] Build()
    {
        byte[] packets = RawPackets ?? BuildPackets();

        // Logical stream: header, section header, packets, XML. A physical offset skips the checksum of every page.
        const int sectionStart = 48;
        long dataStart = sectionStart + 32;
        long xmlStart = dataStart + packets.Length;

        string xml = BuildXml(Phys(sectionStart));
        byte[] xmlBytes = Encoding.UTF8.GetBytes(xml);

        long logicalLength = xmlStart + xmlBytes.Length;
        long pages = (logicalLength + PagePayload - 1) / PagePayload;
        byte[] logical = new byte[pages * PagePayload];

        "ASTM-E57"u8.CopyTo(logical);
        BinaryPrimitives.WriteUInt32LittleEndian(logical.AsSpan(8, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(logical.AsSpan(12, 4), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(logical.AsSpan(16, 8), DeclaredFileLength ?? (ulong)(pages * 1024));
        BinaryPrimitives.WriteUInt64LittleEndian(logical.AsSpan(24, 8), Phys(xmlStart));
        BinaryPrimitives.WriteUInt64LittleEndian(logical.AsSpan(32, 8), DeclaredXmlLength ?? (ulong)xmlBytes.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(logical.AsSpan(40, 8), PageSize);

        logical[sectionStart] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(logical.AsSpan(sectionStart + 8, 8), (ulong)(32 + packets.Length));
        BinaryPrimitives.WriteUInt64LittleEndian(logical.AsSpan(sectionStart + 16, 8), Phys(dataStart));
        packets.CopyTo(logical, dataStart);
        xmlBytes.CopyTo(logical, xmlStart);

        byte[] file = new byte[pages * 1024];
        for (long page = 0; page < pages; page++)
        {
            Span<byte> target = file.AsSpan((int)(page * 1024), 1024);
            logical.AsSpan((int)(page * PagePayload), PagePayload).CopyTo(target);
            BinaryPrimitives.WriteUInt32BigEndian(target.Slice(PagePayload, 4), Crc32C(target[..PagePayload]));
        }

        return file;
    }

    /// <summary>Recomputes the checksum of the page that holds this file offset after a test changed a byte of it.</summary>
    public static void FixChecksum(byte[] file, int offsetInPage)
    {
        int page = offsetInPage / 1024 * 1024;
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(page + PagePayload, 4), Crc32C(file.AsSpan(page, PagePayload)));
    }

    private static ulong Phys(long logical) => (ulong)((logical / PagePayload * 1024) + (logical % PagePayload));

    private byte[] BuildPackets()
    {
        var all = new List<byte>();
        for (int i = 0; i < Points.Count; i += PointsPerPacket)
            all.AddRange(DataPacket([.. Points.Skip(i).Take(PointsPerPacket)]));
        return [.. all];
    }

    private string BuildXml(ulong sectionOffset)
    {
        long count = RecordCount ?? Points.Count;
        string bounds = "";
        if (Points.Count > 0)
        {
            string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
            bounds = $"""
                <cartesianBounds type="Structure">
                  <xMinimum type="Float">{F(Points.Min(p => p.X))}</xMinimum><xMaximum type="Float">{F(Points.Max(p => p.X))}</xMaximum>
                  <yMinimum type="Float">{F(Points.Min(p => p.Y))}</yMinimum><yMaximum type="Float">{F(Points.Max(p => p.Y))}</yMaximum>
                  <zMinimum type="Float">{F(Points.Min(p => p.Z))}</zMinimum><zMaximum type="Float">{F(Points.Max(p => p.Z))}</zMaximum>
                </cartesianBounds>
                """;
        }

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            {XmlPrologue}
            <e57Root type="Structure" xmlns="http://www.astm.org/e57/2010">
              <formatVersionMajor type="Integer">1</formatVersionMajor>
              <formatVersionMinor type="Integer">0</formatVersionMinor>
              <guid type="String">{Guid}</guid>
              <data3D type="Vector" allowHeterogeneousChildren="1">
                <vectorChild type="Structure">
                  <guid type="String">scan-guid-001</guid>
                  <name type="String">Synthetic Scan 01</name>
                  {bounds}
                  {PoseXml}
                  <points type="CompressedVector" fileOffset="{sectionOffset.ToString(CultureInfo.InvariantCulture)}" recordCount="{count.ToString(CultureInfo.InvariantCulture)}">
                    {PrototypeXml}
                    <codecs type="Vector" allowHeterogeneousChildren="1">{CodecsXml}</codecs>
                  </points>
                </vectorChild>
              </data3D>
            </e57Root>
            """;
    }

    private static readonly uint[] CrcTable = CreateCrcTable();

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint entry = i;
            for (int bit = 0; bit < 8; bit++) entry = (entry & 1) != 0 ? (entry >> 1) ^ 0x82F63B78u : entry >> 1;
            table[i] = entry;
        }
        return table;
    }

    private static uint Crc32C(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data) crc = (crc >> 8) ^ CrcTable[(crc ^ b) & 0xFF];
        return ~crc;
    }
}
