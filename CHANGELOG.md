# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Read legacy Leica LGS octree points with source RGB and intensity.

### Changed

- Identify Leica LGS and LGSx from their container footer and report unsupported legacy point encoding explicitly.

## [0.1.2] - 2026-11-26

### Added

- Read checksum-verified E57 image blobs and scanner-aligned FARO RGB panoramas.
- Expose retained source point attributes and decoder concurrency through scene adapter options.

### Changed

- Leica LGSX version 22 decodes differential Morton positions, native RGB and intensity, retaining duplicate returns.
- FARO FLS distance mode 26 decodes both range-grid halves with optional Morton RGB and intensity.
- ReCap version 3.2 decodes stored point records with RGB, intensity and normals. ReCap project scans report their referenced scan's channels.

## [0.1.1] - 2026-11-25

### Added

- `PclIO.Scene` scene adapter: `PclSceneReader.Read` turns a point cloud file or an opened `PclDocument` into an IOScene `SceneDocument` in metres — one model, one element per scan with the engine's scan id as `SourceId`, non-indexed `Points` geometry with normals when every point of a scan has one, and the scan properties as text. `PclSceneOptions` sets the model key, a total point cap (default 1,000,000, the IOScene contract holds small clouds), a subsampling step and a metres-per-unit factor; `PclSceneResult` reports the scene, the counts and diagnostics for what was left out (per-point colour, intensity and classification are not part of IOScene 0.1.0). The engine stays a leaf: only the adapter references IOScene (`[0.1.0, 0.2.0-0.0)`), and it pins the engine exactly because both ship on one tag.
- Command line: `pclio export <input> <output> --format scene` (also inferred from the extension `.scene.json`) writes the IOScene scene JSON, prints the conversion diagnostics on standard error, keeps at most 1,000,000 points unless `--limit` is given and rejects `--no-transform`.
- Read packed CC BIN point normals in file versions 41, 42 and 50.
- CC BIN (`*.bin`, `CCB2`) reader supporting single and multi-cloud entity datasets with 32-bit floating point Cartesian XYZ coordinates, global shift and scale vectors, 8-bit RGB colour arrays, 4x4 coordinate transform matrices, and scalar fields (including intensity). Added `CcBinDocument`, `CcBinScan`, and `PclFileKind.CcBin`.
- Leica PTG (`*.ptg`) gridded point cloud reader supporting binary scan files with `PTG\0` signature, magic number `0x928FA3C7`, a binary TLV metadata header (`%%version`, `%%cols`, `%%rows`, `%%properties`, `%%transform` and name records), a uint64 column offset array, and column validity bitmasks (most significant bit first) with single or double precision Cartesian XYZ coordinates, intensity and RGB colour. The scanner pose matrix is transposed from the PTG row-vector convention on load. Top-level ASCII index files (`.ptg`) referencing binary scan files are aggregated into multi-scan documents. Added `PtgDocument`, `PtgScan`, and `PclFileKind.LeicaPtg`.
- Entwine Point Tile (EPT) reader supporting dataset manifests (`ept.json`), octree hierarchy traversal (`ept-hierarchy/`) with sub-hierarchy page resolution and cycle detection, and point data tile streaming (`ept-data/`) across `"laszip"`, `"binary"`, and `"zstandard"` compression formats. Added `EptDocument`, `EptScan`, `EptKey`, `EptNode`, and `EptDimension`, with spatial bounding box queries (`FindNodes`, `ReadPoints`) and schema-driven dimension extraction.

### Changed

- Leica containers that assign their points to their setups now open as one scan per setup, with the id and the name of the
  setup. A container that does not assign its points to its setups still opens as one scan, with a warning.
- The pose and the bounds of a Leica setup are read from the record of the setup and applied to its points, unless
  `ApplyTransform` is false. A record that does not hold usable values is left unread, and the setup keeps an unknown pose.
- The point count of a Leica scan is taken from the container when it states one, so that reading it does not decode the
  point data. Only a container without that number still needs the decoding.
- The Leica reader counts the data it cannot use (the points that are dropped as padding, the compressed streams that are
  skipped and the point data that ends at an unknown block) and reports it in the warnings of the document. The new
  `PclReadOptions.Strict` makes such a read fail with `InvalidDataException` instead.
- The point count of a FARO scan is now the number of valid returns, which is what a full read returns. The size of the
  scan grid is available as `FaroScan.GridSize`.
