# Third-party notices

PclIO uses the following components.

| Component | Use | License |
|---|---|---|
| [ZstdSharp.Port](https://github.com/oleg-st/ZstdSharp) 0.8.8 | Zstandard decompression of point blocks in Leica scan containers | MIT, copyright Oleg Stepanischev 2026 (as declared in the package) |

ZstdSharp.Port is referenced as an unmodified NuGet package; PclIO contains none of its source code. The release archives of PclIO and PclIO.Cli contain its assembly, `ZstdSharp.dll`, next to this file. The package describes itself as a port of the zstd compression library to C#, names the repository above as its source, and declares the MIT license expression. It is derived from zstd, so the copyright and license notices of zstd that apply to it are those published in that repository; see it for the full license terms.

The MIT License terms of ZstdSharp.Port, with the copyright line as declared in the package:

> Copyright (c) 2026 Oleg Stepanischev
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

Development and build-time packages (xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk, Microsoft.SourceLink.GitHub) are not part of the published packages.

ASTM E57, ASPRS LAS, Zstandard and the compound file (OLE2) container are names of file formats and standards. They are used only to say which formats PclIO reads or writes.

Autodesk and ReCap are trademarks of Autodesk, Inc. Leica and Leica Geosystems, FARO, EdgeWise, PointCab and PinPoint are trademarks of their respective holders. PclIO is not affiliated with, endorsed by or sponsored by Autodesk, Leica Geosystems, FARO, ClearEdge3D, PointCab or any other vendor named in this repository. The names are used only to say which file formats PclIO reads. No vendor code or binaries are included.
