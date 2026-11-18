using PclIO.Internal;

namespace PclIO.Formats.Laz;

/// <summary>Decompressor for LASzip POINT10 v2 records.</summary>
internal sealed class LaszipPoint10Decompressor
{
    private readonly LaszipArithmeticDecoder _dec;
    private readonly LaszipDataModel _mChangedValues;
    private readonly LaszipIntegerCompressor _icIntensity;
    private readonly LaszipDataModel[] _mScanAngleRank;
    private readonly LaszipIntegerCompressor _icPointSourceId;
    private readonly LaszipDataModel?[] _mBitByte;
    private readonly LaszipDataModel?[] _mClassification;
    private readonly LaszipDataModel?[] _mUserData;
    private readonly LaszipIntegerCompressor _icDx;
    private readonly LaszipIntegerCompressor _icDy;
    private readonly LaszipIntegerCompressor _icZ;

    private readonly StreamingMedian5[] _lastXDiffMedian5;
    private readonly StreamingMedian5[] _lastYDiffMedian5;
    private readonly ushort[] _lastIntensity;
    private readonly int[] _lastHeight;

    private int _lastX;
    private int _lastY;
    private int _lastZ;
    private ushort _currentIntensity;
    private byte _lastFlags;
    private byte _lastClassification;
    private sbyte _lastScanAngle;
    private byte _lastUserData;
    private ushort _lastPointSourceId;

    public LaszipPoint10Decompressor(LaszipArithmeticDecoder dec)
    {
        _dec = dec;
        _mChangedValues = new LaszipDataModel(64);
        _icIntensity = new LaszipIntegerCompressor(dec, 16, 4);
        _mScanAngleRank = [new LaszipDataModel(256), new LaszipDataModel(256)];
        _icPointSourceId = new LaszipIntegerCompressor(dec, 16, 1);
        _mBitByte = new LaszipDataModel[256];
        _mClassification = new LaszipDataModel[256];
        _mUserData = new LaszipDataModel[256];
        _icDx = new LaszipIntegerCompressor(dec, 32, 2);
        _icDy = new LaszipIntegerCompressor(dec, 32, 22);
        _icZ = new LaszipIntegerCompressor(dec, 32, 20);

        _lastXDiffMedian5 = new StreamingMedian5[16];
        _lastYDiffMedian5 = new StreamingMedian5[16];
        for (int i = 0; i < 16; i++)
        {
            _lastXDiffMedian5[i].Init();
            _lastYDiffMedian5[i].Init();
        }
        _lastIntensity = new ushort[16];
        _lastHeight = new int[8];
    }

