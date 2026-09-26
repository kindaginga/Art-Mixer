using System.Text;
using System.Text.Json;
using MaterialLib;

namespace GShufConvert;

public sealed class PackOutput
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public List<ImageOutput> Images { get; } = [];
}

public sealed class ImageOutput
{
    public required string Category { get; init; }
    public required string Material { get; init; }  // relative to Materials
    public string? Slot { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}

/// <summary>Writes the "Gallery Shuffle Packs" mod folder: materials, textures, packs.json and the MCM pages.</summary>
public sealed class Writer
{
    private readonly string _out;
    private readonly Catalog _catalog;
    private readonly GameFiles _game;
    private readonly Report _report;

    public Writer(string outputFolder, Catalog catalog, GameFiles game, Report report)
    {
        _out = outputFolder;
        _catalog = catalog;
        _game = game;
        _report = report;
    }

    public PackOutput WritePack(Source source, List<FoundImage> images, HashSet<string> usedIds)
    {
        var id = Sources.Sanitize(source.Name);
        if (id.Length > 40) id = id[..32] + "-" + ShortHash(id);
        var baseId = id;
        for (var n = 2; usedIds.Contains(id); n++) id = $"{baseId}-{n}";
        usedIds.Add(id);
        var pack = new PackOutput { Id = id, Name = source.Name };
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in images.OrderBy(i => i.Slot ?? i.Diffuse, StringComparer.OrdinalIgnoreCase))
        {
            var stem = Path.GetFileNameWithoutExtension(image.Diffuse);
            if (stem.EndsWith("_d", StringComparison.OrdinalIgnoreCase)) stem = stem[..^2];
            var bar = image.Diffuse.IndexOf('|');
            // Pooled FOMOD options prefix a short hash of the option name; the engine's file paths stay under MAX_PATH.
            if (bar > 0) stem = ShortHash(image.Diffuse[..bar]) + "-" + stem;
            stem = Sources.Sanitize(stem);
            if (stem.Length > 48) stem = stem[..40] + "-" + ShortHash(stem);
            var unique = stem;
            for (var n = 2; !names.Add(unique); n++) unique = $"{stem}-{n}";
            var textureRel = $"GShuf\\Packs\\{id}\\{unique}_d.dds";
            var materialRel = $"GShuf\\Packs\\{id}\\{unique}.bgsm";
            WriteFile(Path.Combine("Textures", textureRel), source.Files[image.Diffuse].Read());
            string? normalRel = null;
            if (image.Normal != null && source.Files.TryGetValue(image.Normal, out var normalFile))
            {
                normalRel = $"GShuf\\Packs\\{id}\\{unique}_n.dds";
                WriteFile(Path.Combine("Textures", normalRel), normalFile.Read());
            }
            var material = TemplateMaterial(image, source);
            if (material == null) continue;
            material.DiffuseTexture = textureRel.Replace('\\', '/');
            if (normalRel != null) material.NormalTexture = normalRel.Replace('\\', '/');
            WriteMaterial(Path.Combine("Materials", materialRel), material);
            pack.Images.Add(new ImageOutput { Category = image.Category, Material = materialRel, Slot = image.Slot, Width = image.Width, Height = image.Height });
        }
        _report.Info($"pack '{pack.Name}' ({pack.Id}): {pack.Images.Count} images in {pack.Images.Select(i => i.Category).Distinct().Count()} categories");
        return pack;
    }

