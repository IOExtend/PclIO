using System.Text;

namespace PclIO.Tests;

/// <summary>A unique folder in the system temporary directory that is removed when the test is done.</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "pclio-tests-" + Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Root);

    /// <summary>The path of a file or folder inside the temporary folder (nothing is created).</summary>
    public string PathOf(string relative) => Path.Combine(Root, relative);

    /// <summary>Writes bytes to a file inside the temporary folder, creating missing folders, and returns its path.</summary>
    public string Write(string relative, byte[] bytes)
    {
        string path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Writes UTF-8 text (no BOM) to a file inside the temporary folder and returns its path.</summary>
    public string WriteText(string relative, string text) => Write(relative, new UTF8Encoding(false).GetBytes(text));

    /// <summary>Removes the folder. A leftover scratch folder must not fail a test, but it is reported on standard error.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only the folder name and the kind of error are printed, not the path of the machine's temporary folder.
            Console.Error.WriteLine($"warning: could not remove the temporary folder '{Path.GetFileName(Root)}' ({ex.GetType().Name}).");
        }
    }
}
