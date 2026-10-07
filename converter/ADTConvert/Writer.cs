using System.Text;
using System.Text.Json;
using MaterialLib;

namespace ADTConvert;

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
    public int[]? MapBounds { get; init; } // the Commonwealth world map data (usable width, height, NW cell x, y, SE cell x, y) the world map needs; none means the game's
    public string? SwapForm { get; init; } // the material swap form ("file:local id", empty for none) the object gets with this version
    public string? Card { get; set; }     // the game's loading screen magazine with this cover (relative to Meshes), for a magazine cover

    /// <summary>True for a magazine cover, the one kind of picture the game's own loading screens show (on its turning magazine).
    /// Models ("#", "&amp;"), textures loaded by name ("%") and texture set entries ("!") show only on the object they replace.</summary>
    public bool IsCover => Category == "magazines" && (Slot == null || Slot.Length == 0 || Slot[0] is not ('#' or '&' or '%' or '!'));
}

/// <summary>Writes the "Art Distribution Tool Packs" mod folder: materials, textures, packs.json and the MCM pages.</summary>
public sealed class Writer
{
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
    // Game textures (relative to Textures, lowercase) the packs name under ADT\Game, with the game archive each is read from.
    private readonly SortedDictionary<string, string> _gameTextures = new(StringComparer.Ordinal);
    // Game models (relative to Meshes, lowercase) the packs name under ADT\Game, with the game archive each is read from.
    private readonly SortedDictionary<string, string> _gameModels = new(StringComparer.Ordinal);
    private int _copiedModels;

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

