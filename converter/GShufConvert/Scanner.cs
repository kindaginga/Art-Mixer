using System.Security.Cryptography;
using System.Text.Json;
using MaterialLib;

namespace GShufConvert;

/// <summary>One thing a pack provides: a picture for the frames of a category, a replacement for one vanilla material,
/// a replacement for one inline shader (a model drawing without a material file), or a replacement model for every object
/// using a vanilla model ("#" slots) or for one game object a plugin reassigns ("&" slots).</summary>
public sealed class FoundImage
{
    public required string Category { get; init; }
    public string? Diffuse { get; init; }                   // picture texture, source path relative to Data
    public string? Normal { get; init; }                    // source path relative to Data, when the source provides one (pictures)
    public string? Slot { get; init; }                      // vanilla material this entry replaces, or "@" and the inline shader key
    public string? MaterialSource { get; init; }            // the mod's own material, source path relative to Data
    public string? GameMaterial { get; init; }              // a game material (relative to Materials) the entry is a copy of
    public string? InlineKey { get; init; }                 // inline shader key when Slot names an inline shader
    public string? ModelSource { get; init; }               // replacement model, source path relative to Data; Slot is "#" and the vanilla model path, or "&" and the object
    public Dictionary<string, string> ModelFiles { get; init; } = new(StringComparer.OrdinalIgnoreCase);  // "t:"/"m:" and a texture or material the model uses -> source path
    public Dictionary<string, string> Swaps { get; init; } = new(StringComparer.OrdinalIgnoreCase);  // texture the material uses -> source path
    public string? GameModel { get; init; }                 // replacement model from the game's own archives, relative to Meshes
    public Dictionary<string, string> Substitutions { get; init; } = new(StringComparer.OrdinalIgnoreCase);  // material -> material baked into the model
    public bool DropSwap { get; init; }                     // the object loses the material swap its vanilla record gives it
    public string? SwapForm { get; init; }                  // the material swap ("file:local id", "" for none) the object keeps with this model
    public string? GameTexture { get; init; }               // a game texture (relative to Textures) a texture set entry points at
    public string? Label { get; init; }                     // the object a plugin reassigns, for the slot list
    public string? Name { get; init; }                      // the name a plugin gives the object, shown on it while this pack's version shows
    public short[]? Bounds { get; init; }                   // the object bounds (OBND min x, y, z, max x, y, z) a plugin gives the object
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Every source file this entry reads.</summary>
    public IEnumerable<string> SourceFiles()
    {
        foreach (var path in new[] { Diffuse, Normal, MaterialSource, ModelSource }) if (path != null) yield return path;
        foreach (var path in Swaps.Values) yield return path;
        foreach (var path in ModelFiles.Values) yield return path;
    }

    /// <summary>Identity by content, so the same replacement shipped by two options of one mod is kept once.</summary>
    public string ContentKey(Source source)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        foreach (var path in SourceFiles().Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) sha.AppendData(source.Files[path].Read());
        var extras = $"{Name}|{GameMaterial}|{GameModel}|{(Bounds == null ? "" : string.Join(",", Bounds))}|{DropSwap}|{SwapForm}|{GameTexture}|{string.Join(";", Substitutions.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Key + "=" + kv.Value))}";
        return Convert.ToHexString(sha.GetHashAndReset()) + "|" + (Slot ?? "") + "|" + extras;
    }

    /// <summary>A copy whose source paths carry the prefix, with the files registered in the target source under those paths.</summary>
    public FoundImage Prefixed(string prefix, Source from, Source into)
    {
        foreach (var path in SourceFiles()) into.Files[prefix + path] = from.Files[path];
        string? P(string? path) => path == null ? null : prefix + path;
        return new FoundImage
        {
            Category = Category, Diffuse = P(Diffuse), Normal = P(Normal), Slot = Slot, MaterialSource = P(MaterialSource), GameMaterial = GameMaterial, InlineKey = InlineKey,
            Swaps = Swaps.ToDictionary(kv => kv.Key, kv => prefix + kv.Value, StringComparer.OrdinalIgnoreCase), Width = Width, Height = Height,
            ModelSource = P(ModelSource), ModelFiles = ModelFiles.ToDictionary(kv => kv.Key, kv => prefix + kv.Value, StringComparer.OrdinalIgnoreCase),
            GameModel = GameModel, Substitutions = new Dictionary<string, string>(Substitutions, StringComparer.OrdinalIgnoreCase), DropSwap = DropSwap, SwapForm = SwapForm, GameTexture = GameTexture, Label = Label, Name = Name, Bounds = Bounds,
        };
    }
}

public static class Scanner
{
    /// <summary>A game record value a converted mod's plugin changes: a material swap's entry for From ("swap") or a texture set's texture Index ("textureSet"), Mod in place of Game.</summary>
    public sealed record Restore(string Kind, string Form, string From, string Mod, string Game, int Index = 0, float? GameColor = null, float? ModColor = null);

    // Vanilla folders and name parts that set the category of a vanilla material or texture outside the catalog.
    private static readonly (string Needle, string Category)[] VanillaCategories =
    [
        ("animobjects\\pipboy\\pipboyscreen", "pipboyscreen"), ("animobjects\\pipboy\\", "pipboyskin"),
        ("pipboy\\characterassets\\", "pipboyskin"), ("props\\pipboymiscitem\\", "pipboyskin"), ("animobjects\\pipboy.nif", "pipboyskin"), ("skeletons\\skeletonpipboyarm", "pipboyskin"),
        ("comicsandmagazines", "magazines"), ("magazine", "magazines"), ("almanac", "magazines"),
        ("paintingsgeneric", "paintings"), ("\\painting", "paintings"),
        ("poster", "posters"), ("\\quest\\", "quest"), ("signage", "signage"), ("billboard", "signage"),
    ];

