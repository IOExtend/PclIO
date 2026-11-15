---
title: Private test fixtures
---

# Private test fixtures

Some tests need input that cannot be published: real scan files written by scanner and project software. The repository ships
none. Those tests find their input through one helper, `TestFiles` in the test project, and are reported as **skipped with a
reason** when it is missing. They never pass silently and never fail for lack of a file.

## Where the files are looked for

1. The folder named by the environment variable `PCLIO_TEST_FILES`.
2. A folder `test_files` in the repository root (or in any parent of the test binary folder). It is listed in `.gitignore`.

A fixture is found by its extension, anywhere up to three folder levels below the root, and the first file in ordinal path order
is used. No file name is written into a test. A format with no file in the folder has its tests skipped.

Run with the private files: `PCLIO_TEST_FILES=<folder> dotnet test` (PowerShell: `$env:PCLIO_TEST_FILES = '<folder>'`).

## How a test skips

The attributes `[SkippableFact]` and `[SkippableTheory]` (`SkippableFact.cs`) report a test that throws `SkipException` as
skipped; xunit 2.9.3 has no runtime skip of its own. `TestFiles` throws it when a fixture is missing, so a test needs no code
of its own to skip. The skip message names the missing extension and the environment variable, never a path. No test uses a fixed absolute path.

## What runs without the private files

The tests that run without private files use synthetic data built in the tests (`SyntheticFixtures`, `PinPointPackedFixtures`): small files made by code that
follow the layout each reader accepts, and crafted inputs that must be rejected (truncated, cyclic, oversized, path traversal).
These cover format detection, the readers' error handling, the exporters and the command line.

`RepositoryHygieneTests` also scans every committed file and the git history for personal data and forbidden wording, and checks that no scan, model or other binary content is committed and that the shipped projects reference no package. The Zstandard and Snappy decoders are also tested with vectors (`ZstdVectors`, `SnappyVectors`) made by reference compressors from fixed rules; they contain no scan data. The history check is skipped when git or the history is not available.

## Rules for private fixtures

- Keep them outside version control. Do not add them to the repository, to an issue or to a pull request.
- Assert only what holds for any file of the format: the kind is detected, scans exist, coordinates are finite, a requested number
  of points is returned, exporters write what they count. Do not assert names, identifiers, serial numbers, GUIDs or point counts
  of a particular file, because that fingerprints the file.
- Do not put the names of customers, sites or projects, file names of private files, or local paths into test names, comments or
  messages. Use neutral names such as `sample.rcs`.
- Tests that write diagnostic output write it to a folder in the system temporary folder and delete it afterwards.