        // The files of a patch joined to its host carry "mod|" in front of their paths; a short hash of it keeps names apart and short.
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
            var relative = $"ADT\\Packs\\{id}\\{unique}{suffix}.dds";
            WriteFile(Path.Combine("Textures", relative), source.Files[sourcePath].Read());
            copied[sourcePath] = relative;
            return relative;
        }

        static bool IsObjectOrModel(FoundImage image) => image.Slot != null && image.Slot.Length > 0 && image.Slot[0] is '&' or '#' or '%' or '!';
        // Textures the game loads by name: the pack's copy, which the plugin loads in place of the game's.
        foreach (var image in images.Where(i => i.Slot != null && i.Slot.StartsWith('%')).OrderBy(i => i.Slot, StringComparer.OrdinalIgnoreCase))
        {
            pack.Images.Add(new ImageOutput { Category = image.Category, Material = Copy(image.Swaps[image.Slot![1..]]), Slot = image.Slot, Width = image.Width, Height = image.Height, MapBounds = image.MapBounds });
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
            var materialRel = $"ADT\\Packs\\{id}\\{uniqueMaterial}{(material is BGEM ? ".bgem" : ".bgsm")}";
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
                        written = $"ADT\\Packs\\{id}\\{unique}{(file is BGEM ? ".bgem" : ".bgsm")}";
                        WriteMaterial(Path.Combine("Materials", written), file);
                        copiedMaterials[materialSource] = written;
                    }
                    return "Materials\\" + written;
                }
                if (packMaterials.TryGetValue(material, out var replacement)) return "Materials\\" + replacement;
                return substituted ? "Materials\\" + material : null;
            }

            // Objects that share a model and a baked swap share one written file; a joined mod's model sits in a folder named by a hash of its prefix.
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
                    modelRel = $"ADT\\Packs\\{id}\\{relative}";
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
            pack.Video = $"ADT\\Packs\\{id}\\MainMenuLoop.bk2";
            WriteFile(Path.Combine("Video", pack.Video), video.Read());
        }
        // Interface files keep their layout under the pack folder; the plugin opens them in place of the game's.
        var menuArt = menuVideo != null || menuMusic != null;
        if (!menuArt && interfaceFiles is { Count: > 0 })
        {
            _report.Info($"{source.Name}: {Report.Count(interfaceFiles.Count, "main menu interface file", "main menu interface files")} left to the mod; it ships no main menu video or music, so it is no MENU ART choice");
        }
        foreach (var file in (menuArt ? interfaceFiles : null) ?? [])
        {
            var copy = $"Interface\\ADT\\Packs\\{id}\\{file.GamePath["interface\\".Length..]}";
            WriteFile(copy, source.Files[file.Source].Read());
            pack.Movies.Add((file.GamePath, copy, file.Category));
        }
        // The game names its menu theme by a fixed path the plugin rewrites in place, so the pack's copy gets a name that fits it.
        if (menuMusic != null && source.Files.TryGetValue(menuMusic, out var music))
        {
            pack.Music = $"ADT\\{ShortHash(id)}{Path.GetExtension(menuMusic).ToLowerInvariant()}";
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
        var relative = $"ADT\\Packs\\vanilla\\inline\\{stem}-{ShortHash(key)}{(material is BGEM ? ".bgem" : ".bgsm")}";
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
        WriteFile(Path.Combine("ADT", "vanilla-slots.json"), Encoding.UTF8.GetBytes(rows.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        _report.Info($"slot list: {Report.Count(_catalog.Slots.Count, "catalogued art slot", "catalogued art slots")} plus {Report.Count(_extraSlots.Count, "more vanilla material, model or object", "more vanilla materials, models and objects")} the packs replace");
    }

    /// <summary>The vanilla pack: a material for every catalogued vanilla picture, and the game's own version of everything else
    /// a pack replaces that loads a file a converted mod ships at the game's path. Its textures stay in the game's archives.</summary>
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
            var materialRel = $"ADT\\Packs\\vanilla\\{Path.ChangeExtension(slot.Material, material is BGEM ? ".bgem" : ".bgsm")}";
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
                    if (_game.ReadTexture(target) is { } texture) own = new ImageOutput { Category = category, Material = VanillaTexture(target, texture), Slot = slot, MapBounds = category == "pipboymap" ? _game.GameWorldMap() : null };
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

        if (packs.Any(p => p.Video != null))
        {
            // Copies the game's loose menu video unless it is a converted mod's copy; a mod Vortex deployed over it leaves the game's own as MainMenuLoop.bk2.vortex_backup.
            var gameVideo = Path.Combine(_game.DataFolder, "Video", "MainMenuLoop.bk2");
            var copies = packs.Where(p => p.Video != null).Select(p => File.ReadAllBytes(Path.Combine(_out, "Video", p.Video!))).ToList();
            var video = new[] { gameVideo, gameVideo + ".vortex_backup" }.Where(File.Exists).Select(File.ReadAllBytes).FirstOrDefault(bytes => !copies.Any(copy => copy.AsSpan().SequenceEqual(bytes)));
            if (video != null)
            {
                pack.Video = "ADT\\Packs\\vanilla\\MainMenuLoop.bk2";
                WriteFile(Path.Combine("Video", pack.Video), video);
            }
            else
            {
                _report.Warn("vanilla: the game's own main menu video could not be read apart from a converted mod's copy; disable every main menu mod in your mod manager, keep it ticked here and convert again, so Vanilla in MENU ART plays the game's own video");
            }
        }
        if (packs.Any(p => p.Music != null) && _game.ReadAudio("music\\special\\mus_maintheme") is { } theme)
        {
            pack.Music = $"ADT\\{ShortHash("vanilla")}{theme.Extension}";
            WriteFile(Path.Combine("Music", pack.Music), theme.Data);
        }
        _report.Info($"pack 'Vanilla': {Report.Count(pictures, "picture", "pictures")} of the game's own, and the game's own version of {Report.Count(pack.Images.Count - pictures, "other replaced item", "other replaced items")}{(pack.Movies.Count > 0 ? $", {Report.Count(pack.Movies.Count, "interface file", "interface files")}" : "")}{(pack.Video != null ? ", main menu video" : "")}{(pack.Music != null ? ", main menu theme" : "")}");
        return pack;
    }

    // The path (relative to Textures) that shows the game's own texture past any mod's file: ADT\Game\<its path>, which the plugin
    // serves from the game's texture archive, or a copy in the vanilla pack for a texture that is in no texture archive.
    private string VanillaTexture(string texture, byte[] data)
    {
        if (_game.TextureArchive(texture) is { } archive)
        {
            _gameTextures[texture.ToLowerInvariant().Replace('/', '\\')] = archive;
            return $"ADT\\Game\\{texture}";
        }
        var relative = $"ADT\\Packs\\vanilla\\{texture}";
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
            if (texture.Length == 0 || texture.StartsWith("adt\\", StringComparison.OrdinalIgnoreCase) || !_overridden.Contains("textures\\" + texture)) continue;
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
                    result = $"ADT\\Packs\\vanilla\\{Path.ChangeExtension(material, file is BGEM ? ".bgem" : ".bgsm")}";
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

    // The path (relative to Meshes) that loads the game's own model: its own path when no converted mod ships it; otherwise
    // ADT\Game\<its path>, an empty file the plugin answers for with the model from the game's archive, or a copy in the vanilla
    // pack when no archive has it. Null when the game has no such model.
    private string? VanillaModel(string model)
    {
        if (!_overridden.Contains("meshes\\" + model)) return _game.HasMesh(model) ? model : null;
        if (_game.MeshArchive(model) is { } archive)
        {
            // Writes an empty file at the model's ADT\Game path once.
            if (_gameModels.TryAdd(model.ToLowerInvariant().Replace('/', '\\'), archive)) WriteFile(Path.Combine("Meshes", "ADT", "Game", model), []);
            return $"ADT\\Game\\{model}";
        }
        if (_game.ReadMesh(model) is not { } data) return null;
        var relative = $"ADT\\Packs\\vanilla\\{model}";
        WriteFile(Path.Combine("Meshes", relative), data);
        _copiedModels++;
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

    /// <summary>Gives every pack magazine cover a loading screen card: the game's own loading screen magazine with that cover
    /// and the back that goes with it, as the game's magazine loading screens show covers.</summary>
    public void WriteLoadCards(List<PackOutput> packs)
    {
        var total = 0;
        var missing = 0;
        var withBack = 0;
        var twins = 0;
        var unpaired = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in packs.Where(p => p.Id != "vanilla"))
        {
            var covers = pack.Images.Where(i => i.IsCover).Select(i => ShownBy(i.Slot)).OfType<string>().Select(m => Paths.Normalize(m, "materials")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var image in pack.Images.Where(i => i.IsCover))
            {
                // The loading screen magazine is the high resolution model, so a cover the pack has in both resolutions gets one card, from the high resolution one.
                if (ShownBy(image.Slot) is { } shown && HighResolutionTwin(Paths.Normalize(shown, "materials")) is { } twin && covers.Contains(twin))
                {
                    twins++;
                    continue;
                }
                total++;
                var back = CoverBack(pack, packs, image, unpaired);
                var card = MagazineCard(image, back.Material);
                if (card == null)
                {
                    missing++;
                    continue;
                }
                var relative = $"ADT\\LoadCards\\{pack.Id}\\{Path.GetFileNameWithoutExtension(image.Material)}.nif";
                WriteFile(Path.Combine("Meshes", relative), card);
                if (back.Own) withBack++;
                image.Card = relative;
            }
        }
        if (total > 0) _report.Info($"loading screens: {Report.Count(total - missing, "magazine cover", "magazine covers")} got a card on the game's loading screen magazine{(missing > 0 ? $"; {Report.Count(missing, "cover has", "covers have")} none, because the game's magazine model could not be read" : "")}{(withBack > 0 ? $"; {Report.Count(withBack, "card carries", "cards carry")} the pack's own back cover" : "")}{(twins > 0 ? $"; {Report.Count(twins, "low resolution cover", "low resolution covers")} left to the card of the same cover in high resolution" : "")}");
        if (unpaired.Count > 0) _report.Info($"loading screens: cards show the game's own back where the pack has no version of the back that goes with the cover: {string.Join(", ", unpaired)}");
        if (packs.FirstOrDefault(p => p.Id == "vanilla") is { } vanilla) WriteVanillaScreenCards(vanilla, packs);
    }

    // Gives the vanilla pack a card of every loading screen model with a file a converted mod ships at the game's path or a pack model.
    private void WriteVanillaScreenCards(PackOutput vanilla, List<PackOutput> packs)
    {
        var remodeled = packs.Where(p => p.Id != "vanilla").SelectMany(p => p.Images).Where(i => i.Slot is { } s && s.StartsWith('&') && i.Material.Length > 0).Select(i => i.Slot).ToHashSet(StringComparer.OrdinalIgnoreCase);
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
        var relative = $"ADT\\LoadCards\\vanilla\\{Path.ChangeExtension(record.Model, null)}{(record.Swap is { } s2 ? "-" + ShortHash(s2.Text) : "")}.nif";
        WriteFile(Path.Combine("Meshes", relative), card);
        return relative;
    }

    // The game's loading screen magazine with the picture's material on its cover and the back that goes with that cover
    // (relative to Materials) on its back.
    private byte[]? MagazineCard(ImageOutput image, string? back)
    {
        static bool Is(string reference, string material) => Paths.Normalize(reference, "materials").Equals(material, StringComparison.OrdinalIgnoreCase);
        var magazine = _game.ReadMesh(MagazineCardModel);
        return magazine == null ? null : NifFile.Rewrite(magazine, reference =>
            Is(reference, MagazineCardMaterial) ? "Materials\\" + image.Material
            : back != null && Is(reference, MagazineCardBackMaterial) ? "Materials\\" + back
            : null);
    }

    // The material a slot shows: a swap entry's slot ("=swap form|material") shows what the game's swap puts there.
    private string? ShownBy(string? slot) => slot is { Length: > 1 } scoped && scoped[0] == '=' ? SwapShows(scoped[1..]) : slot;

    private Dictionary<string, List<string>>? _coverBacks;

    // The back cover that goes with a magazine cover, as the world pairs them: a game material swap that puts the cover on a
    // magazine also names the back beside it. The pack's own version of that back (Own), else the game's: the vanilla pack's
    // copy, or the game's file. Only backs of the loading screen magazine's own kind (the high resolution set) fit its back.
    private (string? Material, bool Own) CoverBack(PackOutput pack, List<PackOutput> packs, ImageOutput cover, SortedSet<string> unpaired)
    {
        if (_coverBacks == null)
        {
            _coverBacks = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var swap in _game.GameMaterialSwaps())
            {
                var shown = swap.Where(s => s.Replacement.Length > 0).Select(s => Paths.Normalize(s.Replacement, "materials")).ToList();
                var backs = shown.Where(m => IsMagazineBack(m) && m.StartsWith(@"props\comicsandmagazineshighres\", StringComparison.OrdinalIgnoreCase)).ToList();
                if (backs.Count == 0) continue;
                foreach (var front in shown.Where(m => !IsMagazineBack(m)))
                {
                    if (!_coverBacks.TryGetValue(front, out var list)) _coverBacks[front] = list = [];
                    foreach (var b in backs.Where(b => !list.Contains(b, StringComparer.OrdinalIgnoreCase))) list.Add(b);
                }
            }
        }
        if (ShownBy(cover.Slot) is not { } material) return default;
        var name = Path.GetFileNameWithoutExtension(material);
        if (!_coverBacks.TryGetValue(Paths.Normalize(material, "materials"), out var paired))
        {
            unpaired.Add($"{name} (the game's records name no back for it)");
            return default;
        }
        ImageOutput? Version(PackOutput? of) => paired.Select(b => of?.Images.FirstOrDefault(i => i.Category == "magazinebacks" && ShownBy(i.Slot) is { } shown && Paths.Normalize(shown, "materials").Equals(b, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(i => i != null);
        if (Version(pack) is { } own) return (own.Material, true);
        unpaired.Add($"{name} ({string.Join(" or ", paired.Select(Path.GetFileNameWithoutExtension))})");
        return (Version(packs.FirstOrDefault(p => p.Id == "vanilla"))?.Material ?? paired[0], false);
    }

    // The high resolution version of a low resolution magazine cover material: the same file in the high resolution folder, or
    // the same name without its "low" ending; null for a material that is not a low resolution cover.
    private static string? HighResolutionTwin(string material)
    {
        const string low = @"props\comicsandmagazines\";
        if (material.StartsWith(low, StringComparison.OrdinalIgnoreCase)) return @"props\comicsandmagazineshighres\" + material[low.Length..];
        var stem = Path.ChangeExtension(material, null);
        return stem.EndsWith("low", StringComparison.OrdinalIgnoreCase) ? stem[..^3] + Path.GetExtension(material) : null;
    }

    // The share pages in menu order, their MCM section names and their page titles; the ADT_ prefix keeps Papyrus from
    // recasing the section names (its strings share one case-insensitive cache).
    private static readonly string[] Categories = ["paintings", "posters", "signage", "magazines", "quest", "other", "pipboyscreen", "pipboyskin", "pipboymap", "loadingscreens"];
    private static readonly string[] Titles = ["ADT_Paintings", "ADT_Posters", "ADT_Signage", "ADT_Magazines", "ADT_Quest", "ADT_Other", "ADT_PipBoyScreen", "ADT_PipBoySkin", "ADT_PipBoyMap", "ADT_LoadingScreens"];
    private static readonly string[] PageTitles = ["Paintings", "Posters", "Signage", "Magazines", "Quest", "Other replacers", "Pip-Boy screen", "Pip-Boy skin", "World map", "Loading screens"];

    /// <summary>Locations with game files of their own: the folder their art sits in, the plugin their records come from and the page heading.</summary>
    private static readonly (string Folder, string Plugin, string Name)[] Locations = [("dlc03", "dlccoast.esm", "Far Harbor"), ("dlc04", "dlcnukaworld.esm", "Nuka-World")];

    /// <summary>The pages a pack's one slider can sit on, in menu order.</summary>
    private static readonly string[] ArtPages = ["paintings", "posters", "signage", "magazines", "quest", "other"];

    /// <summary>What a page's objects are called in a help line.</summary>
    private static readonly Dictionary<string, (string One, string Many)> Nouns = new(StringComparer.Ordinal)
    {
        ["paintings"] = ("painting", "paintings"), ["posters"] = ("poster", "posters"), ["signage"] = ("sign", "signs"),
        ["magazines"] = ("magazine", "magazines"), ["quest"] = ("quest picture", "quest pictures"), ["other"] = ("prop", "props"),
    };

    /// <summary>How much of a pack's art a page holds.</summary>
    private static int OnPage(PackOutput pack, string page) => pack.CountIn(page);

    /// <summary>The page a pack's one slider sits on: the page with the most of its art, the first in menu order on a tie; "" for a
    /// pack with art on none. packs.json carries it, so the plugin reads the page and never works it out.</summary>
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

    /// <summary>The packs a page or list shows, in slot order: a page lists the packs whose one control sits on it; loading screens
    /// every pack with magazine covers; the Pip-Boy lists every pack with something for them.</summary>
    private static List<PackOutput> Eligible(List<PackOutput> packs, string category) =>
        packs.Where(p => p.Id != "vanilla" && (ArtPages.Contains(category)
            ? MainPage(p) == category
            : p.Covers(category) || (category == "loadingscreens" && p.Images.Any(i => i.IsCover)))).ToList();

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

    /// <summary>The location each pack of a picture page is tied to ("" for none): a pack is tied to a location when everything it
    /// replaces on the page is that location's.</summary>
    private List<string> PageLocations(List<PackOutput> slots, string category)
    {
        var keys = slots.Select(p => p.Images.Where(i => i.Category == category).SelectMany(ObjectKeys)
            .Concat(p.Movies.Where(m => m.Category == category).Select(m => "movie:" + m.Path.ToLowerInvariant().Replace('/', '\\'))).ToHashSet()).ToList();
        return keys.Select(k => k.Select(LocationOf).Distinct().ToList() is [var only] ? only : "").ToList();
    }

    /// <summary>A category's section name in packs.json and in the MCM settings.</summary>
    private static string SectionTitle(string category) => Titles[Array.IndexOf(Categories, category)];

    /// <summary>Names how the MCM settings apply (one slider per pack on the page with most of its art, setting all of its art; one on/off
    /// switch per pack on Other replacers; no Vanilla rows; keys named after the pack, iPack_ and bPack_); part of the signature, so a
    /// conversion under another model resets the shares.</summary>
    private const string SettingsModel = "settings-model-6";

    /// <summary>The pack signature the MCM script compares with the stored one, resetting every share when they differ: each pack with
    /// its item count, and each share category's setting keys with the packs in slot order, so the signature changes exactly when
    /// a setting would name a different pack.</summary>
    private string Signature(List<PackOutput> packs, int slotsPerCategory)
    {
        var text = new StringBuilder(SettingsModel);
        foreach (var pack in packs) text.Append($"|{pack.Id}:{pack.Images.Count}");
        foreach (var category in Categories)
        {
            var slots = Eligible(packs, category).Take(slotsPerCategory).ToList();
            text.Append($"|{SectionTitle(category)}={string.Join(",", slots.Select(p => p.Id))}");
        }
        return ShortHash(text.ToString());
    }

    public void WritePacksJson(List<PackOutput> packs, List<Scanner.Restore> restores, int slotsPerCategory, List<ConvertedMod> mods)
    {
        // Every share category in menu order with the packs its slots name, so the plugin reads the slot order and never works it out.
        var categories = Categories.Select(c => new { section = SectionTitle(c), packs = Eligible(packs, c).Take(slotsPerCategory).Select(p => p.Id).ToList() }).ToList();
        _report.Info($"game art: {Report.Count(_gameTextures.Count, "game texture", "game textures")} and {Report.Count(_gameModels.Count, "game model", "game models")} left in the game's archives for the plugin to read; {Report.Count(_vanillaTextures.Count, "texture", "textures")} and {Report.Count(_copiedModels, "model", "models")} outside those archives copied");
        var doc = new
        {
            version = 1,
            generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            signature = Signature(packs, slotsPerCategory),
            mods = mods.Select(m => new { name = m.Name, folder = m.Folder, contains = m.Contains, group = m.Group }),
            categories,
            gameTextures = _gameTextures,
            gameModels = _gameModels,
            restores = restores.Distinct().Select(r => new { kind = r.Kind, form = r.Form, from = r.From, mod = r.Mod, game = r.Game, index = r.Index, gameColor = r.GameColor, modColor = r.ModColor }),
            packs = packs.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                page = MainPage(p) is { Length: > 0 } page ? page : null,
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
                    if (i.MapBounds != null) row["mapBounds"] = i.MapBounds;
                    if (i.Card != null) row["card"] = i.Card;
                    return row;
                }),
            }),
        };
        WriteFile(Path.Combine("ADT", "packs.json"), JsonSerializer.SerializeToUtf8Bytes(doc, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Generates the MCM pages: one per art category, listing the packs whose one slider sits there (the page with most of
    /// the pack's art), packs tied to one location under that location's heading; the Other replacers page (see OtherPage); and one
    /// Pip-Boy page with a list for the screen background and one for the skin, and a World map page with a list for the world
    /// map (each Vanilla or a pack), stored as iPick.</summary>
    public void WriteMcm(List<PackOutput> packs, int slotsPerCategory)
    {
        var pages = new List<object>();
        var pipboyContent = new List<object>
        {
            new { type = "text", text = "Pick a pack, or Vanilla.", html = false },
        };
        var mapContent = new List<object>
        {
            new { type = "text", text = "Pick a pack, or Vanilla.", html = false },
        };
        foreach (var pack in packs.Where(p => p.Id != "vanilla" && MainPage(p).Length > 0 && ArtPages.Count(page => OnPage(p, page) > 0) > 1))
        {
            _report.Info($"{pack.Name}: its {(MainPage(pack) == "other" ? "switch" : "slider")} sits on {PageTitles[Array.IndexOf(Categories, MainPage(pack))]} and sets its {Reach(pack)}");
        }
        var settings = new StringBuilder("[ADT_Main]\nbNewLayoutEachLoad=0\niSeed=1\nsPackSignature=\n");
        foreach (var category in Categories)
        {
            var index = Array.IndexOf(Categories, category);
            var sectionName = SectionTitle(category);
            var pick = category.StartsWith("pipboy", StringComparison.Ordinal);
            // Loading screens show a pack's covers on the game's magazine, so every pack with covers has a share there (the plugin orders them the same way).
            var loading = category == "loadingscreens";
            var eligible = Eligible(packs, category);
            var slots = eligible.Take(slotsPerCategory).ToList();
            if (eligible.Count > slotsPerCategory)
            {
                _report.Warn($"{PageTitles[index]}: {Report.Count(eligible.Count, "pack", "packs")} but only {Report.Count(slotsPerCategory, "slot", "slots")}; left out: {string.Join(", ", eligible.Skip(slotsPerCategory).Select(p => p.Name))}. Convert fewer mods for this category.");
            }
            settings.Append($"\n[{sectionName}]\n");
            if (category == "other")
            {
                // One on/off switch per pack.
                foreach (var pack in slots) settings.Append($"bPack_{pack.Id}=0\n");
                if (slots.Count > 0) pages.Add(new { pageDisplayName = "Other replacers", content = OtherPage(packs, slots) });
                continue;
            }
            if (pick)
            {
                settings.Append("iPick=0\n");
                if (slots.Count == 0) continue;
                var options = new List<string> { "Vanilla" };
                options.AddRange(slots.Select(p => p.Name));
                var label = category switch { "pipboyscreen" => "Screen background", "pipboyskin" => "Skin", _ => "World map" };
                (category == "pipboymap" ? mapContent : pipboyContent).Add(new { id = $"iPick:{sectionName}", type = "dropdown", text = label, help = Report.Count(slots.Count, "pack", "packs"), valueOptions = new { options = options.ToArray(), sourceType = "ModSettingInt" } });
                continue;
            }
            foreach (var pack in slots) settings.Append($"iPack_{pack.Id}=0\n");
            if (slots.Count == 0) continue;
            object Slider(int slot)
            {
                var pack = slots[slot - 1];
                if (loading)
                {
                    var covers = pack.Images.Count(i => i.IsCover);
                    return new { id = $"iPack_{pack.Id}:{sectionName}", type = "slider", text = pack.Name, help = Report.Count(covers, "cover", "covers"), valueOptions = new { min = 0, max = 100, step = 1, sourceType = "ModSettingInt" } };
                }
                return new { id = $"iPack_{pack.Id}:{sectionName}", type = "slider", text = pack.Name, help = Reach(pack), valueOptions = new { min = 0, max = 100, step = 1, sourceType = "ModSettingInt" } };
            }
            var locations = loading ? slots.Select(_ => "").ToList() : PageLocations(slots, category);
            var intro = loading
                ? "Slider: percent of loading screens."
                : "Slider: percent of the pack's art shown.";
            var page = new List<object> { new { type = "text", text = intro, html = false } };
            // The packs tied to no one location first, then each location's under its heading.
            var sections = Enumerable.Range(1, slots.Count).GroupBy(slot => locations[slot - 1]).OrderBy(s => s.Key.Length == 0 ? -1 : Array.FindIndex(Locations, l => l.Name == s.Key)).ToList();
            var headings = sections.Count > 1 || sections[0].Key.Length > 0;
            foreach (var section in sections)
            {
                if (headings) page.Add(new { type = "section", text = section.Key.Length == 0 ? "Base game" : section.Key });
                foreach (var slot in section) page.Add(Slider(slot));
            }
            pages.Add(new { pageDisplayName = PageTitles[index], content = page });
        }
        if (pipboyContent.Count > 1) pages.Add(new { pageDisplayName = "Pip-Boy", content = pipboyContent.ToArray() });
        if (mapContent.Count > 1) pages.Add(new { pageDisplayName = "World map", content = mapContent.ToArray() });
        var config = new
        {
            modName = "ArtDistributionTool",
            displayName = "Art Distribution Tool",
            minMcmVersion = 2,
            pluginRequirements = new[] { "ArtDistributionTool.esl" },
            content = new object[]
            {
                new { type = "text", text = "<font size='24'><font color='#FF0000'>Art</font> <font color='#FFFFFF'>Distribution</font> <font color='#A020F0'>Tool</font></font>", html = true },
                new { type = "text", text = packs.Any(p => p.Id != "vanilla") ? $"{Report.Count(packs.Count(p => p.Id != "vanilla"), "pack", "packs")}, {DateTime.Now:yyyy-MM-dd}" : "No packs. Run the converter.", html = false },
                new { type = "text", text = "Changes take effect on Apply all.", html = false },
                new { type = "spacer", numLines = 1 },
                new { type = "section", text = "Options" },
                new { id = "bNewLayoutEachLoad:ADT_Main", type = "switcher", text = "New layout on every load", help = "New arrangement on every load.", valueOptions = new { sourceType = "ModSettingBool" } },
                new { type = "button", text = "Apply all", help = "Saves, reloads, applies every change.", action = new { type = "CallFunction", form = "ArtDistributionTool.esl|800", function = "ApplyAll" } },
                new { type = "button", text = "Shuffle layout", help = "New arrangement, same settings.", action = new { type = "CallFunction", form = "ArtDistributionTool.esl|800", function = "Shuffle" } },
                new { type = "button", text = "Reset all settings", help = "Everything back to default.", action = new { type = "CallFunction", form = "ArtDistributionTool.esl|800", function = "ResetAll" } },
            },
            pages,
        };
        WriteFile(Path.Combine("MCM", "Config", "ArtDistributionTool", "config.json"), JsonSerializer.SerializeToUtf8Bytes(config, new JsonSerializerOptions { WriteIndented = true }));
        // MCM reads the section names into a 1,024-byte buffer and ignores the whole file when they fill it.
        var sectionBytes = System.Text.RegularExpressions.Regex.Matches(settings.ToString(), @"^\[(.+)\]$", System.Text.RegularExpressions.RegexOptions.Multiline).Sum(m => m.Groups[1].Value.Length + 1);
        _report.Info($"MCM settings.ini: {sectionBytes} of the 1,024 bytes MCM reads for section names");
        if (sectionBytes >= 1022) throw new InvalidOperationException($"MCM settings.ini section names take {sectionBytes} of the 1,024 bytes MCM reads, so MCM would ignore every setting");
        WriteFile(Path.Combine("MCM", "Config", "ArtDistributionTool", "settings.ini"), Encoding.ASCII.GetBytes(settings.ToString()));
    }

    /// <summary>The Other replacers page: one on/off switch per pack whose control sits here. On shows the pack on every object it
    /// replaces; packs that are on for the same object share it evenly, and each switch's help line gives the split.</summary>
    private List<object> OtherPage(List<PackOutput> packs, List<PackOutput> slots)
    {
        var content = new List<object>
        {
            new { type = "text", text = "Switch: pack on or off.", html = false },
        };
        // Only what a page control sets: the Pip-Boy lists and magazine backs follow rules of their own.
        static bool Counts(ImageOutput image) => ArtPages.Contains(image.Category);
        // Every pack's objects, so a switch can name the packs it shares objects with.
        var keys = packs.Where(p => p.Id != "vanilla").ToDictionary(p => p, p => p.Images.Where(Counts).SelectMany(ObjectKeys).Where(k => k != "*").ToHashSet(StringComparer.Ordinal));
        for (var s = 0; s < slots.Count; s++)
        {
            var pack = slots[s];
            // This pack's items that other packs also replace, grouped by which packs those are.
            var shared = pack.Images.Where(Counts)
                .Select(image => keys.Where(k => k.Key != pack && ObjectKeys(image).Any(key => key != "*" && k.Value.Contains(key))).Select(k => k.Key).ToList())
                .Where(with => with.Count > 0)
                .GroupBy(with => string.Join("|", with.Select(p => p.Id)))
                .Select(g => (With: g.First(), Count: g.Count())).OrderByDescending(g => g.Count).ToList();
            // Items by the share each pack gets when every pack that has them is on: "2 at 50%, 1 at 33%".
            var splits = shared.GroupBy(g => 100 / (g.With.Count + 1)).OrderByDescending(g => g.Key).Select(g => $"{g.Sum(x => x.Count)} at {g.Key}%");
            content.Add(new
            {
                id = $"bPack_{pack.Id}:ADT_Other",
                type = "switcher",
                text = pack.Name,
                help = $"{Reach(pack)}{(shared.Count > 0 ? ". Shared: " + string.Join(", ", splits) : "")}",
                valueOptions = new { sourceType = "ModSettingBool" },
            });
        }
        return content;
    }

    public void WriteReport()
    {
        WriteFile(Path.Combine("ADT", "convert-report.txt"), Encoding.UTF8.GetBytes(_report.ToString()));
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