- The layout of the samples of a FARO scan folder is derived from the size of its data stream and the size of the grid, and
  the scale of the intensity from the values that are stored. The warning about an assumed layout is only made when the
  sizes do not fit.
- The node layout of a ReCap scan is described in one place per container version. A file of a version without a described
  layout is rejected with a message that names the version and the described ones.
- A ReCap node payload whose byte count is not a whole multiple of a known point record stride (as in BLK360 exports,
  container 3.2, whose records are variable-length) is rejected with a message that names the container version instead of
  being decoded with a guessed stride.
- The channels of a ReCap scan follow from the file: the size of the payload of the nodes tells whether the points hold a
  colour, an intensity and a normal. `HasColors`, `HasIntensities` and `HasNormals` are no longer constants.
- The transform of a ReCap project scan node is applied to its points unless `ApplyTransform` is false; when the manifest
  states no transform, the transform of the scan file is used.
- The per-point flags (`FilterBits`) of the PinPoint sectioned layout are read and validated. The new
  `PclReadOptions.ExcludeFilteredPoints` excludes the points with a non-zero flag; by default every stored point is returned.
- The normals (`Normals`) of the PinPoint sectioned layout are read when the file holds them, and `HasNormals` reports
  what the file contains.
- The PinPoint coordinate plausibility rule is now `PclReadOptions.MaxCoordinateMagnitude` (default 5000, null for no
  limit); non-finite coordinates are still never returned. A full read reports how many points it dropped in the warnings
  of the document, and fails instead with `PclReadOptions.Strict`.
- The point count of a PinPoint location of a manifest with a data object list is taken from the data file the object
  refers to, as for manifests with a scan position list. Locations that share one file each report its full count.
- The Snappy decoder now keeps a window of up to 8 MiB per stream (the declared uncompressed length, capped), instead of
  a fixed 1 MiB, so streams with copies further back decode within bounded memory.
- Scaled-integer Cartesian coordinates of E57 scans are decoded (raw value times scale plus offset, with the binary width
  from the range of the field).
- Spherical coordinates of E57 scans are converted to Cartesian points as the standard defines; scaled integers work for
  the spherical components too.
- Points that the E57 validity flags mark as invalid are skipped, and flagged colour or intensity channels are left empty.
  Colour with a wider Integer range from zero (16 or 32 bits) and intensity as a wider Integer, a Float or a scaled
  integer are decoded; other shapes stay unread.
- The E57 reader decodes plain bit-packing only and names any other codec of the scan in the rejection message. The point
  prototype limit is the packet header bound of 65535 fields.
- The Zstandard decoder is faster on Huffman-heavy data: the four Huffman streams of a block now decode interleaved
  instead of one after another, so independent bit readers overlap in the processor; the per-symbol mask is hoisted out
  of the bit reader loop. No unsafe code, no weakened checks; the benchmark in `ZstdBenchmarkTests` shows the speed
  next to the reference decoder (about 1.7 times slower on the benchmark payload, down from about 2).
- The EdgeWise cluster position scale is now `PclReadOptions.EdgeWiseCoordinateScale`, so the caller states it; the
  default keeps the long-standing assumption (32768 units = 0.128 m).
- The LAS exporter writes 1.4 with point formats 6 to 8 (`PclLasOptions` selects version, format, scale and offset);
  1.2 with point format 2 stays the default and is byte-identical to before. A projected `EPSG:NNNN` coordinate system
  of the source is written as a GeoTIFF record for LAS 1.2 when asked; LAS 1.4 point formats 6 and above require WKT,
  which the exporter does not produce, so a 1.4 file carries no coordinate system record.
- LAS files are read (`PclFileKind.Las`): versions 1.0 to 1.4 with point formats 0 to 10, the legacy and the extended
  1.4 point counts, scale and offset, and the coordinate system records when present. GPS time, return numbers, scan
  angles, point source ids, near infrared and waveform packet descriptors are not interpreted.
- PCD files are read (`PclFileKind.Pcd`): ASCII, binary and binary compressed (LZF, decoded by a decoder of its own)
  point data with the declared fields, organised and unorganised clouds, and the viewpoint as metadata. Points with
  non-finite coordinates are not returned.
- PLY files are read (`PclFileKind.Ply`): ASCII and binary little and big endian vertices with positions, 8-bit
  colour, intensity and normals by conventional names; faces and other elements are ignored. Vertices with
  non-finite coordinates are not returned.
