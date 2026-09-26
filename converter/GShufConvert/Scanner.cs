using System.Text.Json;
using MaterialLib;

namespace GShufConvert;

/// <summary>One picture found in a source.</summary>
public sealed class FoundImage
{
    public required string Category { get; init; }
    public required string Diffuse { get; init; }          // source path relative to Data
    public string? Normal { get; init; }                    // source path relative to Data, when the source provides one
    public string? Slot { get; init; }                      // vanilla material this image replaces
    public string? MaterialSource { get; init; }            // standalone BGSM path relative to Data
    public int Width { get; init; }
    public int Height { get; init; }
}

public static class Scanner
{
    private static readonly string[] ExcludeWords = ["frame", "_n.", "_s.", "_g.", "_e.", "_p.", "normal", "spec", "glow", "wood", "metal", "glass", "lod"];
    private static readonly (string Word, string Category)[] CategoryWords =
    [
        ("magazine", "magazines"), ("comic", "magazines"), ("poster", "posters"), ("sign", "signage"), ("billboard", "signage"),
        ("painting", "paintings"), ("pinup", "paintings"), ("canvas", "paintings"), ("portrait", "paintings"), ("art", "paintings"),
    ];

    /// <summary>Finds every picture in the source: vanilla replacers by path, standalone art by material, or a gshuf-pack.json manifest.</summary>
    public static List<FoundImage> Scan(Source source, Catalog catalog, Report report)
    {
        var manifest = source.Files.Values.FirstOrDefault(f => Path.GetFileName(f.DataPath).Equals("gshuf-pack.json", StringComparison.OrdinalIgnoreCase));
        if (manifest != null) return FromManifest(source, manifest, catalog, report);

        var images = new Dictionary<string, FoundImage>(StringComparer.OrdinalIgnoreCase);
        var usedDiffuse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Replacer textures sitting at vanilla art paths.
        foreach (var file in source.Files.Values)
        {
            if (!file.DataPath.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase) || !file.DataPath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) continue;
            var relative = Paths.Normalize(file.DataPath, "textures");
            if (!catalog.ByDiffuse.TryGetValue(relative, out var slot)) continue;
            var dims = file.Dimensions() ?? (slot.Width, slot.Height);
            foreach (var s in catalog.Slots.Where(s => s.Diffuse == relative))
            {
                var normalPath = Paths.WithRoot(s.Normal, "textures");
                images[s.Material] = new FoundImage
                {
                    Category = s.Category, Diffuse = file.DataPath, Slot = s.Material, Width = dims.Width, Height = dims.Height,
                    Normal = source.Files.ContainsKey(normalPath) ? normalPath : null,
                };
            }
            usedDiffuse.Add(file.DataPath);
        }

        // Materials: vanilla slot materials that point at new textures, and standalone materials.
        var standalone = new List<(SourceFile File, BGSM Material)>();
        foreach (var file in source.Files.Values)
        {
            if (!file.DataPath.StartsWith("materials\\", StringComparison.OrdinalIgnoreCase) || !file.DataPath.EndsWith(".bgsm", StringComparison.OrdinalIgnoreCase)) continue;
            BGSM material;
            try
            {
                material = new BGSM();
                using var reader = new BinaryReader(new MemoryStream(file.Read()));
                material.Deserialize(reader);
            }
            catch (Exception e)
            {
                report.Warn($"{source.Name}: cannot read material {file.DataPath}: {e.Message}");
                continue;
            }
            var relative = Paths.Normalize(file.DataPath, "materials");
            var diffuse = Paths.Normalize(material.DiffuseTexture ?? "", "textures");
            if (diffuse.Length == 0) continue;
            if (catalog.ByMaterial.TryGetValue(relative, out var slot))
            {
                if (diffuse == slot.Diffuse || images.ContainsKey(slot.Material)) continue;
                var diffusePath = Paths.WithRoot(diffuse, "textures");
                if (!source.Files.TryGetValue(diffusePath, out var texture))
                {
                    report.Warn($"{source.Name}: {file.DataPath} points at {diffusePath}, which the mod does not contain; skipped");
                    continue;
                }
                var dims = texture.Dimensions() ?? (slot.Width, slot.Height);
                var normal = Paths.Normalize(material.NormalTexture ?? "", "textures");
                var normalPath = Paths.WithRoot(normal, "textures");
                images[slot.Material] = new FoundImage
                {
                    Category = slot.Category, Diffuse = diffusePath, Slot = slot.Material, Width = dims.Width, Height = dims.Height,
                    Normal = normal.Length > 0 && normal != slot.Normal && source.Files.ContainsKey(normalPath) ? normalPath : null,
                };
                usedDiffuse.Add(diffusePath);
                continue;
            }
            standalone.Add((file, material));
        }

