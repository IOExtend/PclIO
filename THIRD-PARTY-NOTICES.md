# Third-party notices

PclIO has no third-party runtime components: the published packages depend on no other package, and no third-party source code or binaries are included. The Zstandard and Snappy decoders it needs are its own code, written from the public format specifications (RFC 8878 for Zstandard, including its XXH64 content checksum, and the Snappy format description).

Development and build-time packages (xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk, Microsoft.SourceLink.GitHub) are not part of the published packages.

ASTM E57, ASPRS LAS, Zstandard, Snappy and the compound file (OLE2) container are names of file formats and standards. They are used only to say which formats PclIO reads or writes.

Autodesk and ReCap are trademarks of Autodesk, Inc. Leica and Leica Geosystems, FARO, EdgeWise, PointCab and PinPoint are trademarks of their respective holders. PclIO is not affiliated with, endorsed by or sponsored by Autodesk, Leica Geosystems, FARO, ClearEdge3D, PointCab or any other vendor named in this repository. The names are used only to say which file formats PclIO reads. No vendor code or binaries are included.
