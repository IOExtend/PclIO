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
}
