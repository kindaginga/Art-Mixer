using System.Text;
using Microsoft.Win32;

namespace ArtMixerConvert;

/// <summary>A place the converted packs can be delivered to.</summary>
public sealed class Destination
{
    public required string Label { get; init; }
    public required string Kind { get; init; }          // "mo2" or "vortex"
    public string? GameFolder { get; init; }
    public string? ModsFolder { get; init; }             // MO2: the instance's mods folder; Vortex: its Fallout 4 staging folder
    public string? DownloadsFolder { get; init; }        // the manager's Fallout 4 downloads folder
    public string? ProfileFolder { get; init; }          // MO2: the instance's active profile
    public string? InstanceFolder { get; init; }         // MO2: the folder holding the instance's ModOrganizer.ini
    public IReadOnlyDictionary<string, bool>? ModStates { get; init; }  // Vortex: whether its Fallout 4 profile enables each mod, by mod id (the staging folder name)
    public override string ToString() => Label;
}

/// <summary>Finds Mod Organizer 2 instances, Vortex and the game folder without asking the player.</summary>
public static class Managers
{
    /// <summary>The destination the converter was started from (installed as a tool inside a manager), if any.</summary>
    public static Destination? Preferred(List<Destination> destinations)
    {
        var here = AppContext.BaseDirectory.TrimEnd('\\');
        foreach (var d in destinations)
        {
            if (d.Kind == "mo2" && d.ModsFolder != null && here.StartsWith(d.ModsFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return d;
        }
        if (here.Contains("\\Vortex\\", StringComparison.OrdinalIgnoreCase)) return destinations.FirstOrDefault(d => d.Kind == "vortex");
        foreach (var d in destinations)
        {
            // Deployed into the game's Data folder (Vortex hard links or a manual install).
            if (d.GameFolder != null && here.StartsWith(d.GameFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return d.Kind == "mo2" ? d : destinations.FirstOrDefault(x => x.Kind == "vortex") ?? d;
        }
        return null;
    }

    /// <summary>The folder Mod Organizer 2 keeps its global instances in, one subfolder per instance.</summary>
    internal static readonly string Mo2Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModOrganizer");

    public static List<Destination> Detect()
    {
        var list = new List<Destination>();
        if (Directory.Exists(Mo2Root))
        {
            foreach (var dir in Directory.EnumerateDirectories(Mo2Root).OrderBy(d => d))
            {
                Destination? instance;
                try { instance = Mo2Instance(dir); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { instance = null; }
                if (instance != null) list.Add(instance);
            }
        }
        // A portable instance keeps ModOrganizer.ini beside ModOrganizer.exe, above the mods folder the converter runs from.
        var above = InstanceAbove(AppContext.BaseDirectory);
        if (above != null && !list.Any(d => SameFolder(d.InstanceFolder, above.InstanceFolder))) list.Add(above);
        var vortex = Vortex(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vortex"));
        if (vortex != null) list.Add(vortex);
        return list;
    }

    /// <summary>The Mod Organizer 2 instance whose ModOrganizer.ini is in this folder, or null when there is none or it is not
    /// a Fallout 4 instance.</summary>
    public static Destination? Mo2Instance(string dir)
    {
        var ini = Path.Combine(dir, "ModOrganizer.ini");
        if (!File.Exists(ini)) return null;
        var values = ReadIni(ini);
        if (!values.TryGetValue("general.gamename", out var game) || !game.Contains("Fallout 4", StringComparison.OrdinalIgnoreCase)) return null;
        var gamePath = values.TryGetValue("general.gamepath", out var gp) ? Unwrap(gp) : null;
        var baseDir = values.TryGetValue("settings.base_directory", out var bd) ? Unwrap(bd) : dir;
        string Folder(string key, string fallback) => values.TryGetValue($"settings.{key}", out var value) ? Unwrap(value).Replace("%BASE_DIR%", baseDir) : Path.Combine(baseDir, fallback);
        var profile = values.TryGetValue("general.selected_profile", out var sp) ? Unwrap(sp) : "Default";
        var global = SameFolder(Path.GetDirectoryName(dir), Mo2Root);
        var portable = File.Exists(Path.Combine(dir, "portable.txt"));
        return new Destination
        {
            Label = global ? $"Mod Organizer 2 instance: {Path.GetFileName(dir)}" : $"Mod Organizer 2 {(portable ? "portable " : "")}instance: {dir}",
            Kind = "mo2",
            GameFolder = gamePath,
            ModsFolder = Folder("mod_directory", "mods"),
            DownloadsFolder = Folder("download_directory", "downloads"),
            ProfileFolder = Path.Combine(Folder("profiles_directory", "profiles"), profile),
            InstanceFolder = dir,
        };
    }

    /// <summary>The nearest Mod Organizer 2 instance for Fallout 4 whose folder is this one or holds it, found by walking up
    /// to the drive root. Null when there is none.</summary>
    public static Destination? InstanceAbove(string folder)
    {
        for (var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            try { if (Mo2Instance(dir) is { } instance) return instance; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>Whether two paths name the same folder; false when either is null.</summary>
    internal static bool SameFolder(string? a, string? b)
    {
        return a != null && b != null && Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Vortex's Fallout 4 setup inside its data folder (normally %APPDATA%\Vortex), or null when that folder has no
    /// Fallout 4 downloads folder.</summary>
    public static Destination? Vortex(string vortexRoot)
    {
        var downloads = Path.Combine(vortexRoot, "downloads", "fallout4");
        if (!Directory.Exists(downloads)) return null;
        return new Destination
        {
            Label = "Vortex (zip into its Fallout 4 downloads, then install it there)",
            Kind = "vortex",
            GameFolder = VortexGameFolder(vortexRoot) ?? RegistryGameFolder(),
            DownloadsFolder = downloads,
            ModsFolder = VortexStagingFolder(vortexRoot) ?? Path.Combine(vortexRoot, "fallout4", "mods"),
            ModStates = VortexModStates(vortexRoot),
        };
    }

    /// <summary>Whether Vortex's Fallout 4 profile (settings###profiles###lastActiveProfile###fallout4) enables each mod, by mod id:
    /// persistent###profiles###(profile)###modState###(mod)###enabled. Null when Vortex has no Fallout 4 profile.</summary>
    private static IReadOnlyDictionary<string, bool>? VortexModStates(string vortexRoot)
    {
        var state = Path.Combine(vortexRoot, "state.v2");
        var profile = LevelDb.ReadJsonString(state, "settings###profiles###lastActiveProfile###fallout4");
        if (string.IsNullOrEmpty(profile)) return null;
        var prefix = $"persistent###profiles###{profile}###modState###";
        const string suffix = "###enabled";
        var states = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in LevelDb.ReadPrefix(state, prefix))
        {
            if (!key.EndsWith(suffix, StringComparison.Ordinal) || value == null) continue;
            states[key[prefix.Length..^suffix.Length]] = Encoding.UTF8.GetString(value) == "true";
        }
        return states;
    }

    /// <summary>Vortex's Fallout 4 game folder as its settings name it (settings###gameMode###discovered###fallout4###path, which a
    /// location set by hand also lands in), or null when it is not set or Fallout4.exe is not there.</summary>
    private static string? VortexGameFolder(string vortexRoot)
    {
        var path = LevelDb.ReadJsonString(Path.Combine(vortexRoot, "state.v2"), "settings###gameMode###discovered###fallout4###path");
        return path != null && File.Exists(Path.Combine(path, "Fallout4.exe")) ? path.TrimEnd('\\') : null;
    }

    /// <summary>Vortex's Fallout 4 staging folder as its settings name it (settings###mods###installPath###fallout4, with {USERDATA}
    /// and {game} filled in), or null when it is not set.</summary>
    private static string? VortexStagingFolder(string vortexRoot)
    {
        var value = LevelDb.ReadJsonString(Path.Combine(vortexRoot, "state.v2"), "settings###mods###installPath###fallout4");
        return string.IsNullOrWhiteSpace(value) ? null : value.Replace("{USERDATA}", vortexRoot, StringComparison.OrdinalIgnoreCase).Replace("{game}", "fallout4", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The destination a folder holds: a Mod Organizer 2 instance (the folder with ModOrganizer.ini) or Vortex's data
    /// folder. Null when it is neither.</summary>
    public static Destination? FromFolder(string folder)
    {
        var dir = Path.GetFullPath(folder).TrimEnd('\\', '/');
        return Mo2Instance(dir) ?? Vortex(dir);
    }

    /// <summary>The Fallout 4 folder the game registered with Windows, if any.</summary>
    public static string? RegistryGameFolder()
    {
        try
        {
            foreach (var key in new[] { @"SOFTWARE\WOW6432Node\Bethesda Softworks\Fallout4", @"SOFTWARE\Bethesda Softworks\Fallout4" })
            {
                using var reg = Registry.LocalMachine.OpenSubKey(key);
                var path = reg?.GetValue("Installed Path") as string;
                if (path != null && File.Exists(Path.Combine(path, "Fallout4.exe"))) return path.TrimEnd('\\');
            }
        }
        catch { /* registry access is optional */ }
        return null;
    }

    private static Dictionary<string, string> ReadIni(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = "";
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            values[$"{section}.{line[..eq].Trim()}"] = line[(eq + 1)..].Trim();
        }
        return values;
    }

    // Decodes a QSettings value from ModOrganizer.ini (quotes, backslash, hex and octal escapes, @ByteArray UTF-8 bytes) into a Windows path.
    internal static string Unwrap(string value)
    {
        var text = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '"') continue;
            if (c != '\\') { text.Append(c); continue; }
            if (++i == value.Length) break;
            c = value[i];
            var radix = c == 'x' ? 16 : c is >= '0' and <= '7' ? 8 : 0;
            if (radix == 0)
            {
                var escaped = c switch { 'a' => '\a', 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', 'v' => '\v', '"' or '?' or '\'' or '\\' => c, _ => '\0' };
                if (escaped != '\0') text.Append(escaped);
                continue;
            }
            var code = radix == 8 ? c - '0' : -1;
            while (i + 1 < value.Length && Digit(value[i + 1], radix) is var digit and >= 0) { code = Math.Max(code, 0) * radix + digit; i++; }
            if (code >= 0) text.Append((char)code);
        }
        var decoded = text.ToString();
        if (decoded.StartsWith("@ByteArray(") && decoded.EndsWith(')') && decoded.All(ch => ch <= 0xFF)) decoded = Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(decoded[11..^1]));
        return decoded.Replace('/', '\\');
    }

    // The value of one hex or octal digit, or -1 when the character is not a digit of that radix.
    private static int Digit(char c, int radix)
    {
        var digit = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : radix;
        return digit < radix ? digit : -1;
    }
}
