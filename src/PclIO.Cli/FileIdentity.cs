using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PclIO.CommandLine;

/// <summary>Tells whether two existing paths lead to the same file, however each one is spelled.</summary>
internal static class FileIdentity
{
    /// <summary>
    /// True when both paths are the same file. Windows has spellings that no string comparison can resolve (the
    /// verbatim and device prefixes, administrative shares), so the volume and file index are compared there.
    /// Elsewhere links are already followed by the caller, so this returns false.
    /// </summary>
    public static bool IsSameFile(string first, string second)
    {
        if (!OperatingSystem.IsWindows()) return false;

        return Read(first) == Read(second);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416", Justification = "Called on Windows only.")]
    private static (uint Volume, uint IndexHigh, uint IndexLow) Read(string path)
    {
        // Sharing is fully open so that a file another process holds does not make the comparison fail.
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation info))
            throw new IOException($"cannot read the file identity ({Marshal.GetLastPInvokeError()}).");

        return (info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    // BY_HANDLE_FILE_INFORMATION; the three file times are kept as pairs of 32-bit values to match the native packing.
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
