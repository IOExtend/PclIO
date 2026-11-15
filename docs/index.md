---
title: PclIO
---

# PclIO

<p align="center">
  <img src="images/pclio-logo.png" alt="PclIO" width="128">
</p>

PclIO is a .NET library and command line tool that reads point cloud files from several scanner and project formats and exports the points to LAS, XYZ, PTS and JSON, without the software that wrote the files.

- Detects the format of a file or folder from its content, extension or layout.
- Opens a file as a document with its scans, bounds, transforms, metadata and warnings, and streams the points with a point limit, a subsampling step and an optional scan transform.
- Exports to ASPRS LAS 1.2 (point format 2), ASCII XYZ, ASCII PTS and JSON.
- Ships the command line tool `pclio` with `info`, `scans`, `points` and `export`.

The library targets .NET 8 and .NET 10, is managed code with one runtime dependency, never writes to the console and never contacts the network. It is tested on Windows, Linux and macOS. PclIO is a preview (0.x).

## Where to go next

- [Getting started](getting-started.md): install the tool or the package, the supported formats, exit codes and a first export.
- [Public API](public-api.md): the supported surface of the library.
- [Dependencies](dependencies.md): packages and build settings.
- [Private test fixtures](test-fixtures.md): how tests that need real scan files find them and when they are skipped.
- [API reference](api/PclIO.yml): every public type, generated from the XML documentation.
- [Source on GitHub](https://github.com/IOExtend/PclIO) and [DeepWiki](https://deepwiki.com/IOExtend/PclIO) for an automatically generated overview of the repository.

PclIO is not affiliated with, endorsed by or sponsored by Autodesk, Leica Geosystems, FARO, ClearEdge3D, PointCab or any other vendor named in this documentation. Autodesk and ReCap are trademarks of Autodesk, Inc.; Leica and Leica Geosystems, FARO, EdgeWise, PointCab and PinPoint are trademarks of their respective holders. The names are used only to say which file formats PclIO reads.
