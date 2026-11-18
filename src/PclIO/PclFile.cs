using PclIO.Formats.E57;
using PclIO.Formats.EdgeWise;
using PclIO.Formats.Faro;
using PclIO.Formats.Las;
using PclIO.Formats.Laz;
using PclIO.Formats.Leica;
using PclIO.Formats.Pcd;
using PclIO.Formats.PinPoint;
using PclIO.Formats.Ply;
using PclIO.Formats.Potree;
using PclIO.Formats.Pts;
using PclIO.Formats.Ptx;
using PclIO.Formats.Recap;
using PclIO.Formats.Sbf;
using PclIO.Formats.Tiles3d;
using PclIO.Formats.Xyz;
using PclIO.Internal;

namespace PclIO;

/// <summary>Static entry point for point cloud format detection and document opening.</summary>
public static class PclFile
{
    /// <summary>
    /// Detects the format of a file or directory from its signature, extension or directory layout.
    /// Returns <see cref="PclFileKind.Unknown"/> for anything the library does not read.
    /// </summary>
    /// <param name="path">Path of a file or directory.</param>
    /// <exception cref="FileNotFoundException">The path is neither an existing file nor a directory.</exception>
    public static PclFileKind DetectKind(string path) => KindDetector.Detect(path);

    /// <summary>
    /// Detects the format from the first bytes of a seekable stream and an optional file name or extension.
    /// The stream position is restored.
    /// </summary>
    /// <param name="stream">A seekable stream positioned at the start of the data.</param>
    /// <param name="extension">File name or extension (with leading dot) used where the format is identified by extension.</param>
    /// <exception cref="NotSupportedException">The stream is not seekable.</exception>
    public static PclFileKind DetectKind(Stream stream, string? extension = null) => KindDetector.Detect(stream, extension);

    /// <summary>Opens a point cloud document from disk, choosing the reader with <see cref="DetectKind(string)"/>.</summary>
    /// <param name="path">Path of a file or directory.</param>
    /// <param name="options">Read options, or null for <see cref="PclReadOptions.Default"/>.</param>
    /// <exception cref="FileNotFoundException">The path is neither an existing file nor a directory.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory the reader needs is missing or disappears while reading.</exception>
    /// <exception cref="NotSupportedException">The format is not recognized or uses an unsupported feature.</exception>
    /// <exception cref="InvalidDataException">The file is malformed or truncated.</exception>
    public static PclDocument Open(string path, PclReadOptions? options = null)
    {
        options ??= PclReadOptions.Default;
        PclFileKind kind = DetectKind(path);
        return kind switch
        {
            PclFileKind.E57 => E57Document.Open(path, options),
            PclFileKind.RecapProject => RcpProjectDocument.Open(path, options),
            PclFileKind.RecapScan => RcsScanDocument.Open(path, options),
            PclFileKind.LeicaLgsx or PclFileKind.LeicaLgs => LgsxDocument.Open(path, options),
            PclFileKind.FaroFls => FaroFlsDocument.Open(path, options),
            PclFileKind.PinPoint => PinPointDocument.Open(path, options),
            PclFileKind.EdgeWise => EdgeWiseDocument.Open(path, options),
            PclFileKind.Las => LasDocument.Open(path, options),
            PclFileKind.Pcd => PcdDocument.Open(path, options),
            PclFileKind.Ply => PlyDocument.Open(path, options),
            PclFileKind.Pts => PtsDocument.Open(path, options),
            PclFileKind.Ptx => PtxDocument.Open(path, options),
            PclFileKind.Xyz => XyzDocument.Open(path, options),
            PclFileKind.Sbf => SbfDocument.Open(path, options),
            PclFileKind.Tiles3d => Tiles3dDocument.Open(path, options),
            PclFileKind.Potree => PotreeDocument.Open(path, options),
            PclFileKind.Laz => LazDocument.Open(path, options),
            _ => throw new NotSupportedException($"Unsupported or unrecognized point cloud file: '{path}'"),
        };
    }
}