    /// <summary>The vanilla pack: every catalogued vanilla picture copied out of the player's own archives.</summary>
    public PackOutput WriteVanillaPack()
    {
        var pack = new PackOutput { Id = "vanilla", Name = "Vanilla" };
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in _catalog.Slots)
        {
            if (slot.Blank || slot.Diffuse.Length == 0) continue;
            var materialBytes = _game.ReadMaterial(slot.Material);
            var textureBytes = _game.ReadTexture(slot.Diffuse);
            if (materialBytes == null || textureBytes == null)
            {
                _report.Warn($"vanilla: missing {(materialBytes == null ? slot.Material : slot.Diffuse)} in the game archives");
                continue;
            }
            var stem = Sources.Sanitize(Path.GetFileNameWithoutExtension(slot.Diffuse).Replace("_d", ""));
            var textureRel = $"GShuf\\Packs\\vanilla\\{stem}_d.dds";
            if (done.Add(textureRel)) WriteFile(Path.Combine("Textures", textureRel), textureBytes);
            var materialRel = $"GShuf\\Packs\\vanilla\\{Sources.Sanitize(Path.GetFileNameWithoutExtension(slot.Material))}.bgsm";
            var material = new BGSM();
            using (var reader = new BinaryReader(new MemoryStream(materialBytes))) material.Deserialize(reader);
            material.DiffuseTexture = textureRel.Replace('\\', '/');
            WriteMaterial(Path.Combine("Materials", materialRel), material);
            pack.Images.Add(new ImageOutput { Category = slot.Category, Material = materialRel, Slot = slot.Material, Width = slot.Width, Height = slot.Height });
        }
        _report.Info($"pack 'Vanilla': {pack.Images.Count} images copied from the game archives");
        return pack;
    }

    public void WritePacksJson(List<PackOutput> packs)
    {
        var doc = new
        {
            version = 1,
            generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            packs = packs.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                images = p.Images.Select(i => i.Slot != null
                    ? (object)new { category = i.Category, material = i.Material, slot = i.Slot, width = i.Width, height = i.Height }
                    : new { category = i.Category, material = i.Material, width = i.Width, height = i.Height }),
            }),
        };
        WriteFile(Path.Combine("GShuf", "packs.json"), JsonSerializer.SerializeToUtf8Bytes(doc, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Generates the MCM pages: one per category with a slider per pack and the derived Vanilla slider.</summary>
    public void WriteMcm(List<PackOutput> packs, int slotsPerCategory)
    {
        string[] categories = ["paintings", "posters", "signage", "magazines", "quest"];
        string[] titles = ["Paintings", "Posters", "Signage", "Magazines", "Quest"];
        var pages = new List<object>();
        var settings = new StringBuilder("[Main]\nbBlankCanvases=0\niSeed=1\n");
        for (var c = 0; c < categories.Length; c++)
        {
            var eligible = packs.Where(p => p.Id != "vanilla" && p.Images.Any(i => i.Category == categories[c])).ToList();
            var slots = eligible.Take(slotsPerCategory).ToList();
            if (eligible.Count > slotsPerCategory)
            {
                _report.Warn($"{titles[c]}: {eligible.Count} packs but only {slotsPerCategory} slider slots; without a slider: {string.Join(", ", eligible.Skip(slotsPerCategory).Select(p => p.Name))}. Convert fewer mods or fewer FOMOD sets for this category.");
            }
            settings.Append($"\n[{titles[c]}]\n");
            for (var s = 1; s <= slotsPerCategory; s++) settings.Append($"iSlot{s}=0\n");
            settings.Append("iVanilla=100\n");
            if (slots.Count == 0) continue;
            var content = new List<object>
            {
                new { type = "text", text = $"Share of {titles[c].ToLowerInvariant()} each pack receives. The shares add up to 100; whatever is left stays vanilla.", html = false },
            };
            for (var s = 0; s < slots.Count; s++)
            {
                var count = slots[s].Images.Count(i => i.Category == categories[c]);
                content.Add(new
                {
                    id = $"iSlot{s + 1}:{titles[c]}",
                    type = "slider",
                    text = slots[s].Name,
                    help = $"{count} pictures. Applies when an area loads.",
                    valueOptions = new { min = 0, max = 100, step = 1, sourceType = "ModSettingInt" },
                });
            }
            content.Add(new
            {
                id = $"iVanilla:{titles[c]}",
                type = "slider",
                text = "Vanilla (remainder)",
                help = "What the pack shares leave over. It follows the other sliders and cannot be set directly.",
                valueOptions = new { min = 0, max = 100, step = 1, sourceType = "ModSettingInt" },
            });
            pages.Add(new { pageDisplayName = titles[c], content });
        }
        var config = new
        {
            modName = "GalleryShuffle",
            displayName = "Gallery Shuffle",
            minMcmVersion = 2,
            pluginRequirements = new[] { "GalleryShuffle.esl" },
            content = new object[]
            {
                new { type = "text", text = "<font size='24'>Gallery Shuffle</font>", html = true },
                new { type = "text", text = $"{packs.Count(p => p.Id != "vanilla")} art packs converted on {DateTime.Now:yyyy-MM-dd}. Open a category page to set each pack's share.", html = false },
                new { type = "spacer", numLines = 1 },
                new { type = "section", text = "Options" },
                new { id = "bBlankCanvases:Main", type = "switcher", text = "Put art on blank canvases", help = "When on, empty canvases and frames also receive pack art. Applies when an area loads.", valueOptions = new { sourceType = "ModSettingBool" } },
                new { type = "button", text = "Shuffle layout", help = "Picks a new arrangement with the same pack shares. It appears when an area loads (fast travel, doors, or loading a save).", action = new { type = "CallFunction", form = "GalleryShuffle.esl|800", function = "Shuffle" } },
            },
            pages,
        };
        WriteFile(Path.Combine("MCM", "Config", "GalleryShuffle", "config.json"), JsonSerializer.SerializeToUtf8Bytes(config, new JsonSerializerOptions { WriteIndented = true }));
        WriteFile(Path.Combine("MCM", "Config", "GalleryShuffle", "settings.ini"), Encoding.ASCII.GetBytes(settings.ToString()));
    }

    public void WriteReport()
    {
        WriteFile(Path.Combine("GShuf", "convert-report.txt"), Encoding.UTF8.GetBytes(_report.ToString()));
    }

    private static string ShortHash(string text)
    {
        return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToLowerInvariant())))[..8].ToLowerInvariant();
    }

    private BGSM? TemplateMaterial(FoundImage image, Source source)
    {
        byte[]? bytes = null;
        if (image.MaterialSource != null && source.Files.TryGetValue(image.MaterialSource, out var own)) bytes = own.Read();
        else if (image.Slot != null) bytes = _game.ReadMaterial(image.Slot);
        bytes ??= _game.ReadMaterial(_catalog.Slots.First(s => s.Category == image.Category && !s.Blank).Material);
        if (bytes == null)
        {
            _report.Warn($"no template material available for {image.Diffuse}; skipped");
            return null;
        }
        var material = new BGSM();
        using var reader = new BinaryReader(new MemoryStream(bytes));
        material.Deserialize(reader);
        return material;
    }

    private void WriteMaterial(string relative, BGSM material)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.Latin1, leaveOpen: true)) material.Serialize(writer);
        WriteFile(relative, stream.ToArray());
    }

    private void WriteFile(string relative, byte[] bytes)
    {
        // The game resolves loose files as <game>\Data\<relative>; Windows path limits apply there, not in this tool.
        if (relative.Length > 150) _report.Warn($"path is {relative.Length} characters, which may exceed the game's path limit: {relative}");
        var path = Path.Combine(_out, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }
}

