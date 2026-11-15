namespace PclIO.Internal;

internal static class FormatError
{
    public static InvalidDataException Corrupted(string message) => new($"Malformed point cloud file: {message}");

    public static InvalidDataException Truncated(string expected) => new($"Point cloud file is truncated; expected {expected}.");

    public static NotSupportedException Unsupported(string message) => new($"Unsupported point cloud feature: {message}");
}
