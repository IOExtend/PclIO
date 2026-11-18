namespace PclIO.Formats.Copc;

/// <summary>Identifies a voxel in the COPC octree hierarchy by level and 3D integer indices.</summary>
/// <param name="Level">Octree depth level (0 is root).</param>
/// <param name="X">Voxel integer X index at this level.</param>
/// <param name="Y">Voxel integer Y index at this level.</param>
/// <param name="Z">Voxel integer Z index at this level.</param>
public readonly record struct CopcVoxelKey(int Level, int X, int Y, int Z)
{
    /// <summary>Calculates the axis-aligned spatial bounding box of this voxel from the dataset center and half-size.</summary>
    public PclBounds GetBounds(PclVector3 center, double halfSize)
    {
        if (Level < 0 || halfSize <= 0) return PclBounds.Empty;
        double side = (2.0 * halfSize) / Math.Pow(2.0, Level);
        double minX = (center.X - halfSize) + (X * side);
        double minY = (center.Y - halfSize) + (Y * side);
        double minZ = (center.Z - halfSize) + (Z * side);
        return new PclBounds(new PclVector3(minX, minY, minZ), new PclVector3(minX + side, minY + side, minZ + side));
    }

    /// <inheritdoc />
    public override string ToString() => $"({Level}-{X}-{Y}-{Z})";
}

/// <summary>Represents a resolved node in the COPC octree hierarchy.</summary>
/// <param name="Key">Voxel key identifying the node in the octree.</param>
/// <param name="Bounds">Spatial bounding box of the node voxel.</param>
/// <param name="Offset">Absolute file offset of the compressed point chunk.</param>
/// <param name="ByteSize">Size of the compressed point chunk in bytes.</param>
/// <param name="PointCount">Number of points in this node chunk.</param>
public sealed record CopcNode(CopcVoxelKey Key, PclBounds Bounds, ulong Offset, int ByteSize, int PointCount);

/// <summary>Metadata stored in the COPC info VLR.</summary>
internal sealed record CopcInfo(
    PclVector3 Center,
    double HalfSize,
    double Spacing,
    ulong RootHierarchyOffset,
    ulong RootHierarchySize,
    double? GpsTimeMin,
    double? GpsTimeMax);
