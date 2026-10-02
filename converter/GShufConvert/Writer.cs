using System.Text;
using System.Text.Json;
using MaterialLib;

namespace GShufConvert;

public sealed class PackOutput
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public List<ImageOutput> Images { get; } = [];
    /// <summary>Main menu video relative to Video, when the mod ships one.</summary>
    public string? Video { get; set; }
    /// <summary>Main menu theme relative to Music, when the mod ships one.</summary>
    public string? Music { get; set; }

    /// <summary>Interface files the pack replaces: the game path, the pack's copy (relative to Data) and the pick it follows.</summary>
    public List<(string Path, string File, string Category)> Movies { get; } = [];

    /// <summary>Loading screens this pack shows with a card of their own model (the vanilla pack's).</summary>
    public List<ScreenCard> Screens { get; } = [];

    public bool Covers(string category) => Images.Any(i => i.Category == category) || Movies.Any(m => m.Category == category);
    public int CountIn(string category) => Images.Count(i => i.Category == category) + Movies.Count(m => m.Category == category);
}

/// <summary>A loading screen's vanilla card: the screen, the card model (relative to Meshes), the form the game's screen shows, that form's game model and swap, and its color remap.</summary>
public sealed record ScreenCard(string Screen, string Card, string Model, string GameModel, string? Swap, float? Color);

/// <summary>A slot outside the art catalog; Restore is the vanilla look rebuilt as a material file, for inline shaders, and
/// Label names the object of a plugin-reassigned model.</summary>
public sealed record ExtraSlot(string Material, string Category, string Diffuse, int Width, int Height, string Restore, string Label = "");

public sealed class ImageOutput
{
    public required string Category { get; set; }
    public required string Material { get; init; }  // relative to Materials
    public string? Slot { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool DropSwap { get; init; }  // the object loads the model without its vanilla material swap
    public string? Name { get; init; }   // the name the object shows while this pack's version shows there
    public short[]? Bounds { get; init; } // object bounds the pack's version needs (min x, y, z, max x, y, z)
    public string? SwapForm { get; init; } // the material swap form ("file:local id", empty for none) the object gets with this version
    public string? Card { get; set; }     // loading screen card model (relative to Meshes) the picture shows on
    public float CardWidth { get; set; }  // width of a flat picture card in units, 0 for a magazine card
    public NifFile.UvLayout? CardLayout { get; set; }  // the part of the picture's texture its card shows, and which way up
    public string? CardTexture { get; set; }  // the texture (relative to Textures) a card drawn from its frame's face shows

    /// <summary>True for a picture of an art category; models ("#", "&"), textures loaded by name ("%"), sounds ("~") and texture set entries ("!") replace only their own slot.</summary>
    public bool IsPicture => Scanner.PictureCategories.Contains(Category) && (Slot == null || Slot.Length == 0 || Slot[0] is not ('#' or '&' or '%' or '~' or '!'));
}

/// <summary>Writes the "Gallery Shuffle Packs" mod folder: materials, textures, packs.json and the MCM pages.</summary>
public sealed class Writer
{
    // Every picture card is scaled to fit this box (units, width by height) and centred: the whole 16:9 screen at the card
    // framing's distance of 50 units, where the loading screen shows 21.25 units top to bottom (measured on 1.10.984 at
    // 2560x1440: 781 pixels for 11.52 units). The plugin shrinks a card that is wider than a narrower screen.
    private const float CardHeight = 21.25f;
    private const float CardWidth = CardHeight * 16f / 9f;
    // Magazine covers show on the model the game's own magazine loading screens use, in place of its cover material.
    private const string MagazineCardModel = @"loadscreenart\magazine.nif";
    private const string MagazineCardMaterial = @"props\comicsandmagazineshighres\awesometales\awesometales1.bgsm";
    private const string MagazineCardBackMaterial = @"props\comicsandmagazineshighres\backside\comicbackbare01.bgsm";
    private readonly string _out;
    private readonly Catalog _catalog;
    private readonly GameFiles _game;
    private readonly Report _report;
    private readonly Dictionary<string, ExtraSlot> _extraSlots = new(StringComparer.OrdinalIgnoreCase);
    // Files (relative to Data, lowercase) the converted mods ship at the game's own paths.
    private readonly HashSet<string> _overridden = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _vanillaMaterials = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _vanillaTextures = new(StringComparer.OrdinalIgnoreCase);

    public Writer(string outputFolder, Catalog catalog, GameFiles game, Report report)
    {
        _out = outputFolder;
        _catalog = catalog;
        _game = game;
        _report = report;
    }

    /// <summary>Records every file a mod ships at one of the game's own paths.</summary>
    public void NoteOverrides(Source source)
    {
        foreach (var file in source.Files.Values)
        {
            var path = file.DataPath.Replace('/', '\\').ToLowerInvariant();
            var own = path.Split('\\')[0] switch
            {
                "sound" or "music" => _game.HasAudio(path),
                "video" => File.Exists(Path.Combine(_game.DataFolder, path)),
                "textures" or "materials" or "meshes" or "interface" => _game.HasGameFile(path),
                _ => false,
            };
            if (own) _overridden.Add(path);
        }
    }

