namespace PclIO.Internal;

/// <summary>Decompresses an LZF stream: Marc Lehmann's byte-oriented LZ77 format with literal runs and matches.</summary>
/// <remarks>
/// The stream is a sequence of tokens read from <c>input</c>: a control byte below 32 starts a literal run of
/// <c>control + 1</c> bytes that follow, and any other control byte starts a match. A match copies
/// <c>(control >> 5) + 2</c> bytes, or, when <c>control >> 5</c> is 7, <c>7 + 2</c> plus one extra length byte more,
/// from <c>(control &amp; 0x1F) * 256</c> plus the next byte plus one bytes back; a match may overlap its own output.
/// Matches reach at most 8192 bytes back and copy at most 264 bytes. Anything else is malformed.
/// </remarks>
internal static class LzfDecoder
{
    /// <summary>Decompresses <paramref name="input"/> into exactly <paramref name="expectedLength"/> bytes.</summary>
    /// <exception cref="InvalidDataException">The stream ends early, overruns the output or holds trailing bytes.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> input, int expectedLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedLength);
        byte[] output = new byte[expectedLength];
        int ip = 0;
        int op = 0;

        while (op < expectedLength)
        {
            if (ip >= input.Length) throw FormatError.Corrupted("LZF stream ends before the output is complete.");
            int ctrl = input[ip++];

            if (ctrl < 32)
            {
                // A literal run: the next ctrl + 1 bytes are copied as they are.
                int length = ctrl + 1;
                if (length > input.Length - ip) throw FormatError.Corrupted("LZF literal run reaches past the input.");
                if (length > expectedLength - op) throw FormatError.Corrupted("LZF literal run reaches past the output.");
                input.Slice(ip, length).CopyTo(output.AsSpan(op, length));
                ip += length;
                op += length;
            }
            else
            {
                // A match: its length follows from the control byte (plus one extra byte at the longest code),
                // and its offset from the low bits of the control byte and the next input byte.
                int length = ctrl >> 5;
                int offset = (ctrl & 0x1F) << 8;
                if (length == 7)
                {
                    if (ip >= input.Length) throw FormatError.Corrupted("LZF match length reaches past the input.");
                    length += input[ip++];
                }

                if (ip >= input.Length) throw FormatError.Corrupted("LZF match offset reaches past the input.");
                offset += input[ip++] + 1;
                if (offset > op) throw FormatError.Corrupted("LZF match reaches before the start of the output.");

                length += 2;
                if (length > expectedLength - op) throw FormatError.Corrupted("LZF match reaches past the output.");

                // A match may overlap its own output, so it proceeds byte by byte.
                int from = op - offset;
                for (int i = 0; i < length; i++) output[op++] = output[from++];
            }
        }

        if (ip != input.Length) throw FormatError.Corrupted("LZF stream holds bytes past the output.");
        return output;
    }
}
