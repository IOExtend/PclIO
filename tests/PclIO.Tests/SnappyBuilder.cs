namespace PclIO.Tests;

/// <summary>Assembles a raw Snappy stream element by element, so that a test decides exactly which tags the stream uses.</summary>
internal sealed class SnappyBuilder
{
    private readonly List<byte> _elements = [];
    private long _length;

    /// <summary>Appends a literal of at least one byte, using the shortest length encoding.</summary>
    public SnappyBuilder Literal(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) throw new ArgumentException("A literal holds at least one byte.", nameof(data));

        long n = data.Length - 1;
        if (n < 60)
        {
            _elements.Add((byte)(n << 2));
        }
        else
        {
            int extra = n < 1L << 8 ? 1 : n < 1L << 16 ? 2 : n < 1L << 24 ? 3 : 4;
            _elements.Add((byte)((59 + extra) << 2));
            for (int i = 0; i < extra; i++) _elements.Add((byte)(n >> (8 * i)));
        }

        _elements.AddRange(data.ToArray());
        _length += data.Length;
        return this;
    }

    /// <summary>Appends a copy; <paramref name="tagBytes"/> is the number of offset bytes (1, 2 or 4) that selects the tag kind.</summary>
    public SnappyBuilder Copy(long offset, int length, int tagBytes)
    {
        switch (tagBytes)
        {
            case 1:
                if (length is < 4 or > 11 || offset >= 2048) throw new ArgumentException("Does not fit a one-byte-offset copy.");
                _elements.Add((byte)(1 | ((length - 4) << 2) | ((int)(offset >> 8) << 5)));
                _elements.Add((byte)offset);
                break;
            case 2:
                if (length is < 1 or > 64 || offset >= 1 << 16) throw new ArgumentException("Does not fit a two-byte-offset copy.");
                _elements.Add((byte)(2 | ((length - 1) << 2)));
                _elements.Add((byte)offset);
                _elements.Add((byte)(offset >> 8));
                break;
            case 4:
                if (length is < 1 or > 64) throw new ArgumentException("Does not fit a four-byte-offset copy.");
                _elements.Add((byte)(3 | ((length - 1) << 2)));
                for (int i = 0; i < 4; i++) _elements.Add((byte)(offset >> (8 * i)));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(tagBytes));
        }

        _length += length;
        return this;
    }

    /// <summary>Appends raw bytes after the last element, to build a stream that does not end where it should.</summary>
    public SnappyBuilder Raw(params byte[] bytes)
    {
        _elements.AddRange(bytes);
        return this;
    }

    /// <summary>The stream, with the preamble holding the length of everything appended as data.</summary>
    public byte[] Build() => Build(_length);

    /// <summary>The stream with a preamble that declares <paramref name="declaredLength"/> bytes, whatever the elements produce.</summary>
    public byte[] Build(long declaredLength)
    {
        var result = new List<byte>();
        long v = declaredLength;
        while (v >= 0x80)
        {
            result.Add((byte)(v | 0x80));
            v >>= 7;
        }

        result.Add((byte)v);
        result.AddRange(_elements);
        return [.. result];
    }

    /// <summary>A stream made of literals of at most <paramref name="fragment"/> bytes.</summary>
    public static byte[] OfLiterals(byte[] data, int fragment = 65536)
    {
        var builder = new SnappyBuilder();
        for (int i = 0; i < data.Length; i += fragment) builder.Literal(data.AsSpan(i, Math.Min(fragment, data.Length - i)));
        return builder.Build();
    }
}
