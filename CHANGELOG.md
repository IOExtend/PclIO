# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
- The channels of a ReCap scan follow from the file: the size of the payload of the nodes tells whether the points hold a
  colour, an intensity and a normal. `HasColors`, `HasIntensities` and `HasNormals` are no longer constants.
- The transform of a ReCap project scan node is applied to its points unless `ApplyTransform` is false; when the manifest
  states no transform, the transform of the scan file is used.
- The per-point flags (`FilterBits`) of the PinPoint sectioned layout are read and validated. The new
  `PclReadOptions.ExcludeFilteredPoints` excludes the points with a non-zero flag; by default every stored point is returned.

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
