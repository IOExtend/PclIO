using PclIO.Internal;

namespace PclIO.Formats.Laz;

/// <summary>LASzip arithmetic entropy decoder.</summary>
internal sealed class LaszipArithmeticDecoder
{
    private const uint MinLength = 0x01000000u;
    private const uint MaxLength = 0xFFFFFFFFu;

    private readonly byte[] _data;
    private int _ptr;
    private uint _value;
    private uint _length;

    public int Position => _ptr;

    public LaszipArithmeticDecoder(byte[] data, int offset = 0)
    {
        _data = data;
        _ptr = offset + 3;
        if (offset + 4 > data.Length)
            throw FormatError.Truncated("LASzip arithmetic stream");

        _value = ((uint)data[offset] << 24) |
                 ((uint)data[offset + 1] << 16) |
                 ((uint)data[offset + 2] << 8) |
                 data[offset + 3];
        _length = MaxLength;
    }

    public uint DecodeBit(LaszipBitModel m)
    {
        uint x = m.Bit0Prob * (_length >> 13);
        uint bit = _value >= x ? 1u : 0u;
        if (bit == 0)
        {
            _length = x;
            m.IncrementBit0();
        }
        else
        {
            _value -= x;
            _length -= x;
        }

        if (_length < MinLength) Renorm();
        if (--m.BitsUntilUpdate == 0) m.Update();
        return bit;
    }

    public uint DecodeSymbol(LaszipDataModel m)
    {
        uint x;
        uint sym;
        uint y = _length;

        if (m.DecoderTable != null)
        {
            _length >>= 15;
            if (_length == 0) throw FormatError.Corrupted("Corrupted arithmetic coder state.");
            uint dv = _value / _length;
            int t = (int)(dv >> m.TableShift);
            if (t > m.TableSize) throw FormatError.Corrupted("Corrupted arithmetic coder symbol index.");

            sym = m.DecoderTable[t];
            uint n = m.DecoderTable[t + 1] + 1;
            while (n > sym + 1)
            {
                uint k = (sym + n) >> 1;
                if (m.Distribution[k] > dv) n = k; else sym = k;
            }

            x = m.Distribution[sym] * _length;
            if (sym != (uint)m.LastSymbol) y = m.Distribution[sym + 1] * _length;
        }
        else
        {
            x = 0;
            sym = 0;
            _length >>= 15;
            if (_length == 0) throw FormatError.Corrupted("Corrupted arithmetic coder state.");
            uint n = (uint)m.Symbols;
            uint k = n >> 1;
            do
            {
                uint z = _length * m.Distribution[k];
                if (z > _value)
                {
                    n = k;
                    y = z;
                }
                else
                {
                    sym = k;
                    x = z;
                }
            } while ((k = (sym + n) >> 1) != sym);
        }

        _value -= x;
        _length = y - x;

        if (_length < MinLength) Renorm();
        m.SymbolCount[sym]++;
        if (--m.SymbolsUntilUpdate == 0) m.Update();
        return sym;
    }

    public ushort ReadShort()
    {
        _length >>= 16;
        if (_length == 0) throw FormatError.Corrupted("Corrupted arithmetic coder state.");
        uint sym = _value / _length;
        _value -= _length * sym;
        if (_length < MinLength) Renorm();
        if (sym >= (1u << 16)) throw FormatError.Corrupted("Arithmetic coder short overflow.");
        return (ushort)sym;
    }

    public uint ReadBits(int bits)
    {
        if (bits is <= 0 or > 32) throw new ArgumentOutOfRangeException(nameof(bits));
        if (bits > 19)
        {
            uint tmp = ReadShort();
            uint tmp1 = ReadBits(bits - 16) << 16;
            return tmp1 | tmp;
        }

        _length >>= bits;
        if (_length == 0) throw FormatError.Corrupted("Corrupted arithmetic coder state.");
        uint sym = _value / _length;
        _value -= _length * sym;
        if (_length < MinLength) Renorm();
        if (sym >= (1u << bits)) throw FormatError.Corrupted("Arithmetic coder bits overflow.");
        return sym;
    }

    public uint ReadInt()
    {
        uint lowerInt = ReadShort();
        uint upperInt = ReadShort();
        return (upperInt << 16) | lowerInt;
    }

    private void Renorm()
    {
        do
        {
            _ptr++;
            byte b = _ptr < _data.Length ? _data[_ptr] : (byte)0;
            _value = (_value << 8) | b;
        } while ((_length <<= 8) < MinLength);
    }
}