    /// <summary>The art categories whose pictures can show on loading screens.</summary>
    public static readonly HashSet<string> PictureCategories = new(StringComparer.Ordinal) { "paintings", "posters", "signage", "magazines", "quest" };

    /// <summary>Category of a vanilla material or texture by its folder and name; "other" when it is not art.</summary>
    public static string ClassifyVanilla(string material, string texture)
    {
        var text = (material + " " + texture).ToLowerInvariant();
        return VanillaCategories.FirstOrDefault(v => text.Contains(v.Needle)).Category ?? "other";
    }

    // Textures the game loads by name that belong to a pick: the Pip-Boy screen effects, world map and perk chart follow the
    // Pip-Boy screen list, the loading screen color table the loading screens.
    private static readonly (string Needle, string Category)[] ByNameCategories =
    [
        ("animobjects\\pipboy\\pipboyfx", "pipboyscreen"), ("animobjects\\pipboy\\pipboyscan", "pipboyscreen"), ("animobjects\\pipboy\\pipboyvscan", "pipboyscreen"),
        ("interface\\pip-boy\\", "pipboyscreen"), ("interface\\perkgrid\\", "pipboyscreen"), ("effects\\luts\\loadingmenucolorlut", "loadingscreens"),
    ];

    // Sound file types the game plays, in the order it tries them for a sound's name.
    private static readonly string[] AudioExtensions = [".wav", ".fuz", ".xwm"];

    /// <summary>The page a texture the game loads by name is shared on.</summary>
    public static string ByNameCategory(string texture)
    {
        var lower = texture.ToLowerInvariant();
        return ByNameCategories.FirstOrDefault(c => lower.StartsWith(c.Needle, StringComparison.Ordinal)).Category ?? ClassifyVanilla("", texture);
    }