        int missingTexture = 0, excluded = 0, noDims = 0, duplicate = 0;
        string? missingExample = null;
        foreach (var (file, material) in standalone)
        {
            var diffuse = Paths.Normalize(material.DiffuseTexture ?? "", "textures");
            var diffusePath = Paths.WithRoot(diffuse, "textures");
            if (usedDiffuse.Contains(diffusePath) || catalog.ByDiffuse.ContainsKey(diffuse)) { duplicate++; continue; }
            if (!source.Files.TryGetValue(diffusePath, out var texture)) { missingTexture++; missingExample ??= $"{file.DataPath} -> {diffusePath}"; continue; }
            var lowered = (file.DataPath + " " + diffusePath).ToLowerInvariant();
            if (ExcludeWords.Any(w => Path.GetFileName(diffusePath).ToLowerInvariant().Contains(w))) { excluded++; continue; }
            var dims = texture.Dimensions();
            if (dims == null) { noDims++; continue; }
            var category = CategoryWords.FirstOrDefault(cw => lowered.Contains(cw.Word)).Category
                ?? (dims.Value.Width >= dims.Value.Height * 1.8 ? "paintings" : "posters");
            var normal = Paths.Normalize(material.NormalTexture ?? "", "textures");
            var normalPath = Paths.WithRoot(normal, "textures");
            usedDiffuse.Add(diffusePath);
            images[diffusePath] = new FoundImage
            {
                Category = category, Diffuse = diffusePath, MaterialSource = file.DataPath, Width = dims.Value.Width, Height = dims.Value.Height,
                Normal = normal.Length > 0 && source.Files.ContainsKey(normalPath) && !catalog.Slots.Any(s => s.Normal == normal) ? normalPath : null,
            };
        }

        if (standalone.Count > 0)
        {
            report.Info($"{source.Name}: {standalone.Count} standalone materials: {images.Count} pictures, {duplicate} sharing a texture, {missingTexture} without their texture{(missingExample != null ? $" (e.g. {missingExample})" : "")}, {excluded} excluded by name, {noDims} unreadable");
        }
        return images.Values.ToList();
    }

    /// <summary>The main menu video a mod ships (Video\MainMenuLoop.bk2), as a source path, or null.</summary>
    public static string? FindMenuVideo(Source source)
    {
        return source.Files.Keys.FirstOrDefault(k => k.Equals("video\\mainmenuloop.bk2", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reads an explicit pack manifest: {"images":[{"texture":"Textures\\...","category":"paintings","slot":"optional vanilla material"}]}.</summary>
    private static List<FoundImage> FromManifest(Source source, SourceFile manifest, Catalog catalog, Report report)
    {
        var images = new List<FoundImage>();
        try
        {
            using var doc = JsonDocument.Parse(manifest.Read());
            foreach (var row in doc.RootElement.GetProperty("images").EnumerateArray())
            {
                var texture = (row.GetProperty("texture").GetString() ?? "").Replace('/', '\\');
                if (!texture.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase)) texture = "textures\\" + texture;
                if (!source.Files.TryGetValue(texture, out var file))
                {
                    report.Warn($"{source.Name}: manifest texture missing: {texture}");
                    continue;
                }
                var slot = row.TryGetProperty("slot", out var s) ? Paths.Normalize(s.GetString() ?? "", "materials") : "";
                var category = row.TryGetProperty("category", out var c) ? (c.GetString() ?? "").ToLowerInvariant() : "";
                if (slot.Length > 0 && catalog.ByMaterial.TryGetValue(slot, out var vanilla)) category = vanilla.Category;
                if (category.Length == 0) category = "paintings";
                var dims = file.Dimensions() ?? (0, 0);
                images.Add(new FoundImage { Category = category, Diffuse = texture, Slot = slot.Length > 0 ? slot : null, Width = dims.Width, Height = dims.Height });
            }
            report.Info($"{source.Name}: manifest lists {images.Count} images");
        }
        catch (Exception e)
        {
            report.Warn($"{source.Name}: cannot read gshuf-pack.json: {e.Message}");
        }
        return images;
    }
}
