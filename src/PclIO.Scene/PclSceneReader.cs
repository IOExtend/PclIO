using System.Globalization;
using System.Reflection;
using IOScene;

namespace PclIO.Scene;

/// <summary>Converts a point cloud file, or a <see cref="PclDocument"/> read by PclIO, into a <see cref="SceneDocument"/>.</summary>
/// <remarks>
/// <para>
/// The scene has one <see cref="SceneModel"/>: <c>Key</c> and <c>Name</c> are <see cref="PclSceneOptions.ModelKey"/>, else the
/// file name; <c>SourceFormat</c> is the format id of <see cref="PclDocument.Kind"/> (see <c>docs/scene.md</c>). One
/// <see cref="SceneElement"/> per scan holds the scan's properties as text; its points become one non-indexed
/// <see cref="SceneTopology.Points"/> geometry, placed by one instance.
/// </para>
/// <para>
/// Point positions are read with the scan transform applied and scaled by <see cref="PclSceneOptions.MetresPerUnit"/>, so the
/// scene is in metres with one coordinate space. Per-point colour, intensity and classification are not part of IOScene 0.1.0
/// and are left out with a diagnostic; normals are kept when every point of a scan has one.
/// </para>
/// </remarks>
public static class PclSceneReader
{
    private static readonly string Producer = "PclIO.Scene "
        + typeof(PclSceneReader).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

    /// <summary>Reads the point cloud file or dataset at <paramref name="path"/> with <see cref="PclFile.Open(string, PclReadOptions?)"/> and converts it.</summary>
    /// <param name="path">The point cloud file or dataset folder.</param>
    /// <param name="options">Conversion options; null uses the defaults.</param>
    /// <returns>The scene with a report of the conversion.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> or <see cref="PclSceneOptions.ModelKey"/> is empty.</exception>
    /// <exception cref="NotSupportedException">The format is not recognized or uses an unsupported feature.</exception>
    /// <exception cref="InvalidDataException">The file is malformed or truncated.</exception>
    public static PclSceneResult Read(string path, PclSceneOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string key = options?.ModelKey ?? Path.GetFileName(path);
        if (key.Length == 0)
        {
            throw new ArgumentException("PclSceneOptions.ModelKey must not be empty; leave it null to use the file name.", nameof(options));
        }

        using var document = PclFile.Open(path);
        return Convert(document, key, path, options ?? PclSceneOptions.Default);
    }

    /// <summary>
    /// Converts an opened document. <see cref="PclSceneOptions.ModelKey"/> replaces the key derived from
    /// <see cref="PclDocument.FilePath"/>; a document whose file path has no file name needs it.
    /// </summary>
    /// <param name="document">The document, for example from <see cref="PclFile.Open(string, PclReadOptions?)"/>.</param>
    /// <param name="options">Conversion options; null uses the defaults.</param>
    /// <returns>The scene with a report of the conversion.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    /// <exception cref="ArgumentException">The model key is empty and the document has no file name to derive one from.</exception>
    public static PclSceneResult Read(PclDocument document, PclSceneOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= PclSceneOptions.Default;
        string key = options.ModelKey ?? Path.GetFileName(document.FilePath);
        if (string.IsNullOrEmpty(key))
        {
            throw new ArgumentException("A document without a file name needs PclSceneOptions.ModelKey.", nameof(options));
        }

        return Convert(document, key, null, options);
    }

