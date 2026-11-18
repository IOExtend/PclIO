namespace PclIO;

/// <summary>Identifies the format kind of a point cloud file or container.</summary>
public enum PclFileKind
{
    /// <summary>Unrecognized or unsupported format.</summary>
    Unknown = 0,

    /// <summary>ASTM E57 3D Imaging Data File (.e57).</summary>
    E57 = 1,

    /// <summary>ReCap project archive (.rcp).</summary>
    RecapProject = 2,

    /// <summary>ReCap scan file (.rcs).</summary>
    RecapScan = 3,

    /// <summary>Leica container (.lgsx).</summary>
    LeicaLgsx = 4,

    /// <summary>Leica legacy container (.lgs).</summary>
    LeicaLgs = 5,

    /// <summary>FARO scan: a single .fls file, or a directory with a FARO scan layout.</summary>
    FaroFls = 6,

    /// <summary>PinPoint / PointCab project manifest (.srf, .smf) or a directory with ScanProject.json.</summary>
    PinPoint = 7,

    /// <summary>ClearEdge3D EdgeWise point database and scan clusters (.c3db, .c3ptx).</summary>
    EdgeWise = 8,

    /// <summary>ASPRS LAS point cloud file (.las).</summary>
    Las = 9,

    /// <summary>Point Cloud Library point cloud file (.pcd).</summary>
    Pcd = 10,

    /// <summary>Stanford polygon file with point cloud data (.ply).</summary>
    Ply = 11,

    /// <summary>Leica Cyclone point cloud exchange format (.pts).</summary>
    Pts = 12,

    /// <summary>Leica Cyclone gridded point cloud exchange format (.ptx).</summary>
    Ptx = 13,

    /// <summary>Delimited text point cloud (.xyz, .asc, .txt, .csv).</summary>
    Xyz = 14,

    /// <summary>Simple Binary Format point cloud (.sbf, .sbf.data).</summary>
    Sbf = 15,

    /// <summary>OGC 3D Tiles point cloud (.pnts, .glb, tileset.json).</summary>
    Tiles3d = 16,

    /// <summary>Potree octree point cloud (cloud.js, metadata.json, .hrc, .bin).</summary>
    Potree = 17,

    /// <summary>LASzip compressed ASPRS LAS point cloud (.laz).</summary>
    Laz = 18,

    /// <summary>Cloud Optimized Point Cloud (clustered octree in LAZ, .copc.laz).</summary>
    Copc = 19,
}
