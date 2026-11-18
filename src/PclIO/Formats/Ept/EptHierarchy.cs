using System.Text.Json;
using PclIO.Internal;

namespace PclIO.Formats.Ept;

/// <summary>Discovers and traverses the octree hierarchy of an Entwine Point Tile dataset.</summary>
internal static class EptHierarchy
{
    private const int MaxHierarchyPages = 10000;
    private const int MaxPageBytes = 16 * 1024 * 1024; // 16 MB max per hierarchy JSON

    public static List<EptNode> ReadHierarchy(string datasetDir, PclBounds rootBounds, string dataType)
    {
        string ext = dataType.ToLowerInvariant() switch
        {
            "laszip" => ".laz",
            "binary" => ".bin",
            "zstandard" => ".zst",
            _ => ".laz"
        };

        var nodes = new List<EptNode>();
        string hierDir = Path.Combine(datasetDir, "ept-hierarchy");
        string dataDir = Path.Combine(datasetDir, "ept-data");

        if (Directory.Exists(hierDir))
        {
            string rootHierFile = Path.Combine(hierDir, "0-0-0-0.json");
            if (File.Exists(rootHierFile))
            {
                var pagesToRead = new Queue<string>();
                var visited = new HashSet<string>(StringComparer.Ordinal);
                pagesToRead.Enqueue("0-0-0-0");

                while (pagesToRead.Count > 0)
                {
                    string pageKey = pagesToRead.Dequeue();
                    if (!visited.Add(pageKey))
                        throw FormatError.Corrupted($"Cyclic EPT hierarchy reference detected at node '{pageKey}'.");

                    if (visited.Count > MaxHierarchyPages)
                        throw FormatError.Corrupted($"EPT hierarchy exceeds safety limit of {MaxHierarchyPages} pages.");

                    string pagePath = Path.Combine(hierDir, pageKey + ".json");
                    if (!File.Exists(pagePath)) continue;

                    var fi = new FileInfo(pagePath);
                    if (fi.Length > MaxPageBytes)
                        throw FormatError.Corrupted($"EPT hierarchy file '{pageKey}.json' exceeds size limit of {MaxPageBytes} bytes.");

                    byte[] pageBytes = File.ReadAllBytes(pagePath);
                    using var doc = JsonDocument.Parse(pageBytes);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        throw FormatError.Corrupted($"EPT hierarchy page '{pageKey}.json' must be a JSON object.");

                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        string nodeKeyStr = prop.Name;
                        long count = prop.Value.GetInt64();

                        if (!EptKey.TryParse(nodeKeyStr, out var nodeKey)) continue;

                        if (count > 0)
                        {
                            var bounds = nodeKey.GetBounds(rootBounds);
                            string tilePath = Path.Combine(dataDir, nodeKeyStr + ext);
                            nodes.Add(new EptNode(nodeKey, bounds, File.Exists(tilePath) ? tilePath : null, count));
                        }
                        else if (count == -1)
                        {
                            pagesToRead.Enqueue(nodeKeyStr);
                        }
                    }
                }
            }
        }

        // If no nodes discovered from ept-hierarchy, fall back to enumerating tiles in ept-data
        if (nodes.Count == 0 && Directory.Exists(dataDir))
        {
            var files = Directory.EnumerateFiles(dataDir, "*" + ext, SearchOption.AllDirectories);
            foreach (string file in files)
            {
                string baseName = Path.GetFileNameWithoutExtension(file);
                if (EptKey.TryParse(baseName, out var nodeKey))
                {
                    var bounds = nodeKey.GetBounds(rootBounds);
                    nodes.Add(new EptNode(nodeKey, bounds, file, -1));
                }
            }
        }

        return nodes;
    }
}
