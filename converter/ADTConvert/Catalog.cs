using System.Text.Json;

namespace ADTConvert;

public sealed class Slot
{
    public required string Material { get; init; }   // relative to Materials, lowercase, backslashes
    public required string Category { get; init; }
    public required string Diffuse { get; init; }    // relative to Textures, lowercase, backslashes
    public required string Normal { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool Blank { get; init; }
}

/// <summary>The vanilla art slots (vanilla-slots.json), indexed by material path and by diffuse texture path.</summary>
public sealed class Catalog
{
    public List<Slot> Slots { get; } = [];
    public Dictionary<string, Slot> ByMaterial { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Slot> ByDiffuse { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static Catalog Load(string path)
    {
        var catalog = new Catalog();
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            var slot = new Slot
            {
                Material = Paths.Normalize(row.GetProperty("material").GetString() ?? "", "materials"),
                Category = (row.GetProperty("category").GetString() ?? "").ToLowerInvariant(),
                Diffuse = Paths.Normalize(row.GetProperty("diffuse").GetString() ?? "", "textures"),
                Normal = Paths.Normalize(row.TryGetProperty("normal", out var n) ? n.GetString() ?? "" : "", "textures"),
                Width = row.TryGetProperty("width", out var w) ? w.GetInt32() : 0,
                Height = row.TryGetProperty("height", out var h) ? h.GetInt32() : 0,
                Blank = (row.GetProperty("diffuse").GetString() ?? "").Contains("paintingcanvas", StringComparison.OrdinalIgnoreCase) || (row.GetProperty("material").GetString() ?? "").Contains("blank", StringComparison.OrdinalIgnoreCase),
            };
            if (slot.Material.Length == 0) continue;
            catalog.Slots.Add(slot);
            catalog.ByMaterial[slot.Material] = slot;
            if (slot.Diffuse.Length > 0 && !catalog.ByDiffuse.ContainsKey(slot.Diffuse)) catalog.ByDiffuse[slot.Diffuse] = slot;
        }
        return catalog;
    }
}

public static class Paths
{
    /// <summary>Lowercases, converts slashes and strips a leading Data\ and the given root folder (materials or textures).</summary>
    public static string Normalize(string path, string root)
    {
        var p = path.Replace('/', '\\').ToLowerInvariant().TrimStart('\\');
        if (p.StartsWith("data\\")) p = p[5..];
        if (p.StartsWith(root + "\\")) p = p[(root.Length + 1)..];
        return p;
    }

    /// <summary>Path relative to Data with the root folder in front, for lookups in source file maps.</summary>
    public static string WithRoot(string relative, string root) => root + "\\" + relative;
}
