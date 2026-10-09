using System.Text;
using System.Text.Json;
using MaterialLib;

namespace ArtMixerConvert;

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

    /// <summary>What the model index cache holds: the inline shaders and the sky shader textures of the game's models, and per
    /// model (outside the precombined meshes) the material files and inline shader keys it draws with.</summary>
    private sealed record ModelIndex(List<InlineUse> Shaders, List<string> Sky, Dictionary<string, ModelUse> Models);

    /// <summary>The material files (relative to Materials) and inline shader keys (see <see cref="InlineUse"/>) one model draws with.</summary>
    public sealed record ModelUse(List<string> Materials, List<string> Inline);
    private Dictionary<string, ModelUse>? _models;

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

    /// <summary>Every game model (relative to Meshes, outside the precombined meshes) with the materials and inline shaders it draws with.</summary>
    public IReadOnlyDictionary<string, ModelUse> Models
    {
        get
        {
            EnsureInlineIndex();
            return _models!;
        }
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
        var cacheFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArtMixer");
        var cache = Path.Combine(cacheFolder, $"model-index-v3-{Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(identity)))[..12]}.json");
        if (File.Exists(cache))
        {
            try
            {
                var index = JsonSerializer.Deserialize<ModelIndex>(File.ReadAllBytes(cache)) ?? new ModelIndex([], [], []);
                _inline = index.Shaders.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);
                _models = new Dictionary<string, ModelUse>(index.Models ?? [], StringComparer.OrdinalIgnoreCase);
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
        _models = new Dictionary<string, ModelUse>(StringComparer.OrdinalIgnoreCase);
        var models = 0;
        foreach (var archive in _meshes)
        {
            archive.ForEachGeneral(p => p.EndsWith(".nif", StringComparison.OrdinalIgnoreCase), (path, bytes) =>
            {
                models++;
                var precombined = path.Contains("\\precombined\\", StringComparison.OrdinalIgnoreCase);
                var shaders = NifFile.InlineShaders(bytes);
                if (!precombined)
                {
                    var materials = NifFile.References(bytes).Where(NifFile.NamesMaterialFile).Select(m => Paths.Normalize(m, "materials")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var keys = shaders.Select(s => s.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (materials.Count > 0 || keys.Count > 0) _models[Paths.Normalize(path, "meshes")] = new ModelUse(materials, keys);
                }
                foreach (var shader in shaders)
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
            File.WriteAllBytes(cache, JsonSerializer.SerializeToUtf8Bytes(new ModelIndex(_inline.Values.ToList(), _skyTextures.Order(StringComparer.Ordinal).ToList(), _models)));
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

    /// <summary>The Commonwealth world map data (MNAM: usable width, height, NW cell x, y, SE cell x, y) the game's own plugins
    /// give the world, the last one's when several do, so Vanilla puts the game's world map back with its own cells.</summary>
    public int[]? GameWorldMap()
    {
        if (_gameWorldMapRead) return _gameWorldMap;
        _gameWorldMapRead = true;
        var commonwealth = new FormKey("Fallout4.esm", 0x3C);
        foreach (var master in GameMasters())
        {
            try
            {
                if (PluginFile.Read(master, File.ReadAllBytes(Path.Combine(_dataFolder, master)), label => label == "WRLD", worldChildren: false).WorldMaps.TryGetValue(commonwealth, out var map)) _gameWorldMap = map;
            }
            catch (Exception e)
            {
                _report.Warn($"cannot read the world map data of {master}: {e.Message}");
            }
        }
        return _gameWorldMap;
    }
    private int[]? _gameWorldMap;
    private bool _gameWorldMapRead;

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

    /// <summary>Every material swap of the game's own plugins.</summary>
    public IEnumerable<List<PluginFile.Substitution>> GameMaterialSwaps()
    {
        IndexGameRecords(["MSWP"]);
        return _gameSwaps.Values;
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

    public byte[]? ReadMaterial(string relative) => Read(_materials, "materials\\" + relative);
    public byte[]? ReadTexture(string relative) => Read(_textures, "textures\\" + relative) ?? Read(_interface, "textures\\" + relative);
    /// <summary>The file name of the texture archive ReadTexture takes a texture from, or null when it comes from another kind of archive.</summary>
    public string? TextureArchive(string relative)
    {
        var archive = _textures.FirstOrDefault(a => a.Contains("textures\\" + relative));
        return archive is { IsTextureArchive: true } ? Path.GetFileName(archive.Path) : null;
    }
    public byte[]? ReadMesh(string relative) => Read(_meshes, "meshes\\" + relative);
    /// <summary>The file name of the archive ReadMesh takes a model from, or null when no archive has it.</summary>
    public string? MeshArchive(string relative) => _meshes.FirstOrDefault(a => a.Contains("meshes\\" + relative)) is { } archive ? Path.GetFileName(archive.Path) : null;
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
