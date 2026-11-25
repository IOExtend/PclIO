---
title: Dependencies
---

# Dependencies

All package versions live in `Directory.Packages.props` (central package management); project files carry no versions.
Build settings shared by every project live in `Directory.Build.props`; the SDK is pinned in `global.json`
(`10.0.100`, `rollForward: latestFeature`, no prereleases).

## Runtime packages

The engine (`PclIO`) and the command line tool (`PclIO.Cli`) depend on the .NET base class library only (`System.IO.Compression`,
`System.Xml`, `System.Text.Json`) and reference no native code and no package. A test checks that neither project file references a package.

The scene adapter (`PclIO.Scene`) is the only shipped project with a package dependency: `IOScene` `[0.1.0, 0.2.0-0.0)`, the neutral
scene contract of the IOExtend ecosystem. The upper bound keeps out the next breaking line; the adapter pins the engine as an exact
`[X.Y.Z]` dependency because both ship on one tag. The CLI references the adapter as a project, so the tool carries IOScene too.

### Decisions

- **The engine is a leaf.** No engine code references IOScene or another IOExtend package; the Zstandard decoder for the point blocks
  of Leica containers and the Snappy decoder for the channels of PinPoint data files are part of the library, written from the public
  format specifications.
- **Decompression bound.** The Zstandard decoder takes an explicit maximum size, so a frame that declares more than the limit is
  rejected before memory is allocated. The Snappy decoder is streaming; memory use does not depend on the size of the data.

## Build-time packages

`Microsoft.SourceLink.GitHub` 10.0.403 is referenced by every project with `PrivateAssets` all; it is not a dependency of the published packages.
The 11.x line is not used because it pulls `System.IO.Hashing` 11, which does not support `net8.0` and produces a build warning.

## Test packages

Pinned to exact versions: Microsoft.NET.Test.Sdk 18.12.0, xunit 2.9.3 and xunit.runner.visualstudio 4.0.1.
The test project references the library and the CLI project; the adapter test project (`PclIO.Scene.Tests`) references the adapter
and links the temporary-directory helper of the main test project.

## Auditing

`NuGetAudit` is on with `NuGetAuditMode=all` (direct and transitive) and `NuGetAuditLevel=low`, so every known
advisory is reported as an NU1901-NU1904 warning at restore. Check manually:

    dotnet list package --vulnerable --include-transitive
    dotnet list package --outdated

Re-run the audit when changing dependencies.

## Build settings

`Directory.Build.props` sets `LangVersion` 14.0, `Nullable` enable, `ImplicitUsings` enable, `Deterministic` true,
`EmbedUntrackedSources` true, `PublishRepositoryUrl` true, `VersionPrefix` 0.1.0, the package icon (`icon.png` in each packable project folder), symbol packages (`snupkg`),
`DeterministicSourcePaths` true and a `PathMap` that maps the repository folder to `/_/` (so a local pack contains no local
absolute path), and `ContinuousIntegrationBuild` true when `CI`, `GITHUB_ACTIONS` or `TF_BUILD` is set. `src/Directory.Build.props` turns
warnings into errors for every project under `src`. The library, the adapter, the CLI and the tests target `net8.0` and `net10.0`.
