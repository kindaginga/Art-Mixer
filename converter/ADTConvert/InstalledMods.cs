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
    /// <summary>The list group the mod is shown in: the kind of art it holds the most of (see <see cref="InstalledMods.Groups"/>).</summary>
    public string Group { get; init; } = "";
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
    /// <summary>The converter list's groups in display order: each art category's group name, the main menu files, props and set dressing, and mods that are mostly not art last.</summary>
    public static readonly string[] Groups = ["Paintings", "Posters", "Signage", "Magazines", "Quest", "Pip-Boy", "World map", "Main menu", PropsGroup, NotArtGroup];
    public const string PropsGroup = "Props and set dressing";
    /// <summary>The group of a mod most of whose changes to the game are not art, such as a graphics overhaul, an item sorter or a keyword framework.</summary>
    public const string NotArtGroup = "Mostly not art";

    /// <summary>The list group of an art category.</summary>
    public static string GroupOf(string category) => category switch
    {
        "paintings" => "Paintings",
        "posters" => "Posters",
        "signage" => "Signage",
        "magazines" or "loadingscreens" or "magazinebacks" => "Magazines",
        "quest" => "Quest",
        "pipboyscreen" or "pipboyskin" => "Pip-Boy",
        "pipboymap" => "World map",
        "mainmenu" => "Main menu",
        _ => PropsGroup,
    };

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
                Group = tally.Group,
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

        /// <summary>Whether a texture, material or model path (relative to Data) names a file the game ships; false without a game folder.</summary>
        public bool IsGameFile(string relative) => _art != null && _art.Contains(relative);

        /// <summary>Whether a model path (relative to Meshes or to Data) names a model the game ships; false without a game folder.</summary>
        public bool IsGameMesh(string model) => _art != null && model.Length > 0 && _art.Contains("meshes\\" + Paths.Normalize(model, "meshes"));

        /// <summary>The art category of a game object a plugin's version gives another model or material swap, or other bounds for an
        /// object that is not a prop, or null when it changes none (a name change alone is not art); without a game folder, the
        /// category of the plugin's model when it is at an art path.</summary>
        public string? ObjectCategory(PluginFile.ModelRecord record)
        {
            if (_files.Value is not { } files) return Scanner.IsArtPath(record.Model) ? Scanner.ClassifyVanilla(record.Model, "") : null;
            files.IndexGameRecords([record.Type]);
            if (files.GameModelRecord(record.Key) is not { } game || !Scanner.InScope(Scanner.ClassifyVanilla(game.Model, ""), game.Model)) return null;
            var category = Scanner.ClassifyVanilla(game.Model, "");
            if (!IsGameMesh(record.Model) || record.Swap is { } own && !IsGamePlugin(own.File)) return category;
            if (!string.Equals(game.Model, record.Model, StringComparison.OrdinalIgnoreCase) || !Nullable.Equals(game.Swap, record.Swap)) return category;
            // Returns null for a prop or set dressing object whose model and swap stay the game's.
            if (category == "other") return null;
            var bounds = record.FieldData("OBND");
            return bounds is { Length: 12 } && !bounds.AsSpan().SequenceEqual(game.FieldData("OBND") ?? []) ? category : null;
        }

        /// <summary>The art category of the first art entry a plugin's version of a game material swap shows another material for, as the converter converts it, or null; "other" without a game folder.</summary>
        public string? SwapCategory(FormKey key, List<PluginFile.Substitution> substitutions)
        {
            if (_files.Value is not { } files) return "other";
            files.IndexGameRecords(["MSWP", "TXST"]);
            if (files.GameMaterialSwap(key) is not { } game) return null;
            return Scanner.ChangedSwapEntries(game, substitutions).Select(e => (Category: Scanner.ClassifyVanilla(e.Game.Original, ""), e.Game.Original)).Where(e => Scanner.InScope(e.Category, e.Original)).Select(e => e.Category).FirstOrDefault();
        }

        /// <summary>The art category of the first art index a plugin's version of a game texture set names another texture at, as the converter converts it, or null; "other" without a game folder.</summary>
        public string? SetCategory(FormKey key, string[] textures)
        {
            if (_files.Value is not { } files) return "other";
            files.IndexGameRecords(["MSWP", "TXST"]);
            if (files.GameTextureSet(key) is not { } set) return null;
            return Scanner.ChangedSetIndexes(set, textures).Select(i => (Category: Scanner.ByNameCategory(set[i]), Path: set[i])).Where(e => Scanner.InScope(e.Category, e.Path)).Select(e => e.Category).FirstOrDefault();
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
        private readonly Dictionary<string, int> _groups = [];
        private void Count(string group) => _groups[group] = _groups.GetValueOrDefault(group) + 1;
        // Game textures, materials and models the mod replaces, and how many of those are art.
        private int _replaced, _replacedArt;
        // Game objects, material swaps and texture sets the mod's plugins carry their own version of.
        private int _records;
        /// <summary>Mostly not art when fewer than half of what the mod changes of the game (the textures, materials and models it replaces and the
        /// records its plugins carry) is art; otherwise the group of the kind of art the mod holds the most of, the first in list order on a tie.</summary>
        public string Group => _replaced + _records > 0 && (_replacedArt + GameRecords) * 2 < _replaced + _records ? NotArtGroup
            : _groups.Count == 0 ? PropsGroup : Groups.Where(_groups.ContainsKey).OrderByDescending(g => _groups[g]).First();
        /// <summary>Game objects, material swaps and texture sets the mod's plugins change the look of.</summary>
        public int GameRecords { get; private set; }
        /// <summary>Whether the files include Art Distribution Tool's own: its plugin or anything in an ADT folder.</summary>
        public bool Own { get; private set; }
        public bool Any => GameRecords > 0 || _counts.Any(c => c > 0);

        private readonly List<(string Name, byte[] Data)> _plugins = [];

        /// <summary>Keeps a plugin for <see cref="Finish"/>.</summary>
        public void AddPlugin(string name, byte[] data) => _plugins.Add((name, data));

        /// <summary>Counts the game's own objects, material swaps and texture sets the kept plugins change the look of, as the converter reads them.</summary>
        public void Finish(Report report)
        {
            foreach (var (name, data) in _plugins)
            {
                try
                {
                    var plugin = PluginFile.Read(name, data);
                    var models = plugin.Models.Where(m => IsGamePlugin(m.Key.File)).ToList();
                    var swaps = plugin.MaterialSwaps.Where(kv => IsGamePlugin(kv.Key.File)).ToList();
                    var sets = plugin.TextureSets.Where(kv => IsGamePlugin(kv.Key.File)).ToList();
                    _records += models.Count + swaps.Count + sets.Count;
                    var categories = models.Select(game.ObjectCategory)
                        .Concat(swaps.Select(kv => game.SwapCategory(kv.Key, kv.Value)))
                        .Concat(sets.Select(kv => game.SetCategory(kv.Key, kv.Value)));
                    foreach (var category in categories.OfType<string>())
                    {
                        GameRecords++;
                        Count(GroupOf(category));
                    }
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
                var relative = string.Join('\\', parts[i..]);
                if (Kinds[kind].Root is "textures" or "materials" or "meshes" && Kinds[kind].Extensions.Contains(extension) && game.IsGameFile(relative))
                {
                    _replaced++;
                    if (Scanner.IsArtPath(string.Join('\\', parts[(i + 1)..]))) _replacedArt++;
                }
                if (Kinds[kind].Extensions.Contains(extension) && game.Takes(Kinds[kind].Root, relative))
                {
                    _counts[kind]++;
                    var inner = string.Join('\\', parts[(i + 1)..]);
                    Count(Kinds[kind].Root is "video" or "music" or "interface" ? "Main menu" : GroupOf(Scanner.ByNameCategory(inner)));
                }
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
