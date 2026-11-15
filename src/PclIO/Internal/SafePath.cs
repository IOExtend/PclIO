namespace PclIO.Internal;

/// <summary>
/// Resolves paths that come from file contents and rejects those that lexically leave their base directory.
/// Symbolic links and junctions inside the base directory are not resolved.
/// </summary>
internal static class SafePath
{
    private static readonly char[] ExtraInvalid = ['*', '?', ':'];

    /// <summary>
    /// Joins an untrusted relative path to <paramref name="baseDirectory"/> and returns the full path.
    /// Throws <see cref="InvalidDataException"/> when the input is empty, rooted, a UNC or device path,
    /// contains invalid characters, or resolves outside the base directory. The check is lexical; symbolic links are not followed.
    /// </summary>
    public static string Combine(string baseDirectory, string untrustedRelativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseDirectory);

        if (string.IsNullOrWhiteSpace(untrustedRelativePath))
            throw FormatError.Corrupted("referenced path is empty.");

        if (untrustedRelativePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || untrustedRelativePath.IndexOfAny(ExtraInvalid) >= 0)
            throw FormatError.Corrupted("referenced path contains invalid characters.");

        // Treat both separators alike on every OS: a leading one means rooted, UNC or device.
        string relative = untrustedRelativePath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        if (relative[0] == Path.DirectorySeparatorChar || Path.IsPathRooted(relative))
            throw FormatError.Corrupted("referenced path must be relative.");

        string baseFull;
        string full;
        try
        {
            baseFull = Path.GetFullPath(baseDirectory);
            full = Path.GetFullPath(Path.Combine(baseFull, relative));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw FormatError.Corrupted("referenced path is not a valid path.");
        }

        if (!Path.EndsInDirectorySeparator(baseFull)) baseFull += Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(baseFull, comparison))
            throw FormatError.Corrupted("referenced path resolves outside the project directory.");

        return full;
    }
}
