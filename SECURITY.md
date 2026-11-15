# Security policy

## Supported versions

PclIO is in preview (0.x). Security fixes are made on the latest release only.

## Reporting a vulnerability

Please do not open a public issue for a security problem. Use GitHub private vulnerability reporting:
open the Security tab of the repository and choose "Report a vulnerability". Include the version of PclIO, the
command or API call, and a description of the problem. A sample file that triggers it helps, but do not send files
with confidential content; a small synthetic file or a description of its structure is enough.

You can expect an acknowledgement within a few days. Fixes are released as soon as they are verified, and the report
is credited unless you prefer otherwise.

## Scope

PclIO parses files that can come from untrusted sources. Problems that matter most:

- crashes, hangs or unbounded memory use caused by a crafted point cloud file or project (declared sizes and counts that are not
  checked, cyclic or deeply nested structures, decompression bombs, endless loops);
- reading or writing files outside the folder a command was given (path traversal through a file name stored inside a
  project file, or through an output path; the containment check on stored names is lexical and does not resolve symbolic links);
- any behaviour that executes code or contacts the network (the library does neither).