    public PackOutput WritePack(Source source, List<FoundImage> images, HashSet<string> usedIds, string? menuVideo = null, string? menuMusic = null, List<Scanner.InterfaceFile>? interfaceFiles = null)
    {
        var id = Sources.Sanitize(source.Name);
        if (id.Length > 40) id = id[..32] + "-" + ShortHash(id);
        var baseId = id;
        for (var n = 2; usedIds.Contains(id); n++) id = $"{baseId}-{n}";
        usedIds.Add(id);
        var pack = new PackOutput { Id = id, Name = source.Name };
        var materialNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var textureNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var copied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Pooled FOMOD options carry "option|" in front of their paths; a short hash of the option keeps names apart and short.
        string Stem(string sourcePath, string name)
        {
            var bar = sourcePath.IndexOf('|');
            var stem = Sources.Sanitize(bar > 0 ? ShortHash(sourcePath[..bar]) + "-" + name : name);
            return stem.Length > 48 ? stem[..40] + "-" + ShortHash(stem) : stem;
        }

        // Copies a texture into the pack once and returns its path relative to Textures; the map-type suffix (_d, _n) is kept.
        string Copy(string sourcePath)
        {
            if (copied.TryGetValue(sourcePath, out var done)) return done;
            var name = Path.GetFileNameWithoutExtension(sourcePath);
            var suffix = "";
            var underscore = name.LastIndexOf('_');
            if (underscore > 0 && name.Length - underscore <= 3)
            {
                suffix = name[underscore..].ToLowerInvariant();
                name = name[..underscore];
            }
            var stem = Stem(sourcePath, name);
            var unique = stem;
            for (var n = 2; !textureNames.Add(unique + suffix); n++) unique = $"{stem}-{n}";
            var relative = $"GShuf\\Packs\\{id}\\{unique}{suffix}.dds";
            WriteFile(Path.Combine("Textures", relative), source.Files[sourcePath].Read());
            copied[sourcePath] = relative;
            return relative;
        }

        static bool IsObjectOrModel(FoundImage image) => image.Slot != null && image.Slot.Length > 0 && image.Slot[0] is '&' or '#' or '%' or '~' or '!';
        // Sound and music files: the pack's copy under Sound or Music, where the game registers loose sound files at startup.
        var soundNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in images.Where(i => i.Slot != null && i.Slot.StartsWith('~')).OrderBy(i => i.Slot, StringComparer.OrdinalIgnoreCase))
        {
            var sourcePath = image.Swaps[image.Slot![1..]];
            var root = sourcePath.Contains('|') ? sourcePath[(sourcePath.IndexOf('|') + 1)..].Split('\\')[0] : sourcePath.Split('\\')[0];
            var stem = Stem(sourcePath, Path.GetFileNameWithoutExtension(sourcePath));
            var unique = stem;
            for (var n = 2; !soundNames.Add(unique); n++) unique = $"{stem}-{n}";
            var copy = $"{(root.Equals("music", StringComparison.OrdinalIgnoreCase) ? "Music" : "Sound")}\\GShuf\\Packs\\{id}\\{unique}{Path.GetExtension(sourcePath).ToLowerInvariant()}";
            WriteFile(copy, source.Files[sourcePath].Read());
            pack.Images.Add(new ImageOutput { Category = image.Category, Material = copy, Slot = image.Slot });
        }
        // Textures the game loads by name: the pack's copy, which the plugin loads in place of the game's.
        foreach (var image in images.Where(i => i.Slot != null && i.Slot.StartsWith('%')).OrderBy(i => i.Slot, StringComparer.OrdinalIgnoreCase))
        {
            pack.Images.Add(new ImageOutput { Category = image.Category, Material = Copy(image.Swaps[image.Slot![1..]]), Slot = image.Slot, Width = image.Width, Height = image.Height });
        }
        // Texture set textures: the pack's copy, which the plugin writes into the texture set.
        foreach (var image in images.Where(i => i.Slot != null && i.Slot.StartsWith('!')).OrderBy(i => i.Slot, StringComparer.OrdinalIgnoreCase))
        {
            pack.Images.Add(new ImageOutput { Category = image.Category, Material = image.GameTexture ?? Copy(image.Swaps.Values.First()), Slot = image.Slot, Width = image.Width, Height = image.Height });
        }
        foreach (var image in images.Where(i => !IsObjectOrModel(i)).OrderBy(i => i.Slot ?? i.Diffuse, StringComparer.OrdinalIgnoreCase))
        {
            var material = TemplateMaterial(image, source);
            if (material == null) continue;
            string name;
            if (image.Slot != null && (image.Swaps.Count > 0 || image.MaterialSource != null || image.GameMaterial != null || image.InlineKey != null))
            {
                // A replacement: every texture slot the mod ships a file for points at the pack's copy of that file.
                foreach (var (get, set) in MaterialFiles.TextureSlots(material))
                {
                    var current = Paths.Normalize(get() ?? "", "textures");
                    if (current.Length > 0 && image.Swaps.TryGetValue(current, out var sourcePath)) set(Copy(sourcePath).Replace('\\', '/'));
                }
                name = Path.GetFileNameWithoutExtension(image.InlineKey ?? image.Slot);
                if (image.InlineKey != null && name.EndsWith("_d", StringComparison.OrdinalIgnoreCase)) name = name[..^2];
            }
            else
            {
                // A picture: the template's diffuse and normal show the picture.
                name = Path.GetFileNameWithoutExtension(image.Diffuse!);
                if (name.EndsWith("_d", StringComparison.OrdinalIgnoreCase)) name = name[..^2];
                SetPicture(material, Copy(image.Diffuse!), image.Normal != null ? Copy(image.Normal) : null);
            }
            var materialStem = Stem(image.MaterialSource ?? image.Diffuse ?? image.Slot!, name);
            var uniqueMaterial = materialStem;
            for (var n = 2; !materialNames.Add(uniqueMaterial); n++) uniqueMaterial = $"{materialStem}-{n}";
            var materialRel = $"GShuf\\Packs\\{id}\\{uniqueMaterial}{(material is BGEM ? ".bgem" : ".bgsm")}";
            WriteMaterial(Path.Combine("Materials", materialRel), material);
            pack.Images.Add(new ImageOutput { Category = image.Category, Material = materialRel, Slot = image.Slot, Width = image.Width, Height = image.Height });
            if (image.Slot != null && !_catalog.ByMaterial.ContainsKey(image.Slot) && !_extraSlots.ContainsKey(image.Slot))
            {
                _extraSlots[image.Slot] = new ExtraSlot(image.Slot, image.Category, image.InlineKey ?? _game.DiffuseOf(image.Slot), image.Width, image.Height,
                    image.InlineKey != null ? WriteInlineRestore(image.InlineKey) : "");
            }
        }
        // Replacement models, rewritten so every texture and material the mod ships for them points at the pack's copy, and
        // every vanilla material this pack replaces points at the pack's version.
        var packMaterials = pack.Images.Where(i => i.Slot != null).GroupBy(i => i.Slot!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Material, StringComparer.OrdinalIgnoreCase);
        var copiedMaterials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var writtenModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in images.Where(i => i.ModelSource != null || i.GameModel != null).OrderBy(i => i.Slot, StringComparer.OrdinalIgnoreCase))
        {
            string? Map(string reference)
            {
                var ext = Path.GetExtension(reference).ToLowerInvariant();
                if (ext == ".dds")
                {
                    var texture = Paths.Normalize(reference, "textures");
                    return image.ModelFiles.TryGetValue("t:" + texture, out var textureSource) ? "textures\\" + Copy(textureSource) : null;
                }
                if (ext is not (".bgsm" or ".bgem")) return null;
                var material = Paths.Normalize(reference, "materials");
                // A material swap the plugin gives the object is baked in: its replacement stands where the original was.
                var substituted = image.Substitutions.TryGetValue(material, out var substitute);
                if (substituted) material = substitute!;
                if (image.ModelFiles.TryGetValue("m:" + material, out var materialSource))
                {
                    if (!copiedMaterials.TryGetValue(materialSource, out var written))
                    {
                        var file = MaterialFiles.Read(source.Files[materialSource].Read());
                        foreach (var (get, set) in MaterialFiles.TextureSlots(file))
                        {
                            var current = Paths.Normalize(get() ?? "", "textures");
                            if (current.Length > 0 && image.ModelFiles.TryGetValue("t:" + current, out var textureSource)) set(Copy(textureSource).Replace('\\', '/'));
                        }
                        var stem = Stem(materialSource, Path.GetFileNameWithoutExtension(material));
                        var unique = stem;
                        for (var n = 2; !materialNames.Add(unique); n++) unique = $"{stem}-{n}";
                        written = $"GShuf\\Packs\\{id}\\{unique}{(file is BGEM ? ".bgem" : ".bgsm")}";
                        WriteMaterial(Path.Combine("Materials", written), file);
                        copiedMaterials[materialSource] = written;
                    }
                    return "Materials\\" + written;
                }
                if (packMaterials.TryGetValue(material, out var replacement)) return "Materials\\" + replacement;
                return substituted ? "Materials\\" + material : null;
            }

            // Objects that share a model and a baked swap share one written file; a pooled option's model sits in a folder named by a hash of the option.
            var modelSource = image.ModelSource ?? image.GameModel!;
            var optionBar = modelSource.IndexOf('|');
            var relative = Paths.Normalize(optionBar > 0 ? modelSource[(optionBar + 1)..] : modelSource, "meshes");
            // A game model carries no option prefix, so the option comes from the files the entry puts on it.
            var optionPath = image.ModelSource ?? image.ModelFiles.Values.FirstOrDefault() ?? "";
            var optionEnd = optionPath.IndexOf('|');
            if (optionEnd > 0) relative = ShortHash(optionPath[..optionEnd]) + "\\" + relative;
            if (image.Substitutions.Count > 0)
            {
                var swapKey = string.Join("|", image.Substitutions.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Key + "=" + kv.Value));
                relative = Path.ChangeExtension(relative, null) + "-" + ShortHash(swapKey) + ".nif";
            }
            var writeKey = (image.ModelSource ?? "game:" + image.GameModel) + "|" + relative;
            if (!writtenModels.TryGetValue(writeKey, out var modelRel))
            {
                var original = image.ModelSource != null ? source.Files[image.ModelSource].Read() : _game.ReadMesh(image.GameModel!);
                byte[]? rewritten;
                try
                {
                    rewritten = original == null ? null : NifFile.Rewrite(original, Map);
                }
                catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or EndOfStreamException or InvalidDataException or IOException)
                {
                    rewritten = null;
                }
                if (rewritten == null)
                {
                    _report.Warn($"{source.Name}: {image.ModelSource ?? image.GameModel} is not a Fallout 4 model; skipped");
                    continue;
                }
                // A game model the pack changes nothing in is loaded from the game's own archives.
                if (image.ModelSource == null && rewritten.AsSpan().SequenceEqual(original)) modelRel = image.GameModel!;
                else
                {
                    modelRel = $"GShuf\\Packs\\{id}\\{relative}";
                    WriteFile(Path.Combine("Meshes", modelRel), rewritten);
                }
                writtenModels[writeKey] = modelRel;
            }
            pack.Images.Add(new ImageOutput { Category = image.Category, Material = modelRel, Slot = image.Slot, DropSwap = image.DropSwap, SwapForm = image.SwapForm, Name = image.Name, Bounds = image.Bounds });
            if (!_catalog.ByMaterial.ContainsKey(image.Slot!) && !_extraSlots.ContainsKey(image.Slot!))
            {
                _extraSlots[image.Slot!] = new ExtraSlot(image.Slot!, image.Category, "", 0, 0, "", image.Label ?? "");
            }
        }

        // Objects whose pack version is only a name or bounds: the model stays whatever the object loads.
        foreach (var image in images.Where(i => i.Slot != null && i.Slot.StartsWith('&') && i.ModelSource == null && i.GameModel == null && (i.Name != null || i.Bounds != null)).OrderBy(i => i.Slot, StringComparer.OrdinalIgnoreCase))
        {
            pack.Images.Add(new ImageOutput { Category = image.Category, Material = "", Slot = image.Slot, Name = image.Name, Bounds = image.Bounds });
            if (!_extraSlots.ContainsKey(image.Slot!)) _extraSlots[image.Slot!] = new ExtraSlot(image.Slot!, image.Category, "", 0, 0, "", image.Label ?? "");
        }

        if (menuVideo != null && source.Files.TryGetValue(menuVideo, out var video))
        {
            pack.Video = $"GShuf\\Packs\\{id}\\MainMenuLoop.bk2";
            WriteFile(Path.Combine("Video", pack.Video), video.Read());
        }
        // Interface files keep their layout under the pack folder; the plugin opens them in place of the game's.
        foreach (var file in interfaceFiles ?? [])
        {
            var copy = $"Interface\\GShuf\\Packs\\{id}\\{file.GamePath["interface\\".Length..]}";
            WriteFile(copy, source.Files[file.Source].Read());
            pack.Movies.Add((file.GamePath, copy, file.Category));
        }
        // The game names its menu theme by a fixed path the plugin rewrites in place, so the pack's copy gets a name that fits it.
        if (menuMusic != null && source.Files.TryGetValue(menuMusic, out var music))
        {
            pack.Music = $"GShuf\\{ShortHash(id)}{Path.GetExtension(menuMusic).ToLowerInvariant()}";
            WriteFile(Path.Combine("Music", pack.Music), music.Read());
        }
        var counts = string.Join(", ", pack.Images.GroupBy(i => i.Category).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}"));
        _report.Info($"pack '{pack.Name}' ({pack.Id}): {Report.Count(pack.Images.Count, "item", "items")} ({(counts.Length > 0 ? counts : "none")}){(pack.Video != null ? ", main menu video" : "")}{(pack.Music != null ? ", main menu theme" : "")}{(pack.Movies.Count > 0 ? $", {Report.Count(pack.Movies.Count, "interface file", "interface files")}" : "")}");
        return pack;
    }

    /// <summary>Writes the vanilla look of an inline shader as a material file, which the plugin applies to put the vanilla look back.</summary>
    private string WriteInlineRestore(string key)
    {
        var shader = _game.InlineShader(key);
        if (shader == null) return "";
        var material = shader.Build();
        UseVanillaTextures(material);
        var stem = Sources.Sanitize(Path.GetFileNameWithoutExtension(key));
        if (stem.Length > 48) stem = stem[..40] + "-" + ShortHash(key);
        var relative = $"GShuf\\Packs\\vanilla\\inline\\{stem}-{ShortHash(key)}{(material is BGEM ? ".bgem" : ".bgsm")}";
        WriteMaterial(Path.Combine("Materials", relative), material);
        return relative;
    }

    /// <summary>Writes the slot list the plugin reads: the catalogued vanilla art slots plus every other vanilla material a pack replaces.</summary>
    public void WriteCatalog(string catalogPath)
    {
        var rows = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(catalogPath))!.AsArray();
        foreach (var row in rows)
        {
            if (row != null && IsMagazineBack(row["material"]?.GetValue<string>())) row["category"] = "magazinebacks";
        }
        foreach (var slot in _extraSlots.Values.OrderBy(s => s.Material, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["material"] = slot.Material, ["category"] = slot.Category, ["diffuse"] = slot.Diffuse, ["normal"] = "", ["width"] = slot.Width, ["height"] = slot.Height,
                ["restore"] = slot.Restore,
            });
            if (slot.Label.Length > 0) rows[^1]!["label"] = slot.Label;
            if (slot.Material.StartsWith('&') && FormKey.FromText(slot.Material[1..]) is { } key && _game.GameModelRecord(key) is { } record) rows[^1]!["model"] = record.Model;
        }
        WriteFile(Path.Combine("GShuf", "vanilla-slots.json"), Encoding.UTF8.GetBytes(rows.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        _report.Info($"slot list: {Report.Count(_catalog.Slots.Count, "catalogued art slot", "catalogued art slots")} plus {Report.Count(_extraSlots.Count, "more vanilla material, model or object", "more vanilla materials, models and objects")} the packs replace");
    }

    /// <summary>The vanilla pack: every catalogued vanilla picture copied out of the player's own archives, and the game's own
    /// version of everything else a pack replaces that loads a file a converted mod ships at the game's path.</summary>
    public PackOutput WriteVanillaPack(List<PackOutput> packs)
    {
        var pack = new PackOutput { Id = "vanilla", Name = "Vanilla" };
        foreach (var slot in _catalog.Slots)
        {
            if (slot.Blank || slot.Diffuse.Length == 0) continue;
            var materialBytes = _game.ReadMaterial(slot.Material);
            var textureBytes = _game.ReadTexture(slot.Diffuse);
            if (materialBytes == null)
            {
                _report.Warn($"vanilla: missing {slot.Material} in the game archives");
                continue;
            }
            if (textureBytes == null)
            {
                _report.Warn($"vanilla: missing {slot.Diffuse} (named by {slot.Material}) in the game archives");
                continue;
            }
            // Each copy keeps the game file's folder, so same-named files of two folders (low and high resolution covers) stay apart.
            var textureRel = VanillaTexture(slot.Diffuse, textureBytes);
            var material = MaterialFiles.Read(materialBytes);
            var materialRel = $"GShuf\\Packs\\vanilla\\{Path.ChangeExtension(slot.Material, material is BGEM ? ".bgem" : ".bgsm")}";
            SetPicture(material, textureRel, null);
            UseVanillaTextures(material);
            WriteMaterial(Path.Combine("Materials", materialRel), material);
            _vanillaMaterials[slot.Material] = materialRel;
            pack.Images.Add(new ImageOutput { Category = slot.Category, Material = materialRel, Slot = slot.Material, Width = slot.Width, Height = slot.Height });
        }
        var pictures = pack.Images.Count;

        var covered = new HashSet<string>(pack.Images.Select(i => i.Slot!), StringComparer.OrdinalIgnoreCase);
        foreach (var versions in packs.SelectMany(p => p.Images).Where(i => i.Slot is { Length: > 1 } && !covered.Contains(i.Slot)).GroupBy(i => i.Slot!, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var slot = versions.Key;
            var category = versions.First().Category;
            var target = slot[1..];
            ImageOutput? own = null;
            switch (slot[0])
            {
                case '#':
                    var model = Paths.Normalize(target, "meshes");
                    if (VanillaModel(model) is { } copy && !copy.Equals(model, StringComparison.OrdinalIgnoreCase)) own = new ImageOutput { Category = category, Material = copy, Slot = slot };
                    break;
                case '&':
                    own = VanillaObject(slot, category, versions.ToList());
                    break;
                case '%':
                    if (_game.ReadTexture(target) is { } texture) own = new ImageOutput { Category = category, Material = VanillaTexture(target, texture), Slot = slot };
                    break;
                case '~':
                    if (_game.ReadAudio(target) is { } audio)
                    {
                        var root = target.StartsWith("music\\", StringComparison.OrdinalIgnoreCase) ? "Music" : "Sound";
                        var file = $"{root}\\GShuf\\Packs\\vanilla\\{target[(root.Length + 1)..]}{audio.Extension}";
                        WriteFile(file, audio.Data);
                        own = new ImageOutput { Category = category, Material = file, Slot = slot };
                    }
                    break;
                case '=':
                    if (SwapShows(target) is { } shown && VanillaMaterial(shown) is { } shownCopy) own = new ImageOutput { Category = category, Material = shownCopy, Slot = slot };
                    break;
                case '!':
                    if (SetTexture(target) is { } setTexture)
                    {
                        var setCopy = _overridden.Contains("textures\\" + setTexture) && _game.ReadTexture(setTexture) is { } setBytes ? VanillaTexture(setTexture, setBytes) : setTexture;
                        own = new ImageOutput { Category = category, Material = setCopy, Slot = slot };
                    }
                    break;
                case '@':
                    if (_game.InlineShader(target)?.Textures.Any(t => _overridden.Contains("textures\\" + t)) == true && WriteInlineRestore(target) is { Length: > 0 } restore)
                    {
                        own = new ImageOutput { Category = category, Material = restore, Slot = slot };
                    }
                    break;
                default:
                    if (VanillaMaterial(slot) is { } material) own = new ImageOutput { Category = category, Material = material, Slot = slot };
                    break;
            }
            if (own != null) pack.Images.Add(own);
        }

        foreach (var movie in packs.SelectMany(p => p.Movies).DistinctBy(m => m.Path.ToLowerInvariant()))
        {
            if (_game.ReadInterface(movie.Path) is not { } data) continue;
            var copy = $"Interface\\GShuf\\Packs\\vanilla\\{movie.Path["interface\\".Length..]}";
            WriteFile(copy, data);
            pack.Movies.Add((movie.Path, copy, movie.Category));
        }
        if (packs.Any(p => p.Video != null))
        {
            // Copies the game's loose menu video unless it is a converted mod's copy.
            var gameVideo = Path.Combine(_game.DataFolder, "Video", "MainMenuLoop.bk2");
            var video = File.Exists(gameVideo) ? File.ReadAllBytes(gameVideo) : null;
            if (video != null && !packs.Where(p => p.Video != null).Any(p => File.ReadAllBytes(Path.Combine(_out, "Video", p.Video!)).AsSpan().SequenceEqual(video)))
            {
                pack.Video = "GShuf\\Packs\\vanilla\\MainMenuLoop.bk2";
                WriteFile(Path.Combine("Video", pack.Video), video);
            }
            else
            {
                _report.Warn("vanilla: the game's own main menu video could not be read apart from a converted mod's copy; convert again with that mod disabled so Vanilla in the corner box plays the game's own video");
            }
        }
        if (packs.Any(p => p.Music != null) && _game.ReadAudio("music\\special\\mus_maintheme") is { } theme)
        {
            pack.Music = $"GShuf\\{ShortHash("vanilla")}{theme.Extension}";
            WriteFile(Path.Combine("Music", pack.Music), theme.Data);
        }
        _report.Info($"pack 'Vanilla': {Report.Count(pictures, "picture", "pictures")} copied from the game archives, and the game's own version of {Report.Count(pack.Images.Count - pictures, "other replaced item", "other replaced items")}{(pack.Movies.Count > 0 ? $", {Report.Count(pack.Movies.Count, "interface file", "interface files")}" : "")}{(pack.Video != null ? ", main menu video" : "")}{(pack.Music != null ? ", main menu theme" : "")}");
        return pack;
    }

    // Copies a game texture into the vanilla pack once, keeping its folder, and returns its path relative to Textures.
    private string VanillaTexture(string texture, byte[] data)
    {
        var relative = $"GShuf\\Packs\\vanilla\\{texture}";
        if (_vanillaTextures.Add(relative)) WriteFile(Path.Combine("Textures", relative), data);
        return relative;
    }

    // Points every texture of a material that a converted mod ships at the game's path to the vanilla pack's copy; true when one changed.
    private bool UseVanillaTextures(BaseMaterialFile material)
    {
        var changed = false;
        foreach (var (get, set) in MaterialFiles.TextureSlots(material))
        {
            var texture = Paths.Normalize(get() ?? "", "textures");
            if (texture.Length == 0 || texture.StartsWith("gshuf\\", StringComparison.OrdinalIgnoreCase) || !_overridden.Contains("textures\\" + texture)) continue;
            if (_game.ReadTexture(texture) is not { } data) continue;
            set(VanillaTexture(texture, data).Replace('\\', '/'));
            changed = true;
        }
        return changed;
    }

    // The vanilla pack's copy of a game material (relative to Materials) when a converted mod ships it or one of its textures at
    // the game's path, or null when the game's own file shows as it is.
    private string? VanillaMaterial(string material)
    {
        if (_vanillaMaterials.TryGetValue(material, out var done)) return done;
        string? result = null;
        if (_game.ReadMaterial(material) is { } bytes)
        {
            try
            {
                var file = MaterialFiles.Read(bytes);
                if (UseVanillaTextures(file) | _overridden.Contains("materials\\" + material))
                {
                    result = $"GShuf\\Packs\\vanilla\\{Path.ChangeExtension(material, file is BGEM ? ".bgem" : ".bgsm")}";
                    WriteMaterial(Path.Combine("Materials", result), file);
                }
            }
            catch (Exception e) when (e is ArgumentException or ArgumentOutOfRangeException or IndexOutOfRangeException or EndOfStreamException or InvalidDataException or IOException)
            {
                _report.Warn($"vanilla: cannot read the game material {material}: {e.Message}");
            }
        }
        _vanillaMaterials[material] = result;
        return result;
    }

    // The vanilla pack's copy of a game model a converted mod ships, the game's own path otherwise, or null when the game has none.
    private string? VanillaModel(string model)
    {
        if (!_overridden.Contains("meshes\\" + model)) return _game.HasMesh(model) ? model : null;
        if (_game.ReadMesh(model) is not { } data) return null;
        var relative = $"GShuf\\Packs\\vanilla\\{model}";
        WriteFile(Path.Combine("Meshes", relative), data);
        return relative;
    }

    // The material the game's swap shows in place of one material ("swap form|material"), or null.
    private string? SwapShows(string target)
    {
        var bar = target.IndexOf('|');
        if (bar < 0 || FormKey.FromText(target[..bar]) is not { } key || _game.GameMaterialSwap(key) is not { } list) return null;
        var original = target[(bar + 1)..];
        var entry = list.FirstOrDefault(s => Paths.Normalize(s.Original, "materials").Equals(original, StringComparison.OrdinalIgnoreCase) && s.Replacement.Length > 0);
        return entry == null ? null : Paths.Normalize(entry.Replacement, "materials");
    }

    // The game's texture at one index of a texture set ("set form|index", relative to Textures), or null.
    private string? SetTexture(string target)
    {
        var bar = target.IndexOf('|');
        if (bar < 0 || FormKey.FromText(target[..bar]) is not { } key || !int.TryParse(target[(bar + 1)..], out var index) || index is < 0 or > 7) return null;
        return _game.GameTextureSet(key) is { } textures && textures[index].Length > 0 ? textures[index] : null;
    }

    // The game's own model, material swap, name and bounds of an object a pack gives another model, name or bounds.
    private ImageOutput? VanillaObject(string slot, string category, List<ImageOutput> versions)
    {
        var record = FormKey.FromText(slot[1..]) is { } key ? _game.GameModelRecord(key) : null;
        if (record == null) return null;
        var material = "";
        string? swapForm = null;
        if (versions.Any(v => v.Material.Length > 0))
        {
            material = VanillaModel(Paths.Normalize(record.Model, "meshes")) ?? "";
            if (material.Length > 0) swapForm = record.Swap is { } swap ? swap.Text : "";
        }
        var name = versions.Any(v => v.Name != null) ? PluginFile.Text(record.FieldData("FULL"), _game.IsLocalized(record.Source), id => _game.GameString(record.Source, id)) : null;
        var obnd = record.FieldData("OBND");
        var bounds = versions.Any(v => v.Bounds != null) && obnd is { Length: 12 } ? Enumerable.Range(0, 6).Select(i => BitConverter.ToInt16(obnd, i * 2)).ToArray() : null;
        if (material.Length == 0 && name == null && bounds == null) return null;
        return new ImageOutput { Category = category, Material = material, Slot = slot, SwapForm = swapForm, Name = name, Bounds = bounds };
    }

    /// <summary>Gives every pack picture a loading screen card that already shows it: the flat sheet at the aspect of the
    /// picture's frame in the world (for a picture the game has no frame for, a frame of its category whose picture has the
    /// same aspect, the rule the world distribution uses), showing the part of the texture that frame shows, centred and
    /// scaled to the card box, with the picture's material. Magazine covers use the game's loading screen magazine.</summary>
    public void WriteLoadCards(List<PackOutput> packs)
    {
        var total = 0;
        var missing = 0;
        var shapes = new Dictionary<string, NifFile.PictureShape?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in packs.Where(p => p.Id != "vanilla"))
        {
            foreach (var image in pack.Images.Where(i => i.IsPicture))
            {
                total++;
                var relative = $"GShuf\\LoadCards\\{pack.Id}\\{Path.GetFileNameWithoutExtension(image.Material)}.nif";
                var card = image.Category == "magazines" ? MagazineCard(image) : SheetCard(image, pack.Id, shapes);
                if (card is not { } made)
                {
                    missing++;
                    continue;
                }
                WriteFile(Path.Combine("Meshes", relative), made.Model);
                image.Card = relative;
                image.CardWidth = made.Width;
                image.CardLayout = made.Layout;
            }
        }
        var drawn = packs.Sum(p => p.Images.Count(i => i.CardTexture != null));
        if (total > 0) _report.Info($"loading screens: {Report.Count(total - missing, "picture", "pictures")} got a card ({drawn} drawn from their frame's whole face){(missing > 0 ? $"; {Report.Count(missing, "picture has", "pictures have")} no frame in the game to take the shape from" : "")}");
        if (packs.FirstOrDefault(p => p.Id == "vanilla") is { } vanilla) WriteVanillaScreenCards(vanilla, packs);
    }

    // Gives the vanilla pack a card of every loading screen model with a file a converted mod ships at the game's path or a pack model.
    private void WriteVanillaScreenCards(PackOutput vanilla, List<PackOutput> packs)
    {
        var remodeled = packs.Where(p => p.Id != "vanilla").SelectMany(p => p.Images).Where(i => i.Slot is { } s && s.StartsWith('&') && i.Material.Length > 0).Select(i => i.Slot!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_overridden.Count == 0 && remodeled.Count == 0) return;
        var cards = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var screens = _game.GameLoadScreens();
        var unknown = 0;
        foreach (var (screen, record) in screens)
        {
            if (record == null)
            {
                unknown++;
                continue;
            }
            var forced = remodeled.Contains("&" + record.Key.Text);
            var key = record.Model + "|" + (record.Swap?.Text ?? "") + "|" + forced;
            if (!cards.TryGetValue(key, out var card))
            {
                card = VanillaScreenCard(record, forced);
                cards[key] = card;
            }
            if (card != null) vanilla.Screens.Add(new ScreenCard(screen.Text, card, record.Key.Text, record.Model, record.Swap?.Text, record.ColorIndex));
        }
        if (vanilla.Screens.Count > 0 || unknown > 0)
        {
            _report.Info($"loading screens: {Report.Count(vanilla.Screens.Count, "screen shows", "screens show")} a model whose files a converted mod ships, so Vanilla shows {(vanilla.Screens.Count == 1 ? "it" : "them")} from the vanilla pack's copies ({Report.Count(cards.Values.Count(c => c != null), "card", "cards")}){(unknown > 0 ? $"; {Report.Count(unknown, "screen shows", "screens show")} a form that is not a static or static collection, left as the game loads it" : "")}");
        }
    }

    // Writes one loading screen model with its swap applied and overridden files pointed at vanilla copies; null when nothing is overridden and a_forced is off.
    private string? VanillaScreenCard(PluginFile.ModelRecord record, bool a_forced)
    {
        if (_game.ReadMesh(record.Model) is not { } data) return null;
        var substitutions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (record.Swap is { } swap && _game.GameMaterialSwap(swap) is { } list)
        {
            foreach (var s in list.Where(s => s.Original.Length > 0 && s.Replacement.Length > 0)) substitutions[Paths.Normalize(s.Original, "materials")] = Paths.Normalize(s.Replacement, "materials");
        }
        var overridden = a_forced || _overridden.Contains("meshes\\" + record.Model);
        byte[]? card;
        try
        {
            card = NifFile.Rewrite(data, reference =>
            {
                var ext = Path.GetExtension(reference).ToLowerInvariant();
                if (ext == ".dds")
                {
                    var texture = Paths.Normalize(reference, "textures");
                    if (!_overridden.Contains("textures\\" + texture) || _game.ReadTexture(texture) is not { } bytes) return null;
                    overridden = true;
                    return "textures\\" + VanillaTexture(texture, bytes);
                }
                if (ext is not (".bgsm" or ".bgem")) return null;
                var material = Paths.Normalize(reference, "materials");
                var shown = substitutions.GetValueOrDefault(material, material);
                if (_overridden.Contains("materials\\" + shown) || _game.TexturesOf(shown).Any(t => _overridden.Contains("textures\\" + t)))
                {
                    overridden = true;
                    if (VanillaMaterial(shown) is { } copy) return "Materials\\" + copy;
                }
                return shown.Equals(material, StringComparison.OrdinalIgnoreCase) ? null : "Materials\\" + shown;
            });
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or EndOfStreamException or InvalidDataException or IOException)
        {
            _report.Warn($"loading screens: cannot read the game model {record.Model}: {e.Message}");
            return null;
        }
        if (!overridden || card == null) return null;
        var relative = $"GShuf\\LoadCards\\vanilla\\{Path.ChangeExtension(record.Model, null)}{(record.Swap is { } s2 ? "-" + ShortHash(s2.Text) : "")}.nif";
        WriteFile(Path.Combine("Meshes", relative), card);
        return relative;
    }

    // For a picture of a mod's own: the frame of a catalogued slot of its category whose picture has the same aspect.
    private (string Model, string Material)? StandaloneFrame(ImageOutput image)
    {
        if (image.Width <= 0 || image.Height <= 0) return null;
        foreach (var slot in _catalog.Slots.Where(s => s.Category == image.Category && !s.Blank && s.Width > 0 && s.Height > 0).OrderBy(s => s.Material, StringComparer.Ordinal))
        {
            if ((long)slot.Width * image.Height != (long)image.Width * slot.Height) continue;
            if (_game.FrameOf(slot.Material) is { } frame) return frame;
        }
        return null;
    }

    // The flat sheet every picture card is made from (a 4-vertex quad in its own X-Z plane facing -Y, no node transforms) and its material.
    private const string SheetModel = @"setdressing\signage\advertsposter01.nif";
    private const string SheetMaterial = @"setdressing\signage\advertsposters01.bgsm";

    // The game's loading screen magazine with the picture's material on its cover; a back cover (a picture for one of the
    // game's backside materials) goes on the back, which the plugin turns to the camera with its back cover framing. Width 0
    // marks a card the plugin does not resize; the rectangle is the part of the texture that cover shows.
    private (byte[] Model, float Width, NifFile.UvLayout Layout)? MagazineCard(ImageOutput image)
    {
        var target = IsBackCover(image.Slot) ? MagazineCardBackMaterial : MagazineCardMaterial;
        var magazine = _game.ReadMesh(MagazineCardModel);
        var cover = magazine == null ? null : NifFile.MaterialShape(magazine, name => Paths.Normalize(name, "materials").Equals(target, StringComparison.OrdinalIgnoreCase));
        var card = magazine == null ? null : NifFile.Rewrite(magazine, reference =>
            Paths.Normalize(reference, "materials").Equals(target, StringComparison.OrdinalIgnoreCase) ? "Materials\\" + image.Material : null);
        return card == null || cover is not { } c ? null : (card, 0f, c.Layout);
    }

    private static bool IsBackCover(string? slot) => slot != null && slot.Contains(@"\backside\", StringComparison.OrdinalIgnoreCase);

    // The flat sheet showing the picture the way its frame shows it, with its width and the part of the texture it shows. A face
    // of one texture island takes the pack's material with the texture coordinates moved onto the sheet; a face of several (a
    // sign's lettering over its plate) is drawn from the front into a texture of its own, shown by a copy of the sheet's material.
    private (byte[] Model, float Width, NifFile.UvLayout Layout)? SheetCard(ImageOutput image, string packId, Dictionary<string, NifFile.PictureShape?> shapes)
    {
        var frameMaterial = image.Slot is { Length: > 1 } scoped && scoped[0] == '=' ? SwapShows(scoped[1..]) : image.Slot;
        var frame = frameMaterial != null ? _game.FrameOf(frameMaterial) : null;
        frame ??= StandaloneFrame(image);
        if (frame is not { } f) return null;
        var key = f.Model + "|" + f.Material;
        if (!shapes.TryGetValue(key, out var shape))
        {
            var model = _game.ReadMesh(f.Model);
            var found = model == null ? null : NifFile.MaterialShape(model, name => Paths.Normalize(name, "materials").Equals(f.Material, StringComparison.OrdinalIgnoreCase));
            shape = found is { } m && m.Size.Width > 0 && m.Size.Height > 0 ? m : null;
            shapes[key] = shape;
        }
        if (shape is not { } s) return null;
        var cardMaterial = "Materials\\" + image.Material;
        var (pictureWidth, pictureHeight) = s.Size;
        var target = s.Layout;
        if (s.Islands > 1 && s.FaceSize.Width > 0 && s.FaceSize.Height > 0 && DrawFace(image, s) is { } drawing)
        {
            var stem = Path.GetFileNameWithoutExtension(image.Material);
            var texture = $"GShuf\\LoadCards\\{packId}\\{stem}_d.dds";
            var bgra = new byte[drawing.Rgba.Length];
            for (var i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) = (drawing.Rgba[i + 2], drawing.Rgba[i + 1], drawing.Rgba[i], drawing.Rgba[i + 3]);
            var material = DrawnCardMaterial(texture);
            if (material != null)
            {
                WriteFile(Path.Combine("Textures", texture), Dds.Build(drawing.Width, drawing.Height, 1, 87, false, bgra));
                var materialPath = $"GShuf\\LoadCards\\{packId}\\{stem}.bgsm";
                WriteMaterial(Path.Combine("Materials", materialPath), material);
                cardMaterial = "Materials\\" + materialPath;
                (pictureWidth, pictureHeight) = s.FaceSize;
                target = new NifFile.UvLayout(0, 0, 1, 0, 0, 1);
                image.CardTexture = texture;
            }
        }
        var sheet = _game.ReadMesh(SheetModel);
        var renamed = sheet == null ? null : NifFile.Rewrite(sheet, reference =>
            Paths.Normalize(reference, "materials").Equals(SheetMaterial, StringComparison.OrdinalIgnoreCase) ? cardMaterial : null);
        var sheetShape = renamed == null ? null : NifFile.MaterialShape(renamed, name => Paths.Normalize(name, "materials").Equals(Paths.Normalize(cardMaterial, "materials"), StringComparison.OrdinalIgnoreCase));
        if (sheetShape is not { } ss || ss.Extents.X <= 0 || ss.Extents.Z <= 0) return null;
        var height = Math.Min(CardHeight, CardWidth * pictureHeight / pictureWidth);
        var width = height * pictureWidth / pictureHeight;
        var card = NifFile.Reshape(renamed!, width / ss.Extents.X, height / ss.Extents.Z, ss.Layout, target);
        return card == null ? null : (card, width, s.Layout);
    }

    // A copy of the sheet's own material that shows a drawn face: its texture, a flat normal map, no specular, and alpha testing
    // so the parts no triangle covers stay see-through.
    private BGSM? DrawnCardMaterial(string texture)
    {
        var bytes = _game.ReadMaterial(SheetMaterial);
        if (bytes == null || MaterialFiles.Read(bytes) is not BGSM material) return null;
        material.DiffuseTexture = texture.Replace('\\', '/');
        material.NormalTexture = "Shared/FlatFlat_n.dds";
        material.SmoothSpecTexture = "Shared/Black01_d.dds";
        material.AlphaTest = true;
        material.AlphaTestRef = 128;
        (material.UOffset, material.VOffset, material.UScale, material.VScale) = (0, 0, 1, 1);
        return material;
    }

    // The picture's material (the pack's copy) and diffuse texture (the pack's copy, else the game's), or why they cannot be read.
    private (BaseMaterialFile? Material, byte[]? Texture, string Relative, string? Problem) PictureTexture(ImageOutput image)
    {
        var materialPath = Path.Combine(_out, "Materials", image.Material);
        if (!File.Exists(materialPath)) return (null, null, "", "its material file is missing from the output");
        BaseMaterialFile material;
        try { material = MaterialFiles.Read(File.ReadAllBytes(materialPath)); }
        catch (Exception e) { return (null, null, "", $"its material cannot be read ({e.Message})"); }
        var diffuse = material switch { BGSM m => m.DiffuseTexture, BGEM e => e.BaseTexture, _ => null };
        if (string.IsNullOrEmpty(diffuse)) return (null, null, "", "its material names no picture texture");
        var relative = Paths.Normalize(diffuse, "textures");
        var packCopy = Path.Combine(_out, "Textures", relative);
        var texture = File.Exists(packCopy) ? File.ReadAllBytes(packCopy) : _game.ReadTexture(relative);
        if (texture == null) return (null, null, relative, $"texture {relative} was found neither in the output nor in the game archives");
        if (Dds.ReadDimensions(texture) == null) return (null, null, relative, $"texture {relative} is not a DDS file");
        return (material, texture, relative, null);
    }

    // The picture's face drawn from the front with the pack's texture: each triangle, farthest first, sampled with the material's
    // texture offset and scale and its alpha test or blending; pixels no triangle covers stay transparent. The resolution keeps
    // the texture's own texel density, with the longer side at most 2048 pixels.
    private (byte[] Rgba, int Width, int Height)? DrawFace(ImageOutput image, NifFile.PictureShape shape)
    {
        var (material, bytes, _, _) = PictureTexture(image);
        if (material == null || bytes == null || Dds.DecodeTop(bytes) is not { } texture) return null;
        static double Cross(double ax, double ay, double bx, double by) => ax * by - ay * bx;
        double sum = 0, total = 0;
        foreach (var t in shape.Face)
        {
            var faceArea = Math.Abs(Cross(t.P1.X - t.P0.X, t.P1.Y - t.P0.Y, t.P2.X - t.P0.X, t.P2.Y - t.P0.Y)) / 2;
            var texelArea = Math.Abs(Cross(t.T1.U - t.T0.U, t.T1.V - t.T0.V, t.T2.U - t.T0.U, t.T2.V - t.T0.V)) / 2 * Math.Abs(material.UScale * material.VScale) * texture.Width * texture.Height;
            if (faceArea <= 0) continue;
            sum += Math.Sqrt(texelArea / faceArea) * faceArea;
            total += faceArea;
        }
        var perUnit = Math.Min(total > 0 ? sum / total : 1, 2048 / Math.Max(shape.FaceSize.Width, shape.FaceSize.Height));
        var width = Math.Max(1, (int)Math.Ceiling(shape.FaceSize.Width * perUnit));
        var height = Math.Max(1, (int)Math.Ceiling(shape.FaceSize.Height * perUnit));
        var rgba = new byte[width * height * 4];
        foreach (var t in shape.Face.OrderBy(t => t.Depth))
        {
            var (x0, y0, x1, y1, x2, y2) = (t.P0.X * perUnit, t.P0.Y * perUnit, t.P1.X * perUnit, t.P1.Y * perUnit, t.P2.X * perUnit, t.P2.Y * perUnit);
            var area = Cross(x1 - x0, y1 - y0, x2 - x0, y2 - y0);
            if (Math.Abs(area) < 1e-9) continue;
            var left = Math.Max(0, (int)Math.Floor(Math.Min(x0, Math.Min(x1, x2))));
            var right = Math.Min(width - 1, (int)Math.Ceiling(Math.Max(x0, Math.Max(x1, x2))));
            var top = Math.Max(0, (int)Math.Floor(Math.Min(y0, Math.Min(y1, y2))));
            var bottom = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(y0, Math.Max(y1, y2))));
            for (var py = top; py <= bottom; py++)
            {
                for (var px = left; px <= right; px++)
                {
                    var (cx, cy) = (px + 0.5, py + 0.5);
                    var w0 = Cross(x1 - cx, y1 - cy, x2 - cx, y2 - cy) / area;
                    var w1 = Cross(x2 - cx, y2 - cy, x0 - cx, y0 - cy) / area;
                    var w2 = 1 - w0 - w1;
                    if (w0 < -1e-6 || w1 < -1e-6 || w2 < -1e-6) continue;
                    var u = (w0 * t.T0.U + w1 * t.T1.U + w2 * t.T2.U) * material.UScale + material.UOffset;
                    var v = (w0 * t.T0.V + w1 * t.T1.V + w2 * t.T2.V) * material.VScale + material.VOffset;
                    var (r, g, b, a) = Sample(texture, u, v);
                    var at = (py * width + px) * 4;
                    if (material.AlphaTest)
                    {
                        if (a < material.AlphaTestRef) continue;
                        (rgba[at], rgba[at + 1], rgba[at + 2], rgba[at + 3]) = ((byte)r, (byte)g, (byte)b, 255);
                    }
                    else if (material.AlphaBlendMode == BaseMaterialFile.AlphaBlendModeType.Standard)
                    {
                        // Source over what is already drawn.
                        var (sa, da) = (a / 255.0, rgba[at + 3] / 255.0);
                        var oa = sa + da * (1 - sa);
                        if (oa <= 0) continue;
                        byte Mix(double s1, byte d1) => (byte)Math.Round((s1 * sa + d1 * da * (1 - sa)) / oa);
                        (rgba[at], rgba[at + 1], rgba[at + 2], rgba[at + 3]) = (Mix(r, rgba[at]), Mix(g, rgba[at + 1]), Mix(b, rgba[at + 2]), (byte)Math.Round(oa * 255));
                    }
                    else
                    {
                        (rgba[at], rgba[at + 1], rgba[at + 2], rgba[at + 3]) = ((byte)r, (byte)g, (byte)b, 255);
                    }
                }
            }
        }
        return (rgba, width, height);
    }

    // The texture's colour at (u, v): bilinear between the four nearest texels, the texture repeating past its edges.
    private static (double R, double G, double B, double A) Sample((byte[] Rgba, int Width, int Height) texture, double u, double v)
    {
        var x = u * texture.Width - 0.5;
        var y = v * texture.Height - 0.5;
        var (ix, iy) = ((int)Math.Floor(x), (int)Math.Floor(y));
        var (fx, fy) = (x - ix, y - iy);
        int Wrap(int i, int n) => ((i % n) + n) % n;
        double Texel(int tx, int ty, int channel) => texture.Rgba[(Wrap(ty, texture.Height) * texture.Width + Wrap(tx, texture.Width)) * 4 + channel];
        double Channel(int c) =>
            (Texel(ix, iy, c) * (1 - fx) + Texel(ix + 1, iy, c) * fx) * (1 - fy) + (Texel(ix, iy + 1, c) * (1 - fx) + Texel(ix + 1, iy + 1, c) * fx) * fy;
        return (Channel(0), Channel(1), Channel(2), Channel(3));
    }

    /// <summary>The folder Automated Loading Screen Replacer (ALR, Nexus 52322) turns pictures from, relative to Data, and the
    /// prefix of the files Gallery Shuffle puts there.</summary>
    public const string AlrImageFolder = @"F4SE\Plugins\ALR_Image_Dir";
    public const string AlrFilePrefix = "GShuf-";

    /// <summary>The optional ALR module: every pack picture that has a loading screen card, cut to the part of its texture the
    /// card shows and turned the way it reads on its frame, into <paramref name="folder"/>, the picture folder ALR reads: a DDS
    /// file when the texture already reads that way, else a PNG file.</summary>
    public void WriteAlrImages(List<PackOutput> packs, string folder)
    {
        Directory.CreateDirectory(folder);
        var written = 0;
        var turned = 0;
        var failed = new List<(string Material, string Problem)>();
        foreach (var pack in packs.Where(p => p.Id != "vanilla"))
        {
            foreach (var image in pack.Images.Where(i => i.Card != null && i.CardLayout != null))
            {
                var (bytes, extension, problem) = AlrImage(image);
                if (bytes == null)
                {
                    failed.Add((image.Material, problem ?? "unknown"));
                    continue;
                }
                File.WriteAllBytes(Path.Combine(folder, $"{AlrFilePrefix}{pack.Id}-{Path.GetFileNameWithoutExtension(image.Material)}{extension}"), bytes);
                written++;
                if (extension == ".png") turned++;
            }
        }
        _report.Info($"ALR module: {Report.Count(written, "picture", "pictures")} written to {folder} ({turned} redrawn upright because their frames show them mirrored or turned)");
        foreach (var (material, problem) in failed) _report.Warn($"ALR module: {material} left out: {problem}");
    }

    // The picture's diffuse texture (the pack's copy, else the game's), cut to the part its card shows and turned the way it reads
    // on its frame, with the file extension that goes with it, or why it cannot be.
    private (byte[]? Bytes, string Extension, string? Problem) AlrImage(ImageOutput image)
    {
        // A card drawn from its frame's whole face already holds the picture as it reads.
        if (image.CardTexture != null)
        {
            var drawn = Path.Combine(_out, "Textures", image.CardTexture);
            return File.Exists(drawn) && Dds.DecodeTop(File.ReadAllBytes(drawn)) is { } d
                ? (Png(d.Rgba, d.Width, d.Height), ".png", null)
                : (null, "", $"its drawn card texture {image.CardTexture} cannot be read");
        }
        var (material, texture, relative, problem) = PictureTexture(image);
        if (material == null || texture == null) return (null, "", problem);
        if (Dds.ReadDimensions(texture) is not { } size) return (null, "", $"texture {relative} is not a DDS file");
        // The material's own texture offset and scale move the part the shape's coordinates select.
        var card = image.CardLayout!.Value;
        var layout = new NifFile.UvLayout(card.OriginU * material.UScale + material.UOffset, card.OriginV * material.VScale + material.VOffset,
            card.AcrossU * material.UScale, card.AcrossV * material.VScale, card.DownU * material.UScale, card.DownV * material.VScale);
        // Textures repeat, so a rectangle inside one repeat of the texture is that same part of it.
        var shifted = layout.Rect;
        layout = layout with { OriginU = layout.OriginU - MathF.Floor(shifted.U0 + 0.01f), OriginV = layout.OriginV - MathF.Floor(shifted.V0 + 0.01f) };
        var rect = layout.Rect;
        if (rect.U0 < -0.01f || rect.U1 > 1.01f || rect.V0 < -0.01f || rect.V1 > 1.01f)
            return (null, "", $"its frame repeats the texture (coordinates {rect.U0:0.###}..{rect.U1:0.###} across, {rect.V0:0.###}..{rect.V1:0.###} down), so no single picture can be cut from it");
        if (layout.Upright)
        {
            var x0 = (int)Math.Floor(Math.Clamp(rect.U0, 0f, 1f) * size.Width);
            var x1 = (int)Math.Ceiling(Math.Clamp(rect.U1, 0f, 1f) * size.Width);
            var y0 = (int)Math.Floor(Math.Clamp(rect.V0, 0f, 1f) * size.Height);
            var y1 = (int)Math.Ceiling(Math.Clamp(rect.V1, 0f, 1f) * size.Height);
            // A rectangle within a block of the whole texture keeps the file as it is.
            if (x0 < 4 && y0 < 4 && x1 > size.Width - 4 && y1 > size.Height - 4) return (texture, ".dds", null);
            var cut = Dds.Crop(texture, x0, y0, x1, y1);
            return cut != null ? (cut, ".dds", null) : (null, "", $"texture {relative} is in a format this cannot cut");
        }
        // A picture its frame shows mirrored or turned is redrawn the way it reads there.
        if (Dds.DecodeTop(texture) is not { } top) return (null, "", $"texture {relative} is in a format this cannot read");
        var width = (int)MathF.Round(MathF.Abs(layout.AcrossU) * top.Width + MathF.Abs(layout.AcrossV) * top.Height);
        var height = (int)MathF.Round(MathF.Abs(layout.DownU) * top.Width + MathF.Abs(layout.DownV) * top.Height);
        if (width <= 0 || height <= 0) return (null, "", "its part of the texture is smaller than a pixel");
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (u, v) = layout.At((x + 0.5f) / width, (y + 0.5f) / height);
                var px = Math.Clamp((int)(u * top.Width), 0, top.Width - 1);
                var py = Math.Clamp((int)(v * top.Height), 0, top.Height - 1);
                Buffer.BlockCopy(top.Rgba, (py * top.Width + px) * 4, rgba, (y * width + x) * 4, 4);
            }
        }
        return (Png(rgba, width, height), ".png", null);
    }

    // RGBA pixels (row by row) as a PNG file.
    private static byte[] Png(byte[] rgba, int width, int height)
    {
        using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var row = new byte[width * 4];
        for (var y = 0; y < height; y++)
        {
            // The bitmap stores each pixel as blue, green, red, alpha.
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                (row[4 * x], row[4 * x + 1], row[4 * x + 2], row[4 * x + 3]) = (rgba[i + 2], rgba[i + 1], rgba[i], rgba[i + 3]);
            }
            System.Runtime.InteropServices.Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
        }
        bitmap.UnlockBits(data);
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }

    // The share pages in menu order, their MCM section names and their page titles; the GShuf_ prefix keeps Papyrus from
    // recasing the section names (its strings share one case-insensitive cache).
    private static readonly string[] Categories = ["paintings", "posters", "signage", "magazines", "quest", "other", "pipboyscreen", "pipboyskin", "loadingscreens"];
    private static readonly string[] Titles = ["GShuf_Paintings", "GShuf_Posters", "GShuf_Signage", "GShuf_Magazines", "GShuf_Quest", "GShuf_Other", "GShuf_PipBoyScreen", "GShuf_PipBoySkin", "GShuf_LoadingScreens"];
    private static readonly string[] PageTitles = ["Paintings", "Posters", "Signage", "Magazines", "Quest", "Other replacers", "Pip-Boy screen", "Pip-Boy skin", "Loading screens"];

    /// <summary>Locations with game files of their own: the folder their art sits in, the plugin their records come from and the page heading.</summary>
    private static readonly (string Folder, string Plugin, string Name)[] Locations = [("dlc03", "dlccoast.esm", "Far Harbor"), ("dlc04", "dlcnukaworld.esm", "Nuka-World")];

    /// <summary>Packs of one category page (1-based slots) that replace some of the same objects, so their shares add up to 100,
    /// and the location every one of them is tied to ("" when they are not all tied to the same one).</summary>
    public sealed record SlotGroup(List<int> Slots, string Location);

    /// <summary>The pages a pack's one slider can sit on, in menu order.</summary>
    private static readonly string[] ArtPages = ["paintings", "posters", "signage", "magazines", "quest", "other"];

    /// <summary>What a page's objects are called in a help line.</summary>
    private static readonly Dictionary<string, (string One, string Many)> Nouns = new(StringComparer.Ordinal)
    {
        ["paintings"] = ("painting", "paintings"), ["posters"] = ("poster", "posters"), ["signage"] = ("sign", "signs"),
        ["magazines"] = ("magazine", "magazines"), ["quest"] = ("quest picture", "quest pictures"), ["other"] = ("prop", "props"),
    };

    /// <summary>How much of a pack's art a page holds; the Other replacers page counts every kind.</summary>
    private static int OnPage(PackOutput pack, string page) =>
        page == "other" ? pack.Images.Count(i => IsKind(i.Category)) + pack.Movies.Count(m => IsKind(m.Category)) : pack.CountIn(page);

    /// <summary>The page a pack's one slider sits on: the page with the most of its art, the first in menu order on a tie; "" for a
    /// pack with art on none (the plugin's Packs::MainPage applies the same rule).</summary>
    public static string MainPage(PackOutput pack)
    {
        var best = "";
        var most = 0;
        foreach (var page in ArtPages)
        {
            var n = OnPage(pack, page);
            if (n > most) (best, most) = (page, n);
        }
        return best;
    }

    /// <summary>What a pack's slider sets, its main page's objects first: "129 magazines, 4 paintings, 4 signs, 1 poster and 1 prop".</summary>
    private static string Reach(PackOutput pack)
    {
        var main = MainPage(pack);
        var parts = ArtPages.Select(page => (Page: page, Count: OnPage(pack, page))).Where(x => x.Count > 0)
            .OrderBy(x => x.Page == main ? 0 : 1).ThenByDescending(x => x.Count)
            .Select(x => Report.Count(x.Count, Nouns[x.Page].One, Nouns[x.Page].Many)).ToList();
        return parts.Count <= 1 ? string.Concat(parts) : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
    }

    /// <summary>"shows" for a pack with one object, "show" otherwise, so a help line reads right.</summary>
    private static string Shows(PackOutput pack) => ArtPages.Sum(page => OnPage(pack, page)) == 1 ? "shows" : "show";

    /// <summary>The packs a page or list shows, in slot order: a page lists the packs whose one slider sits on it; a kind lists the Other
    /// replacers packs with something for it; loading screens every pack with pictures; the Pip-Boy lists every pack with something for them.</summary>
    private static List<PackOutput> Eligible(List<PackOutput> packs, string category) =>
        packs.Where(p => p.Id != "vanilla" && (ArtPages.Contains(category)
            ? MainPage(p) == category
            : IsKind(category)
                ? MainPage(p) == "other" && p.Covers(category)
                : p.Covers(category) || (category == "loadingscreens" && p.Images.Any(i => i.IsPicture)))).ToList();

    /// <summary>True for a kind category of the Other replacers page ("other_" and its folders).</summary>
    private static bool IsKind(string category) => category.StartsWith("other_", StringComparison.Ordinal);

    /// <summary>True for a slot (a material, or a material swap entry's material) that is one of the game's magazine backs: the back
    /// folders of the magazine materials, and the comic and journal materials named for their back (Props\Grognak\GrognakAprilBackPrewar
    /// on Comic_GrognakApr_Prewar.nif, SurgicalJournalRaisedByRobotsBack on Journal_Surgical01.nif); a back follows its magazine's
    /// cover and has no slider.</summary>
    private static bool IsMagazineBack(string? slot)
    {
        var text = (slot ?? "").ToLowerInvariant().Replace('/', '\\');
        if (text.StartsWith('=')) text = text[(text.IndexOf('|') + 1)..];
        if (text.StartsWith(@"props\comicsandmagazines", StringComparison.Ordinal) && text.Contains(@"\backside\", StringComparison.Ordinal)) return true;
        return text.StartsWith(@"props\grognak\", StringComparison.Ordinal) && Path.GetFileNameWithoutExtension(text).Contains("back", StringComparison.Ordinal);
    }

    /// <summary>Moves magazine backs off the Magazines and Other replacers pages into "magazinebacks", which no page shows: the plugin
    /// gives a back the pack its magazine's cover shows, so a pack with only backs gets no slider.</summary>
    public void SplitMagazineBacks(List<PackOutput> packs)
    {
        foreach (var image in packs.SelectMany(p => p.Images).Where(i => i.Category != "magazinebacks" && IsMagazineBack(i.Slot))) image.Category = "magazinebacks";
        foreach (var (key, slot) in _extraSlots.ToList())
        {
            if (slot.Category != "magazinebacks" && IsMagazineBack(slot.Material)) _extraSlots[key] = slot with { Category = "magazinebacks" };
        }
        foreach (var pack in packs.Where(p => p.Id != "vanilla" && p.Images.Any(i => i.Category == "magazinebacks") && !p.Images.Any(i => i.Category == "magazines")))
        {
            var nothingElse = pack.Images.All(i => i.Category == "magazinebacks") && pack.Video == null && pack.Music == null && pack.Movies.Count == 0;
            _report.Warn($"{pack.Name}: has magazine backs but no magazine covers, so its backs are not used{(nothingElse ? " and it gets no slider" : "")}. A back shows only with a cover from the same pack.");
        }
    }

    /// <summary>A model path as the game's records and "#" slots both name it: lowercase, backslashes, relative to Meshes.</summary>
    private static string ModelKey(string path)
    {
        var key = path.ToLowerInvariant().Replace('/', '\\');
        return key.StartsWith(@"meshes\", StringComparison.Ordinal) ? key[7..] : key;
    }

    /// <summary>Keys of the objects an image can show on, shared by two packs exactly when both can land on the same object; "*" for a picture that fills any frame.</summary>
    private IEnumerable<string> ObjectKeys(ImageOutput image)
    {
        var slot = (image.Slot ?? "").ToLowerInvariant().Replace('/', '\\');
        if (slot.Length == 0)
        {
            yield return "*";
            yield break;
        }
        var rest = slot[1..];
        switch (slot[0])
        {
            case '#':
                yield return "model:" + ModelKey(rest);
                break;
            case '&':
                // The object's model file slot rolls together with the object's own slot.
                yield return "object:" + rest;
                if (FormKey.FromText(rest) is { } key && _game.GameModelRecord(key) is { Model.Length: > 0 } record) yield return "model:" + ModelKey(record.Model);
                break;
            case '%':
                yield return "texture:" + rest;
                break;
            case '~':
                yield return "sound:" + rest;
                break;
            case '!':
                // A texture set rolls once for all its entries.
                yield return "set:" + rest.Split('|')[0];
                break;
            case '=':
                // A swap entry's slot and the material it shows roll together on the same geometry.
                yield return "material:" + rest[(rest.IndexOf('|') + 1)..];
                break;
            case '@':
                yield return slot;
                break;
            default:
                yield return "material:" + slot;
                break;
        }
    }

    /// <summary>The location whose game files an object key names, or "" for the base game's and for a picture that fills any frame.</summary>
    private static string LocationOf(string key)
    {
        var colon = key.IndexOf(':');
        var kind = colon < 0 ? "" : key[..colon];
        var rest = colon < 0 ? key.TrimStart('@') : key[(colon + 1)..];
        if (kind is "object" or "set")
        {
            var file = rest[..Math.Max(0, rest.LastIndexOf(':'))];
            return Locations.FirstOrDefault(l => l.Plugin == file).Name ?? "";
        }
        var folders = rest.Split('\\').Take(3).ToList();
        return Locations.FirstOrDefault(l => folders.Contains(l.Folder)).Name ?? "";
    }

    /// <summary>Every pack of a page as its own group, with the location it is tied to ("" for none): sliders are independent, so an
    /// object two packs replace splits between them by their numbers. The Pip-Boy lists are one group.</summary>
    public List<SlotGroup> Groups(List<PackOutput> slots, string category)
    {
        if (category is "pipboyscreen" or "pipboyskin") return slots.Count == 0 ? [] : [new SlotGroup(Enumerable.Range(1, slots.Count).ToList(), "")];
        if (category == "other" || IsKind(category) || category == "loadingscreens") return slots.Select((_, i) => new SlotGroup([i + 1], "")).ToList();
        var keys = slots.Select(p => p.Images.Where(i => i.Category == category).SelectMany(ObjectKeys)
            .Concat(p.Movies.Where(m => m.Category == category).Select(m => "movie:" + m.Path.ToLowerInvariant().Replace('/', '\\'))).ToHashSet()).ToList();
        // A pack is tied to a location when everything it replaces on this page is that location's.
        var locations = keys.Select(k => k.Select(LocationOf).Distinct().ToList() is [var only] ? only : "").ToList();
        return slots.Select((_, i) => new SlotGroup([i + 1], locations[i])).ToList();
    }

    /// <summary>Each kind category of the Other replacers page: the location it belongs to ("" for none) and its folders as the game spells them.</summary>
    private readonly Dictionary<string, (string Location, List<string> Folders)> _kinds = new(StringComparer.Ordinal);

    /// <summary>Moves everything on the Other replacers page into a category per kind of object: the game folder the replaced file
    /// sits in (its first two folders, after a location's own folder), so packs of different kinds never share a slider.</summary>
    public void SplitOtherKinds(List<PackOutput> packs)
    {
        foreach (var pack in packs)
        {
            foreach (var image in pack.Images.Where(i => i.Category == "other")) image.Category = KindCategory(KindPath(image.Slot));
            for (var m = 0; m < pack.Movies.Count; m++)
            {
                if (pack.Movies[m].Category == "other") pack.Movies[m] = pack.Movies[m] with { Category = KindCategory(pack.Movies[m].Path.ToLowerInvariant().Replace('/', '\\')) };
            }
        }
        foreach (var (key, slot) in _extraSlots.ToList())
        {
            if (slot.Category == "other") _extraSlots[key] = slot with { Category = KindCategory(KindPath(slot.Material)) };
        }
        _report.Info($"other replacers: {Report.Count(_kinds.Count, "kind of object", "kinds of object")}, each with its own sliders");
    }

    /// <summary>The game file whose folders name a slot's kind: the model of an object, the material a swap entry shows, the file of the rest.</summary>
    private string KindPath(string? slot)
    {
        var text = (slot ?? "").ToLowerInvariant().Replace('/', '\\');
        if (text.Length == 0) return "";
        var rest = text[1..];
        return text[0] switch
        {
            '#' or '%' or '@' or '~' => rest,
            '&' => FormKey.FromText(rest) is { } key && _game.GameModelRecord(key) is { Model.Length: > 0 } record ? ModelKey(record.Model) : @"objects\" + rest,
            '=' => rest[(rest.IndexOf('|') + 1)..],
            '!' => @"texture sets\" + rest,
            _ => text,
        };
    }

    /// <summary>The category of the kind a game file belongs to: "other_", a location's folder when it has one, and its first two folders.</summary>
    private string KindCategory(string path)
    {
        var folders = path.Split('\\', StringSplitOptions.RemoveEmptyEntries).SkipLast(1).ToList();
        var location = folders.Count > 0 ? Locations.FirstOrDefault(l => l.Folder == folders[0]) : default;
        var inLocation = location.Folder != null;
        if (inLocation) folders.RemoveAt(0);
        var kind = folders.Take(2).ToList();
        if (kind.Count == 0) kind.Add("general");
        var parts = (inLocation ? kind.Prepend(location.Folder!) : kind).Select(p => new string(p.Where(char.IsAsciiLetterOrDigit).ToArray()));
        var category = "other_" + string.Join("_", parts);
        if (!_kinds.ContainsKey(category))
        {
            var spelled = new List<string>();
            for (var depth = 1; depth <= kind.Count; depth++) spelled.Add(_game.FolderCase(string.Join('\\', inLocation ? kind.Take(depth).Prepend(location.Folder!) : kind.Take(depth))).Split('\\')[^1]);
            _kinds[category] = (inLocation ? location.Name! : "", spelled);
        }
        return category;
    }

    /// <summary>A folder name in plain words: "FoodAndFoodWare" becomes "Food and food ware"; words in capitals, such as "TVs", stay.</summary>
    private static string Humanize(string folder)
    {
        var words = System.Text.RegularExpressions.Regex.Split(folder.Replace('_', ' '), @"(?<=[a-z0-9])(?=[A-Z])|\s+").Where(w => w.Length > 0).ToList();
        for (var i = 0; i < words.Count; i++)
        {
            var capitals = words[i].Count(char.IsUpper);
            words[i] = capitals > 1 ? words[i] : i == 0 ? char.ToUpperInvariant(words[i][0]) + words[i][1..] : words[i].ToLowerInvariant();
        }
        return string.Join(' ', words);
    }

    /// <summary>The heading of every kind category: its last folder in plain words, with its first folder when two kinds of a
    /// location would read alike, after the location's name.</summary>
    private Dictionary<string, string> KindTitles(IEnumerable<string> categories)
    {
        var titles = categories.ToDictionary(c => c, c => Humanize(_kinds[c].Folders[^1]), StringComparer.Ordinal);
        foreach (var clash in titles.GroupBy(t => (_kinds[t.Key].Location, t.Value.ToLowerInvariant())).Where(g => g.Count() > 1).SelectMany(g => g).ToList())
        {
            var folders = _kinds[clash.Key].Folders;
            titles[clash.Key] = folders.Count > 1 ? $"{Humanize(folders[0])}: {Humanize(folders[^1]).ToLowerInvariant()}" : Humanize(folders[0]);
        }
        foreach (var category in titles.Keys.ToList())
        {
            if (_kinds[category].Location.Length > 0) titles[category] = $"{_kinds[category].Location}: {titles[category]}";
        }
        return titles;
    }

    /// <summary>Every share category in menu order: the picture pages, the Other replacers page's packs, each of its kinds by heading, the lists and loading screens.</summary>
    private List<string> ShareCategories(List<PackOutput> packs)
    {
        var kinds = packs.SelectMany(p => p.Images.Select(i => i.Category).Concat(p.Movies.Select(m => m.Category))).Where(c => c.StartsWith("other_", StringComparison.Ordinal)).Distinct().ToList();
        var titles = KindTitles(kinds);
        var ordered = kinds.OrderBy(k => _kinds[k].Location.Length == 0 ? -1 : Array.FindIndex(Locations, l => l.Name == _kinds[k].Location)).ThenBy(k => titles[k], StringComparer.OrdinalIgnoreCase);
        return Categories.TakeWhile(c => c != "other").Append("other").Concat(ordered).Concat(Categories.SkipWhile(c => c != "other").Skip(1)).ToList();
    }

    /// <summary>A category's section name in packs.json: "GShuf_" and the category, first letter in capitals (kinds keep their lowercase folders).</summary>
    private static string SectionTitle(string category)
    {
        var index = Array.IndexOf(Categories, category);
        return index >= 0 ? Titles[index] : "GShuf_Other" + category[5..];
    }

    /// <summary>The MCM section a category's settings live in: GShuf_Other for every kind, the category's own section for the rest.</summary>
    private static string SettingSection(string category)
    {
        return category.StartsWith("other_", StringComparison.Ordinal) ? "GShuf_Other" : SectionTitle(category);
    }

    /// <summary>A category's MCM setting name for a key such as "iSlot3": kinds add their kind after it ("iSlot3_setdressing_foodandfoodware"),
    /// the rest use the key as it is.</summary>
    private static string SettingKey(string key, string category)
    {
        return category.StartsWith("other_", StringComparison.Ordinal) ? $"{key}_{category["other_".Length..]}" : key;
    }

    /// <summary>Names how the MCM settings apply (one slider per pack on the page with most of its art, setting all of its art; per-kind
    /// sliders under a switch on Other replacers; no Vanilla rows); part of the signature, so a conversion under another model resets the shares.</summary>
    private const string SettingsModel = "settings-model-4";

    /// <summary>The pack signature the MCM script compares with the stored one, resetting every share when they differ: each pack with
    /// its item count, and each share category's setting keys with the packs in slot order and the groups, so the signature changes
    /// exactly when a setting would name a different pack.</summary>
    private string Signature(List<PackOutput> packs, int slotsPerCategory)
    {
        var text = new StringBuilder(SettingsModel);
        foreach (var pack in packs) text.Append($"|{pack.Id}:{pack.Images.Count}");
        foreach (var category in ShareCategories(packs))
        {
            var slots = Eligible(packs, category).Take(slotsPerCategory).ToList();
            text.Append($"|{SettingKey("iSlot", category)}:{SettingSection(category)}={string.Join(",", slots.Select(p => p.Id))}");
            foreach (var group in Groups(slots, category)) text.Append($"[{string.Join(",", group.Slots)}]");
        }
        return ShortHash(text.ToString());
    }

    public void WritePacksJson(List<PackOutput> packs, List<Scanner.Restore> restores, int slotsPerCategory)
    {
        var categories = ShareCategories(packs);
        var groups = new Dictionary<string, List<List<int>>>();
        foreach (var category in categories)
        {
            var found = Groups(Eligible(packs, category).Take(slotsPerCategory).ToList(), category);
            if (found.Count > 0) groups[category] = found.Select(g => g.Slots).ToList();
        }
        var sections = categories.Select(SectionTitle).ToList();
        if (sections.Count > 128) throw new InvalidOperationException($"{Report.Count(sections.Count, "share category", "share categories")} in packs.json, but a Papyrus array holds at most 128; convert fewer mods");
        var doc = new
        {
            version = 1,
            generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            signature = Signature(packs, slotsPerCategory),
            sections,
            groups,
            restores = restores.Distinct().Select(r => new { kind = r.Kind, form = r.Form, from = r.From, mod = r.Mod, game = r.Game, index = r.Index, gameColor = r.GameColor, modColor = r.ModColor }),
            packs = packs.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                video = p.Video,
                music = p.Music,
                movies = p.Movies.Count == 0 ? null : p.Movies.Select(m => new { path = m.Path, file = m.File, category = m.Category }),
                screens = p.Screens.Count == 0 ? null : p.Screens.Select(s => new { screen = s.Screen, card = s.Card, model = s.Model, gameModel = s.GameModel, swap = s.Swap, color = s.Color }),
                images = p.Images.Select(i =>
                {
                    var row = new Dictionary<string, object> { ["category"] = i.Category, ["material"] = i.Material };
                    if (i.Slot != null) row["slot"] = i.Slot;
                    row["width"] = i.Width;
                    row["height"] = i.Height;
                    if (i.DropSwap) row["dropSwap"] = true;
                    if (i.SwapForm != null) row["swapForm"] = i.SwapForm;
                    if (i.Name != null) row["name"] = i.Name;
                    if (i.Bounds != null) row["bounds"] = i.Bounds;
                    if (i.Card != null) row["card"] = i.Card;
                    if (i.CardWidth > 0) row["cardWidth"] = Math.Round(i.CardWidth, 3);
                    return row;
                }),
            }),
        };
        WriteFile(Path.Combine("GShuf", "packs.json"), JsonSerializer.SerializeToUtf8Bytes(doc, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Generates the MCM pages: one per art category, listing the packs whose one slider sits there (the page with most of
    /// the pack's art), packs tied to one location under that location's heading; the Other replacers page (see OtherPage); and one
    /// Pip-Boy page with a list for the screen background and one for the skin (Vanilla or a pack), stored as iPick.</summary>
    public void WriteMcm(List<PackOutput> packs, int slotsPerCategory)
    {
        var pages = new List<object>();
        var pipboyContent = new List<object>
        {
            new { type = "text", text = "Which pack the Pip-Boy wears; Vanilla keeps the game's own. Changes take effect when you apply them.", html = false },
        };
        foreach (var pack in packs.Where(p => p.Id != "vanilla" && MainPage(p).Length > 0 && ArtPages.Count(page => OnPage(p, page) > 0) > 1))
        {
            _report.Info($"{pack.Name}: its slider sits on {PageTitles[Array.IndexOf(Categories, MainPage(pack))]} and sets its {Reach(pack)}");
        }
        var settings = new StringBuilder("[GShuf_Main]\nbBlankCanvases=0\nbNewLayoutEachLoad=0\niSeed=1\nsPackSignature=\n");
        var categories = ShareCategories(packs);
        var kindTitles = KindTitles(categories.Where(c => c.StartsWith("other_", StringComparison.Ordinal)));
        // The Other replacers page's settings, inserted as one GShuf_Other section where the page comes in menu order.
        var otherSettings = new StringBuilder();
        var otherAt = -1;
        foreach (var category in categories)
        {
            var index = Array.IndexOf(Categories, category);
            var kind = index < 0;
            var sectionName = SettingSection(category);
            var pick = category.StartsWith("pipboy", StringComparison.Ordinal);
            // Loading screens show a pack's pictures, so every pack with pictures has a share there (the plugin orders them the same way).
            var loading = category == "loadingscreens";
            var eligible = Eligible(packs, category);
            var slots = eligible.Take(slotsPerCategory).ToList();
            if (eligible.Count > slotsPerCategory)
            {
                var where = kind ? $"Other replacers, {kindTitles[category]}" : PageTitles[index];
                _report.Warn($"{where}: {Report.Count(eligible.Count, "pack", "packs")} but only {Report.Count(slotsPerCategory, "slot", "slots")}; left out: {string.Join(", ", eligible.Skip(slotsPerCategory).Select(p => p.Name))}. Convert fewer mods or fewer FOMOD sets for this category.");
            }
            if (category == "other")
            {
                if (otherAt < 0) otherAt = settings.Length;
                // One share and one "Set by kind of object" switch per pack.
                for (var s = 1; s <= slots.Count; s++) otherSettings.Append($"iSlot{s}=0\n");
                for (var s = 1; s <= slots.Count; s++) otherSettings.Append($"bKinds{s}=0\n");
                if (slots.Count > 0) pages.Add(new { pageDisplayName = "Other replacers", content = OtherPage(packs, slots, slotsPerCategory, kindTitles) });
                continue;
            }
            if (kind)
            {
                // One share per pack the kind shows, the kind after each key; the Other replacers page shows them under each pack's switch.
                for (var s = 1; s <= slots.Count; s++) otherSettings.Append($"{SettingKey($"iSlot{s}", category)}=0\n");
                continue;
            }
            settings.Append($"\n[{sectionName}]\n");
            if (pick)
            {
                settings.Append("iPick=0\n");
                if (slots.Count == 0) continue;
                var options = new List<string> { "Vanilla" };
                options.AddRange(slots.Select(p => p.Name));
                var label = category == "pipboyscreen" ? "Screen background" : "Skin";
                pipboyContent.Add(new { id = $"iPick:{sectionName}", type = "dropdown", text = label, help = $"{Report.Count(slots.Count, "pack offers", "packs offer")} a {label.ToLowerInvariant()}.", valueOptions = new { options = options.ToArray(), sourceType = "ModSettingInt" } });
                continue;
            }
            for (var s = 1; s <= slotsPerCategory; s++) settings.Append($"iSlot{s}=0\n");
            if (slots.Count == 0) continue;
            object Slider(int slot)
            {
                var pack = slots[slot - 1];
                if (loading)
                {
                    var pictures = pack.Images.Count(i => i.IsPicture) + pack.CountIn(category);
                    return new { id = $"{SettingKey($"iSlot{slot}", category)}:{sectionName}", type = "slider", text = pack.Name, help = $"Percent of loading screens that show one of this pack's {Report.Count(pictures, "picture", "pictures")}.", valueOptions = new { min = 0, max = 100, step = 1, sourceType = "ModSettingInt" } };
                }
                return new { id = $"{SettingKey($"iSlot{slot}", category)}:{sectionName}", type = "slider", text = pack.Name, help = $"Percent of this pack's {Reach(pack)} that {Shows(pack)} its art.", valueOptions = new { min = 0, max = 100, step = 1, sourceType = "ModSettingInt" } };
            }
            // One slider per pack, in slot order.
            void AddGroups(List<object> content, IEnumerable<SlotGroup> found)
            {
                foreach (var group in found) foreach (var slot in group.Slots) content.Add(Slider(slot));
            }
            var groups = Groups(slots, category);
            var intro = loading
                ? "Each slider is the percent (0 to 100) of loading screens that show one of a pack's pictures. While ALR (Automated Loading Screen Replacer) is active, it shows the loading screens and these sliders do nothing."
                : "Each slider is the percent (0 to 100) of a pack's pictures that show its art; the rest show the game's own. Changes take effect when you apply them.";
            var page = new List<object> { new { type = "text", text = intro, html = false } };
            // The packs tied to no one location first, then each location's under its heading.
            var sections = groups.GroupBy(g => g.Location).OrderBy(s => s.Key.Length == 0 ? -1 : Array.FindIndex(Locations, l => l.Name == s.Key)).ToList();
            var headings = sections.Count > 1 || sections[0].Key.Length > 0;
            foreach (var section in sections)
            {
                if (headings) page.Add(new { type = "section", text = section.Key.Length == 0 ? "Base game" : section.Key });
                AddGroups(page, section);
            }
            pages.Add(new { pageDisplayName = PageTitles[index], content = page });
        }
        if (otherAt >= 0) settings.Insert(otherAt, "\n[GShuf_Other]\n" + otherSettings);
        if (pipboyContent.Count > 1) pages.Add(new { pageDisplayName = "Pip-Boy", content = pipboyContent.ToArray() });
        var config = new
        {
            modName = "GalleryShuffle",
            displayName = "Gallery Shuffle",
            minMcmVersion = 2,
            pluginRequirements = new[] { "GalleryShuffle.esl" },
            content = new object[]
            {
                new { type = "text", text = "<font size='24'>Gallery Shuffle</font>", html = true },
                new { type = "text", text = $"{Report.Count(packs.Count(p => p.Id != "vanilla"), "art pack", "art packs")} converted on {DateTime.Now:yyyy-MM-dd}. Each pack has one slider, on the page with most of its art.", html = false },
                new { type = "text", text = "Changes wait until you press Apply all, or answer Apply all when you leave the menu. Cancel there puts every setting back.", html = false },
                new { type = "spacer", numLines = 1 },
                new { type = "section", text = "Options" },
                new { id = "bBlankCanvases:GShuf_Main", type = "switcher", text = "Put art on blank canvases", help = "When on, empty canvases and frames also get pack art.", valueOptions = new { sourceType = "ModSettingBool" } },
                new { id = "bNewLayoutEachLoad:GShuf_Main", type = "switcher", text = "New layout on every load", help = "When on, every save you load gets a new arrangement with the same shares.", valueOptions = new { sourceType = "ModSettingBool" } },
                new { type = "button", text = "Apply all", help = "Saves as GShufApply, returns to the main menu and loads that save, so every change shows at once.", action = new { type = "CallFunction", form = "GalleryShuffle.esl|800", function = "ApplyAll" } },
                new { type = "button", text = "Shuffle layout", help = "Picks a new arrangement with the same shares.", action = new { type = "CallFunction", form = "GalleryShuffle.esl|800", function = "Shuffle" } },
                new { type = "button", text = "Reset all settings", help = "Every slider to 0, Pip-Boy on Vanilla, blank canvases off, new layout on every load off, seed 1, vanilla main menu video.", action = new { type = "CallFunction", form = "GalleryShuffle.esl|800", function = "ResetAll" } },
            },
            pages,
        };
        WriteFile(Path.Combine("MCM", "Config", "GalleryShuffle", "config.json"), JsonSerializer.SerializeToUtf8Bytes(config, new JsonSerializerOptions { WriteIndented = true }));
        // MCM reads the section names into a 1,024-byte buffer and ignores the whole file when they fill it.
        var sectionBytes = System.Text.RegularExpressions.Regex.Matches(settings.ToString(), @"^\[(.+)\]$", System.Text.RegularExpressions.RegexOptions.Multiline).Sum(m => m.Groups[1].Value.Length + 1);
        _report.Info($"MCM settings.ini: {sectionBytes} of the 1,024 bytes MCM reads for section names");
        if (sectionBytes >= 1022) throw new InvalidOperationException($"MCM settings.ini section names take {sectionBytes} of the 1,024 bytes MCM reads, so MCM would ignore every setting");
        WriteFile(Path.Combine("MCM", "Config", "GalleryShuffle", "settings.ini"), Encoding.ASCII.GetBytes(settings.ToString()));
    }

    /// <summary>The Other replacers page: one slider per pack whose slider sits here, its share of every object it replaces; a pack of
    /// more than one kind also gets a "Set by kind of object" switch that shows a slider per kind underneath it (MCM groupControl and
    /// groupCondition).</summary>
    private List<object> OtherPage(List<PackOutput> packs, List<PackOutput> slots, int slotsPerCategory, Dictionary<string, string> kindTitles)
    {
        var content = new List<object>
        {
            new { type = "text", text = "Each slider is the percent (0 to 100) of the objects a pack replaces that show its art; the rest show the game's own. Changes take effect when you apply them.", html = false },
        };
        for (var s = 0; s < slots.Count; s++)
        {
            var pack = slots[s];
            var kinds = pack.Images.Select(i => i.Category).Concat(pack.Movies.Select(m => m.Category)).Where(IsKind).Distinct().OrderBy(k => kindTitles[k], StringComparer.OrdinalIgnoreCase).ToList();
            content.Add(new
            {
                id = $"iSlot{s + 1}:GShuf_Other",
                type = "slider",
                text = pack.Name,
                help = $"Percent of this pack's {Reach(pack)} that {Shows(pack)} its art.",
                valueOptions = new { min = 0, max = 100, step = 1, sourceType = "ModSettingInt" },
            });
            if (kinds.Count < 2) continue;
            content.Add(new
            {
                id = $"bKinds{s + 1}:GShuf_Other",
                type = "switcher",
                text = "Set by kind of object",
                help = "When on, the sliders below set this pack's percent for each kind of object instead of its own slider above.",
                groupControl = s + 1,
                valueOptions = new { sourceType = "ModSettingBool" },
            });
            foreach (var kind in kinds)
            {
                var slot = Eligible(packs, kind).Take(slotsPerCategory).ToList().IndexOf(pack) + 1;
                if (slot < 1) continue;
                content.Add(new
                {
                    id = $"{SettingKey($"iSlot{slot}", kind)}:GShuf_Other",
                    type = "slider",
                    text = kindTitles[kind],
                    help = $"Percent of this pack's {Report.Count(pack.CountIn(kind), "object", "objects")} of this kind that {(pack.CountIn(kind) == 1 ? "shows" : "show")} its art, while the switch is on.",
                    groupCondition = s + 1,
                    valueOptions = new { min = 0, max = 100, step = 1, sourceType = "ModSettingInt" },
                });
            }
        }
        return content;
    }

    public void WriteReport()
    {
        WriteFile(Path.Combine("GShuf", "convert-report.txt"), Encoding.UTF8.GetBytes(_report.ToString()));
    }

    private static string ShortHash(string text)
    {
        return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToLowerInvariant())))[..8].ToLowerInvariant();
    }

    private BaseMaterialFile? TemplateMaterial(FoundImage image, Source source)
    {
        if (image.InlineKey != null)
        {
            var shader = _game.InlineShader(image.InlineKey);
            if (shader != null) return shader.Build();
            _report.Warn($"the model shader {image.InlineKey} could not be read again; skipped");
            return null;
        }
        byte[]? bytes = null;
        if (image.MaterialSource != null && source.Files.TryGetValue(image.MaterialSource, out var own)) bytes = own.Read();
        else if (image.GameMaterial != null) bytes = _game.ReadMaterial(image.GameMaterial);
        else if (image.Slot != null) bytes = _game.ReadMaterial(image.Slot);
        var template = _catalog.Slots.FirstOrDefault(s => s.Category == image.Category && !s.Blank);
        if (bytes == null && template != null) bytes = _game.ReadMaterial(template.Material);
        if (bytes == null)
        {
            _report.Warn($"no template material available for {image.Slot ?? image.Diffuse}; skipped");
            return null;
        }
        try
        {
            return MaterialFiles.Read(bytes);
        }
        catch (Exception e)
        {
            _report.Warn($"cannot read the template material for {image.Slot ?? image.Diffuse}: {e.Message}; skipped");
            return null;
        }
    }

    /// <summary>Points a template material's diffuse (or effect base) and normal textures at a picture.</summary>
    private static void SetPicture(BaseMaterialFile material, string diffuse, string? normal)
    {
        switch (material)
        {
            case BGSM m:
                m.DiffuseTexture = diffuse.Replace('\\', '/');
                if (normal != null) m.NormalTexture = normal.Replace('\\', '/');
                break;
            case BGEM e:
                e.BaseTexture = diffuse.Replace('\\', '/');
                if (normal != null) e.NormalTexture = normal.Replace('\\', '/');
                break;
        }
    }

    private void WriteMaterial(string relative, BaseMaterialFile material)
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
    private readonly List<Ba2Archive> _meshes = [];
    private readonly List<Ba2Archive> _audio = [];
    private readonly Report _report;
    private Dictionary<string, InlineUse>? _inline;
    private Dictionary<string, List<string>>? _inlineByTexture;
    private HashSet<string> _skyTextures = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>? _byName;

    /// <summary>What the model index cache holds: the inline shaders and the sky shader textures of the game's models.</summary>
    private sealed record ModelIndex(List<InlineUse> Shaders, List<string> Sky);

    /// <summary>A vanilla model that draws with an inline shader keyed by <c>Key</c>, and every texture that shader uses.</summary>
    public sealed record InlineUse(string Key, string Mesh, bool Effect, List<string> Textures);
    private Dictionary<string, List<string>>? _materialsByTexture;
    private readonly Dictionary<string, List<string>> _texturesByMaterial = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _diffuseByMaterial = new(StringComparer.OrdinalIgnoreCase);

    public GameFiles(string gameFolder, Report report)
    {
        _report = report;
        var data = Path.Combine(gameFolder, "Data");
        _dataFolder = data;
        var defaults = Path.Combine(gameFolder, "Fallout4_Default.ini");
        var language = File.Exists(defaults) ? File.ReadAllLines(defaults).Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("sLanguage=", StringComparison.OrdinalIgnoreCase)) : null;
        Language = language != null && language.Length > 10 ? language[10..].Trim().ToLowerInvariant() : "en";
        var interfaceArchive = Path.Combine(data, "Fallout4 - Interface.ba2");
        if (File.Exists(interfaceArchive)) _interface.Add(new Ba2Archive(interfaceArchive));
        // Lists the archives from the last one the game registers to the first.
        var precedence = ArchivePrecedence(gameFolder);
        foreach (var path in Directory.EnumerateFiles(data, "*.ba2").OrderByDescending(precedence).ThenByDescending(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(path);
            if (Rank(path) > 2) continue;
            try
            {
                Ba2Archive? archive = null;
                if (name.Contains(" - Materials", StringComparison.OrdinalIgnoreCase) || name.EndsWith(" - Main.ba2", StringComparison.OrdinalIgnoreCase)) _materials.Add(archive = new Ba2Archive(path));
                else if (name.Contains(" - Textures", StringComparison.OrdinalIgnoreCase) || name.Contains(" - Misc", StringComparison.OrdinalIgnoreCase)) _textures.Add(new Ba2Archive(path));
                if (name.Contains(" - Meshes", StringComparison.OrdinalIgnoreCase) || name.EndsWith(" - Main.ba2", StringComparison.OrdinalIgnoreCase)) _meshes.Add(archive ??= new Ba2Archive(path));
                if (name.Contains(" - Sounds", StringComparison.OrdinalIgnoreCase) || name.Contains(" - Voices", StringComparison.OrdinalIgnoreCase) || name.Contains(" - Startup", StringComparison.OrdinalIgnoreCase)) _audio.Add(new Ba2Archive(path));
                else if (name.EndsWith(" - Main.ba2", StringComparison.OrdinalIgnoreCase)) _audio.Add(archive!);
            }
            catch (Exception e)
            {
                report.Warn($"cannot read {name}: {e.Message}");
            }
        }
        report.Info($"game archives: {_materials.Count} material, {_textures.Count} texture and {_meshes.Count} model archives in {data}");
    }

    private Dictionary<string, string>? _folderCase;

    /// <summary>A game folder (lowercase, relative to Materials, Meshes or Textures) as the game's archives spell it most often, or
    /// the folder itself when no archive has it.</summary>
    public string FolderCase(string folder)
    {
        if (_folderCase == null)
        {
            var counts = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            foreach (var archive in _materials.Concat(_meshes).Concat(_textures).Concat(_audio).Distinct())
            {
                foreach (var path in archive.Paths)
                {
                    var parts = path.Replace('/', '\\').Split('\\');
                    var start = parts.Length > 1 && parts[0].ToLowerInvariant() is "materials" or "meshes" or "textures" ? 1 : 0;
                    for (var depth = 1; depth <= 3 && start + depth < parts.Length; depth++)
                    {
                        var spelled = string.Join('\\', parts, start, depth);
                        var key = spelled.ToLowerInvariant();
                        if (!counts.TryGetValue(key, out var forms)) counts[key] = forms = new(StringComparer.Ordinal);
                        forms[spelled] = forms.GetValueOrDefault(spelled) + 1;
                    }
                }
            }
            _folderCase = counts.ToDictionary(kv => kv.Key, kv => kv.Value.MaxBy(f => f.Value).Key, StringComparer.Ordinal);
        }
        return _folderCase.TryGetValue(folder, out var found) ? found : folder;
    }

    /// <summary>Keys (see <see cref="InlineUse"/>) of every vanilla inline shader that uses the texture in any slot.</summary>
    public IReadOnlyList<string> InlineKeysUsing(string texture)
    {
        EnsureInlineIndex();
        return _inlineByTexture!.TryGetValue(texture, out var keys) ? keys : [];
    }

    public InlineUse? Inline(string key)
    {
        EnsureInlineIndex();
        return _inline!.TryGetValue(key, out var use) ? use : null;
    }

    /// <summary>The vanilla inline shader with this key, read again from its model, or null.</summary>
    public InlineShader? InlineShader(string key)
    {
        var use = Inline(key);
        var archive = use == null ? null : _meshes.FirstOrDefault(a => a.Contains(use.Mesh));
        return archive == null ? null : NifFile.InlineShaders(archive.Read(use!.Mesh)).FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    private void EnsureInlineIndex()
    {
        if (_inline != null) return;
        // The index depends only on the game's model archives, so it is kept between runs under the archives' sizes and dates.
        var identity = string.Join("|", _meshes.Select(a => { var f = new FileInfo(a.Path); return $"{f.Name}:{f.Length}:{f.LastWriteTimeUtc.Ticks}"; }));
        var cacheFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GalleryShuffle");
        var cache = Path.Combine(cacheFolder, $"model-index-v2-{Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(identity)))[..12]}.json");
        if (File.Exists(cache))
        {
            try
            {
                var index = JsonSerializer.Deserialize<ModelIndex>(File.ReadAllBytes(cache)) ?? new ModelIndex([], []);
                _inline = index.Shaders.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);
                _skyTextures = new HashSet<string>(index.Sky, StringComparer.OrdinalIgnoreCase);
                BuildInlineReverse();
                _report.Info($"inline shader index: {Report.Count(_inline.Count, "shader", "shaders")} from the cache");
                return;
            }
            catch (Exception e)
            {
                _report.Info($"inline shader cache unreadable ({e.Message}); rebuilding it");
            }
        }
        _report.Info("indexing the game's models for shaders without a material file (first run only)");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _inline = new Dictionary<string, InlineUse>(StringComparer.OrdinalIgnoreCase);
        var models = 0;
        foreach (var archive in _meshes)
        {
            archive.ForEachGeneral(p => p.EndsWith(".nif", StringComparison.OrdinalIgnoreCase), (path, bytes) =>
            {
                models++;
                var precombined = path.Contains("\\precombined\\", StringComparison.OrdinalIgnoreCase);
                foreach (var shader in NifFile.InlineShaders(bytes))
                {
                    // A standalone model is kept over a precombined copy of the same shader; its settings are the source ones.
                    if (_inline.TryGetValue(shader.Key, out var known) && (precombined || !known.Mesh.Contains("\\precombined\\", StringComparison.OrdinalIgnoreCase))) continue;
                    _inline[shader.Key] = new InlineUse(shader.Key, path, shader.Effect, shader.Textures);
                }
                _skyTextures.UnionWith(NifFile.SkyTextures(bytes));
            });
        }
        BuildInlineReverse();
        try
        {
            Directory.CreateDirectory(cacheFolder);
            File.WriteAllBytes(cache, JsonSerializer.SerializeToUtf8Bytes(new ModelIndex(_inline.Values.ToList(), _skyTextures.Order(StringComparer.Ordinal).ToList())));
        }
        catch (Exception e)
        {
            _report.Info($"inline shader cache not written ({e.Message})");
        }
        _report.Info($"inline shader index: {Report.Count(_inline.Count, "shader", "shaders")} without a material file in {Report.Count(models, "vanilla model", "vanilla models")} ({clock.Elapsed.TotalSeconds:0} s)");
    }

    /// <summary>True when the game loads the texture (relative to Textures) by its name rather than through a material file:
    /// a path its executable names, a texture field of a record in its own plugins (texture sets, weather, climate, effect
    /// shaders) or the texture of a sky shader in its models.</summary>
    public bool LoadsByName(string texture)
    {
        if (_byName == null)
        {
            EnsureInlineIndex();
            var names = new HashSet<string>(_skyTextures, StringComparer.OrdinalIgnoreCase);
            var exe = Path.Combine(Path.GetDirectoryName(_dataFolder) ?? _dataFolder, "Fallout4.exe");
            if (File.Exists(exe)) names.UnionWith(ExecutableTextures(File.ReadAllBytes(exe)));
            foreach (var master in GameMasters())
            {
                try
                {
                    names.UnionWith(PluginFile.TexturePaths(File.ReadAllBytes(Path.Combine(_dataFolder, master))));
                }
                catch (Exception e)
                {
                    _report.Warn($"cannot read {master} for the textures it names: {e.Message}");
                }
            }
            _byName = names;
            _report.Info($"textures the game loads by name: {_byName.Count} (executable, game plugins and sky shaders)");
        }
        return _byName.Contains(texture);
    }

    /// <summary>Every texture path (relative to Textures, lowercase) written as text in the executable, without format patterns.</summary>
    private static List<string> ExecutableTextures(byte[] exe)
    {
        var result = new List<string>();
        var suffix = ".dds"u8;
        for (var i = exe.AsSpan().IndexOf(suffix); i >= 0;)
        {
            var end = i + suffix.Length;
            var start = i;
            while (start > 0 && exe[start - 1] >= 0x20 && exe[start - 1] < 0x7F) start--;
            if (end < exe.Length && exe[end] == 0 && end - start > suffix.Length)
            {
                var text = Encoding.ASCII.GetString(exe, start, end - start);
                if (!text.Contains('%') && (text.Contains('\\') || text.Contains('/'))) result.Add(Paths.Normalize(text, "textures"));
            }
            var next = exe.AsSpan(end).IndexOf(suffix);
            i = next < 0 ? -1 : end + next;
        }
        return result;
    }

    private void BuildInlineReverse()
    {
        _inlineByTexture = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var use in _inline!.Values)
        {
            foreach (var texture in use.Textures)
            {
                if (!_inlineByTexture.TryGetValue(texture, out var keys)) _inlineByTexture[texture] = keys = [];
                keys.Add(use.Key);
            }
        }
    }

    private readonly string _dataFolder;
    private readonly List<Ba2Archive> _interface = [];
    private readonly Dictionary<string, Dictionary<uint, string>> _strings = new(StringComparer.OrdinalIgnoreCase);
    private List<string>? _gameMasters;

    /// <summary>The game's text language (sLanguage in Fallout4_Default.ini next to the game), "en" when unset.</summary>
    public string Language { get; }

    /// <summary>A text of one of the game's own plugins from its string tables in the game's language, or null.</summary>
    public string? GameString(string plugin, uint id)
    {
        if (!_strings.TryGetValue(plugin, out var table))
        {
            table = [];
            var stem = Path.GetFileNameWithoutExtension(plugin);
            foreach (var (ext, prefixed) in new[] { ("STRINGS", false), ("DLSTRINGS", true), ("ILSTRINGS", true) })
            {
                var path = $"strings\\{stem}_{Language}.{ext}";
                var bytes = Read(_interface, path) ?? Read(_materials, path) ?? Read(_meshes, path);
                if (bytes == null) continue;
                foreach (var (key, text) in PluginFile.ReadStrings(bytes, prefixed)) table.TryAdd(key, text);
            }
            _strings[plugin] = table;
        }
        return table.TryGetValue(id, out var value) ? value : null;
    }
    private readonly HashSet<string> _indexedTypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _localized = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the game plugin keeps its texts in string tables (read by <see cref="IndexGameRecords"/>).</summary>
    public bool IsLocalized(string plugin) => _localized.Contains(plugin);
    private readonly Dictionary<FormKey, PluginFile.ModelRecord> _gameModels = [];
    private readonly Dictionary<FormKey, List<PluginFile.Substitution>> _gameSwaps = [];

    /// <summary>The game's own plugins in the order the engine loads them: Fallout4.esm, the DLC, then the Creation Club
    /// plugins in Fallout4.ccc order (the implicitly active plugins as LOOT and the mod managers list them).</summary>
    public IReadOnlyList<string> GameMasters()
    {
        if (_gameMasters != null) return _gameMasters;
        string[] fixedOrder = ["Fallout4.esm", "DLCRobot.esm", "DLCworkshop01.esm", "DLCCoast.esm", "DLCworkshop02.esm", "DLCworkshop03.esm", "DLCNukaWorld.esm", "DLCUltraHighResolution.esm"];
        var list = fixedOrder.Where(f => File.Exists(Path.Combine(_dataFolder, f))).ToList();
        var ccc = Path.Combine(Path.GetDirectoryName(_dataFolder) ?? _dataFolder, "Fallout4.ccc");
        var creations = File.Exists(ccc)
            ? File.ReadAllLines(ccc).Select(l => l.Trim()).Where(l => l.Length > 0)
            : Directory.EnumerateFiles(_dataFolder, "cc*.es?").Select(f => Path.GetFileName(f)).Order(StringComparer.OrdinalIgnoreCase);
        list.AddRange(creations.Where(f => File.Exists(Path.Combine(_dataFolder, f)) && !list.Contains(f, StringComparer.OrdinalIgnoreCase)));
        _gameMasters = list;
        return list;
    }

    public bool IsGameMaster(string plugin) => GameMasters().Contains(plugin, StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads the model block of every record of the given types, and every material swap, from the game's own
    /// plugins; a later plugin's version of a record wins, as in the game.</summary>
    public void IndexGameRecords(IEnumerable<string> types)
    {
        var wanted = types.Where(_indexedTypes.Add).ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0) return;
        foreach (var master in GameMasters())
        {
            try
            {
                var plugin = PluginFile.Read(master, File.ReadAllBytes(Path.Combine(_dataFolder, master)), wanted.Contains);
                if (plugin.Localized) _localized.Add(master);
                foreach (var record in plugin.Models) _gameModels[record.Key] = record;
                foreach (var (key, substitutions) in plugin.MaterialSwaps) _gameSwaps[key] = substitutions;
                foreach (var (key, textures) in plugin.TextureSets) _gameTextureSets[key] = textures;
                foreach (var (key, shown) in plugin.LoadScreens) _gameLoadScreens[key] = shown;
            }
            catch (Exception e)
            {
                _report.Warn($"cannot read {master}: {e.Message}");
            }
        }
        _report.Info($"game records: {string.Join(", ", wanted.Order())} read from {GameMasters().Count} game plugins");
    }

    /// <summary>The game's own version of a record's model block, or null (call <see cref="IndexGameRecords"/> for its type first).</summary>
    public PluginFile.ModelRecord? GameModelRecord(FormKey key) => _gameModels.TryGetValue(key, out var record) ? record : null;

    private Dictionary<string, (string Model, string Material)>? _frames;

    /// <summary>The game model that shows a material in the world and the material's name in that model: a static whose
    /// material swap puts the material on its model (paintings), else a static whose model names the material (posters, signs).</summary>
    public (string Model, string Material)? FrameOf(string material)
    {
        EnsureFrames();
        return _frames!.TryGetValue(material, out var frame) ? frame : null;
    }

    private void EnsureFrames()
    {
        if (_frames != null) return;
        _frames = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        IndexGameRecords(["STAT", "MSWP"]);
        var statics = _gameModels.Values.Where(r => r.Type == "STAT").OrderBy(r => r.Key.Local).ToList();
        foreach (var record in statics)
        {
            if (record.Swap is not { } swap || !_gameSwaps.TryGetValue(swap, out var substitutions)) continue;
            foreach (var substitution in substitutions)
            {
                if (substitution.Original.Length > 0 && substitution.Replacement.Length > 0) _frames.TryAdd(substitution.Replacement, (record.Model, substitution.Original));
            }
        }
        var materials = StaticModelMaterials(statics.Select(r => r.Model).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        foreach (var record in statics)
        {
            if (!materials.TryGetValue(record.Model, out var list)) continue;
            foreach (var material in list) _frames.TryAdd(material, (record.Model, material));
        }
        _report.Info($"frames: {_frames.Count} materials the game's statics show");
    }

    // The materials each static model names; read once per game version and kept between runs.
    private Dictionary<string, List<string>> StaticModelMaterials(List<string> models)
    {
        var identity = string.Join("|", _meshes.Select(a => { var f = new FileInfo(a.Path); return $"{f.Name}:{f.Length}:{f.LastWriteTimeUtc.Ticks}"; }));
        var cacheFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GalleryShuffle");
        var cache = Path.Combine(cacheFolder, $"static-materials-v1-{Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(identity)))[..12]}.json");
        if (File.Exists(cache))
        {
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllBytes(cache)) is { } cached
                    ? new Dictionary<string, List<string>>(cached, StringComparer.OrdinalIgnoreCase)
                    : [];
            }
            catch (Exception e)
            {
                _report.Info($"static material cache unreadable ({e.Message}); rebuilding it");
            }
        }
        var wanted = new HashSet<string>(models.Select(m => "meshes\\" + m), StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in _meshes)
        {
            archive.ForEachGeneral(p => wanted.Contains(p), (path, bytes) =>
            {
                var model = Paths.Normalize(path, "meshes");
                if (result.ContainsKey(model)) return;
                result[model] = NifFile.References(bytes).Where(r => r.EndsWith(".bgsm", StringComparison.OrdinalIgnoreCase) || r.EndsWith(".bgem", StringComparison.OrdinalIgnoreCase))
                    .Select(r => Paths.Normalize(r, "materials")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            });
        }
        try
        {
            Directory.CreateDirectory(cacheFolder);
            File.WriteAllBytes(cache, JsonSerializer.SerializeToUtf8Bytes(result));
        }
        catch (Exception e)
        {
            _report.Info($"static material cache not written ({e.Message})");
        }
        return result;
    }

    /// <summary>The substitutions of one of the game's own material swaps, or null.</summary>
    public List<PluginFile.Substitution>? GameMaterialSwap(FormKey key) => _gameSwaps.TryGetValue(key, out var list) ? list : null;

    /// <summary>The game's loading screens with the static or static collection each shows (null for another kind of form).</summary>
    public List<(FormKey Screen, PluginFile.ModelRecord? Model)> GameLoadScreens()
    {
        IndexGameRecords(["LSCR", "STAT", "SCOL", "MSWP"]);
        return _gameLoadScreens.OrderBy(kv => kv.Key.File, StringComparer.OrdinalIgnoreCase).ThenBy(kv => kv.Key.Local).Select(kv => (kv.Key, GameModelRecord(kv.Value))).ToList();
    }
    private readonly Dictionary<FormKey, FormKey> _gameLoadScreens = [];

    /// <summary>The game's own texture set record (TX00 to TX07), or null (call <see cref="IndexGameRecords"/> for TXST first).</summary>
    public string[]? GameTextureSet(FormKey key) => _gameTextureSets.TryGetValue(key, out var textures) ? textures : null;
    private readonly Dictionary<FormKey, string[]> _gameTextureSets = [];

    // The position at which the game registers an archive: base archives in Fallout4_Default.ini [Archive] order, then DLC and Creation Club by plugin load order.
    private Func<string, int> ArchivePrecedence(string gameFolder)
    {
        var baseOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var ini = Path.Combine(gameFolder, "Fallout4_Default.ini");
        if (File.Exists(ini))
        {
            var values = File.ReadAllLines(ini).Select(l => l.Split('=', 2)).Where(p => p.Length == 2).GroupBy(p => p[0].Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last()[1], StringComparer.OrdinalIgnoreCase);
            foreach (var key in new[] { "sResourceIndexFileList", "sResourceStartUpArchiveList", "SResourceArchiveList", "SResourceArchiveList2" })
            {
                if (values.TryGetValue(key, out var list))
                {
                    foreach (var name in list.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0)) baseOrder.TryAdd(name, baseOrder.Count);
                }
                // The texture patch archive of 1.11 runtimes, which no ini list names, follows the texture archives it patches.
                if (key == "sResourceIndexFileList") baseOrder.TryAdd("Fallout4 - TexturesPatch.ba2", baseOrder.Count);
            }
        }
        var plugins = GameMasters().Select((p, i) => (Stem: Path.GetFileNameWithoutExtension(p), Index: i)).ToDictionary(t => t.Stem, t => t.Index, StringComparer.OrdinalIgnoreCase);
        return path =>
        {
            var name = Path.GetFileName(path);
            if (Rank(path) == 0) return baseOrder.TryGetValue(name, out var position) ? position : -1;
            var cut = name.IndexOf(" - ", StringComparison.Ordinal);
            return cut > 0 && plugins.TryGetValue(name[..cut], out var index) ? 100000 + index : -1;
        };
    }

    // 0 base game, 1 DLC, 2 Creation Club; anything else is a mod archive and not vanilla.
    private static int Rank(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith("Fallout4 - ", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.StartsWith("DLC", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.StartsWith("cc", StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    /// <summary>True when the game has a sound or music file at the path (relative to Data, any extension): it finds a sound by
    /// folder and name and tries .wav, .fuz and .xwm.</summary>
    public bool HasAudio(string relative)
    {
        var stem = Path.ChangeExtension(relative, null);
        return new[] { ".wav", ".fuz", ".xwm" }.Any(ext => _audio.Any(a => a.Contains(stem + ext)));
    }

    public byte[]? ReadMaterial(string relative) => Read(_materials, "materials\\" + relative);
    public byte[]? ReadTexture(string relative) => Read(_textures, "textures\\" + relative) ?? Read(_interface, "textures\\" + relative);
    public byte[]? ReadMesh(string relative) => Read(_meshes, "meshes\\" + relative);
    public byte[]? ReadInterface(string relative) => Read(_interface, relative) ?? Read(_materials, relative) ?? Read(_meshes, relative) ?? Read(_textures, relative);
    /// <summary>The game's Data folder.</summary>
    public string DataFolder => _dataFolder;

    /// <summary>The game's own sound or music file for a path without extension (relative to Data), in the order the game tries
    /// extensions, or null.</summary>
    public (string Extension, byte[] Data)? ReadAudio(string stem)
    {
        foreach (var extension in new[] { ".wav", ".fuz", ".xwm" })
        {
            if (Read(_audio, stem + extension) is { } data) return (extension, data);
        }
        return null;
    }
    public bool HasMaterial(string relative) => _materials.Any(a => a.Contains("materials\\" + relative));
    public bool HasTexture(string relative) => _textures.Any(a => a.Contains("textures\\" + relative)) || _interface.Any(a => a.Contains("textures\\" + relative));
    public bool HasMesh(string relative) => _meshes.Any(a => a.Contains("meshes\\" + relative));
    /// <summary>True when one of the game's own archives carries the file (path relative to Data).</summary>
    public bool HasGameFile(string relative) => _interface.Any(a => a.Contains(relative)) || _materials.Any(a => a.Contains(relative)) || _meshes.Any(a => a.Contains(relative)) || _textures.Any(a => a.Contains(relative));

    /// <summary>Width and height of a vanilla texture, or null when no archive ReadTexture searches carries it.</summary>
    public (int Width, int Height)? TextureDimensions(string relative)
    {
        var archive = _textures.FirstOrDefault(a => a.Contains("textures\\" + relative)) ?? _interface.FirstOrDefault(a => a.Contains("textures\\" + relative));
        return archive?.Dimensions("textures\\" + relative);
    }

    /// <summary>Every vanilla material (relative to Materials) that uses the texture in any slot.</summary>
    public IReadOnlyList<string> MaterialsUsing(string texture)
    {
        EnsureIndex();
        return _materialsByTexture!.TryGetValue(texture, out var list) ? list : [];
    }

    /// <summary>Every texture (relative to Textures) a vanilla material uses.</summary>
    public IReadOnlyList<string> TexturesOf(string material)
    {
        EnsureIndex();
        return _texturesByMaterial.TryGetValue(material, out var list) ? list : [];
    }

    /// <summary>The diffuse (or effect base) texture of a vanilla material, relative to Textures, or empty.</summary>
    public string DiffuseOf(string material)
    {
        EnsureIndex();
        return _diffuseByMaterial.TryGetValue(material, out var diffuse) ? diffuse : "";
    }

    private void EnsureIndex()
    {
        if (_materialsByTexture != null) return;
        _materialsByTexture = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        int count = 0, unreadable = 0;
        foreach (var archive in _materials)
        {
            foreach (var path in archive.Paths.ToList())
            {
                if (!path.EndsWith(".bgsm", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".bgem", StringComparison.OrdinalIgnoreCase)) continue;
                var material = Paths.Normalize(path, "materials");
                if (_texturesByMaterial.ContainsKey(material)) continue;
                MaterialLib.BaseMaterialFile file;
                try
                {
                    file = MaterialFiles.Read(archive.Read(path));
                }
                catch (Exception)
                {
                    unreadable++;
                    continue;
                }
                var textures = MaterialFiles.Textures(file);
                _texturesByMaterial[material] = textures;
                _diffuseByMaterial[material] = MaterialFiles.Diffuse(file);
                foreach (var texture in textures)
                {
                    if (!_materialsByTexture.TryGetValue(texture, out var users)) _materialsByTexture[texture] = users = [];
                    users.Add(material);
                }
                count++;
            }
        }
        _report.Info($"material index: {Report.Count(count, "vanilla material", "vanilla materials")} using {Report.Count(_materialsByTexture.Count, "texture", "textures")}{(unreadable > 0 ? $", {unreadable} unreadable" : "")}");
    }

    private static byte[]? Read(List<Ba2Archive> archives, string path)
    {
        foreach (var archive in archives)
        {
            if (archive.Contains(path)) return archive.Read(path);
        }
        return null;
    }
}
