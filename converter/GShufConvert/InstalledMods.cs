using SharpCompress.Archives;

namespace GShufConvert;

/// <summary>An installed mod folder or a downloaded archive that holds files the converter reads.</summary>
public sealed class InstalledMod
{
    public required string Name { get; init; }
    /// <summary>The mod folder or archive, as the converter takes it for an input.</summary>
    public required string Location { get; init; }
    public required bool IsDownload { get; init; }
    /// <summary>Mod Organizer 2 mod folders: whether the active profile enables the mod. Null elsewhere.</summary>
    public bool? Enabled { get; init; }
    /// <summary>What the converter reads in it, for example "412 textures, 37 materials, 2 models".</summary>
    public required string Summary { get; init; }
    public string Where => IsDownload ? "download" : "mod folder";
    public string Status => Enabled switch { true => "enabled", false => "disabled", null => "" };
    public override string ToString() => Name;
}

/// <summary>What a search found, with how many mod folders and downloads it looked through.</summary>
public sealed record InstalledScan(List<InstalledMod> Mods, int ModFolders, int Downloads);

/// <summary>Finds the art mods a Mod Organizer 2 instance or Vortex already holds, in its mod folders and its downloads, by
/// file names only: loose files, BA2 name tables and archive entry lists.</summary>
public static class InstalledMods
{
    // The Data folders the converter reads, the file types it takes from each, and the words the summary counts them in.
    private static readonly (string Root, string[] Extensions, string One, string Many)[] Kinds =
    [
        ("textures", [".dds"], "texture", "textures"),
        ("materials", [".bgsm", ".bgem"], "material", "materials"),
        ("meshes", [".nif"], "model", "models"),
        ("video", [".bk2"], "video", "videos"),
        ("music", [".xwm", ".wav", ".fuz"], "music file", "music files"),
        ("sound", [".xwm", ".wav", ".fuz"], "sound", "sounds"),
        ("interface", [".swf"], "interface file", "interface files"),
    ];
    private static readonly string[] ArchiveExtensions = [".zip", ".7z", ".rar"];
    private static readonly EnumerationOptions Recursive = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };

    /// <summary>Every mod folder and downloaded archive of the destination that holds textures, materials, models, videos, music,
    /// sounds or interface files (loose or in BA2 archives), except Gallery Shuffle's own mods. Mod folders first, then
    /// downloads, each by name.</summary>
    public static InstalledScan Find(Destination destination, Report report, Action<string>? progress = null)
    {
        var profile = destination.Kind == "mo2" ? ReadModList(destination.ProfileFolder, report) : null;
        var folders = List(destination.ModsFolder, Directory.EnumerateDirectories, report);
        var downloads = List(destination.DownloadsFolder, folder => Directory.EnumerateFiles(folder).Where(f => ArchiveExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())), report);
        var game = new GameNames(destination.GameFolder, report);
        var found = new List<InstalledMod>();
        // Downloads an installed mod came from: MO2 records the archive in the mod's meta.ini, Vortex names the staging folder after it.
        var installedFrom = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < folders.Count; i++)
        {
            var name = Path.GetFileName(folders[i]);
            progress?.Invoke($"Looking through mod folder {i + 1} of {folders.Count}: {name}");
            installedFrom.Add(destination.Kind == "mo2" ? InstallationFile(folders[i]) ?? "" : name);
            var tally = TallyFolder(folders[i], game, report);
            if (tally == null || !tally.Any) continue;
            found.Add(new InstalledMod
            {
                Name = destination.Kind == "mo2" ? name : Sources.DisplayName(name),
                Location = folders[i],
                IsDownload = false,
                Enabled = profile?.GetValueOrDefault(name),
                Summary = tally.Summary,
            });
        }
        for (var i = 0; i < downloads.Count; i++)
        {
            var name = Path.GetFileName(downloads[i]);
            if (installedFrom.Contains(name) || installedFrom.Contains(Path.GetFileNameWithoutExtension(name))) continue;
            progress?.Invoke($"Looking through download {i + 1} of {downloads.Count}: {name}");
            var tally = TallyArchive(downloads[i], game, report);
            if (tally == null || !tally.Any) continue;
            found.Add(new InstalledMod { Name = name, Location = downloads[i], IsDownload = true, Summary = tally.Summary });
        }
        var sorted = found.OrderBy(m => m.IsDownload).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new InstalledScan(sorted, folders.Count, downloads.Count);
    }

    /// <summary>Counts what the converter reads in a mod folder: loose files under the Data folders it reads, and the names
    /// inside the folder's BA2 archives. Null for Gallery Shuffle's own mods and for an unreadable folder.</summary>
    private static Tally? TallyFolder(string folder, GameNames game, Report report)
    {
        if (Path.GetFileName(folder).Equals(Conversion.OutputModName, StringComparison.OrdinalIgnoreCase)) return null;
        if (File.Exists(Path.Combine(folder, "GalleryShuffle.esl")) || Directory.Exists(Path.Combine(folder, "GShuf"))) return null;
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

    /// <summary>Counts the files the converter reads in a download by entry name and inner BA2 name table; null for Gallery Shuffle's own or an unreadable archive.</summary>
    private static Tally? TallyArchive(string path, GameNames game, Report report)
    {
        if (Path.GetFileNameWithoutExtension(path).Equals(Conversion.OutputModName, StringComparison.OrdinalIgnoreCase)) return null;
        var tally = new Tally(game);
        try
        {
            using var archive = ArchiveFactory.OpenArchive(path);
            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory) continue;
                var key = entry.Key ?? "";
                if (key.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var stream = entry.OpenEntryStream();
                        foreach (var inner in Ba2Archive.ReadNames(stream)) tally.Add(inner);
                    }
                    catch (Exception e) when (e is InvalidDataException or EndOfStreamException or IOException or NotSupportedException or ArgumentException
                        or SharpCompress.Common.SharpCompressException or System.Security.Cryptography.CryptographicException)
                    {
                        report.Warn($"{Path.GetFileName(path)}: cannot read the file names in {Path.GetFileName(key.Replace('\\', '/'))}: {e.Message}");
                        tally.Archives++;
                    }
                }
                else if (IsPlugin(key) || IsStringTable(key))
                {
                    using var stream = entry.OpenEntryStream();
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    var entryName = Path.GetFileName(key.Replace('\\', '/'));
                    if (IsPlugin(key)) tally.AddPlugin(entryName, buffer.ToArray());
                    else tally.AddStrings(entryName, buffer.ToArray());
                }
                else tally.Add(key);
            }
        }
        catch (Exception e)
        {
            report.Warn($"cannot list the files in {Path.GetFileName(path)}: {e.Message}");
            return null;
        }
        tally.Finish(report);
        return tally.Own ? null : tally;
    }

    private static bool IsPlugin(string path) => Path.GetExtension(path).ToLowerInvariant() is ".esp" or ".esm" or ".esl";

    private static bool IsStringTable(string path) => Path.GetExtension(path).ToLowerInvariant() is ".strings" or ".dlstrings" or ".ilstrings";

    /// <summary>Whether a plugin file name is the game's own: the base game, a DLC or a Creation Club plugin.</summary>
    private static bool IsGamePlugin(string name) =>
        name.Equals("Fallout4.esm", StringComparison.OrdinalIgnoreCase) || (name.StartsWith("DLC", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".esm", StringComparison.OrdinalIgnoreCase))
        || name.StartsWith("cc", StringComparison.OrdinalIgnoreCase);

    /// <summary>The download a Mod Organizer 2 mod was installed from (the file name in its meta.ini installationFile), or null.</summary>
    private static string? InstallationFile(string folder)
    {
        var meta = Path.Combine(folder, "meta.ini");
        if (!File.Exists(meta)) return null;
        try
        {
            var line = File.ReadLines(meta).FirstOrDefault(l => l.StartsWith("installationFile=", StringComparison.OrdinalIgnoreCase));
            var value = line?["installationFile=".Length..].Trim().Trim('"').Replace("\\\\", "\\").Replace('/', '\\');
            return string.IsNullOrEmpty(value) ? null : Path.GetFileName(value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Names of the game's own interface, sound and music files, which decide whether a mod's file of those kinds
    /// replaces one; without a game folder every such file counts.</summary>
    private sealed class GameNames
    {
        private readonly HashSet<string>? _interface;
        private readonly HashSet<string>? _audio;
        private readonly HashSet<string>? _art;
        private readonly Lazy<GameFiles?> _files;

        public GameNames(string? gameFolder, Report report)
        {
            _files = new Lazy<GameFiles?>(() => gameFolder != null && File.Exists(Path.Combine(gameFolder, "Fallout4.exe")) ? new GameFiles(gameFolder, report) : null);
            var data = gameFolder == null ? null : Path.Combine(gameFolder, "Data");
            if (data == null || !Directory.Exists(data)) return;
            _interface = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _audio = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _art = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(data, "*.ba2"))
            {
                var name = Path.GetFileName(path);
                // Only the game's own archives (base game, DLC, Creation Club), not archives of mods deployed into Data.
                if (!name.StartsWith("Fallout4 - ", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("DLC", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("cc", StringComparison.OrdinalIgnoreCase)) continue;
                var isInterface = name.Equals("Fallout4 - Interface.ba2", StringComparison.OrdinalIgnoreCase);
                var isAudio = name.Contains(" - Sounds", StringComparison.OrdinalIgnoreCase) || name.Contains(" - Voices", StringComparison.OrdinalIgnoreCase)
                    || name.Contains(" - Startup", StringComparison.OrdinalIgnoreCase) || name.EndsWith(" - Main.ba2", StringComparison.OrdinalIgnoreCase);
                try
                {
                    foreach (var inner in new Ba2Archive(path).Paths)
                    {
                        var normal = inner.Replace('/', '\\');
                        if (normal.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase) || normal.StartsWith("materials\\", StringComparison.OrdinalIgnoreCase) || normal.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase)) _art.Add(normal);
                        if (isInterface) _interface.Add(normal);
                        if (isAudio && (normal.StartsWith("sound\\", StringComparison.OrdinalIgnoreCase) || normal.StartsWith("music\\", StringComparison.OrdinalIgnoreCase))) _audio.Add(Path.ChangeExtension(normal, null));
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

        /// <summary>Whether a plugin's version of a game object gives it another model, material swap, name or bounds; true without a game folder.</summary>
        public bool ChangesObject(PluginFile plugin, PluginFile.ModelRecord record, Func<uint, string?> pluginStrings)
        {
            if (!IsGameMesh(record.Model) || record.Swap is { } own && !IsGamePlugin(own.File)) return true;
            if (_files.Value is not { } files) return true;
            files.IndexGameRecords([record.Type]);
            if (files.GameModelRecord(record.Key) is not { } game) return false;
            if (!string.Equals(game.Model, record.Model, StringComparison.OrdinalIgnoreCase) || !Nullable.Equals(game.Swap, record.Swap)) return true;
            var bounds = record.FieldData("OBND");
            if (bounds is { Length: 12 } && !bounds.AsSpan().SequenceEqual(game.FieldData("OBND") ?? [])) return true;
            var name = PluginFile.Text(record.FieldData("FULL"), plugin.Localized, pluginStrings);
            return !string.IsNullOrEmpty(name) && name != PluginFile.Text(game.FieldData("FULL"), files.IsLocalized(game.Source), id => files.GameString(game.Source, id));
        }

        /// <summary>Whether a plugin's version of a game material swap shows another material for an entry, as the converter converts it; true without a game folder.</summary>
        public bool ChangesSwap(FormKey key, List<PluginFile.Substitution> substitutions)
        {
            if (_files.Value is not { } files) return true;
            files.IndexGameRecords(["MSWP", "TXST"]);
            return files.GameMaterialSwap(key) is { } game && Scanner.ChangedSwapEntries(game, substitutions).Any();
        }

        /// <summary>Whether a plugin's version of a game texture set names another texture at an index, as the converter converts it; true without a game folder.</summary>
        public bool ChangesSet(FormKey key, string[] textures)
        {
            if (_files.Value is not { } files) return true;
            files.IndexGameRecords(["MSWP", "TXST"]);
            return files.GameTextureSet(key) is { } set && Scanner.ChangedSetIndexes(set, textures).Any();
        }

        /// <summary>Whether a mod file (relative to Data) of the given kind is one the converter takes.</summary>
        public bool Takes(string root, string relative)
        {
            return root switch
            {
                "interface" => _interface == null || _interface.Contains(relative),
                "video" => relative.Equals("video\\mainmenuloop.bk2", StringComparison.OrdinalIgnoreCase),
                "music" or "sound" => _audio == null || _audio.Contains(Path.ChangeExtension(relative, null)) || relative.Equals("music\\special\\mus_maintheme.xwm", StringComparison.OrdinalIgnoreCase),
                _ => _art == null || _art.Contains(relative),
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
        public int Archives { get; set; }
        /// <summary>Game objects, material swaps and texture sets the mod's plugins change the look of.</summary>
        public int GameRecords { get; private set; }
        /// <summary>Whether the files include Gallery Shuffle's own: its plugin or anything in a GShuf folder.</summary>
        public bool Own { get; private set; }
        public bool Any => Archives > 0 || GameRecords > 0 || _counts.Any(c => c > 0);

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
            if (parts[^1].Equals("GalleryShuffle.esl", StringComparison.OrdinalIgnoreCase) || parts.Any(p => p.Equals("gshuf", StringComparison.OrdinalIgnoreCase)))
            {
                Own = true;
                return;
            }
            var extension = Path.GetExtension(parts[^1]).ToLowerInvariant();
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var kind = Array.FindIndex(Kinds, k => k.Root.Equals(parts[i], StringComparison.OrdinalIgnoreCase));
                if (kind < 0) continue;
                // Character mods (faces, skin, hair, eyes) and LooksMenu's own menu file are not art mods, so they do not count.
                if (i + 2 < parts.Length && parts[i + 1].Equals("actors", StringComparison.OrdinalIgnoreCase) && parts[i + 2].Equals("character", StringComparison.OrdinalIgnoreCase)) return;
                if (Kinds[kind].Root == "interface" && parts[^1].Equals("looksmenu.swf", StringComparison.OrdinalIgnoreCase)) return;
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
                if (Archives > 0) parts.Add(Report.Count(Archives, "BA2 archive", "BA2 archives"));
                return string.Join(", ", parts);
            }
        }
    }
}
