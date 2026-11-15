---
title: Dependencies
---

# Dependencies

All package versions live in `Directory.Packages.props` (central package management); project files carry no versions.
Build settings shared by every project live in `Directory.Build.props`; the SDK is pinned in `global.json`
(`10.0.100`, `rollForward: latestFeature`, no prereleases).

## Runtime packages (library)

| Package | Version | License | Used for |
|---|---|---|---|
| ZstdSharp.Port | 0.8.8 | MIT | Zstandard decompression of the point blocks in Leica scan containers. |

ZstdSharp.Port is the only runtime dependency. The E57, ReCap, FARO, PinPoint and EdgeWise readers use only the .NET base class library
(`System.IO.Compression`, `System.Xml`, `System.Text.Json`). The library references no native code.

### Decisions

- **Decompression bound.** The library decodes each zstd frame with an explicit maximum size, so a frame that declares more than the
  limit is rejected before memory is allocated.

## Build-time packages

`Microsoft.SourceLink.GitHub` 10.0.403 is referenced by every project with `PrivateAssets` all; it is not a dependency of the published packages.
The 11.x line is not used because it pulls `System.IO.Hashing` 11, which does not support `net8.0` and produces a build warning.

## Test packages

Pinned to exact versions: Microsoft.NET.Test.Sdk 18.12.0, xunit 2.9.3 and xunit.runner.visualstudio 4.0.1.
The test project references the library and the CLI project; it takes ZstdSharp.Port from the library.

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
warnings into errors for the library and the CLI. The library, the CLI and the tests target `net8.0` and `net10.0`.
