---
title: Scene adapter
---

# PclIO.Scene: point clouds to IOScene

`PclIO.Scene` converts a point cloud file or dataset read by PclIO into an `IOScene` `SceneDocument` for viewers and coordination tools. The scene follows the [IOScene contract](https://github.com/IOExtend/IOScene/blob/main/docs/contract.md) and its [key rules](https://github.com/IOExtend/IOScene/blob/main/docs/keys.md): `SceneModel.Key` plus `SceneElement.SourceId` is the identity that may be stored outside the scene, and the numeric ids are local to it.

**Status:** preview. `PclIO.Scene` is released on the `PclIO` release tag with exactly the same version as `PclIO`, and needs `IOScene` 0.1.0 or a later 0.1 version. The engine itself stays a leaf: it references no other IOExtend package, and only this adapter and the command line tool reference IOScene.

## Usage

```csharp
using IOScene;
using PclIO.Scene;

PclSceneResult result = PclSceneReader.Read("site.e57");

foreach (SceneDiagnostic d in result.Diagnostics)
{
    Console.WriteLine($"{d.Severity} {d.Code}: {d.Message}");
}

SceneJson.Save(result.Document, "site.scene.json");
```

`PclSceneReader.Read(PclDocument, PclSceneOptions)` converts a document that is already open; the model key then comes from `PclDocument.FilePath`, or from `PclSceneOptions.ModelKey`. The command line writes the same scene: `pclio export site.e57 site.scene.json` (the format is also inferred from the extension `.scene.json`).

## What the scene holds

The scene has one model. Geometry positions are in metres, right-handed, Z up, in one coordinate space: the scan transform is applied while reading and the positions are scaled by `PclSceneOptions.MetresPerUnit`.

| Scene part | Content |
| --- | --- |
| `SceneModel.Key`, `Name` | `PclSceneOptions.ModelKey`, else the file name with its extension (for a dataset folder, the folder name). |
| `SceneModel` fields | `SourceFormat` is the format id of `PclDocument.Kind` (table below); `Path` is set only when the scene is read from a path; `Producer` is `PclIO.Scene <version>`. |
| Elements | A root `Model` element named like the model, then one `Element` per scan with `ParentId` pointing at the root. `Category` is `Point Cloud`, `TypeName` the format id. |
| `SourceId` | The engine's scan id (`PclScan.Id`), on scan elements only. This derivation does not change; a test pins it. |
| Property sets | One set named `Scan properties` per scan with properties, holding `PclScan.Properties` as text values (`SceneValueKind.Text`). The properties have no machine key yet: the `pcl:` namespace is not registered in the IOScene key registry. |
| Geometry, instances | One non-indexed `Points` geometry per scan with points, placed by one instance with no transform (the positions are already in scene coordinates). Normals are kept when every point of the scan has one. |

### `SourceFormat` ids

| Id | Kind | Id | Kind |
| --- | --- | --- | --- |
| `e57` | E57 | `pts` | PTS |
| `rcp` | ReCap project | `ptx` | PTX |
| `rcs` | ReCap scan | `xyz` | XYZ / text |
| `lgsx` | Leica container | `sbf` | SBF |
| `lgs` | Leica legacy container | `pnts` | 3D Tiles |
| `fls` | FARO scan | `potree` | Potree |
| `srf` | PinPoint | `laz` | LAZ |
| `c3db` | EdgeWise | `copc` | COPC |
| `las` | LAS | `ept` | EPT |
| `pcd` | PCD | `ptg` | Leica PTG |
| `ply` | PLY | `ccbin` | CC BIN |

These ids are used by this producer from its first release; the matching rows in the IOScene `docs/keys.md` registry are added there before the first published release, as the registry rules require.

## Options

| Option | Default | Meaning |
| --- | --- | --- |
| `ModelKey` | null (file name) | The model key; empty is invalid. |
| `MaxPoints` | 1,000,000 | Total point cap across scans; null for no limit. The IOScene contract holds small clouds in memory and puts large point clouds outside the model. Points beyond the cap are reported with `points-capped`. |
| `SubsampleStep` | 1 | Passed to the engine; readers apply it inside their own blocks, so the spacing is approximate. |
| `MetresPerUnit` | 1.0 | Metres in one source coordinate unit, applied to every position. Set it when the source uses another unit, for example 0.3048 for a LAS file in feet. |

## Diagnostics

`PclSceneResult.Diagnostics` lists what the conversion left out of the document it was given and could detect; it is empty when it detected nothing.

| Code | Severity | Meaning |
| --- | --- | --- |
| `points-capped` | Warning | The point limit was reached; the entry names the limit and how many declared points are not in the scene. |
| `colours-dropped` | Info | Per-point colour is not part of IOScene 0.1.0; the entry names how many scans carry colour the scene does not hold. |
| `intensities-dropped` | Info | Per-point intensity is not part of IOScene 0.1.0 (one aggregated entry with the scan count). |
| `classifications-dropped` | Info | Per-point classification is not part of IOScene 0.1.0 (one aggregated entry with the scan count). |
| `normals-incomplete` | Info | Some points of a scan have no normal; the normals of those scans are left out (one aggregated entry with the scan count). |
| `empty-scan` | Info | Scans that were read but yielded no points have no geometry (one aggregated entry with the count). Scans skipped because of the cap are reported by `points-capped` alone. |
| `reader-warning` | Warning | A warning of the engine document (`PclDocument.Warnings`), one entry each. |

## Limits

- Per-point colour, intensity and classification are not representable in IOScene 0.1.0 and are dropped with a diagnostic. Per-vertex colour is a known additive item of the contract whose first user is small point clouds; when IOScene ships it, this adapter can keep the colour.
- The scene is a one-way sink: no source file is rebuilt from it, and the engine's exporters (LAS, XYZ, PTS, JSON) remain the lossless path for points.
- The scan transform is baked into the positions; the instance transform is the identity. A consumer rebases large coordinates as the contract describes.
- Normals are passed through as the engine returns them; they are kept only when every point of a scan has one.
- The source length unit is not decoded: positions are taken as metres unless `MetresPerUnit` says otherwise. Georeference and coordinate frames are not part of IOScene 0.1.0.
- `IfcGuid` is null: point cloud scans have no IFC identity.
