using System.Globalization;

namespace PclIO.Formats.Ept;

/// <summary>Identifies an octree node in an Entwine Point Tile hierarchy by depth and 3D integer indices.</summary>
/// <param name="Depth">Octree tree depth (0 is root).</param>
/// <param name="X">Voxel integer X index at this depth.</param>
/// <param name="Y">Voxel integer Y index at this depth.</param>
/// <param name="Z">Voxel integer Z index at this depth.</param>
public readonly record struct EptKey(int Depth, int X, int Y, int Z)
{
    /// <summary>Parses an EPT key string formatted as "depth-x-y-z".</summary>
    public static bool TryParse(string s, out EptKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        string[] parts = s.Split('-');
        if (parts.Length != 4) return false;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int d) || d < 0) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) || x < 0) return false;
        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) || y < 0) return false;
        if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int z) || z < 0) return false;

        key = new EptKey(d, x, y, z);
        return true;
    }

    /// <summary>Calculates the axis-aligned spatial bounding box of this voxel from the root bounding box.</summary>
    public PclBounds GetBounds(PclBounds rootBounds)
    {
        if (Depth < 0 || rootBounds.IsEmpty) return PclBounds.Empty;
        double divisor = Math.Pow(2.0, Depth);
        double stepX = rootBounds.Size.X / divisor;
        double stepY = rootBounds.Size.Y / divisor;
        double stepZ = rootBounds.Size.Z / divisor;

        double minX = rootBounds.Min.X + (X * stepX);
        double minY = rootBounds.Min.Y + (Y * stepY);
        double minZ = rootBounds.Min.Z + (Z * stepZ);

        return new PclBounds(new PclVector3(minX, minY, minZ), new PclVector3(minX + stepX, minY + stepY, minZ + stepZ));
    }

    /// <inheritdoc />
    public override string ToString() => $"{Depth}-{X}-{Y}-{Z}";
}

/// <summary>Represents a point attribute dimension declared in the EPT schema.</summary>
/// <param name="Name">Dimension name (e.g. "X", "Y", "Z", "Intensity", "Red", "Green", "Blue").</param>
/// <param name="Type">Data type ("signed", "unsigned", or "float").</param>
/// <param name="Size">Byte size of the field (1, 2, 4, or 8).</param>
/// <param name="Scale">Coordinate scale factor applied to raw values.</param>
/// <param name="Offset">Coordinate offset added to scaled values.</param>
public sealed record EptDimension(string Name, string Type, int Size, double Scale = 1.0, double Offset = 0.0);

/// <summary>Represents a resolved node in an EPT octree dataset.</summary>
/// <param name="Key">Voxel key identifying the node in the octree.</param>
/// <param name="Bounds">Spatial bounding box of the node voxel.</param>
/// <param name="DataPath">Absolute or relative file path to the node data file.</param>
/// <param name="PointCount">Number of points in this node tile.</param>
public sealed record EptNode(EptKey Key, PclBounds Bounds, string? DataPath, long PointCount);