    public void Init(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 20) throw FormatError.Truncated("LAS point record");
        _lastX = Bytes.ReadInt32LE(raw[..4]);
        _lastY = Bytes.ReadInt32LE(raw.Slice(4, 4));
        _lastZ = Bytes.ReadInt32LE(raw.Slice(8, 4));
        _currentIntensity = Bytes.ReadUInt16LE(raw.Slice(12, 2));
        _lastFlags = raw[14];
        _lastClassification = raw[15];
        _lastScanAngle = (sbyte)raw[16];
        _lastUserData = raw[17];
        _lastPointSourceId = Bytes.ReadUInt16LE(raw.Slice(18, 2));
    }

    public void DecompressNext(out int x, out int y, out int z, out ushort intensity,
        out byte flags, out byte classification, out sbyte scanAngle, out byte userData, out ushort pointSourceId)
    {
        uint changedValues = _dec.DecodeSymbol(_mChangedValues);
        int r, n, m, l;

        if (changedValues != 0)
        {
            if ((changedValues & 32) != 0)
            {
                _mBitByte[_lastFlags] ??= new LaszipDataModel(256);
                _lastFlags = (byte)_dec.DecodeSymbol(_mBitByte[_lastFlags]!);
            }

            r = _lastFlags & 7;
            n = (_lastFlags >> 3) & 7;
            m = LaszipLookupTables.ReturnMap[n, r];
            l = LaszipLookupTables.ReturnLevel[n, r];

            if ((changedValues & 16) != 0)
            {
                _currentIntensity = (ushort)_icIntensity.Decompress(_lastIntensity[m], m < 3 ? m : 3);
                _lastIntensity[m] = _currentIntensity;
            }
            else
            {
                _currentIntensity = _lastIntensity[m];
            }

            if ((changedValues & 8) != 0)
            {
                _mClassification[_lastClassification] ??= new LaszipDataModel(256);
                _lastClassification = (byte)_dec.DecodeSymbol(_mClassification[_lastClassification]!);
            }

            if ((changedValues & 4) != 0)
            {
                int scanDir = (_lastFlags >> 6) & 1;
                uint val = _dec.DecodeSymbol(_mScanAngleRank[scanDir]);
                _lastScanAngle = (sbyte)(byte)(val + (byte)_lastScanAngle);
            }

            if ((changedValues & 2) != 0)
            {
                _mUserData[_lastUserData] ??= new LaszipDataModel(256);
                _lastUserData = (byte)_dec.DecodeSymbol(_mUserData[_lastUserData]!);
            }

            if ((changedValues & 1) != 0)
            {
                _lastPointSourceId = (ushort)_icPointSourceId.Decompress(_lastPointSourceId, 0);
            }
        }
        else
        {
            r = _lastFlags & 7;
            n = (_lastFlags >> 3) & 7;
            m = LaszipLookupTables.ReturnMap[n, r];
            l = LaszipLookupTables.ReturnLevel[n, r];
        }

        int medX = _lastXDiffMedian5[m].Get();
        int diffX = _icDx.Decompress(medX, n == 1 ? 1 : 0);
        _lastX += diffX;
        _lastXDiffMedian5[m].Add(diffX);

        int medY = _lastYDiffMedian5[m].Get();
        int kBits = _icDx.GetK();
        int contextY = (n == 1 ? 1 : 0) + (kBits < 20 ? (kBits & ~1) : 20);
        int diffY = _icDy.Decompress(medY, contextY);
        _lastY += diffY;
        _lastYDiffMedian5[m].Add(diffY);

        int kBitsZ = (_icDx.GetK() + _icDy.GetK()) / 2;
        int contextZ = (n == 1 ? 1 : 0) + (kBitsZ < 18 ? (kBitsZ & ~1) : 18);
        _lastZ = _icZ.Decompress(_lastHeight[l], contextZ);
        _lastHeight[l] = _lastZ;

        x = _lastX;
        y = _lastY;
        z = _lastZ;
        intensity = _currentIntensity;
        flags = _lastFlags;
        classification = _lastClassification;
        scanAngle = _lastScanAngle;
        userData = _lastUserData;
        pointSourceId = _lastPointSourceId;
    }
}

/// <summary>Decompressor for LASzip GPSTIME11 v2 records.</summary>
internal sealed class LaszipGpsTime11Decompressor
{
    private const int GpsTimeMulti = 500;
    private const int GpsTimeMultiMinus = -10;
    private const int GpsTimeMultiUnchanged = GpsTimeMulti - GpsTimeMultiMinus + 1; // 511
    private const int GpsTimeMultiCodeFull = GpsTimeMulti - GpsTimeMultiMinus + 2;  // 512
    private const int GpsTimeMultiTotal = GpsTimeMulti - GpsTimeMultiMinus + 6;     // 516

    private readonly LaszipArithmeticDecoder _dec;
    private readonly LaszipDataModel _mGpsTimeMulti;
    private readonly LaszipDataModel _mGpsTime0Diff;
    private readonly LaszipIntegerCompressor _icGpsTime;

    private readonly long[] _lastGpsTime = new long[4];
    private readonly int[] _lastGpsTimeDiff = new int[4];
    private readonly int[] _multiExtremeCounter = new int[4];
    private int _lastIdx;
    private int _nextIdx;

    public LaszipGpsTime11Decompressor(LaszipArithmeticDecoder dec)
    {
        _dec = dec;
        _mGpsTimeMulti = new LaszipDataModel(GpsTimeMultiTotal);
        _mGpsTime0Diff = new LaszipDataModel(6);
        _icGpsTime = new LaszipIntegerCompressor(dec, 32, 9);
    }

    public void Init(long rawGpsTime)
    {
        _lastGpsTime[0] = rawGpsTime;
        _lastGpsTime[1] = _lastGpsTime[2] = _lastGpsTime[3] = 0;
        _lastGpsTimeDiff[0] = _lastGpsTimeDiff[1] = _lastGpsTimeDiff[2] = _lastGpsTimeDiff[3] = 0;
        _multiExtremeCounter[0] = _multiExtremeCounter[1] = _multiExtremeCounter[2] = _multiExtremeCounter[3] = 0;
        _lastIdx = 0;
        _nextIdx = 0;
    }

