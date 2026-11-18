namespace PclIO.Formats.Laz;

/// <summary>LASzip integer compressor and decompressor.</summary>
internal sealed class LaszipIntegerCompressor
{
    private readonly LaszipArithmeticDecoder _dec;
    private readonly int _bitsHigh;
    private readonly int _corrBits;
    private readonly int _corrRange;
    private readonly int _corrMin;
    private readonly LaszipDataModel[] _mBits;
    private readonly LaszipDataModel[] _mCorrector;
    private readonly LaszipBitModel _mCorrector0;
    private int _k;

    public LaszipIntegerCompressor(LaszipArithmeticDecoder dec, int bits, int contexts, int bitsHigh = 8)
    {
        _dec = dec;
        _bitsHigh = bitsHigh;

        if (bits is > 0 and < 32)
        {
            _corrBits = bits;
            _corrRange = 1 << bits;
            _corrMin = -(_corrRange / 2);
        }
        else
        {
            _corrBits = 32;
            _corrRange = 0;
            _corrMin = int.MinValue;
        }

        _mBits = new LaszipDataModel[contexts];
        for (int i = 0; i < contexts; i++)
            _mBits[i] = new LaszipDataModel(_corrBits + 1);

        _mCorrector0 = new LaszipBitModel();
        _mCorrector = new LaszipDataModel[_corrBits + 1];
        for (int i = 1; i <= _corrBits; i++)
        {
            int count = i <= bitsHigh ? (1 << i) : (1 << bitsHigh);
            _mCorrector[i] = new LaszipDataModel(count);
        }
    }

    public int GetK() => _k;

    public int ReadCorrector(LaszipDataModel model)
    {
        _k = (int)_dec.DecodeSymbol(model);
        int k = _k;
        if (k != 0)
        {
            if (k < 32)
            {
                int c;
                if (k <= _bitsHigh)
                {
                    c = (int)_dec.DecodeSymbol(_mCorrector[k]);
                }
                else
                {
                    int k1 = k - _bitsHigh;
                    c = (int)_dec.DecodeSymbol(_mCorrector[k]);
                    int c1 = (int)_dec.ReadBits(k1);
                    c = (c << k1) | c1;
                }

                if (c >= (1 << (k - 1)))
                    c += 1;
                else
                    c -= ((1 << k) - 1);
                return c;
            }
            return _corrMin;
        }
        return (int)_dec.DecodeBit(_mCorrector0);
    }

    public int Decompress(int pred, int context = 0)
    {
        int real = pred + ReadCorrector(_mBits[context]);
        if (_corrRange != 0)
        {
            if (real < 0) real += _corrRange;
            else if (real >= _corrRange) real -= _corrRange;
        }
        return real;
    }
}
