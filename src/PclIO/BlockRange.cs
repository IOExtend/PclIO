namespace PclIO;

/// <summary>Which blocks of a scan to read: a range of the scan's blocks in file order.</summary>
public record struct BlockRange
{
    private int _startBlock;
    private int? _blockCount;

    /// <summary>Zero-based index of the first block to read. Must not be negative; 0 starts at the first block.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int StartBlock
    {
        get => _startBlock;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _startBlock = value;
        }
    }

    /// <summary>
    /// How many blocks to read at most, or null for all of them from <see cref="StartBlock"/> on.
    /// A range that runs past the last block simply ends there; 0 yields no points.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int? BlockCount
    {
        get => _blockCount;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value ?? 0);
            _blockCount = value;
        }
    }
}
