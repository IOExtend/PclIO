using IOScene;

namespace PclIO.Scene;

/// <summary>The scene made from one point cloud document by <see cref="PclSceneReader"/>, what was lost on the way, and counts.</summary>
public sealed class PclSceneResult
{
    /// <summary>The scene: one model with one element per scan, point geometry in metres, and the scan properties as text.</summary>
    public required SceneDocument Document { get; init; }

    /// <summary>
    /// What the conversion left out of the document it was given and could detect; empty when it detected nothing.
    /// Per-point colour, intensity and classification are not part of IOScene 0.1.0 and are always reported when present.
    /// </summary>
    public IReadOnlyList<SceneDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>The number of scans in the document, each one element of the scene.</summary>
    public int ScanCount { get; init; }

    /// <summary>The number of scans with at least one point in the scene.</summary>
    public int ScansWithPoints { get; init; }

    /// <summary>The number of points in the scene.</summary>
    public long PointCount { get; init; }

    /// <summary>The metres-per-unit factor used: <see cref="PclSceneOptions.MetresPerUnit"/>.</summary>
    public double MetresPerUnit { get; init; }
}