    private static PclSceneResult Convert(PclDocument document, string key, string? path, PclSceneOptions options)
    {
        string formatId = FormatId(document.Kind);
        var scene = new SceneDocument { Title = key };
        var diagnostics = new List<SceneDiagnostic>();

        scene.Models.Add(new SceneModel
        {
            Id = 1,
            Key = key,
            Name = key,
            Path = path,
            SourceFormat = formatId,
            Producer = Producer,
        });

        var root = new SceneElement
        {
            Id = 1,
            ModelId = 1,
            Kind = SceneNodeKind.Model,
            Name = key,
        };
        scene.Elements.Add(root);

        long remaining = options.MaxPoints ?? long.MaxValue;
        long totalRead = 0;
        int scansWithPoints = 0;
        int scansWithColors = 0;
        int scansWithIntensities = 0;
        int scansWithClassifications = 0;
        int scansWithIncompleteNormals = 0;
        int emptyScans = 0;
        double scale = options.MetresPerUnit;

        for (int i = 0; i < document.Scans.Count; i++)
        {
            PclScan scan = document.Scans[i];
            var element = new SceneElement
            {
                Id = 2 + i,
                ModelId = 1,
                ParentId = 1,
                Kind = SceneNodeKind.Element,
                SourceId = scan.Id,
                Name = string.IsNullOrEmpty(scan.Name) ? scan.Id : scan.Name,
                Category = "Point Cloud",
                TypeName = formatId,
            };
            scene.Elements.Add(element);

            if (scan.Properties.Count > 0)
            {
                var set = new ScenePropertySet
                {
                    Id = scene.PropertySets.Count + 1,
                    Name = "Scan properties",
                };
                foreach (var pair in scan.Properties)
                {
                    set.Properties.Add(new SceneProperty
                    {
                        Name = pair.Key,
                        Value = SceneValue.FromText(pair.Value),
                    });
                }
                scene.PropertySets.Add(set);
                element.PropertySetIds.Add(set.Id);
            }

            if (remaining <= 0) continue;

            var readOptions = new PclReadOptions
            {
                MaxPoints = remaining,
                SubsampleStep = options.SubsampleStep,
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
                ApplyTransform = true,
            };

            var vertices = new List<Vector3d>();
            var normals = new List<Vector3d>();
            bool hasColors = false, hasIntensities = false, hasClassifications = false;
            bool anyNormal = false, allNormals = true;
            int geometryId = scene.Geometries.Count + 1;

            foreach (PclPoint point in scan.ReadPoints(readOptions))
            {
                vertices.Add(new Vector3d(point.X * scale, point.Y * scale, point.Z * scale));
                options.PointObserver?.Invoke(geometryId, point);
                if (point.Color.HasValue) hasColors = true;
                if (point.Intensity.HasValue) hasIntensities = true;
                if (point.Classification.HasValue) hasClassifications = true;
                if (point.Normal is { } normal)
                {
                    anyNormal = true;
                    normals.Add(new Vector3d(normal.X, normal.Y, normal.Z));
                }
                else
                {
                    allNormals = false;
                }
            }

            remaining -= vertices.Count;
            totalRead += vertices.Count;
            if (hasColors) scansWithColors++;
            if (hasIntensities) scansWithIntensities++;
            if (hasClassifications) scansWithClassifications++;

            if (vertices.Count == 0)
            {
                emptyScans++;
                continue;
            }

            scansWithPoints++;
            var geometry = new SceneGeometry
            {
                Id = geometryId,
                Topology = SceneTopology.Points,
                Vertices = vertices,
            };
            if (anyNormal && allNormals)
            {
                geometry.Normals = normals;
            }
            else if (anyNormal)
            {
                scansWithIncompleteNormals++;
            }
            scene.Geometries.Add(geometry);
            scene.Instances.Add(new SceneInstance
            {
                ElementId = element.Id,
                GeometryId = geometry.Id,
                Name = element.Name,
            });
        }

        if (options.MaxPoints.HasValue && totalRead >= options.MaxPoints.Value)
        {
            long omitted = document.TotalPointCount - totalRead;
            if (omitted > 0)
            {
                diagnostics.Add(new SceneDiagnostic(SceneSeverity.Warning, "points-capped",
                    string.Create(CultureInfo.InvariantCulture,
                        $"The point limit of {options.MaxPoints.Value} was reached; {omitted} declared points are not in the scene.")));
            }
        }
        if (scansWithColors > 0)
        {
            diagnostics.Add(new SceneDiagnostic(SceneSeverity.Info, "colours-dropped",
                string.Create(CultureInfo.InvariantCulture,
                    $"Per-point colour is not part of IOScene 0.1.0; {scansWithColors} scans carry colour the scene does not hold.")));
        }
        if (scansWithIntensities > 0)
        {
            diagnostics.Add(new SceneDiagnostic(SceneSeverity.Info, "intensities-dropped",
                string.Create(CultureInfo.InvariantCulture,
                    $"Per-point intensity is not part of IOScene 0.1.0; {scansWithIntensities} scans carry intensity the scene does not hold.")));
        }
        if (scansWithClassifications > 0)
        {
            diagnostics.Add(new SceneDiagnostic(SceneSeverity.Info, "classifications-dropped",
                string.Create(CultureInfo.InvariantCulture,
                    $"Per-point classification is not part of IOScene 0.1.0; {scansWithClassifications} scans carry classification the scene does not hold.")));
        }
        if (scansWithIncompleteNormals > 0)
        {
            diagnostics.Add(new SceneDiagnostic(SceneSeverity.Info, "normals-incomplete",
                string.Create(CultureInfo.InvariantCulture,
                    $"{scansWithIncompleteNormals} scans have points without a normal; their normals are left out.")));
        }
        if (emptyScans > 0)
        {
            diagnostics.Add(new SceneDiagnostic(SceneSeverity.Info, "empty-scan",
                string.Create(CultureInfo.InvariantCulture,
                    $"{emptyScans} scans yielded no points and have no geometry in the scene.")));
        }
        foreach (string warning in document.Warnings)
        {
            diagnostics.Add(new SceneDiagnostic(SceneSeverity.Warning, "reader-warning", warning));
        }

        return new PclSceneResult
        {
            Document = scene,
            Diagnostics = diagnostics,
            ScanCount = document.Scans.Count,
            ScansWithPoints = scansWithPoints,
            PointCount = totalRead,
            MetresPerUnit = scale,
        };
    }

    /// <summary>The registered-style id of the format of the content, used as <c>SceneModel.SourceFormat</c> and the element type name.</summary>
    private static string FormatId(PclFileKind kind) => kind switch
    {
        PclFileKind.E57 => "e57",
        PclFileKind.RecapProject => "rcp",
        PclFileKind.RecapScan => "rcs",
        PclFileKind.LeicaLgsx => "lgsx",
        PclFileKind.LeicaLgs => "lgs",
        PclFileKind.FaroFls => "fls",
        PclFileKind.PinPoint => "srf",
        PclFileKind.EdgeWise => "c3db",
        PclFileKind.Las => "las",
        PclFileKind.Pcd => "pcd",
        PclFileKind.Ply => "ply",
        PclFileKind.Pts => "pts",
        PclFileKind.Ptx => "ptx",
        PclFileKind.Xyz => "xyz",
        PclFileKind.Sbf => "sbf",
        PclFileKind.Tiles3d => "pnts",
        PclFileKind.Potree => "potree",
        PclFileKind.Laz => "laz",
        PclFileKind.Copc => "copc",
        PclFileKind.Ept => "ept",
        PclFileKind.LeicaPtg => "ptg",
        PclFileKind.CcBin => "ccbin",
        _ => throw new NotSupportedException($"No scene format id is described for {kind}."),
    };
}
