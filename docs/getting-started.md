---
title: Getting started
---

# Getting started

PclIO is a preview (0.x). To build from source, run `dotnet build PclIO.slnx -c Release` from the repository root.

## Command line tool

```sh
dotnet tool install --global PclIO.Cli
pclio info scan.e57
pclio export scan.e57 scan.las
```

Common commands:

```sh
pclio info scan.e57 --json
pclio scans scan.e57
pclio points scan.e57 --limit 20 --format xyz
pclio export scan.e57 scan.xyz --step 10
pclio export scan.e57 scan.json --limit 100 --force
```

The output format of `export` comes from `--format` or from the extension of the output file (`.las`, `.xyz`, `.pts`, `.json`);
any other extension needs `--format`. The output is written to a temporary file next to the target and moved into place when
it is complete. Export refuses to write to the input (compared after following links and junctions; hard links are not detected),
to a path inside an input folder, to a missing folder or to a directory, and refuses to replace an existing file unless `--force`
is given. `points --format xyz` and `--format pts` print the same lines as the exporters. When points with a NaN or infinite
coordinate are skipped, one `warning: skipped N points ...` line goes to standard error. Run `pclio --help` for all options.

### Exit codes

| Code | Meaning |
| --- | --- |
| 0 | The command succeeded. |
| 1 | The command line is wrong: unknown option, missing or extra argument, invalid value, output refused. |
| 2 | The input cannot be read: it is missing, empty or unreadable. A failed write of the output also exits with 2. |
| 3 | The input is corrupt, truncated or unsupported. |
| 4 | Unexpected internal failure. |

Every failure prints one `error: ...` line on standard error, with no stack trace for any exit code.

## Library

```sh
dotnet add package PclIO
```

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

The library is silent: it never writes to the console. Problems that do not stop reading are collected in
`PclDocument.Warnings`. `PclFile.Open` throws `FileNotFoundException`, `DirectoryNotFoundException` (a directory the reader needs disappears while
reading), `NotSupportedException` (unrecognised format or unsupported feature) or `InvalidDataException` (malformed or truncated file).

## Supported formats

| Format | Extensions | Support level |
| --- | --- | --- |
| ASTM E57 | `.e57` | Points decoded for a defined subset (uncompressed bit-packed streams, single or double precision Cartesian coordinates, 8-bit colour and intensity). Per-point validity flags and images are not read. |
| ReCap scan | `.rcs` | Experimental; layout of major version 3 only. |
| ReCap project | `.rcp` | Lists scans; opens the `.rcs` files it finds next to the project. Node transforms are not applied. |
| Leica scan container | `.lgsx`, `.lgs` | Experimental; poses and bounds are not read, and all points of a container form one scan. |
| FARO scan | `.fls`, or a FARO scan folder | Experimental; the scan grid comes from the stored parameters. A folder scan is read with an assumed layout, and a warning says so. |
| PinPoint project | `.srf`, `.smf`, or a folder with `ScanProject.json` or `.smf`/`.srf` files | Experimental; manifest plus point data files in one of two layouts (named channels, or sections with Snappy-compressed channels); a data file in neither layout is not read. |
| EdgeWise | `.c3db`, `.c3ptx` | Experimental; positions only, with a coordinate scale that is an assumption of the reader. |

A file is detected from its content, its extension or, for a folder, its layout. E57, ReCap scan and Leica files are recognised
by content, `.fls` needs an OLE2 compound file, `.rcp` a ZIP archive, `.srf` and `.smf` a JSON text file, and `.c3db` and `.c3ptx`
are decided by extension alone, without a content check. Anything else is `PclFileKind.Unknown`. The rules are in
[Public API](public-api.md#format-detection).
Anything a decoder does not support fails instead of being guessed, apart from the assumptions named in the README (the FARO folder
record layout, the EdgeWise position scale and the ReCap scan channel flags). The sizes and counts a reader accepts are limited; a
file that exceeds a limit is rejected with `InvalidDataException`, with the exceptions listed in the README.

## Limitations

- The API can change before 1.0.
- Some readers drop points that fail a plausibility rule: FARO cells outside 0.1 m to 350 m, PinPoint points with a
  non-finite coordinate or a coordinate beyond `PclReadOptions.MaxCoordinateMagnitude` in the file's own unit (5000 by default),
  and Leica padding entries. A scan can therefore yield fewer points than `PointCount`. The rules and the limits on file sizes
  and counts are listed in the repository README.
- `SubsampleStep` is applied inside each format's internal blocks, so the spacing is approximate; `MaxPoints` limits one read call.
- LAS and PTS export read the source twice; memory does not depend on the number of points. A point without intensity is written with full
  intensity and a point without colour as white. LAS uses a 1 mm scale by default, so the extent of each axis must fit 32-bit integers at the configured scale.
- Normals are read where a format has them but no exporter writes them.
- Rotation values of a `PclTransform` are stored for information and are not applied by `TransformPoint`.

The supported surface is listed in [Public API](public-api.md); every type is in the [API reference](api/PclIO.yml).
