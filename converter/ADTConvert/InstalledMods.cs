namespace ADTConvert;

/// <summary>An installed mod that holds art the converter takes.</summary>
public sealed class InstalledMod
{
    public required string Name { get; init; }
    /// <summary>The mod's folder, as the converter takes it for an input.</summary>
    public required string Location { get; init; }
    /// <summary>Mod Organizer 2 mod folders: whether the active profile enables the mod. Null elsewhere.</summary>
    public bool? Enabled { get; init; }
    /// <summary>What the converter reads in it, for example "412 textures, 37 materials, 2 models".</summary>
    public required string Summary { get; init; }
    /// <summary>The Status column's text when it is not the enabled state: "converted" for a mod listed from the packs.</summary>
    public string? StatusText { get; init; }
    public string Status => StatusText ?? Enabled switch { true => "enabled", false => "disabled", null => "" };
    public override string ToString() => Name;
}

/// <summary>What a search found, with how many installed mods it looked through.</summary>
public sealed record InstalledScan(List<InstalledMod> Mods, int ModFolders);

/// <summary>Finds the art mods installed in a Mod Organizer 2 instance or Vortex, by file names only: loose files and BA2 name tables.</summary>
public static class InstalledMods
{
    // The Data folders the converter reads, the file types it takes from each, and the words the summary counts them in.
    private static readonly (string Root, string[] Extensions, string One, string Many)[] Kinds =
    [
        ("textures", [".dds"], "texture", "textures"),
        ("materials", [".bgsm", ".bgem"], "material", "materials"),
        ("meshes", [".nif"], "model", "models"),
        ("video", [".bk2"], "video", "videos"),
        ("music", [".xwm", ".wav"], "music file", "music files"),
        ("interface", [".swf"], "interface file", "interface files"),
    ];
    private static readonly EnumerationOptions Recursive = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };

    /// <summary>Every installed mod of the destination that holds art the converter takes (loose or in BA2 archives), except
    /// Art Distribution Tool's own mods, by name.</summary>
    public static InstalledScan Find(Destination destination, Report report, Action<string>? progress = null)
    {
        var profile = destination.Kind == "mo2" ? ReadModList(destination.ProfileFolder, report) : null;
        var folders = List(destination.ModsFolder, Directory.EnumerateDirectories, report);
        var game = new GameNames(destination.GameFolder, report);
        var found = new List<InstalledMod>();
        for (var i = 0; i < folders.Count; i++)
        {
            var name = Path.GetFileName(folders[i]);
            progress?.Invoke($"Looking through installed mod {i + 1} of {folders.Count}: {name}");
            var tally = TallyFolder(folders[i], game, report);
            if (tally == null || !tally.Any) continue;
            found.Add(new InstalledMod
            {
                Name = destination.Kind == "mo2" ? name : Sources.DisplayName(name),
                Location = folders[i],
                Enabled = profile?.GetValueOrDefault(name),
                Summary = tally.Summary,
            });
        }
        return new InstalledScan(found.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList(), folders.Count);
    }

    /// <summary>Counts the art the converter takes from a mod folder: loose files under the Data folders it reads, and the names
    /// inside the folder's BA2 archives. Null for Art Distribution Tool's own mods and for an unreadable folder.</summary>
    private static Tally? TallyFolder(string folder, GameNames game, Report report)
    {
        if (Path.GetFileName(folder).Equals(Conversion.OutputModName, StringComparison.OrdinalIgnoreCase)) return null;
        if (File.Exists(Path.Combine(folder, "ArtDistributionTool.esl")) || Directory.Exists(Path.Combine(folder, "ADT"))) return null;
        var tally = new Tally(game);
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(folder))
            {
                if (!Kinds.Any(k => k.Root.Equals(Path.GetFileName(dir), StringComparison.OrdinalIgnoreCase))) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*", Recursive)) tally.Add(Path.GetRelativePath(folder, file));
            }
            foreach (var archive in Directory.EnumerateFiles(folder, "*.ba2"))
            {
                try
                {
                    foreach (var inner in new Ba2Archive(archive).Paths) tally.Add(inner);
                }
                catch (Exception e)
                {
                    report.Warn($"{Path.GetFileName(folder)}: cannot read {Path.GetFileName(archive)}: {e.Message}");
                }
            }
            foreach (var plugin in Directory.EnumerateFiles(folder).Where(IsPlugin)) tally.AddPlugin(Path.GetFileName(plugin), File.ReadAllBytes(plugin));
            var strings = Path.Combine(folder, "Strings");
            if (Directory.Exists(strings))
            {
                foreach (var table in Directory.EnumerateFiles(strings).Where(IsStringTable)) tally.AddStrings(Path.GetFileName(table), File.ReadAllBytes(table));
            }
            tally.Finish(report);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            report.Warn($"cannot look through {folder}: {e.Message}");
            return null;
        }
        return tally.Own ? null : tally;
    }

    private static bool IsPlugin(string path) => Path.GetExtension(path).ToLowerInvariant() is ".esp" or ".esm" or ".esl";

    private static bool IsStringTable(string path) => Path.GetExtension(path).ToLowerInvariant() is ".strings" or ".dlstrings" or ".ilstrings";

    /// <summary>Whether a plugin file name is the game's own: the base game, a DLC or a Creation Club plugin.</summary>
    private static bool IsGamePlugin(string name) =>
        name.Equals("Fallout4.esm", StringComparison.OrdinalIgnoreCase) || (name.StartsWith("DLC", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".esm", StringComparison.OrdinalIgnoreCase))
        || name.StartsWith("cc", StringComparison.OrdinalIgnoreCase);

    /// <summary>Names of the game's own interface, sound and music files, which decide whether a mod's file of those kinds
    /// replaces one; without a game folder every such file counts.</summary>
    private sealed class GameNames
    {
        private readonly HashSet<string>? _interface;
        private readonly HashSet<string>? _art;
        private readonly Lazy<GameFiles?> _files;

        public GameNames(string? gameFolder, Report report)
        {
            _files = new Lazy<GameFiles?>(() => gameFolder != null && File.Exists(Path.Combine(gameFolder, "Fallout4.exe")) ? new GameFiles(gameFolder, report) : null);
            var data = gameFolder == null ? null : Path.Combine(gameFolder, "Data");
            if (data == null || !Directory.Exists(data)) return;
            _interface = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _art = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(data, "*.ba2"))
            {
                var name = Path.GetFileName(path);
                // Only the game's own archives (base game, DLC, Creation Club), not archives of mods deployed into Data.
                if (!name.StartsWith("Fallout4 - ", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("DLC", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("cc", StringComparison.OrdinalIgnoreCase)) continue;
                var isInterface = name.Equals("Fallout4 - Interface.ba2", StringComparison.OrdinalIgnoreCase);
                try
                {
                    foreach (var inner in new Ba2Archive(path).Paths)
                    {
                        var normal = inner.Replace('/', '\\');
                        if (normal.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase) || normal.StartsWith("materials\\", StringComparison.OrdinalIgnoreCase) || normal.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase)) _art.Add(normal);
                        if (isInterface) _interface.Add(normal);
                    }
                }
                catch (Exception e)
                {
                    report.Warn($"cannot read {name}: {e.Message}");
                }
            }
        }

        /// <summary>Whether a model path (relative to Meshes or to Data) names a model the game ships; false without a game folder.</summary>
        public bool IsGameMesh(string model) => _art != null && model.Length > 0 && _art.Contains("meshes\\" + Paths.Normalize(model, "meshes"));

        /// <summary>The game's language, as its string tables name it ("en" without a game folder).</summary>
        public string Language => _files.Value?.Language ?? "en";

        /// <summary>Whether a plugin's version of a game art object gives it another model, material swap, name or bounds; without a
        /// game folder, whether the plugin's model is at an art path.</summary>
        public bool ChangesObject(PluginFile plugin, PluginFile.ModelRecord record, Func<uint, string?> pluginStrings)
        {
            if (_files.Value is not { } files) return Scanner.IsArtPath(record.Model);
            files.IndexGameRecords([record.Type]);
            if (files.GameModelRecord(record.Key) is not { } game || !Scanner.InScope(Scanner.ClassifyVanilla(game.Model, ""), game.Model)) return false;
            if (!IsGameMesh(record.Model) || record.Swap is { } own && !IsGamePlugin(own.File)) return true;
            if (!string.Equals(game.Model, record.Model, StringComparison.OrdinalIgnoreCase) || !Nullable.Equals(game.Swap, record.Swap)) return true;
            var bounds = record.FieldData("OBND");
            if (bounds is { Length: 12 } && !bounds.AsSpan().SequenceEqual(game.FieldData("OBND") ?? [])) return true;
            var name = PluginFile.Text(record.FieldData("FULL"), plugin.Localized, pluginStrings);
            return !string.IsNullOrEmpty(name) && name != PluginFile.Text(game.FieldData("FULL"), files.IsLocalized(game.Source), id => files.GameString(game.Source, id));
        }

        /// <summary>Whether a plugin's version of a game material swap shows another material for an art entry, as the converter converts it; true without a game folder.</summary>
        public bool ChangesSwap(FormKey key, List<PluginFile.Substitution> substitutions)
        {
            if (_files.Value is not { } files) return true;
            files.IndexGameRecords(["MSWP", "TXST"]);
            return files.GameMaterialSwap(key) is { } game && Scanner.ChangedSwapEntries(game, substitutions).Any(e => Scanner.InScope(Scanner.ClassifyVanilla(e.Game.Original, ""), e.Game.Original));
        }

        /// <summary>Whether a plugin's version of a game texture set names another texture at an art index, as the converter converts it; true without a game folder.</summary>
        public bool ChangesSet(FormKey key, string[] textures)
        {
            if (_files.Value is not { } files) return true;
            files.IndexGameRecords(["MSWP", "TXST"]);
            return files.GameTextureSet(key) is { } set && Scanner.ChangedSetIndexes(set, textures).Any(i => Scanner.InScope(Scanner.ByNameCategory(set[i]), set[i]));
        }

        /// <summary>Whether a mod file (relative to Data) of the given kind is art the converter takes: a main menu
        /// interface file, the menu video or theme, or a texture, material or model of the game's at an art path.</summary>
        public bool Takes(string root, string relative)
        {
            var inner = relative[(relative.IndexOf('\\') + 1)..];
            return root switch
            {
                "interface" => (_interface == null || _interface.Contains(relative)) && Scanner.InterfaceCategory(Path.GetFileName(relative)) != "other",
                "video" => relative.Equals("video\\mainmenuloop.bk2", StringComparison.OrdinalIgnoreCase),
                "music" => Path.ChangeExtension(relative, null).Equals("music\\special\\mus_maintheme", StringComparison.OrdinalIgnoreCase),
                _ => (_art == null || _art.Contains(relative)) && Scanner.IsArtPath(inner),
            };
        }
    }

    /// <summary>Mod folder name to whether the profile enables it, from the profile's modlist.txt ("+" enabled, "-" disabled).
    /// Null when the profile has no mod list.</summary>
    private static Dictionary<string, bool>? ReadModList(string? profileFolder, Report report)
    {
        var path = profileFolder == null ? null : Path.Combine(profileFolder, "modlist.txt");
        if (path == null || !File.Exists(path)) return null;
        var states = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length > 1 && line[0] is '+' or '-') states[line[1..].TrimEnd()] = line[0] == '+';
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            report.Warn($"cannot read {path}: {e.Message}");
            return null;
        }
        return states;
    }

    /// <summary>A folder's entries in name order; none when the folder is missing or unreadable.</summary>
    private static List<string> List(string? folder, Func<string, IEnumerable<string>> entries, Report report)
    {
        if (folder == null || !Directory.Exists(folder)) return [];
        try
        {
            return entries(folder).OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            report.Warn($"cannot look through {folder}: {e.Message}");
            return [];
        }
    }

    /// <summary>The files of one mod the converter reads, counted by kind, and the BA2 archives inside a download.</summary>
    private sealed class Tally(GameNames game)
    {
        private readonly int[] _counts = new int[Kinds.Length];
        /// <summary>Game objects, material swaps and texture sets the mod's plugins change the look of.</summary>
        public int GameRecords { get; private set; }
        /// <summary>Whether the files include Art Distribution Tool's own: its plugin or anything in an ADT folder.</summary>
        public bool Own { get; private set; }
        public bool Any => GameRecords > 0 || _counts.Any(c => c > 0);

        private readonly List<(string Name, byte[] Data)> _plugins = [];
        private readonly Dictionary<string, byte[]> _strings = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Keeps a plugin for <see cref="Finish"/>.</summary>
        public void AddPlugin(string name, byte[] data) => _plugins.Add((name, data));

        /// <summary>Keeps a string table (by file name) for the localized plugins.</summary>
        public void AddStrings(string name, byte[] data) => _strings[name] = data;

        /// <summary>Counts the game's own objects, material swaps and texture sets the kept plugins change the look of, as the converter reads them.</summary>
        public void Finish(Report report)
        {
            foreach (var (name, data) in _plugins)
            {
                try
                {
                    var plugin = PluginFile.Read(name, data);
                    var strings = new Dictionary<uint, string>();
                    if (plugin.Localized)
                    {
                        var stem = Path.GetFileNameWithoutExtension(name);
                        foreach (var (ext, prefixed) in new[] { ("strings", false), ("dlstrings", true), ("ilstrings", true) })
                        {
                            if (!_strings.TryGetValue($"{stem}_{game.Language}.{ext}", out var table)) continue;
                            foreach (var (id, text) in PluginFile.ReadStrings(table, prefixed)) strings.TryAdd(id, text);
                        }
                    }
                    GameRecords += plugin.Models.Count(m => IsGamePlugin(m.Key.File) && game.ChangesObject(plugin, m, id => strings.GetValueOrDefault(id)));
                    GameRecords += plugin.MaterialSwaps.Count(kv => IsGamePlugin(kv.Key.File) && game.ChangesSwap(kv.Key, kv.Value));
                    GameRecords += plugin.TextureSets.Count(kv => IsGamePlugin(kv.Key.File) && game.ChangesSet(kv.Key, kv.Value));
                }
                catch (Exception e)
                {
                    report.Warn($"cannot read the plugin {name}: {e.Message}");
                }
            }
            _plugins.Clear();
        }

        /// <summary>Counts one file by its path. The Data folder it sits in may be nested, as in an archive with a Data folder or
        /// installer option folders.</summary>
        public void Add(string path)
        {
            var parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;
            if (parts[^1].Equals("ArtDistributionTool.esl", StringComparison.OrdinalIgnoreCase) || parts.Any(p => p.Equals("adt", StringComparison.OrdinalIgnoreCase)))
            {
                Own = true;
                return;
            }
            var extension = Path.GetExtension(parts[^1]).ToLowerInvariant();
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var kind = Array.FindIndex(Kinds, k => k.Root.Equals(parts[i], StringComparison.OrdinalIgnoreCase));
                if (kind < 0) continue;
                if (Kinds[kind].Extensions.Contains(extension) && game.Takes(Kinds[kind].Root, string.Join('\\', parts[i..]))) _counts[kind]++;
                return;
            }
        }

        public string Summary
        {
            get
            {
                var parts = Kinds.Select((k, i) => (Kind: k, Count: _counts[i])).Where(x => x.Count > 0).Select(x => Report.Count(x.Count, x.Kind.One, x.Kind.Many)).ToList();
                if (GameRecords > 0) parts.Add(Report.Count(GameRecords, "game record a plugin changes the look of", "game records a plugin changes the look of"));
                return string.Join(", ", parts);
            }
        }
    }
}
