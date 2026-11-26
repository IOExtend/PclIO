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
| `PclFileKind` | `Unknown`, `E57`, `RecapProject`, `RecapScan`, `LeicaLgsx`, `LeicaLgs`, `FaroFls`, `PinPoint`, `EdgeWise`, `Las`, `Pcd`, `Ply`, `Pts`, `Ptx`, `Xyz`, `Sbf`, `Tiles3d`, `Potree`, `Laz`, `Copc`, `Ept`, `LeicaPtg` and `CcBin`. |
| `PclReadOptions` | `MaxPoints` (limit of one read call, null for all, 0 for none), `MaxCoordinateMagnitude` (default 5000, null for no limit; NaN and infinite coordinates are never returned; currently only PinPoint applies it), `EdgeWiseCoordinateScale` (default 0.128 / 32768 metres per unit; the files do not declare it), `SubsampleStep` (1 keeps every point, N keeps about one in N), `Blocks` (range of the scan's blocks in file order, null for all; currently only Leica honours it), `MaxDegreeOfParallelism` (default 1: sequential; above 1 decodes the selected Leica blocks in parallel, keeping file order), `ApplyTransform` (default true), `Strict` (default false: a reader that finds incomplete data fails with `InvalidDataException` instead of adding a warning), `ExcludeFilteredPoints` (default false: points the file marks as filtered are excluded; currently the `FilterBits` channel of the PinPoint sectioned layout), `TextColumns` (explicit column names or layout for delimited text point clouds), `TextDelimiter` (explicit delimiter character for delimited text), `TextCommentPrefix` (prefix for comment lines in delimited text, default `#`) and `TextCoordinateScale` (coordinate scale factor for delimited text, default 1.0). Negative `MaxPoints`, a negative magnitude limit, a negative block start or count, a non-positive or non-finite EdgeWise scale or text coordinate scale, and a step or parallelism below 1 throw `ArgumentOutOfRangeException`. `PclReadOptions.Default` has no limit. |
| `BlockRange` | `StartBlock` (zero-based index of the first block to read) and `BlockCount` (how many blocks at most, null for all from the start on; a range past the last block yields no points, 0 yields none). |

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
  `Las` is recognised by the `LASF` signature with any extension except `.laz` and `.copc.laz`.
  `Laz` is recognised by the `LASF` signature, a valid LASzip VLR and the `.laz` extension.
  `Copc` is recognised by the LAS 1.4 header and the `copc` info VLR with `.copc.laz` or any extension.
  `Pcd` is recognised by its header structure (the documented entries in order) with any extension.
  `Ply` is recognised by its magic word and header structure with any extension.
  `Pts` is recognised by an integer point count header line followed by coordinate lines with any extension, or coordinate lines with the `.pts` extension.
  `Ptx` is recognised by its 10-line header structure (grid dimensions, scanner pose, 4x4 matrix) with any extension.
  `Xyz` is recognised by an explicit column header containing X, Y, Z in any extension, or by numeric coordinate lines with a recognised extension (`.xyz`, `.asc`, `.txt`, `.csv`).
  `Sbf` is recognised by its companion `[SBF]` header structure with any extension, or by the binary signature `0x2A, 0x2A` (`**`) and record size formula (`.sbf`, `.sbf.data`, `.data`).
  `Tiles3d` is recognised by the `pnts` signature, version 1 and matching byte lengths (`.pnts`), the `glTF` signature, version 2 and mesh primitive mode 0 (`.glb`), or JSON containing `asset` and `root` objects (`tileset.json`).
  `Potree` is recognised by `cloud.js` (with `octreeDir` and attribute descriptors) or `metadata.json` (version 2.x and point attributes) in `.js` or `.json`, by `.hrc` hierarchy files with 5-byte node records, or by ancestor `cloud.js`/`metadata.json` next to a `.bin` file.
  `Ept` is recognised by `ept.json` manifest structure (bounds, dataType, schema) or by companion `ept.json` next to octree tiles.
  `LeicaPtg` is recognised by the `PTG\0` tag and magic number `0x928FA3C7` with any extension, or by the `PTG index` header with the `.ptg` extension.
  `CcBin` is recognised by the `CCB2` signature and entity header structure with any extension; a recognised Potree manifest in an ancestor directory takes precedence for `.bin` files.
- `FaroFls` needs an OLE2 compound file with the extension `.fls`, `RecapProject` a ZIP archive with the extension `.rcp`, and
  `PinPoint` a JSON text file with the extension `.smf` or `.srf`.
- The extensions `.c3db` and `.c3ptx` select `EdgeWise` without a content check, so a damaged file with one of these extensions is
  detected as `EdgeWise` and fails with `InvalidDataException` when it is opened.
- A folder is `FaroFls` for a FARO scan folder, `PinPoint` when it holds `ScanProject.json`, or a `.smf` or `.srf` file directly
  inside it, `Tiles3d` when it holds `tileset.json`, `Potree` when it holds `cloud.js` or `metadata.json`, or `Ept` when it holds `ept.json`.

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
| ReCap scan | Stored point count for container version 3.2. For other versions the header count is available and reading points reports the unsupported version. |
| ReCap project | Declared by the project manifest; 0 when the manifest states none. |
| Leica | The number of points the container states, when it states one; otherwise it is computed. The computed value needs the first access to decode all of the point data and caches the result, which can be slow on large files; the access throws `InvalidDataException` for corrupt data. A container that assigns its points to its setups has one scan per setup, each with the id and the name of its setup; otherwise the container is one scan with the id `unified-cloud`. The `Transform` and the `Bounds` of a setup are read from its record and are empty when the record does not hold them. `Blocks` applies per scan in file order; a truncated read (fewer points yielded than selected, via `MaxPoints`) reports no incomplete-data warning. |
| FARO | The number of valid returns, which is counted on the first access by reading the distance data of the scan; `FaroScan.GridSize` is the size of the scan grid (rows times columns). For a folder scan the grid size is limited by the size of the data stream, and `FaroScan.RecordSize` is the size of one sample in bytes. |
| PinPoint | A manifest with a scan position list: declared by the manifest, else derived from the data file, else 0 (not known). A manifest with a data object list: derived from the data file the object refers to, else 0 (not known); locations that share one file each report its full count. |
| EdgeWise | A `.c3db` database node: the count the database declares. A `.c3ptx` file: computed by walking the cluster headers when the file is opened. |
| LAS | The header count (the extended 1.4 count when it states one). It is not checked against the point data until the points are read; a truncated read fails then. |
| PCD | WIDTH times HEIGHT, validated against POINTS when the file is opened. Points with non-finite coordinates are skipped, so a read can yield fewer points. |
| PLY | The vertex count the header declares. It is not checked against the vertex data until the vertices are read; a truncated read fails then. Vertices with non-finite coordinates are skipped, so a read can yield fewer points. |
| PTS | The count declared by the header line, or counted by scanning lines for headerless files. Points with non-finite coordinates are skipped. |
| PTX | Total valid returns across all gridded scans. Empty grid cells (0, 0, 0) and non-finite coordinates are skipped. |
| XYZ | The number of valid data point lines in the file. Non-finite coordinates and comment lines are skipped. |
| SBF | The point count declared in the binary header (and companion header). Points with non-finite coordinates are skipped. |
| 3D Tiles | Declared in the feature table (POINTS_LENGTH) for .pnts tiles, or the POSITION accessor count for .glb point primitives. |
| Potree | Sum of points across all discovered octree node binary files, or declared in metadata.json. |
| LAZ | The header count (the extended 1.4 count when it states one). It is not checked against the chunk data until read. |
| COPC | The header count (the extended 1.4 count when it states one). It is not checked against the chunk data until read. |
| EPT | The point count declared in the manifest, or the sum of points across discovered octree nodes. |
| Leica PTG | Total valid returns across all gridded scans, determined by decoding the column validity bitmasks. |
| CC BIN | The point count declared for the entity. The reader rejects files with a truncated XYZ block or a point count that exceeds the available XYZ data. Points with non-finite coordinates are skipped. Packed point normals are decoded in file versions 41, 42 and 50. A stored zero direction is returned as `PclVector3.Zero`. |

ReCap scans with container version 3.2 report stored point counts from their leaf records. `DeclaredPointCount` holds the count in the file header.
The channel flags in the scan header determine `HasColors`, `HasIntensities` and `HasNormals`. ReCap project scans read and cache those flags on the first successful
request. An unresolved reference reports false for the channel properties. A resolved reference with a damaged or inaccessible header reports a read error.

## Format documents

For sampling plans, `LgsxDocument.GetEncodedPointCount` reads the declared counts in version 22 point block headers without
decompressing the point data. It returns null when the file uses another block layout or has no point blocks.
`PclSceneOptions.PointObserver` receives retained points in scene geometry order, including their source attributes.
`PclSceneOptions.MaxDegreeOfParallelism` passes the requested concurrency to formats that support block decoding.

The E57 reader exposes checksum-verified image blobs through `E57Document.ReadImageBlob`.
`FaroFlsDocument.ReadColorPanorama` returns the scanner-aligned mode 26 RGB raster in an equirectangular projection.
The image is RGB24 in row-major order and has a height of half the requested width. Pixels outside the recorded angles are black.

`E57Document`/`E57Scan`, `RcsScanDocument`/`RcsScan`, `RcpProjectDocument`/`RcpScan`, `LgsxDocument`/`LgsxScan`/`LgsxEntry`/`LgsxSetup`,
`FaroFlsDocument`/`FaroScan`, `PinPointDocument`/`PinPointScan`, `EdgeWiseDocument`/`EdgeWiseScan`, `LasDocument`/`LasScan`, `LazDocument`/`LazScan`, `CopcDocument`/`CopcScan`, `PcdDocument`/`PcdScan`, `PlyDocument`/`PlyScan`, `PtsDocument`/`PtsScan`, `PtxDocument`/`PtxScan`, `XyzDocument`/`XyzScan`, `SbfDocument`/`SbfScan`, `Tiles3dDocument`/`PntsScan`/`GlbScan`, `PotreeDocument`/`PotreeScan`, `EptDocument`/`EptScan`, `PtgDocument`/`PtgScan` and `CcBinDocument`/`CcBinScan` are the readers behind
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
| `PclLasExporter` / `PclLasOptions` / `LasVersion` | `Export(doc, stream, options, lasOptions)` writes ASPRS LAS 1.2 (point format 2) or 1.4 (point formats 6 to 8), with a configurable scale (default 1 mm) and offset (default the minimum of the data). A projected `EPSG:NNNN` coordinate system of the source is written as a GeoTIFF record for LAS 1.2 when asked; LAS 1.4 requires WKT, which the exporter does not produce, so a 1.4 file carries no coordinate system record. Returns the number of points written. |
| `PclXyzExporter` | `Export(doc, writer, options)` writes `X Y Z R G B`, or `X Y Z I`, or `X Y Z` per point. |
| `PclPtsExporter` | `Export(doc, writer, options)` writes the point count, then `X Y Z Intensity R G B` per point. |
| `PclJsonExporter` | `Export(doc, writer, indented, maxPoints)` writes metadata, scans and, when `maxPoints` is above zero, sample points. |

Each exporter has an overload with `out long skippedPoints`, the number of points skipped because a coordinate was NaN or infinite.
Output is culture independent; XYZ and PTS end lines with `\n`, and JSON uses the line ends of the platform. LAS and PTS read the source twice; the others stream it once. Normals are not exported.

## Scene adapter

The `PclIO.Scene` package converts a document into an IOScene `SceneDocument`. It is a separate package so the engine stays a leaf;
the [scene adapter guide](scene.md) describes the mapping, the diagnostics and the limits.

| Type | Use |
|---|---|
| `PclSceneReader` | Static entry point. `Read(path, options)` opens the file with `PclFile.Open` and converts it; `Read(document, options)` converts an opened document. Both return a `PclSceneResult`. |
| `PclSceneOptions` | `ModelKey` (null uses the file name), `MaxPoints` (default 1,000,000, null for no limit), `SubsampleStep` (default 1) and `MetresPerUnit` (default 1.0). A negative limit, a step below 1 or a non-positive or non-finite factor throws `ArgumentOutOfRangeException`. |
| `PclSceneResult` | `Document` (the `SceneDocument`), `Diagnostics` (what the conversion left out), `ScanCount`, `ScansWithPoints`, `PointCount` and `MetresPerUnit`. |

## Command line

`PclIO.Cli` installs the tool `pclio`. The `Program` class exposes `Run(args, stdout, stderr)` and the exit-code constants
`ExitOk`, `ExitUsage`, `ExitFileNotFound`, `ExitFormatError` and `ExitUnexpected`; see [Getting started](getting-started.md) for the commands.