    public long DecompressNext()
    {
        while (true)
        {
            if (_lastGpsTimeDiff[_lastIdx] == 0)
            {
                uint multi = _dec.DecodeSymbol(_mGpsTime0Diff);
                if (multi == 1)
                {
                    _lastGpsTimeDiff[_lastIdx] = _icGpsTime.Decompress(0, 0);
                    _lastGpsTime[_lastIdx] += _lastGpsTimeDiff[_lastIdx];
                    _multiExtremeCounter[_lastIdx] = 0;
                }
                else if (multi == 2)
                {
                    _nextIdx = (_nextIdx + 1) & 3;
                    long hi = _icGpsTime.Decompress((int)(_lastGpsTime[_lastIdx] >> 32), 8);
                    uint lo = _dec.ReadInt();
                    _lastGpsTime[_nextIdx] = (hi << 32) | (lo & 0xFFFFFFFFL);
                    _lastIdx = _nextIdx;
                    _lastGpsTimeDiff[_lastIdx] = 0;
                    _multiExtremeCounter[_lastIdx] = 0;
                }
                else if (multi > 2)
                {
                    _lastIdx = (_lastIdx + (int)multi - 2) & 3;
                    continue;
                }
            }
            else
            {
                uint multi = _dec.DecodeSymbol(_mGpsTimeMulti);
                if (multi == 1)
                {
                    _lastGpsTime[_lastIdx] += _icGpsTime.Decompress(_lastGpsTimeDiff[_lastIdx], 1);
                    _multiExtremeCounter[_lastIdx] = 0;
                }
                else if (multi < GpsTimeMultiUnchanged)
                {
                    int diff;
                    if (multi == 0)
                    {
                        diff = _icGpsTime.Decompress(0, 7);
                        _multiExtremeCounter[_lastIdx]++;
                        if (_multiExtremeCounter[_lastIdx] > 3)
                        {
                            _lastGpsTimeDiff[_lastIdx] = diff;
                            _multiExtremeCounter[_lastIdx] = 0;
                        }
                    }
                    else if (multi < GpsTimeMulti)
                    {
                        int context = multi < 10 ? 2 : 3;
                        diff = _icGpsTime.Decompress((int)multi * _lastGpsTimeDiff[_lastIdx], context);
                    }
                    else if (multi == GpsTimeMulti)
                    {
                        diff = _icGpsTime.Decompress(GpsTimeMulti * _lastGpsTimeDiff[_lastIdx], 4);
                        _multiExtremeCounter[_lastIdx]++;
                        if (_multiExtremeCounter[_lastIdx] > 3)
                        {
                            _lastGpsTimeDiff[_lastIdx] = diff;
                            _multiExtremeCounter[_lastIdx] = 0;
                        }
                    }
                    else
                    {
                        int mVal = GpsTimeMulti - (int)multi;
                        if (mVal > GpsTimeMultiMinus)
                        {
                            diff = _icGpsTime.Decompress(mVal * _lastGpsTimeDiff[_lastIdx], 5);
                        }
                        else
                        {
                            diff = _icGpsTime.Decompress(GpsTimeMultiMinus * _lastGpsTimeDiff[_lastIdx], 6);
                            _multiExtremeCounter[_lastIdx]++;
                            if (_multiExtremeCounter[_lastIdx] > 3)
                            {
                                _lastGpsTimeDiff[_lastIdx] = diff;
                                _multiExtremeCounter[_lastIdx] = 0;
                            }
                        }
                    }
                    _lastGpsTime[_lastIdx] += diff;
                }
                else if (multi == GpsTimeMultiCodeFull)
                {
                    _nextIdx = (_nextIdx + 1) & 3;
                    long hi = _icGpsTime.Decompress((int)(_lastGpsTime[_lastIdx] >> 32), 8);
                    uint lo = _dec.ReadInt();
                    _lastGpsTime[_nextIdx] = (hi << 32) | (lo & 0xFFFFFFFFL);
                    _lastIdx = _nextIdx;
                    _lastGpsTimeDiff[_lastIdx] = 0;
                    _multiExtremeCounter[_lastIdx] = 0;
                }
                else if (multi > GpsTimeMultiCodeFull)
                {
                    _lastIdx = (_lastIdx + (int)multi - GpsTimeMultiCodeFull) & 3;
                    continue;
                }
            }
            break;
        }

        return _lastGpsTime[_lastIdx];
    }
}

/// <summary>Decompressor for LASzip RGB12 v2 records.</summary>
internal sealed class LaszipRgb12Decompressor
{
    private readonly LaszipArithmeticDecoder _dec;
    private readonly LaszipDataModel _mByteUsed;
    private readonly LaszipDataModel[] _mRgbDiff;
    private readonly byte[] _lastRgbBytes = new byte[6];

