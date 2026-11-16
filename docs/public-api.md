---
title: Public API
---

# Public API

The `PclIO` package exposes the types listed here. Format detection, container helpers, byte helpers and path handling are
`internal`; they can change in any release and are visible only to the test project through `InternalsVisibleTo`.
The format readers (see [Format documents](#format-documents)) are public, but each describes one format, so their
format-specific members can change when the support for that format changes.

## Opening a file

| Type | Use |
|---|---|
| `PclFile` | Static entry point. `DetectKind(path)` and `DetectKind(stream, extension)` return a `PclFileKind`; `Open(path, options)` detects the format and returns a `PclDocument`. |
| `PclFileKind` | `Unknown`, `E57`, `RecapProject`, `RecapScan`, `LeicaLgsx`, `LeicaLgs`, `FaroFls`, `PinPoint` and `EdgeWise`. |
| `PclReadOptions` | `MaxPoints` (limit of one read call, null for all, 0 for none), `MaxCoordinateMagnitude` (default 5000, null for no limit; NaN and infinite coordinates are never returned; currently only PinPoint applies it), `SubsampleStep` (1 keeps every point, N keeps about one in N), `ApplyTransform` (default true), `Strict` (default false: a reader that finds incomplete data fails with `InvalidDataException` instead of adding a warning) and `ExcludeFilteredPoints` (default false: points the file marks as filtered are excluded; currently the `FilterBits` channel of the PinPoint sectioned layout). Negative `MaxPoints`, a negative magnitude limit and a step below 1 throw `ArgumentOutOfRangeException`. `PclReadOptions.Default` has no limit. |

`Open` throws `FileNotFoundException` for a missing path, `DirectoryNotFoundException` when a directory the reader needs is
missing or disappears while reading, `NotSupportedException` for an unrecognised format or an unsupported feature, and
`InvalidDataException` for a malformed or truncated file. `DetectKind(path)` throws `FileNotFoundException` when the path is
neither a file nor a directory. `DetectKind(stream, extension)` needs a seekable stream, restores its position, and throws
`NotSupportedException` otherwise.

### Format detection

Detection finds the kind from the content of the file, its extension or the layout of a folder, and returns
`PclFileKind.Unknown` for anything else. Extensions are compared without regard to case; for the stream overload the extension
is the argument, which may also be a file name.

- `E57`, `RecapScan` and the Leica kinds are recognised by content and accepted with any extension; `LeicaLgs` is chosen for the
  extension `.lgs` and `LeicaLgsx` for any other. `EdgeWise` is also recognised by content in a file with another extension.
- `FaroFls` needs an OLE2 compound file with the extension `.fls`, `RecapProject` a ZIP archive with the extension `.rcp`, and
  `PinPoint` a JSON text file with the extension `.smf` or `.srf`.
- The extensions `.c3db` and `.c3ptx` select `EdgeWise` without a content check, so a damaged file with one of these extensions is
  detected as `EdgeWise` and fails with `InvalidDataException` when it is opened.
- A folder is `FaroFls` for a FARO scan folder, and `PinPoint` when it holds `ScanProject.json`, or a `.smf` or `.srf` file directly
  inside it.

`Open` throws `NotSupportedException` for `Unknown`. For `EdgeWise`, the extension `.c3ptx` selects the single cluster file reader
and any other extension the point database reader.

## Reading

| Type | Use |
|---|---|
| `PclDocument` | An opened file (`IDisposable`). `Kind`, `FilePath`, `FileSize`, `Scans`, `TotalPointCount`, `OverallBounds`, `CoordinateSystem`, `Metadata`, `Warnings` and `ReadAllPoints(options)`, which streams the points of all scans in order and applies `MaxPoints` across all scans. `TotalPointCount` is the sum of the scans' `PointCount`; it saturates at `long.MaxValue` and ignores negative counts. |
| `PclScan` | One scan or setup. `Id`, `Name`, `PointCount` (see below for what each reader reports), `Bounds`, `Transform`, `HasColors`, `HasNormals`, `HasIntensities`, `Properties` and `ReadPoints(options)`. |
| `PclPoint` | One point: `Position` (`X`, `Y`, `Z`), `Color`, `Intensity` (scale is format specific), `Normal`, `Classification` and `Index`, a running number within one scan's read call (it starts again at 0 for each scan of `ReadAllPoints`). Missing channels are null. |
| `PclColor`, `PclVector3`, `PclBounds`, `PclTransform` | Value types. `PclBounds.Empty` means unknown bounds. `PclTransform` has `Translation`, `Rotation` (stored for information, never applied), `Scale` and an optional `Matrix4x4` of 16 values; `TransformPoint` applies scale and translation, or the matrix when there is one. |

`PclDocument.Warnings` lists non-fatal problems found while reading. The library never writes to the console.

### What `PointCount` means

`PclScan.PointCount` is a number the reader reports, not a count of the points that `ReadPoints` will return. Some readers
drop points that fail a plausibility rule (see the README), so a scan can yield fewer points than it reports.

| Reader | `PointCount` |
|---|---|
| E57 | Declared by the file. It is not checked against the point data until the points are read. Points that the validity flags mark as invalid are skipped, so a read can yield fewer points. |
| ReCap scan | Declared in the header. It is not checked against the node table. The node layout of major version 3 is decoded; a file of another version is listed but its points cannot be read (`RcsScanDocument.DecodedNodeLayoutVersions` names the decoded versions). |
| ReCap project | Declared by the project manifest; 0 when the manifest states none. |
| Leica | The number of points the container states, when it states one; otherwise it is computed. The computed value needs the first access to decode all of the point data and caches the result, which can be slow on large files; the access throws `InvalidDataException` for corrupt data. A container that assigns its points to its setups has one scan per setup, each with the id and the name of its setup; otherwise the container is one scan with the id `unified-cloud`. The `Transform` and the `Bounds` of a setup are read from its record and are empty when the record does not hold them. |
| FARO | The number of valid returns, which is counted on the first access by reading the distance data of the scan; `FaroScan.GridSize` is the size of the scan grid (rows times columns). For a folder scan the grid size is limited by the size of the data stream, and `FaroScan.RecordSize` is the size of one sample in bytes. |
| PinPoint | A manifest with a scan position list: declared by the manifest, else derived from the data file, else 0 (not known). A manifest with a data object list: derived from the data file the object refers to, else 0 (not known); locations that share one file each report its full count. |
| EdgeWise | A `.c3db` database node: the count the database declares. A `.c3ptx` file: computed by walking the cluster headers when the file is opened. |

`HasColors`, `HasIntensities` and `HasNormals` follow what the file contains, except for the scans of a ReCap project, where all three
are true when the `.rcs` file was found and false otherwise, without the file being opened. A ReCap scan reads the channels
from the node table, so they are known after the node table has been read; a node whose points hold no colour gives its points
no colour.

## Format documents

`E57Document`/`E57Scan`, `RcsScanDocument`/`RcsScan`, `RcpProjectDocument`/`RcpScan`, `LgsxDocument`/`LgsxScan`/`LgsxEntry`/`LgsxSetup`,
`FaroFlsDocument`/`FaroScan`, `PinPointDocument`/`PinPointScan` and `EdgeWiseDocument`/`EdgeWiseScan` are the readers behind
`PclFile.Open`. Each has a static `Open` method and format-specific properties (for example the E57 version or the FARO scanner model).
Their XML documentation states what each reader supports. Prefer `PclFile.Open`, which detects the format and chooses the reader.

## Limits and filters

Readers check the sizes and counts a file declares against fixed limits and reject a file that exceeds one with
`InvalidDataException`. A few readers also drop points without an error or a warning. The numbers and rules are listed under
Limits and Data filters in the [README](https://github.com/IOExtend/PclIO/blob/main/README.md). A few cases differ from
`InvalidDataException`; the README lists them.

## Paths stored in files

File names read from ReCap, PinPoint and EdgeWise project files are resolved only inside the project's own folder. The check is
lexical: symbolic links and junctions are not resolved, so a link inside the project folder counts as part of it. A name that
is empty, rooted, contains invalid characters or resolves outside the folder is rejected.

## Export

| Type | Use |
|---|---|
| `PclLasExporter` | `Export(doc, stream, options)` writes ASPRS LAS 1.2, point format 2, with a 1 mm scale. Returns the number of points written. |
| `PclXyzExporter` | `Export(doc, writer, options)` writes `X Y Z R G B`, or `X Y Z I`, or `X Y Z` per point. |
| `PclPtsExporter` | `Export(doc, writer, options)` writes the point count, then `X Y Z Intensity R G B` per point. |
| `PclJsonExporter` | `Export(doc, writer, indented, maxPoints)` writes metadata, scans and, when `maxPoints` is above zero, sample points. |

Each exporter has an overload with `out long skippedPoints`, the number of points skipped because a coordinate was NaN or infinite.
Output is culture independent; XYZ and PTS end lines with `\n`, and JSON uses the line ends of the platform. LAS and PTS read the source twice; the others stream it once. Normals are not exported.

## Command line

`PclIO.Cli` installs the tool `pclio`. The `Program` class exposes `Run(args, stdout, stderr)` and the exit-code constants
`ExitOk`, `ExitUsage`, `ExitFileNotFound`, `ExitFormatError` and `ExitUnexpected`; see [Getting started](getting-started.md) for the commands.