- PTS files are read (`PclFileKind.Pts`): Leica Cyclone ASCII exchange point clouds with or without an initial
  point count line; positions, intensity, 8-bit colour and normals across 3, 4, 6, 7, 8, 9 and 10 column layouts.
  Points with non-finite coordinates are not returned.
- PTX files are read (`PclFileKind.Ptx`): Leica Cyclone gridded ASCII exchange point clouds containing one or
  more scans per file. Reads 10-line headers (columns, rows, scanner position, scanner orientation axes, 4x4
  transformation matrix) and points across 3, 4, 6 and 7 column layouts (with intensity and 8-bit RGB colour).
  Empty grid cells (coordinates 0, 0, 0) and non-finite coordinates are skipped. Scans apply their 4x4 transformation
  matrices unless `ApplyTransform` is false.
- Delimited text point clouds are read (`PclFileKind.Xyz`): `.xyz`, `.asc`, `.txt` and `.csv` files using comma,
  semicolon, tab or whitespace delimiters. Reads column layouts from a header line (e.g. `x,y,z,nx,ny,nz`), explicit
  `PclReadOptions.TextColumns`, or column count heuristics across 3, 4, 6, 7, 8, 9 and 10 column layouts (with intensity,
  8-bit RGB colour or unit normals, and alpha). Options for `TextColumns`, `TextDelimiter`, `TextCommentPrefix` and
  `TextCoordinateScale`. Comment lines (`#`, `//` or `TextCommentPrefix`) and points with non-finite coordinates are skipped.
- Simple Binary Format point clouds are read (`PclFileKind.Sbf`): binary files (`.sbf.data`, `.data`) and companion
  INI-style header text files (`.sbf`). Reads 32-bit floating point XYZ coordinates and optional 32-bit scalar fields
  mapped to intensity, 8-bit RGB colour or unit normals. Big-endian and little-endian byte orders supported. Applies
  global shift and scale transformations when `ApplyTransform` is true. Points with non-finite coordinates are skipped.
- OGC 3D Tiles point clouds and tilesets are read (`PclFileKind.Tiles3d`):
  `.pnts` tile files (version 1) with float32 or 16-bit quantised positions (`POSITION_QUANTIZED`), RGB, RGBA,
  RGB565 or constant RGBA colour, float32 or octahedral oct16p normals (`NORMAL_OCT16P`), relative-to-center
  offsets (`RTC_CENTER`), and batch table scalar properties (`Intensity`, `Classification`);
  glTF 2.0 binary point clouds (`.glb`) with mesh primitive mode 0 (POINTS), reading `POSITION`, `COLOR_0`,
  `NORMAL`, and `INTENSITY` accessors with standard component types and node transforms;
  3D Tiles tilesets (`tileset.json`) or directories containing them, traversing hierarchical tile trees
  and composing 4x4 transformation matrices for referenced point cloud tiles.
- Potree octree point cloud datasets are read (`PclFileKind.Potree`):
  Potree 1.x datasets with `cloud.js` manifest, reading bounding boxes, coordinate scale/offset, point attributes (`POSITION_CARTESIAN`, `COLOR_PACKED`, `RGB`, `INTENSITY`, `CLASSIFICATION`, `NORMAL_SPHEREMAPPED`, `NORMAL_OCT16`, `NORMAL_FLOATS`), node binary files (`.bin`), and hierarchy files (`.hrc`);
  Potree 2.x datasets with `metadata.json` manifest and `octree.bin` binary point streams with custom attribute schemas;
  Opening supported from manifest files (`cloud.js`, `metadata.json`), hierarchy files (`.hrc`), node binary files (`.bin` with companion manifest in parent directories), or dataset directories.
- LASzip compressed ASPRS LAS point clouds are read (`*.laz`, `PclFileKind.Laz`):
  Point formats 0 to 3 decoded via clean-room arithmetic decoder and integer decompressor, supporting pointwise chunked compression (compressor 2) and pointwise uncompressed records (compressor 1);
  Spatial coordinates, intensity, classification, and 8-bit RGB colour; coordinate scale and offset applied;
  CRS read from GeoTIFF key directory or OGC WKT VLRs;
  Chunk table offsets and per-chunk point counts validated against file boundaries.