/// <summary>Vanilla materials and textures read from the player's own game archives.</summary>
public sealed class GameFiles
{
    private readonly List<Ba2Archive> _materials = [];
    private readonly List<Ba2Archive> _textures = [];

    public GameFiles(string gameFolder, Report report)
    {
        var data = Path.Combine(gameFolder, "Data");
        // Fallout4 archives first, then the DLC archives, so base game files win when both carry a path.
        foreach (var path in Directory.EnumerateFiles(data, "*.ba2").OrderBy(p => Path.GetFileName(p).StartsWith("Fallout4 - ", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(p => p))
        {
            var name = Path.GetFileName(path);
            if (!name.StartsWith("Fallout4 - ", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("DLC", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (name.Contains(" - Materials", StringComparison.OrdinalIgnoreCase)) _materials.Add(new Ba2Archive(path));
                else if (name.Contains(" - Textures", StringComparison.OrdinalIgnoreCase)) _textures.Add(new Ba2Archive(path));
            }
            catch (Exception e)
            {
                report.Warn($"cannot read {name}: {e.Message}");
            }
        }
        report.Info($"game archives: {_materials.Count} material and {_textures.Count} texture archives in {data}");
    }

    public byte[]? ReadMaterial(string relative) => Read(_materials, "materials\\" + relative);
    public byte[]? ReadTexture(string relative) => Read(_textures, "textures\\" + relative);

    private static byte[]? Read(List<Ba2Archive> archives, string path)
    {
        foreach (var archive in archives)
        {
            if (archive.Contains(path)) return archive.Read(path);
        }
        return null;
    }
}
