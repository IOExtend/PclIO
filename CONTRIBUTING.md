# Contributing to PclIO

## Reporting bugs

Open a [GitHub issue](https://github.com/IOExtend/PclIO/issues) with:

- The file format and the extension of the file (and the software that wrote it, if you know it).
- The pclio version.
- The command line used, with confidential values removed.
- A minimal description of how to reproduce the problem.
- Expected and actual results, including relevant error messages.

Do not attach scans if they contain confidential data.
A minimal description is better than sharing a confidential scan.

Report security issues through GitHub private vulnerability reporting in
the Security tab. Do not open public issues for security vulnerabilities.

## Proposing changes

Open an issue first for larger changes so the scope and approach can be
discussed before implementation. Keep changes focused on the agreed problem.

## Development setup

Install the .NET SDK specified by `global.json`. It currently specifies
10.0.100, allows roll-forward to the latest feature band, and excludes
prerelease SDKs.

From the repository root, run:

```sh
dotnet build PclIO.slnx -c Release
dotnet test
```

The library is in `src/PclIO`, the CLI in `src/PclIO.Cli`, and tests in
`tests/PclIO.Tests`.

Tests that need real scan files are skipped with a message when private
fixtures are unavailable. Set `PCLIO_TEST_FILES` to the folder containing
your private fixtures to run them, or put them in an uncommitted `test_files`
folder in the repository root. The repository ships no scans. See
[docs/test-fixtures.md](docs/test-fixtures.md).

## Code conventions

- Follow the surrounding code and repository formatting settings.
- Keep nullable reference types enabled.
- Warnings are treated as errors in `src`.
- Write short English comments that explain why.
- Document public APIs with XML documentation comments.
- Do not write console output from the library. The library must remain
  silent by default; report non-fatal problems through `PclDocument.Warnings`.
- Treat file content as untrusted: check declared sizes and counts against
  the file and against fixed limits, detect cycles, bound recursion and
  allocations, resolve paths read from a file only inside the project folder
  (`SafePath`), and never contact the network.
- Fail explicitly (`InvalidDataException`, `NotSupportedException`) instead of
  returning invented values or partial data as success. Where a value is unknown,
  leave it unknown.
- Keep output deterministic: invariant culture and ordinal ordering.

## Clean-room rule

Do not add code, tables or constants copied or translated from vendor software, vendor SDKs, headers, binaries or other
implementations, anything derived from vendor binaries, or vendor-internal symbol names. Public standards
(for example ASTM E57 and the ASPRS LAS specification) may be used and named. A byte signature or a field offset that a parser
needs stays in the code as a plain constant with a short neutral comment (for example "container signature"). Detailed notes on
proprietary formats do not belong in this repository. Test inputs are synthetic.

## Pull requests

Include:

- A description of the problem and the change.
- Tests for behavior changes, including relevant errors and edge cases. Tests use synthetic data built in the test;
  real files are reached only through the `TestFiles` helper.
- The build and test commands run, their results, and any skipped checks.
- Before and after export comparison results when exports are affected.

Do not add scans, models, customer data, vendor code, or binaries to the
repository. Keep private test fixtures outside version control. Do not put names of customers, sites or
projects, scanner serial numbers, GUIDs, local paths or counts taken from private files into code, tests or documents.

Write commit messages in the imperative mood, for example,
"Fix E57 page checksum check".

## Releases

Push a tag matching the package version, such as `v0.1.0`, to run `release.yml`.
The workflow builds and tests the solution, attaches packages, symbols, source and checksums to a GitHub release published at once (not a draft),
and publishes the NuGet packages through Trusted Publishing. The release notes are the section of `CHANGELOG.md` for the version, so add that section before the tag.
The NuGet policy must name this repository, `release.yml`, the configured NuGet profile and the `PclIO*` package pattern.

## Licensing contributions

Contributions are licensed under the MIT license. By submitting a
contribution, you confirm that you have the right to contribute it under
that license.

## Questions

Ask questions in GitHub Discussions or repository issues.
