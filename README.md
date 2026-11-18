<p align="center">
  <img src="docs/images/pclio-banner.png" alt="PclIO" width="640">
</p>

PclIO is a .NET library and command line tool that reads point cloud files from several scanner and project formats and exports the points to LAS, XYZ, PTS and JSON, without the software that wrote the files.

## What it does

- Detects the format of a file or folder from its content, extension or layout (`PclFile.DetectKind`).
- Opens a file as a `PclDocument` with its scans, bounds, transforms, metadata and warnings, and streams the points (`ReadAllPoints`, `PclScan.ReadPoints`) with a point limit, a subsampling step and an optional scan transform. Leica scans additionally read a block range with opt-in parallel decoding.
- Exports to ASPRS LAS 1.2 (point format 2) and 1.4 (point formats 6 to 8), ASCII XYZ, ASCII PTS and JSON.
- Ships the command line tool `pclio` with `info`, `scans`, `points` and `export`.

## Install

PclIO is a preview (0.x). The packages are published on nuget.org, not on GitHub Packages, so the Packages box of this repository stays empty.

[![PclIO on NuGet](https://img.shields.io/nuget/v/PclIO?label=PclIO)](https://www.nuget.org/packages/PclIO) [![PclIO.Cli on NuGet](https://img.shields.io/nuget/v/PclIO.Cli?label=PclIO.Cli)](https://www.nuget.org/packages/PclIO.Cli)

Install the command line tool:

```sh
dotnet tool install --global PclIO.Cli
```

Add the library to a .NET project:

```sh
dotnet add package PclIO
```

The library targets .NET 8 and .NET 10. It is managed code with no dependencies on other packages (see [third-party notices](THIRD-PARTY-NOTICES.md)), has no native dependencies or Windows-only APIs, and is tested on Windows, Linux and macOS. It does not contact the network and writes nothing to the console.

## Supported formats

| Format | Extensions | Support level |
| --- | --- | --- |
| ASTM E57 | `.e57` | Points decoded for a defined subset: uncompressed bit-packed streams with single or double precision Cartesian coordinates or scaled-integer coordinates (raw value times scale plus offset), or spherical range, azimuth and elevation converted to Cartesian points, with 8-bit colour and 8-bit intensity, or colour with a wider Integer range from zero and intensity as a wider Integer, a Float or a scaled integer. Pose translation and rotation are applied. Only plain bit-packing is decoded: a scan whose codecs vector names another codec lists normally and fails when its points are read, and the message names it. Per-point validity flags skip invalid points and clear invalid channels when their streams hold one flag byte per record; other shapes are not interpreted. Images are not read, so a scan with them still lists and reads its points. Page checksums are verified. |
| ReCap scan | `.rcs` | Experimental. The header is read; point decoding covers the node layouts that are described in the code, which is the one of major version 3. A file of another version is listed with its header, but reading its points fails with a message that names the version. A further layout is described only when the files of that version may be used for the tests, so that its offsets can be verified. The channels of the points follow from the size of the payload of the nodes: colour and intensity when the records are long enough, and normals when the node holds the plane of them. |
| ReCap project | `.rcp` | Lists the scans of the project manifest and opens the `.rcs` files it finds next to the project. The transform of a scan node places the scan in the project and is applied to its points unless `ApplyTransform` is false; when the manifest states no transform, the transform of the scan file is used. |
| Leica scan container | `.lgsx`, `.lgs` | Experimental. Points are decoded from the compressed point blocks: position, and colour, intensity and normals when the block has them. When the container assigns its points to its setups, there is one scan per setup, named after the setup, with the pose and the bounds of the setup when its record holds them; the pose is applied unless `ApplyTransform` is false. When the container does not assign its points, all points of the container form one scan and a warning says so. The point count is taken from the container when it states one; otherwise it is found by decoding the data, which is slow on large files. `PclReadOptions.Blocks` reads only the blocks of the scan in that file-order range (per scan, so a setup scan ranges over its own blocks); `PclReadOptions.MaxDegreeOfParallelism` above 1 decodes those blocks in parallel and still yields the points in file order with consecutive `Index` values. |
| FARO scan | `.fls`, or a FARO scan folder | Experimental. The scan grid and angles are read from the stored parameters and points are projected from the range grid; a file without them fails to open. The point count is the number of valid returns, which is counted on the first access; `FaroScan.GridSize` is the size of the scan grid. For a scan folder the layout of the samples is derived from the size of the data stream and the size of the grid, and the scale of the intensity from the values that are stored; when the sizes do not fit, the layout is assumed and a warning says so. |
| PinPoint project | `.srf`, `.smf`, or a folder with `ScanProject.json` or `.smf`/`.srf` files | Experimental. The manifest (scan names, ids, poses, counts) is read. Points come from the referenced `.bin` files, in either of two layouts: a table of named channels, or numbered sections with a property table and Snappy-compressed channels, which PclIO decodes itself. Colour, intensity and normals are reported only when the file has that channel; in the second layout positions, colours, intensities, normals (`Normals`, three float32 components per point) and the per-point flags (`FilterBits`, one byte per point) are read, while the grid indices stored beside them are not interpreted, so by default every stored point that passes the plausibility rule below is returned. With `PclReadOptions.ExcludeFilteredPoints` the points with a non-zero flag are not returned. A manifest with a scan position list takes the point count from the manifest, else from the data file; a manifest with a data object list takes it from the data file the object refers to, and every location that shares one file reports its full count, because the shares are not recorded. |
| EdgeWise | `.c3db`, `.c3ptx` | Experimental. A `.c3db` point database lists the scan clusters and a `.c3ptx` is one cluster file; the cluster files give positions only, as three 16-bit offsets from the cluster centre per point. The scale of the offsets is not stored in the files, so the reader assumes 32768 units = 0.128 m unless the caller states it with `PclReadOptions.EdgeWiseCoordinateScale`. |
| LAS | `.las` | Points decoded from versions 1.0 to 1.4 with point formats 0 to 10: XYZ with scale and offset, intensity, classification and RGB for the colour formats (2, 3, 5, 7, 8, 10). GPS time, return numbers, scan angles, point source ids, near infrared and waveform packet descriptors are not interpreted; extra bytes past a format's base size are skipped. The extended point counts of 1.4 are used when stated. Coordinate system records name an `EPSG:` code from a GeoTIFF key directory when it holds one, else the OGC WKT when one is stored. |
| PCD | `.pcd` | Points decoded from the 0.7 header in ASCII, binary and binary compressed (LZF) form: XYZ from x, y and z, colour from packed rgb or rgba, intensity, and normals from normal_x, normal_y and normal_z. Points with non-finite coordinates are not returned. The viewpoint is kept as metadata and not applied. |
| PLY | `.ply` | Vertices decoded from ASCII and binary little and big endian files: XYZ from x, y and z, 8-bit colour from red, green and blue (with alpha when present), intensity, and normals from nx, ny and nz. Faces and other elements are ignored. Points with non-finite coordinates are not returned. |
| PTS | `.pts` | Leica Cyclone ASCII exchange point clouds: XYZ coordinates with or without point count header, intensity, 8-bit RGB colour, or unit normals across 3, 4, 6, 7, 8, 9 and 10 column layouts. Points with non-finite coordinates are not returned. |
| PTX | `.ptx` | Leica Cyclone gridded ASCII exchange point clouds: one or more gridded scans per file, each with a 10-line header defining grid dimensions (columns and rows), scanner position and axes, and a 4x4 transformation matrix. XYZ coordinates across 3, 4, 6 and 7 column layouts (with intensity and 8-bit RGB colour). Empty grid cells (coordinates 0, 0, 0) and non-finite coordinates are skipped. Transform matrices are applied to point positions unless `ApplyTransform` is false. |
| XYZ / text | `.xyz`, `.asc`, `.txt`, `.csv` | Delimited text point clouds: comma, semicolon, tab or whitespace delimiters; column layouts determined from a header line (e.g. `x,y,z,nx,ny,nz`), explicit `PclReadOptions.TextColumns`, or column count heuristics (3, 4, 6, 7, 8, 9, 10 columns for XYZ, intensity, 8-bit RGB colour or unit normals, and alpha). Comment lines (`#`, `//` or `TextCommentPrefix`) and points with non-finite coordinates are skipped. Coordinate scale applied via `PclReadOptions.TextCoordinateScale`. |
| SBF | `.sbf`, `.sbf.data` | Simple Binary Format point clouds: single-precision floating point XYZ coordinates and optional scalar fields (intensity, 8-bit RGB colour, unit normals) with an INI-style companion `.sbf` header file declaring point count, global shift and scale, and scalar field names. Big-endian and little-endian byte orders supported. Points with non-finite coordinates are not returned. Global shift and scale are applied via `ApplyTransform`. |

### Format detection

`PclFile.DetectKind` finds the kind from the content of the file, its extension or the layout of a folder, and returns
`PclFileKind.Unknown` for anything else; `PclFile.Open` then throws `NotSupportedException`. Extensions are compared without
regard to case.

- E57, ReCap scan and Leica files are recognised by their content and accepted with any extension; for Leica, `.lgs` selects the
  older container and any other extension `.lgsx`. EdgeWise files are also recognised by content when the extension is none of its own.
  A LAS file is recognised by the `LASF` signature with any extension except `.laz`, which shares the signature and stays unknown. A PCD file is recognised by its header structure with any extension. A PLY file is recognised by its magic word and header structure with any extension. A PTS file is recognised by an integer point count header line followed by coordinate lines with any extension, or coordinate lines with the `.pts` extension. A PTX file is recognised by its 10-line header structure (grid dimensions, scanner pose, 4x4 matrix) with any extension. An XYZ / delimited text file is recognised by an explicit column header containing X, Y, Z in any extension, or by numeric coordinate lines with a recognised extension (`.xyz`, `.asc`, `.txt`, `.csv`). An SBF file is recognised by its companion `[SBF]` header structure with any extension, or by the binary signature `0x2A, 0x2A` (`**`) and record size formula (`.sbf`, `.sbf.data`, `.data`).
- A FARO file must be an OLE2 compound file with the extension `.fls`. A ReCap project must be a ZIP archive with the extension
  `.rcp`. A PinPoint file must be a JSON text file with the extension `.smf` or `.srf`.
- The extensions `.c3db` and `.c3ptx` select EdgeWise without a content check. A `.c3ptx` file is read as one cluster file and any
  other EdgeWise file as a point database.
- A folder is a FARO scan folder or a PinPoint project folder (it holds `ScanProject.json`, or a `.smf` or `.srf` file) by its layout.

So a wrong or damaged file with a recognised extension is reported as unknown, except `.c3db` and `.c3ptx`: those are reported as
EdgeWise and fail with `InvalidDataException` when they are opened. A path that is neither a file nor a folder throws
`FileNotFoundException`. The exact rules and their order are covered by the tests.

### Referenced files and path containment

Reading never executes code from a file. File names stored inside project files (ReCap, PinPoint, EdgeWise) are joined to a
base folder and rejected when they are empty, rooted, contain invalid characters, or resolve outside that folder. This check is
lexical: the path is normalised and compared with the base folder, and symbolic links and junctions are not resolved, so a link
inside the project folder counts as part of it. A rejected name is reported as `InvalidDataException`, or as a warning where the
reader can carry on without the file. The FARO folder reader searches the folder it was given and does not use names from the data.

Where the readers look for the files a project refers to:

- ReCap project: for each scan node, the stored path relative to the project's folder, then the project's support folder, then
  the project's folder, and last a file named after the scan in the support folder. A scan whose file is not found stays listed,
  yields no points and adds a warning.
- PinPoint: the data folder that belongs to the project file, then the folder of the opened project file or folder. Where several
  data files match a scan, the first one in ordinal path order is used.
- EdgeWise database: each cluster file is named relative to the database's folder; a missing one is skipped with a warning.
- SBF: when opening a `.sbf` header file, the reader looks for a companion `.sbf.data` or `.data` binary file in the same directory. When opening a `.sbf.data` or `.data` file, it looks for a companion `.sbf` header file.

### Data filters

These readers drop data without an error or a warning, so a scan can yield fewer points than `PointCount`.

- Every exporter, and the `points` command with `--format xyz` or `pts`, skips a point with a NaN or infinite coordinate; the count is reported (see Exporters).
- FARO: a grid cell without a valid return, or with a distance outside 0.1 m to 350 m, is not read. The point count of the
  scan is the number of the cells that are read, so it matches what a full read returns. A scan folder whose data stream does
  not hold a whole number of samples for the grid is read with the assumed layout of eight bytes per sample, and a warning
  says so.
- PinPoint: a point with a NaN or infinite coordinate is never returned, and neither is a point with a coordinate whose
  magnitude exceeds `PclReadOptions.MaxCoordinateMagnitude` in the file's own unit (5000 by default, null for no limit).
  This is a plausibility rule of the reader, so coordinates in a projected system with large offsets are dropped by default.
  A full read reports how many points it dropped in the warnings of the document, and fails instead with `PclReadOptions.Strict`. A
  data file in neither layout, or in the sectioned layout with an element type or header value the reader does not support, cannot
  be read: the project lists it with a warning and reading its points throws `NotSupportedException`. A compressed stream whose
  copies reach back more than 8 MiB is not decoded: reading its points throws `NotSupportedException`, without an earlier warning.
- Leica: padding entries and points that coincide with a block's origin are dropped, a compressed stream above its size limit is
  skipped, and a block that is not recognised ends the scan. The reader counts what it dropped or skipped and reports it in the
  warnings of the document, so that an incomplete result is visible. With `PclReadOptions.Strict` the read fails instead.
- EdgeWise: a database node whose point count is 0 is skipped.
- PCD: a point with a NaN or infinite coordinate is not returned; a colour, intensity or normal field whose shape the reader does not convert stays unread, so the points come without that channel.
- PLY: a vertex with a NaN or infinite coordinate is not returned; colour that is not stored as 8-bit integers is left unread, with a warning.
- PTS: a point with a NaN or infinite coordinate is not returned.
- PTX: empty grid cells (0, 0, 0) and points with a NaN or infinite coordinate are not returned.
- XYZ / text: a point with a NaN or infinite coordinate is not returned; comment lines are skipped.
### Limits

A file that exceeds one of these limits, or whose declared sizes and counts do not fit the file, is rejected with
`InvalidDataException`, with the exceptions below. A FARO scan grid above its limit gives `NotSupportedException` when the file is
opened. An E57 point prototype with more than 65535 fields gives `NotSupportedException` when the points are read; the scan still
lists. A scan whose codecs vector names another codec than plain bit-packing is rejected the same way, and the message names it. A compressed Leica stream above its size limit is not rejected: its data is skipped and the skip is reported in the warnings
of the document, which is one of the Leica data filters above. An unsupported PinPoint feature (see the data filters) gives
`NotSupportedException` when the points are read. Point data is streamed and there is no limit on the file size, except for FARO container files: each compressed data stream is
read into memory as a whole, so its size must fit a .NET array (about 2 GB), otherwise reading fails with
`NotSupportedException`. The XML of E57 and ReCap project files is read with DTDs prohibited, so no entity is expanded and nothing
outside the file is fetched.

| Format | Limits |
| --- | --- |
| E57 | XML section 32 MiB, nesting depth 64, 1,000,000 elements; declared record counts up to 2^40; at most 65535 fields in a point prototype; at most 4096 consecutive packets without points. |
| ReCap project | Manifest 64 MiB (declared and real size), nesting depth 64, 1,000,000 elements, 65,536 scan nodes. |
| ReCap scan | 500,000 nodes in the node table, which must fit in the file together with the node payloads. |
| Leica | 10,000 container entries; per point block at most 64 compressed streams, 10,000,000 compressed bytes per stream, 16 MiB per decompressed stream (checked before allocation) and 128 MiB decompressed in all; over all blocks of one read 1 GiB plus 64 times the file size decompressed. |
| FARO | Parameters 1 MiB; scan grid of at most 2^27 cells (134,217,728); a distance stream too small for the declared grid is refused; each compressed data stream is held in memory (see above) and is inflated in chunks, and must not inflate beyond its grid size plus 64 KiB. Compound file: 65,536 directory entries, depth 16. |
| PinPoint | Manifest 64 MiB and the default nesting depth of `System.Text.Json` (64); 100,000 scans. The parsed manifest needs memory of about 15 times its size, so a manifest at the limit needs about 1 GB. Sectioned data file: at most 16 sections, a property table of at most 1 MiB nested at most 8 levels, copies in a compressed stream at most 8 MiB back with a decoder window of at most 8 MiB per stream; the sections must be contiguous and fill the file, and the `Points`, `RGB8`, `Intensities8`, `Normals` and `FilterBits` streams must decode to exactly 12, 3, 1, 12 and 1 bytes per stored point. |
| EdgeWise | 1,048,576 characters per database line; 1,000,000 nodes. Patterns that parse a line have a time limit. |
| LAS | Header of at least 227 bytes (375 for 1.4); at most 4096 variable length records of at most 1 MiB each; point records at most 1 MiB. |
| PCD | 4096 header lines of at most 1 MiB each; at most 1024 fields with an element count of at most 65536; decompressed binary data at most 1 GiB. |
| PLY | 4096 header lines of at most 1 MiB each; at most 1024 elements with at most 1024 properties each; vertex records at most 1 MiB. |
| PTS | Lines of at most 1 MiB each; declared point count cannot exceed the physical capacity of the file. |
| PTX | Lines of at most 1 MiB each; declared grid counts cannot exceed the physical capacity of the file. |
| XYZ / text | Lines of at most 1 MiB each; point count cannot exceed the physical capacity of the file. |

## Exporters

| Output | Class | Notes |
| --- | --- | --- |
| LAS 1.2, point format 2<br>LAS 1.4, point formats 6 to 8 | `PclLasExporter` (`PclLasOptions` selects version, format, scale, offset and coordinate system record) | Position, intensity, classification, RGB (formats 2 and 7/8; format 8 adds a zero near infrared). 1 mm scale and offset from the data by default. A projected `EPSG:NNNN` coordinate system of the source is written as a GeoTIFF record for LAS 1.2 only when asked; LAS 1.4 requires WKT, which the exporter does not produce, so a 1.4 file carries no coordinate system record. |
| XYZ | `PclXyzExporter` | `X Y Z R G B` when the point has a colour, else `X Y Z I` when it has an intensity, else `X Y Z`. |
| PTS | `PclPtsExporter` | A line with the point count, then `X Y Z Intensity R G B` per point. |
| JSON | `PclJsonExporter` | Metadata and scans; sample points only when a point limit above zero is given. |

XYZ and PTS output is culture independent (`.` decimal separator) and ends lines with `\n`. JSON output is culture independent; its line ends are those of the platform. Points with a NaN or infinite coordinate are skipped; the overloads with `out long skippedPoints` report how many, and the command line prints `warning: skipped N points with a NaN or infinite coordinate` on standard error when N is not zero. The exception is `pclio points --format json`, which skips nothing and writes such a coordinate as the string `"NaN"`, `"Infinity"` or `"-Infinity"`. Files written by `export` are UTF-8 without a BOM; `info`, `scans` and `points` write to the console with the platform's own line ends and encoding, and control characters in names and messages are printed as `\uXXXX`.

## Command line

```text
pclio info <file> [--json]
pclio scans <file> [--json]
pclio points <file> [--scan <id>] [--limit <n>] [--step <s>] [--format <xyz|pts|json>] [--no-transform]
pclio export <input> <output> [--format <las|xyz|pts|json>] [--limit <n>] [--step <s>] [--no-transform] [--force]
pclio <input> [--json]               same as 'info'
pclio <input> <output> [options]     same as 'export'
```

- `info` prints the format, file size, scans, point count, bounds, coordinate system, metadata and warnings.
- `scans` lists the scans with their point counts, channels, bounds and translation.
- `points` prints points to standard output, at most 1000 unless `--limit` is given. `--scan` selects one scan by 0-based index, id or name. `--format xyz` and `--format pts` print the same lines as the exporters (PTS starts with its count line); `--format json` prints one JSON object per line.
- `export` writes a file. The format comes from `--format` or from the output extension (`.las`, `.xyz`, `.pts`, `.json`); any other extension needs `--format`. The output is written to a temporary file next to the target and moved into place when complete. Export refuses to write to the input (compared after following links and junctions, and on Windows by file identity, so the verbatim and device prefixes and administrative shares are caught too; hard links are not detected elsewhere), to a path inside an input folder, to a missing folder or to a directory, and refuses to replace an existing file unless `--force` is given.
- `--limit` and `--step` take integers of at least 1. `--no-transform` returns coordinates as stored. A JSON export rejects `--step` and `--no-transform`, and `--limit` for it is the number of sample points.
- `--help`, `-h` and `help`, and `--version`, `-v` and `version`, print and exit with code 0.

Errors are one line on standard error, with no stack trace for any exit code.

### Exit codes

| Code | Meaning |
| --- | --- |
| 0 | The command succeeded. |
| 1 | The command line is wrong: unknown option, missing or extra argument, invalid value, output refused. |
| 2 | The input cannot be read: it is missing, empty or unreadable. A failed write of the output also exits with 2, and the message names the file. |
| 3 | The input is corrupt, truncated or unsupported. |
| 4 | Unexpected internal failure. |

## Library

```csharp
using System;
using System.IO;
using PclIO;
using PclIO.Export;

using PclDocument doc = PclFile.Open("scan.e57");
Console.WriteLine($"{doc.Kind}: {doc.Scans.Count} scans, {doc.TotalPointCount} points");

foreach (string warning in doc.Warnings)
{
    Console.Error.WriteLine(warning);
}

foreach (PclPoint point in doc.ReadAllPoints(new PclReadOptions { MaxPoints = 1000 }))
{
    Console.WriteLine($"{point.X} {point.Y} {point.Z}");
}

using FileStream output = File.Create("scan.las");
long written = PclLasExporter.Export(doc, output);
```

`PclFile.Open` throws `FileNotFoundException` for a missing path, `DirectoryNotFoundException` when a directory it needs disappears while reading, `NotSupportedException` for an unrecognised format or an unsupported feature, and `InvalidDataException` for a malformed or truncated file. The library is silent: problems that do not stop reading are collected in `PclDocument.Warnings`.

See the [public API](docs/public-api.md) for the supported surface and [dependencies](docs/dependencies.md) for package details.

## Documentation

[Getting started](docs/getting-started.md), the [public API](docs/public-api.md), [dependencies](docs/dependencies.md) and [private test fixtures](docs/test-fixtures.md) are in `docs/`. The documentation site with the API reference is at <https://ioextend.github.io/PclIO/>, built from them with DocFX (`docfx.json`). [![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/IOExtend/PclIO) offers an automatically generated overview of the repository.

## Limitations

- Version 0.1.0 is a preview. The API can change before 1.0.
- Only the subsets listed under Supported formats are read. Anything else fails with exit code 3 (`InvalidDataException` or `NotSupportedException` in the library) instead of being guessed, apart from the assumptions named under Supported formats (the FARO folder record layout and the EdgeWise position scale unless the caller states it with `PclReadOptions.EdgeWiseCoordinateScale`).
- The decoders other than E57 cover only the file layouts they were written for. Files written by other software versions can be rejected.
- Readers limit the sizes and counts that a file declares; the numbers are listed under Limits. A file that exceeds a limit is rejected, with the exceptions described there. Some readers drop points that fail a plausibility rule; see Data filters.
- `SubsampleStep` is applied inside each format's internal blocks, so the spacing is approximate. `MaxPoints` limits one read call.
- `Blocks` selects the blocks of a scan in file order and is honoured only by the Leica reader; other readers read the whole scan and ignore it. `MaxDegreeOfParallelism` above 1 decodes the selected Leica blocks in parallel and still yields the points in file order; other readers read sequentially and ignore it. Sequential Leica decoding holds one block at a time, while the parallel read holds the decoded points of the selected blocks. The default is 1: sequential streaming.
- A scan transform is scale and translation, or a 4x4 matrix when the format gives one. Rotation values of a `PclTransform` are stored for information and are not applied by `TransformPoint`.
- LAS and PTS export read the source twice (the header needs the count), so the source must be re-enumerable; memory does not depend on the number of points. A point without intensity is written with full intensity and a point without colour as white, because these formats have no "missing" value. LAS stores coordinates with the configured scale (1 mm by default), so the extent of each axis must fit 32-bit integers at that scale; the default offset is the minimum of the data.
- Normals are read where a format has them, but no exporter writes them.
- There is no support for writing or modifying scan files.

## Build from source and tests

Use the .NET SDK selected by `global.json`, which specifies 10.0.100 with `latestFeature` roll-forward.

From the repository root:

```sh
dotnet build PclIO.slnx -c Release
dotnet test
```

Tests that need real scan files are skipped with a message unless `PCLIO_TEST_FILES` points to a folder with private fixtures (or an uncommitted `test_files` folder exists). The repository contains no scans. See [private test fixtures](docs/test-fixtures.md).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) and the [Code of Conduct](CODE_OF_CONDUCT.md). Release changes are recorded in [CHANGELOG.md](CHANGELOG.md).

## Security

Report vulnerabilities through GitHub private vulnerability reporting in the repository's Security tab. Do not open public issues for security vulnerabilities. See [SECURITY.md](SECURITY.md).

## License and trademarks

PclIO is licensed under the [MIT License](LICENSE). Copyright holder: mahach666.

PclIO is not affiliated with, endorsed by or sponsored by Autodesk, Leica Geosystems, FARO, ClearEdge3D, PointCab or any other vendor named in this repository. Autodesk and ReCap are trademarks of Autodesk, Inc.; Leica and Leica Geosystems, FARO, EdgeWise, PointCab and PinPoint are trademarks of their respective holders. These names are used only to say which file formats PclIO reads. LAS is a format of the American Society for Photogrammetry and Remote Sensing (ASPRS), and E57 is an ASTM standard. No vendor code or binaries are included.