    public LaszipRgb12Decompressor(LaszipArithmeticDecoder dec)
    {
        _dec = dec;
        _mByteUsed = new LaszipDataModel(128);
        _mRgbDiff = new LaszipDataModel[6];
        for (int i = 0; i < 6; i++)
            _mRgbDiff[i] = new LaszipDataModel(256);
    }

    public void Init(ReadOnlySpan<byte> rawRgb)
    {
        if (rawRgb.Length < 6) throw FormatError.Truncated("LAS RGB record");
        rawRgb[..6].CopyTo(_lastRgbBytes);
    }

    public void DecompressNext(out ushort r, out ushort g, out ushort b)
    {
        uint sym = _dec.DecodeSymbol(_mByteUsed);
        ushort rVal, gVal, bVal;

        if ((sym & 1) != 0)
        {
            uint corr = _dec.DecodeSymbol(_mRgbDiff[0]);
            rVal = (byte)(corr + _lastRgbBytes[0]);
        }
        else
        {
            rVal = _lastRgbBytes[0];
        }

        if ((sym & 2) != 0)
        {
            uint corr = _dec.DecodeSymbol(_mRgbDiff[1]);
            rVal |= (ushort)((byte)(corr + _lastRgbBytes[1]) << 8);
        }
        else
        {
            rVal |= (ushort)(_lastRgbBytes[1] << 8);
        }

        if ((sym & 64) != 0)
        {
            int diffLow = (rVal & 0xFF) - _lastRgbBytes[0];
            if ((sym & 4) != 0)
            {
                uint corr = _dec.DecodeSymbol(_mRgbDiff[2]);
                gVal = (byte)(corr + ClampByte(diffLow + _lastRgbBytes[2]));
            }
            else
            {
                gVal = _lastRgbBytes[2];
            }

            if ((sym & 16) != 0)
            {
                uint corr = _dec.DecodeSymbol(_mRgbDiff[4]);
                diffLow = (diffLow + ((gVal & 0xFF) - _lastRgbBytes[2])) / 2;
                bVal = (byte)(corr + ClampByte(diffLow + _lastRgbBytes[4]));
            }
            else
            {
                bVal = _lastRgbBytes[4];
            }

            int diffHigh = (rVal >> 8) - _lastRgbBytes[1];
            if ((sym & 8) != 0)
            {
                uint corr = _dec.DecodeSymbol(_mRgbDiff[3]);
                gVal |= (ushort)((byte)(corr + ClampByte(diffHigh + _lastRgbBytes[3])) << 8);
            }
            else
            {
                gVal |= (ushort)(_lastRgbBytes[3] << 8);
            }

            if ((sym & 32) != 0)
            {
                uint corr = _dec.DecodeSymbol(_mRgbDiff[5]);
                diffHigh = (diffHigh + ((gVal >> 8) - _lastRgbBytes[3])) / 2;
                bVal |= (ushort)((byte)(corr + ClampByte(diffHigh + _lastRgbBytes[5])) << 8);
            }
            else
            {
                bVal |= (ushort)(_lastRgbBytes[5] << 8);
            }
        }
        else
        {
            gVal = rVal;
            bVal = rVal;
        }

        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(_lastRgbBytes.AsSpan(0, 2), rVal);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(_lastRgbBytes.AsSpan(2, 2), gVal);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(_lastRgbBytes.AsSpan(4, 2), bVal);

        r = rVal;
        g = gVal;
        b = bVal;
    }

    private static byte ClampByte(int val) => val < 0 ? (byte)0 : val > 255 ? (byte)255 : (byte)val;
}

/// <summary>Decompressor for LASzip BYTE v2 extra bytes records.</summary>
internal sealed class LaszipByteDecompressor
{
    private readonly LaszipArithmeticDecoder _dec;
    private readonly LaszipDataModel[] _models;
    private readonly byte[] _lastBytes;

    public LaszipByteDecompressor(LaszipArithmeticDecoder dec, int count)
    {
        _dec = dec;
        _models = new LaszipDataModel[count];
        for (int i = 0; i < count; i++)
            _models[i] = new LaszipDataModel(256);
        _lastBytes = new byte[count];
    }

    public void Init(ReadOnlySpan<byte> raw)
    {
        raw[.._lastBytes.Length].CopyTo(_lastBytes);
    }

    public void DecompressNext(Span<byte> output)
    {
        for (int i = 0; i < _lastBytes.Length; i++)
        {
            uint sym = _dec.DecodeSymbol(_models[i]);
            byte val = (byte)(_lastBytes[i] + sym);
            _lastBytes[i] = val;
            if (i < output.Length) output[i] = val;
        }
    }
}