- Cloud Optimized Point Clouds are read (`*.copc.laz`, `PclFileKind.Copc`):
  Reads octree hierarchy pages (root and child pages) with cycle detection and boundary checks;
  Octree voxel bounding boxes calculated from center and halfsize metadata;
  Spatial querying by bounding box (`FindNodes`, `ReadPoints(bounds)`) and by specific node (`ReadPoints(node)`);
  LAS point formats 0 to 3 decompressed via LASzip arithmetic decoding;
  LAS 1.4 point formats 6 to 8 read anchor points with coordinates, intensity, classification, and 8-bit RGB colour.
- Leica scans read a block range with opt-in parallel decoding: the new `PclReadOptions.Blocks` selects the blocks of the
  scan in file order (per scan) and the new `PclReadOptions.MaxDegreeOfParallelism` (default 1, sequential streaming)
  decodes the selected blocks in parallel, keeping file order, consecutive `Index` values and `MaxPoints`/`SubsampleStep`
  semantics. Other readers ignore both options. A damaged block fails the read in both paths (the parallel failure is
  unwrapped, not an `AggregateException`); a truncated read reports no incomplete-data warning.

## [0.1.0] - 2026-11-15

First release.

### Added

- Core types `PclDocument`, `PclScan`, `PclPoint`, `PclVector3`, `PclBounds`, `PclColor`, `PclTransform` and `PclReadOptions`; `PclReadOptions.MaxPoints` and `SubsampleStep` are validated, and `MaxPoints` of 0 yields no points.
- Automatic format detection in `PclFile.DetectKind` and `PclFile.Open`, from the signature, the extension or the folder layout. A file with a recognised extension and the wrong content is `PclFileKind.Unknown`, except `.c3db` and `.c3ptx`, which are detected by extension alone and fail when opened.
- Readers for ASTM E57 (a defined subset, with page checksum verification and pose rotation), ReCap scans (`.rcs`) and projects (`.rcp`), Leica containers (`.lgsx`, `.lgs`, with Zstandard decompression of point blocks by a decoder of its own; all points of a container form one scan), FARO scans (`.fls` and scan folders), PinPoint projects (`.srf`, `.smf`, with point data files that store named channels or Snappy-compressed channels in numbered sections) and EdgeWise databases and clusters (`.c3db`, `.c3ptx`).
- Channel availability (`HasColors`, `HasIntensities`, `HasNormals`) follows what the file contains, except for ReCap scans, where it is fixed by the supported layout, and for the scans of a ReCap project, where it depends only on whether the `.rcs` file was found. Colour and intensity that a file does not store are null, and a project manifest that states no point count reports 0, instead of invented values.
- Reading of untrusted files: declared sizes and counts are checked against the file and against fixed limits, XML is read without DTDs, decompressed sizes are capped before allocation, FARO planes are inflated in chunks, and malformed or truncated input fails with `InvalidDataException` and unsupported layouts with `NotSupportedException`. The limits are listed in the README.
- `SafePath`, which resolves file names stored inside ReCap, PinPoint and EdgeWise project files only inside the project folder. The check is lexical; symbolic links are not resolved.
- Exporters for ASPRS LAS 1.2 point format 2, ASCII XYZ, PTS and JSON, with overloads that report points skipped because a coordinate was NaN or infinite. LAS and PTS read the source twice instead of buffering the points. XYZ and PTS output is culture independent and ends lines with `\n`. JSON writes the file name, not its path.
- The command line tool `pclio` with `info`, `scans`, `points` and `export`. Exit codes are 0 success, 1 command line, 2 input unreadable, 3 corrupt or unsupported, 4 unexpected, with one `error:` line on standard error. Options are parsed strictly. `export` refuses to overwrite an existing file without `--force`, refuses to write to the input or into an input folder (compared after following links and junctions), and writes through a temporary file. `points --format xyz|pts` prints the same lines as the exporters. Files written by `export` are UTF-8 without a BOM.
- A test suite with synthetic fixtures; tests that need private scan files are skipped when the files are not available.
- Documentation in `docs/` (getting started, public API, dependencies, private test fixtures), `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, a security policy with private vulnerability reporting, issue and pull request templates, CI, documentation and release workflows, `docfx.json` and `tools/pack-release.ps1`.
- XML documentation on every public type and member.
- No runtime dependencies on other packages: the Zstandard and Snappy decoders are part of the library. `Microsoft.SourceLink.GitHub` is a build-time package only.