    /// <summary>Finds everything a source replaces or adds: the mod's materials at vanilla paths, every vanilla material that uses
    /// a texture the mod ships, pictures for the frames of a category, and textures models draw without a material file.</summary>
    public static List<FoundImage> Scan(Source source, Catalog catalog, GameFiles game, Report report, List<Restore> restores)
    {
        var manifest = source.Files.Values.FirstOrDefault(f => Path.GetFileName(f.DataPath).Equals("gshuf-pack.json", StringComparison.OrdinalIgnoreCase));
        if (manifest != null) return FromManifest(source, manifest, catalog, report);

        // Textures the mod ships, by path relative to Textures.
        var shipped = new Dictionary<string, SourceFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in source.Files.Values)
        {
            if (file.DataPath.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase) && file.DataPath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) shipped[Paths.Normalize(file.DataPath, "textures")] = file;
        }
        var found = new List<FoundImage>();
        var replaced = new Dictionary<string, FoundImage>(StringComparer.OrdinalIgnoreCase);

        // The mod's own materials: at a vanilla path they replace that material, anywhere else they may be new pictures.
        var standalone = new List<(SourceFile File, BaseMaterialFile Material)>();
        var modReferenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in source.Files.Values)
        {
            if (!file.DataPath.StartsWith("materials\\", StringComparison.OrdinalIgnoreCase)) continue;
            if (!file.DataPath.EndsWith(".bgsm", StringComparison.OrdinalIgnoreCase) && !file.DataPath.EndsWith(".bgem", StringComparison.OrdinalIgnoreCase)) continue;
            BaseMaterialFile material;
            try
            {
                material = MaterialFiles.Read(file.Read());
            }
            catch (Exception e)
            {
                report.Warn($"{source.Name}: cannot read material {file.DataPath}: {e.Message}");
                continue;
            }
            var relative = Paths.Normalize(file.DataPath, "materials");
            modReferenced.UnionWith(MaterialFiles.Textures(material));
            if (!catalog.ByMaterial.ContainsKey(relative) && !game.HasMaterial(relative))
            {
                standalone.Add((file, material));
                continue;
            }
            var entry = Replacement(relative, MaterialFiles.Textures(material), MaterialFiles.Diffuse(material), file.DataPath, shipped, catalog, game);
            replaced[relative] = entry;
        }

        // Every vanilla material that uses a texture the mod ships takes that texture, with any other texture the mod ships for it.
        foreach (var texture in shipped.Keys)
        {
            foreach (var material in game.MaterialsUsing(texture))
            {
                if (replaced.ContainsKey(material)) continue;
                var entry = Replacement(material, game.TexturesOf(material), game.DiffuseOf(material), null, shipped, catalog, game);
                replaced[material] = entry;
            }
        }

        // A model that draws a shipped texture with an inline shader (no material file) gets a material file rebuilt from that
        // shader with the mod's textures; the plugin recognizes such models by the shader's key texture.
        var inline = 0;
        foreach (var texture in shipped.Keys)
        {
            foreach (var key in game.InlineKeysUsing(texture))
            {
                var slot = "@" + key;
                if (replaced.ContainsKey(slot)) continue;
                var use = game.Inline(key)!;
                var swaps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in use.Textures)
                {
                    if (shipped.TryGetValue(t, out var file)) swaps[t] = file.DataPath;
                }
                var dims = shipped.TryGetValue(key, out var keyFile) ? keyFile.Dimensions() : game.TextureDimensions(key);
                replaced[slot] = new FoundImage { Category = ClassifyVanilla("", key), Slot = slot, InlineKey = key, Swaps = swaps, Width = dims?.Width ?? 0, Height = dims?.Height ?? 0 };
                inline++;
            }
        }
        found.AddRange(replaced.Values);

        // Models the mod ships at vanilla paths replace those models object by object; each keeps the textures and materials
        // the mod ships for it, so a pack's model always wears that pack's files.
        var models = 0;
        foreach (var file in source.Files.Values)
        {
            if (!file.DataPath.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) || !file.DataPath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)) continue;
            var relative = Paths.Normalize(file.DataPath, "meshes");
            if (!game.HasMesh(relative)) continue;
            var files = ModelFiles(file.Read(), [], source, shipped, report);
            found.Add(new FoundImage { Category = ClassifyVanilla(relative, ""), Slot = "#" + relative, ModelSource = file.DataPath, ModelFiles = files });
            models++;
        }

        // Models and names a plugin gives the game's own objects replace them on exactly those objects.
        var usedPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assignedSlots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in source.Files.Values)
        {
            if (Path.GetExtension(file.DataPath).ToLowerInvariant() is not (".esp" or ".esm" or ".esl")) continue;
            var entries = PluginObjects(source, file, shipped, catalog, game, report, restores);
            if (entries == null) continue;
            usedPlugins.Add(file.DataPath);
            foreach (var entry in entries)
            {
                found.Add(entry);
                if (entry.Slot!.StartsWith('&')) assignedSlots.Add(entry.Slot);
            }
        }

        // Reports the mod's new materials no entry uses, which the converter leaves to the mod's own plugin.
        var usedFiles = new HashSet<string>(found.SelectMany(f => f.SourceFiles()), StringComparer.OrdinalIgnoreCase);
        var unconverted = standalone.Count(s => !usedFiles.Contains(s.File.DataPath));
        if (unconverted > 0)
        {
            report.Info($"{source.Name}: {Report.Count(unconverted, "new material", "new materials")} of the mod's own objects, which its own plugin shows; not converted (keep the mod installed for them)");
        }
        // A game texture that the game loads by its name is replaced at that name, also when a material or model uses it.
        var byName = 0;
        foreach (var (texture, file) in shipped)
        {
            if (found.Any(f => f.Slot == "%" + texture) || !game.HasTexture(texture) || !game.LoadsByName(texture)) continue;
            var dims = file.Dimensions();
            found.Add(new FoundImage
            {
                Category = ByNameCategory(texture), Slot = "%" + texture, Swaps = new(StringComparer.OrdinalIgnoreCase) { [texture] = file.DataPath },
                Width = dims?.Width ?? 0, Height = dims?.Height ?? 0,
            });
            byName++;
        }
        if (byName > 0) report.Info($"{source.Name}: {Report.Count(byName, "texture", "textures")} the game loads by name replaced at that name");

        // Sound and music files over the game's own replace them per play; the game finds a sound by folder and name, so one
        // file per name is kept, in the order the game tries extensions (.wav, .fuz, .xwm). The menu theme follows the corner box.
        var sounds = 0;
        var newSounds = new List<string>();
        var soundSlots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in source.Files.Values.OrderBy(f => Array.IndexOf(AudioExtensions, Path.GetExtension(f.DataPath).ToLowerInvariant())))
        {
            var root = file.DataPath.Split('\\')[0].ToLowerInvariant();
            if (root is not ("sound" or "music") || !AudioExtensions.Contains(Path.GetExtension(file.DataPath).ToLowerInvariant())) continue;
            if (Path.ChangeExtension(file.DataPath, null).Equals("music\\special\\mus_maintheme", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.ChangeExtension(file.DataPath, null).ToLowerInvariant();
            if (!game.HasAudio(file.DataPath))
            {
                newSounds.Add(file.DataPath);
                continue;
            }
            if (!soundSlots.Add(name)) continue;
            found.Add(new FoundImage { Category = "other", Slot = "~" + name, Swaps = new(StringComparer.OrdinalIgnoreCase) { [name] = file.DataPath } });
            sounds++;
        }
        if (sounds > 0) report.Info($"{source.Name}: {Report.Count(sounds, "sound or music file", "sound and music files")} of the game's replaced per play");
        if (newSounds.Count > 0) report.Info($"{source.Name}: {Report.Count(newSounds.Count, "sound file", "sound files")} the game does not have, which only the mod's plugin can play ({string.Join(", ", newSounds.Take(3).Select(Path.GetFileName))}{(newSounds.Count > 3 ? ", ..." : "")})");
        var byCategory = string.Join(", ", replaced.Values.GroupBy(r => r.Category).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}"));
        report.Info($"{source.Name}: {Report.Count(models, "model", "models")} at vanilla paths, {Report.Count(assignedSlots.Count, "object", "objects")} a plugin gives another model or name, {Report.Count(replaced.Count - inline, "vanilla material", "vanilla materials")} and {Report.Count(inline, "model", "models")} without a material file replaced ({(byCategory.Length > 0 ? byCategory : "none")})");
        ReportUnused(source, found, game, modReferenced, usedPlugins, report);
        return found;
    }

    /// <summary>Lists every file of the source that no pack entry uses, grouped by kind with examples. Textures that neither a
    /// vanilla material, a vanilla model nor one of the mod's own materials references are listed apart: the game never loads
    /// them, even with the mod installed on its own.</summary>
    private static void ReportUnused(Source source, List<FoundImage> found, GameFiles game, HashSet<string> modReferenced, HashSet<string> usedPlugins, Report report)
    {
        var used = new HashSet<string>(found.SelectMany(f => f.SourceFiles()), StringComparer.OrdinalIgnoreCase);
        used.UnionWith(usedPlugins);
        var video = FindMenuVideo(source);
        if (video != null) used.Add(video);
        var music = FindMenuMusic(source);
        if (music != null) used.Add(music);
        used.UnionWith(FindInterfaceFiles(source, game).Select(f => f.Source));
        string Kind(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".dds" && path.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase))
            {
                var texture = Paths.Normalize(path, "textures");
                if (!modReferenced.Contains(texture) && game.MaterialsUsing(texture).Count == 0 && game.InlineKeysUsing(texture).Count == 0) return "textures nothing in the game loads";
            }
            var root = path.Split('\\')[0].ToLowerInvariant();
            return ext switch
            {
                ".nif" => "models",
                ".dds" => "textures",
                ".bgsm" or ".bgem" => "materials",
                ".wav" or ".xwm" or ".fuz" or ".lip" => root == "music" ? "music" : "sounds",
                ".bk2" => "videos",
                ".esp" or ".esm" or ".esl" => "plugins",
                ".pex" or ".psc" => "scripts",
                ".swf" => root == "interface" ? "interface files" : "interface files outside the Interface folder, where the game never loads them",
                _ => "other files",
            };
        }
        var groups = source.Files.Keys.Where(k => !used.Contains(k)).GroupBy(Kind).OrderBy(g => g.Key).ToList();
        if (groups.Count == 0) return;
        report.Info($"{source.Name}: files not used yet: " + string.Join("; ", groups.Select(g => $"{g.Count()} {g.Key} ({string.Join(", ", g.Take(3).Select(Path.GetFileName))}{(g.Count() > 3 ? ", ..." : "")})")));
    }

    /// <summary>Textures and materials a model references that the mod ships, keyed "t:" or "m:" and the path relative to
    /// Textures or Materials, with each such material's shipped textures. Extra materials are ones a baked material swap puts on the model.</summary>
    private static Dictionary<string, string> ModelFiles(byte[] model, IEnumerable<string> extraMaterials, Source source, Dictionary<string, SourceFile> shipped, Report report)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        List<string> references;
        try
        {
            references = NifFile.References(model).ToList();
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or EndOfStreamException or InvalidDataException)
        {
            report.Warn($"{source.Name}: a model could not be read ({e.Message}); its files are not tied to it");
            references = [];
        }
        foreach (var reference in references.Concat(extraMaterials.Select(m => Paths.WithRoot(m, "materials"))))
        {
            var ext = Path.GetExtension(reference).ToLowerInvariant();
            if (ext == ".dds")
            {
                var texture = Paths.Normalize(reference, "textures");
                if (shipped.TryGetValue(texture, out var textureFile)) files["t:" + texture] = textureFile.DataPath;
            }
            else if (ext is ".bgsm" or ".bgem")
            {
                var material = Paths.Normalize(reference, "materials");
                if (!source.Files.TryGetValue(Paths.WithRoot(material, "materials"), out var materialFile)) continue;
                try
                {
                    var textures = MaterialFiles.Textures(MaterialFiles.Read(materialFile.Read()));
                    files["m:" + material] = materialFile.DataPath;
                    foreach (var texture in textures)
                    {
                        if (shipped.TryGetValue(texture, out var textureFile)) files["t:" + texture] = textureFile.DataPath;
                    }
                }
                catch (Exception e)
                {
                    report.Warn($"{source.Name}: cannot read material {materialFile.DataPath}: {e.Message}");
                }
            }
        }
        return files;
    }

    // Fields a pack carries per object (model, material swap, name, bounds) or that only identify the record (editor id, texture hashes).
    private static readonly HashSet<string> CarriedFields = new(StringComparer.Ordinal) { "EDID", "MODL", "MODT", "MODS", "MODF", "FULL", "OBND" };

    /// <summary>Every game object a plugin gives another model or another name, as a replacement of exactly that object, or null
    /// when the file is not a readable plugin. The model is the mod's own file or, when the mod does not ship it, the game's; a
    /// material swap the plugin gives the object is baked into the pack's copy of the model; the name shows on each object while
    /// the pack's version shows there. Every other field the plugin changes, and everything else it does, is listed in the report.</summary>
    private static List<FoundImage>? PluginObjects(Source source, SourceFile file, Dictionary<string, SourceFile> shipped, Catalog catalog, GameFiles game, Report report, List<Restore> restores)
    {
        PluginFile plugin;
        try
        {
            plugin = PluginFile.Read(Path.GetFileName(file.DataPath), file.Read());
        }
        catch (Exception e)
        {
            report.Warn($"{source.Name}: cannot read plugin {file.DataPath}: {e.Message}");
            return null;
        }
        var pluginStrings = new Dictionary<uint, string>();
        if (plugin.Localized)
        {
            var stem = Path.GetFileNameWithoutExtension(plugin.Name);
            foreach (var (ext, prefixed) in new[] { ("strings", false), ("dlstrings", true), ("ilstrings", true) })
            {
                if (!source.Files.TryGetValue($"strings\\{stem}_{game.Language}.{ext}", out var table)) continue;
                try
                {
                    foreach (var (id, text) in PluginFile.ReadStrings(table.Read(), prefixed)) pluginStrings.TryAdd(id, text);
                }
                catch (Exception e) when (e is ArgumentException or ArgumentOutOfRangeException or IndexOutOfRangeException or EndOfStreamException)
                {
                    report.Warn($"{source.Name}: cannot read {table.DataPath}: {e.Message}");
                }
            }
        }
        string? PluginText(byte[]? field) => PluginFile.Text(field, plugin.Localized, id => pluginStrings.GetValueOrDefault(id));
        string? GameText(PluginFile.ModelRecord record, byte[]? field) => PluginFile.Text(field, game.IsLocalized(record.Source), id => game.GameString(record.Source, id));

        // Fields that differ once text is compared as text and the unused bytes of conditions are ignored.
        List<string> Differences(PluginFile.ModelRecord vanilla, PluginFile.ModelRecord record)
        {
            static string Value(PluginFile.Field field)
            {
                var data = field.Data;
                if (field.Type == "CTDA" && data.Length >= 4)
                {
                    data = (byte[])data.Clone();
                    data[1] = data[2] = data[3] = 0;
                }
                return Convert.ToHexString(data);
            }
            var before = vanilla.Fields.GroupBy(f => f.Type).ToDictionary(g => g.Key, g => g.ToList());
            var after = record.Fields.GroupBy(f => f.Type).ToDictionary(g => g.Key, g => g.ToList());
            var differing = new List<string>();
            foreach (var type in before.Keys.Union(after.Keys).Where(t => !CarriedFields.Contains(t)).Order(StringComparer.Ordinal))
            {
                var a = before.GetValueOrDefault(type) ?? [];
                var b = after.GetValueOrDefault(type) ?? [];
                if (a.Select(Value).SequenceEqual(b.Select(Value))) continue;
                if (a.Count == 1 && b.Count == 1 && GameText(vanilla, a[0].Data) is { } oldText && PluginText(b[0].Data) is { } newText && oldText == newText) continue;
                differing.Add(type);
            }
            return differing;
        }

        var entries = new List<FoundImage>();
        var overrides = plugin.Models.Where(m => game.IsGameMaster(m.Key.File)).ToList();
        if (overrides.Count > 0) game.IndexGameRecords(overrides.Select(m => m.Type).Append("MSWP"));
        var covered = new HashSet<FormKey>();
        var notCarried = new List<(string Field, PluginFile.ModelRecord Record)>();
        int missingModel = 0, lostSwaps = 0, lostColors = 0;
        foreach (var record in overrides)
        {
            var vanilla = game.GameModelRecord(record.Key);
            if (vanilla == null)
            {
                report.Warn($"{source.Name}: {plugin.Name} changes {record.Type} {record.Key} ({record.EditorId}), which the game's plugins do not define with a model; skipped");
                continue;
            }
            covered.Add(record.Key);
            notCarried.AddRange(Differences(vanilla, record).Select(f => (f, record)));
            var modelChanged = !string.Equals(vanilla.Model, record.Model, StringComparison.OrdinalIgnoreCase);
            var swapChanged = !Nullable.Equals(vanilla.Swap, record.Swap);
            var name = PluginText(record.FieldData("FULL"));
            var vanillaName = GameText(vanilla, vanilla.FieldData("FULL"));
            var nameChanged = !string.IsNullOrEmpty(name) && name != vanillaName;
            var bounds = record.FieldData("OBND");
            var boundsChanged = bounds is { Length: 12 } && !bounds.AsSpan().SequenceEqual(vanilla.FieldData("OBND") ?? []);
            if (!modelChanged && !swapChanged && !nameChanged && !boundsChanged) continue;
            // A new model that keeps the game's swap while the plugin edits that swap is baked with the plugin's edit.
            var keptSwapEdited = modelChanged && !swapChanged && record.Swap is { } kept && plugin.MaterialSwaps.TryGetValue(kept, out var keptOwn)
                && game.GameMaterialSwap(kept) is { } keptGame && !SameSubstitutions(keptOwn, keptGame);
            // A game swap the plugin gives the object and leaves as the game has it is pinned, not baked, so its color remaps stay.
            var pinTarget = swapChanged && record.Swap is { } newSwap && game.IsGameMaster(newSwap.File) && game.GameMaterialSwap(newSwap) is { } newGame
                && (!plugin.MaterialSwaps.TryGetValue(newSwap, out var newOwn) || SameSubstitutions(newOwn, newGame));
            var bakeSwap = (swapChanged && !pinTarget) || keptSwapEdited;
            var substitutions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            SourceFile? modelFile = null;
            byte[]? modelBytes = null;
            if (modelChanged || swapChanged)
            {
                if (bakeSwap && record.Swap is { } target)
                {
                    var list = plugin.MaterialSwaps.TryGetValue(target, out var own) ? own : game.IsGameMaster(target.File) ? game.GameMaterialSwap(target) : null;
                    if (list == null) lostSwaps++;
                    foreach (var substitution in list ?? [])
                    {
                        if (substitution.Original.Length > 0 && substitution.Replacement.Length > 0) substitutions[substitution.Original] = substitution.Replacement;
                        if (substitution.ColorIndex is not null) lostColors++;
                    }
                }
                source.Files.TryGetValue(Paths.WithRoot(record.Model, "meshes"), out modelFile);
                modelBytes = modelFile?.Read() ?? game.ReadMesh(record.Model);
                if (modelBytes == null)
                {
                    report.Warn($"{source.Name}: {plugin.Name} gives {record.EditorId} the model {record.Model}, which neither the mod nor the game ships; the model is left out");
                    missingModel++;
                    if (!nameChanged && !boundsChanged) continue;
                }
            }
            var changes = new List<string>();
            if (modelBytes != null) changes.Add($"model {vanilla.Model} -> {record.Model}");
            if (nameChanged) changes.Add($"name '{vanillaName}' -> '{name}'");
            short[]? boundsValues = boundsChanged ? Enumerable.Range(0, 6).Select(i => BitConverter.ToInt16(bounds!, i * 2)).ToArray() : null;
            if (boundsValues != null) changes.Add($"bounds ({string.Join(", ", boundsValues)})");
            entries.Add(new FoundImage
            {
                Category = ClassifyVanilla(vanilla.Model, ""), Slot = "&" + record.Key.Text,
                ModelSource = modelBytes != null ? modelFile?.DataPath : null, GameModel = modelBytes != null && modelFile == null ? record.Model : null,
                ModelFiles = modelBytes != null ? ModelFiles(modelBytes, substitutions.Values, source, shipped, report) : new(StringComparer.OrdinalIgnoreCase),
                Substitutions = substitutions, DropSwap = modelBytes != null && bakeSwap, SwapForm = modelBytes != null && !bakeSwap ? record.Swap?.Text ?? "" : null,
                Name = nameChanged ? name : null, Bounds = boundsValues,
                Label = $"{record.EditorId} ({record.Type} {record.Key}): {string.Join(", ", changes)}",
            });
        }

        // Changes to the game's material swaps: a slot scoped to the swap and the material it replaces ("=swap|material").
        var swapOverrides = plugin.MaterialSwaps.Where(kv => game.IsGameMaster(kv.Key.File)).ToList();
        var setOverrides = plugin.TextureSets.Where(kv => game.IsGameMaster(kv.Key.File)).ToList();
        if (swapOverrides.Count + setOverrides.Count > 0) game.IndexGameRecords(["MSWP", "TXST"]);
        int swapMaterials = 0, setTextures = 0;
        var notConverted = new List<string>();
        foreach (var (key, substitutions) in swapOverrides)
        {
            var vanilla = game.GameMaterialSwap(key);
            if (vanilla == null) continue;
            covered.Add(key);
            notConverted.AddRange(SwapDifferencesNotConverted(key, vanilla, substitutions));
            foreach (var (original, changed) in ChangedSwapEntries(vanilla, substitutions))
            {
                if (!source.Files.TryGetValue(Paths.WithRoot(changed.Replacement, "materials"), out var materialFile))
                {
                    if (!game.HasMaterial(changed.Replacement))
                    {
                        notConverted.Add($"MSWP {key}: {original.Replacement} -> {changed.Replacement} (a material neither the mod nor the game ships)");
                        continue;
                    }
                    var fromGame = Replacement(original.Replacement, game.TexturesOf(changed.Replacement), game.DiffuseOf(changed.Replacement), null, shipped, catalog, game);
                    entries.Add(new FoundImage { Category = fromGame.Category, Slot = SwapSlot(key, original.Original), Swaps = fromGame.Swaps, Width = fromGame.Width, Height = fromGame.Height, GameMaterial = changed.Replacement });
                    restores.Add(new Restore("swap", key.Text, original.Original, changed.Replacement, original.Replacement, 0, original.ColorIndex, changed.ColorIndex));
                    swapMaterials++;
                    continue;
                }
                BaseMaterialFile material;
                try
                {
                    material = MaterialFiles.Read(materialFile.Read());
                }
                catch (Exception e)
                {
                    report.Warn($"{source.Name}: cannot read material {materialFile.DataPath}: {e.Message}");
                    continue;
                }
                var ownVersion = Replacement(original.Replacement, MaterialFiles.Textures(material), MaterialFiles.Diffuse(material), materialFile.DataPath, shipped, catalog, game);
                entries.Add(new FoundImage { Category = ownVersion.Category, Slot = SwapSlot(key, original.Original), MaterialSource = ownVersion.MaterialSource, Swaps = ownVersion.Swaps, Width = ownVersion.Width, Height = ownVersion.Height });
                restores.Add(new Restore("swap", key.Text, original.Original, changed.Replacement, original.Replacement, 0, original.ColorIndex, changed.ColorIndex));
                swapMaterials++;
            }
        }
        // Changes to the game's texture sets: a slot scoped to the set and the texture's index ("!set|index").
        foreach (var (key, textures) in setOverrides)
        {
            var vanilla = game.GameTextureSet(key);
            if (vanilla == null) continue;
            covered.Add(key);
            for (var i = 0; i < 8; i++)
            {
                if (vanilla[i].Length > 0 && textures[i].Length == 0) notConverted.Add($"TXST {key}: removes texture {i} ({vanilla[i]})");
                else if (vanilla[i].Length == 0 && textures[i].Length > 0) notConverted.Add($"TXST {key}: adds texture {i} ({textures[i]})");
            }
            foreach (var i in ChangedSetIndexes(vanilla, textures))
            {
                string? gameTexture = null;
                if (!shipped.TryGetValue(textures[i], out var textureFile))
                {
                    if (!game.HasTexture(textures[i]))
                    {
                        notConverted.Add($"TXST {key}: {vanilla[i]} -> {textures[i]} (a texture neither the mod nor the game ships)");
                        continue;
                    }
                    gameTexture = textures[i];
                }
                restores.Add(new Restore("textureSet", key.Text, "", textures[i], vanilla[i], i));
                var dims = textureFile?.Dimensions() ?? game.TextureDimensions(textures[i]);
                entries.Add(new FoundImage
                {
                    Category = ByNameCategory(vanilla[i]), Slot = $"!{key.Text}|{i}", GameTexture = gameTexture,
                    Swaps = textureFile == null ? new(StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase) { [vanilla[i]] = textureFile.DataPath },
                    Width = dims?.Width ?? 0, Height = dims?.Height ?? 0,
                });
                setTextures++;
            }
        }

        string ByType(IEnumerable<string> types) => string.Join(", ", types.GroupBy(t => t).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}"));
        var gameChanges = plugin.Records.Where(r => game.IsGameMaster(r.Key.File) && !covered.Contains(r.Key)).Select(r => r.Type).ToList();
        var otherMods = plugin.Records.Where(r => !r.Key.File.Equals(plugin.Name, StringComparison.OrdinalIgnoreCase) && !game.IsGameMaster(r.Key.File)).Select(r => r.Type).ToList();
        var added = plugin.Records.Where(r => r.Key.File.Equals(plugin.Name, StringComparison.OrdinalIgnoreCase)).Select(r => r.Type).ToList();
        var objects = entries.Where(e => e.Slot != null && e.Slot.StartsWith('&')).ToList();
        var parts = new List<string> { $"{Report.Count(objects.Count, "game object gets", "game objects get")} another model or name{(objects.Count > 0 ? $" ({string.Join("; ", objects.Take(6).Select(e => e.Label))}{(objects.Count > 6 ? "; ..." : "")})" : "")}" };
        foreach (var group in notCarried.GroupBy(n => n.Field).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            parts.Add($"not carried over: field {group.Key} on {Report.Count(group.Count(), "object", "objects")} ({string.Join(", ", group.Take(6).Select(n => $"{n.Record.EditorId} {n.Record.Type}"))}{(group.Count() > 6 ? ", ..." : "")})");
        }
        if (swapMaterials > 0) parts.Add($"{Report.Count(swapMaterials, "material", "materials")} a changed material swap of the game's puts on objects");
        if (setTextures > 0) parts.Add($"{Report.Count(setTextures, "texture", "textures")} a changed texture set of the game's uses");
        if (notConverted.Count > 0) parts.Add($"not converted: {string.Join("; ", notConverted.Take(6))}{(notConverted.Count > 6 ? "; ..." : "")}");
        if (gameChanges.Count > 0) parts.Add($"not converted: {Report.Count(gameChanges.Count, "change", "changes")} to game records that are not objects with a model ({ByType(gameChanges)})");
        if (added.Count > 0) parts.Add($"{Report.Count(added.Count, "new record", "new records")} the plugin adds ({ByType(added)}), used only through their art files");
        if (otherMods.Count > 0) parts.Add($"not converted: {Report.Count(otherMods.Count, "change", "changes")} to records of other mods ({ByType(otherMods)})");
        if (lostSwaps > 0) parts.Add($"{Report.Count(lostSwaps, "material swap", "material swaps")} defined outside the plugin and the game left off");
        if (lostColors > 0) parts.Add($"{Report.Count(lostColors, "color remapping value", "color remapping values")} of material swaps not carried over");
        if (missingModel > 0) parts.Add($"{Report.Count(missingModel, "model", "models")} missing");
        if (plugin.Records.Count == 0) parts = ["no records (it only loads the mod's archives)"];
        report.Info($"{source.Name}: plugin {plugin.Name}: {string.Join("; ", parts)}");
        return entries;
    }

    // The slot of one entry of a game material swap: "=", the swap form, "|" and the material it replaces (relative to Materials).
    public static string SwapSlot(FormKey swap, string original) => $"={swap.Text}|{Paths.Normalize(original, "materials")}";

    /// <summary>The entries of a game swap a plugin's version shows another material for, both named: (game entry, plugin entry).</summary>
    public static IEnumerable<(PluginFile.Substitution Game, PluginFile.Substitution Mod)> ChangedSwapEntries(List<PluginFile.Substitution> game, List<PluginFile.Substitution> mod)
    {
        foreach (var original in game.Where(v => v.Original.Length > 0 && v.Replacement.Length > 0))
        {
            var changed = mod.FirstOrDefault(s => s.Original.Equals(original.Original, StringComparison.OrdinalIgnoreCase));
            if (changed != null && changed.Replacement.Length > 0 && !changed.Replacement.Equals(original.Replacement, StringComparison.OrdinalIgnoreCase)) yield return (original, changed);
        }
    }

    /// <summary>The indexes at which a plugin's version of a game texture set names another texture, both named.</summary>
    public static IEnumerable<int> ChangedSetIndexes(string[] game, string[] mod) =>
        Enumerable.Range(0, 8).Where(i => game[i].Length > 0 && mod[i].Length > 0 && !game[i].Equals(mod[i], StringComparison.OrdinalIgnoreCase));

    // The report lines for the differences of a plugin's version of a game swap that no slot carries.
    private static IEnumerable<string> SwapDifferencesNotConverted(FormKey key, List<PluginFile.Substitution> game, List<PluginFile.Substitution> mod)
    {
        foreach (var extra in mod.Where(s => s.Original.Length > 0 && !game.Any(v => v.Original.Equals(s.Original, StringComparison.OrdinalIgnoreCase))))
        {
            yield return $"MSWP {key}: a new substitution {extra.Original} -> {extra.Replacement}";
        }
        foreach (var original in game.Where(v => v.Original.Length > 0))
        {
            var changed = mod.FirstOrDefault(s => s.Original.Equals(original.Original, StringComparison.OrdinalIgnoreCase));
            if (changed == null) yield return $"MSWP {key}: removes the substitution for {original.Original}";
            else if (original.Replacement.Length > 0 && changed.Replacement.Length == 0) yield return $"MSWP {key}: empties the substitution for {original.Original}";
            else if (original.Replacement.Length == 0 && changed.Replacement.Length > 0) yield return $"MSWP {key}: fills the substitution for {original.Original} with {changed.Replacement}";
            if (changed != null && !Nullable.Equals(changed.ColorIndex, original.ColorIndex))
            {
                yield return $"MSWP {key}: color remapping index {original.ColorIndex?.ToString() ?? "none"} -> {changed.ColorIndex?.ToString() ?? "none"} for {original.Original}";
            }
        }
    }

    // Whether two material swaps put the same materials (and color remaps) in place of the same materials.
    private static bool SameSubstitutions(List<PluginFile.Substitution> a, List<PluginFile.Substitution> b)
    {
        static string Key(PluginFile.Substitution s) => $"{Paths.Normalize(s.Original, "materials")}>{Paths.Normalize(s.Replacement, "materials")}>{s.ColorIndex}";
        return a.Select(Key).Order(StringComparer.Ordinal).SequenceEqual(b.Select(Key).Order(StringComparer.Ordinal));
    }

    private static FoundImage Replacement(string material, IReadOnlyList<string> textures, string diffuse, string? ownMaterial, Dictionary<string, SourceFile> shipped, Catalog catalog, GameFiles game)
    {
        var swaps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var texture in textures)
        {
            if (shipped.TryGetValue(texture, out var file)) swaps[texture] = file.DataPath;
        }
        catalog.ByMaterial.TryGetValue(material, out var slot);
        // The vanilla material's own diffuse decides the category, so every pack files the same slot under the same page.
        var category = slot?.Category ?? ClassifyVanilla(material, game.DiffuseOf(material));
        var dims = shipped.TryGetValue(diffuse, out var diffuseFile) ? diffuseFile.Dimensions() : null;
        dims ??= slot != null ? (slot.Width, slot.Height) : game.TextureDimensions(diffuse);
        return new FoundImage { Category = category, Slot = material, MaterialSource = ownMaterial, Swaps = swaps, Width = dims?.Width ?? 0, Height = dims?.Height ?? 0 };
    }

    /// <summary>An interface file a mod ships over one of the game's own: its source path, the game path it replaces
    /// (relative to Data) and the pick it follows.</summary>
    public sealed record InterfaceFile(string Source, string GamePath, string Category);

    /// <summary>Every interface file the mod ships over a file of the game's own interface. The main menu follows the corner
    /// box, Pip-Boy movies the Pip-Boy screen list, the loading menu the loading screens, the rest the Other replacers shares.</summary>
    public static List<InterfaceFile> FindInterfaceFiles(Source source, GameFiles game)
    {
        var list = new List<InterfaceFile>();
        foreach (var path in source.Files.Keys)
        {
            if (!path.StartsWith("interface\\", StringComparison.OrdinalIgnoreCase) || !game.HasGameFile(path)) continue;
            var name = Path.GetFileName(path).ToLowerInvariant();
            var category = name.StartsWith("mainmenu", StringComparison.Ordinal) ? "mainmenu"
                : name.Contains("pipboy", StringComparison.Ordinal) ? "pipboyscreen"
                : name.StartsWith("loadingmenu", StringComparison.Ordinal) ? "loadingscreens"
                : "other";
            list.Add(new InterfaceFile(path, path.ToLowerInvariant(), category));
        }
        return list;
    }

    /// <summary>The main menu theme a mod ships (Music\Special\MUS_MainTheme.xwm, the path the game plays at the menu), as a source path, or null.</summary>
    public static string? FindMenuMusic(Source source)
    {
        return source.Files.Keys.Where(k => Path.ChangeExtension(k, null).Equals("music\\special\\mus_maintheme", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(k).ToLowerInvariant() is ".xwm" or ".wav")
            .OrderBy(k => Path.GetExtension(k).Equals(".xwm", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();
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
                images.Add(new FoundImage { Category = category, Diffuse = file.DataPath, Slot = slot.Length > 0 ? slot : null, Width = dims.Width, Height = dims.Height });
            }
            report.Info($"{source.Name}: manifest lists {Report.Count(images.Count, "image", "images")}");
        }
        catch (Exception e)
        {
            report.Warn($"{source.Name}: cannot read gshuf-pack.json: {e.Message}");
        }
        return images;
    }
}
