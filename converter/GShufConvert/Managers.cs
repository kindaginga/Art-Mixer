using System.Text;
using Microsoft.Win32;

namespace GShufConvert;

/// <summary>A place the converted packs can be delivered to.</summary>
public sealed class Destination
{
    public required string Label { get; init; }
    public required string Kind { get; init; }          // "mo2", "vortex" or "folder"
    public string? GameFolder { get; init; }
    public string? ModsFolder { get; init; }             // MO2: the instance's mods folder; Vortex: its Fallout 4 staging folder
    public string? DownloadsFolder { get; init; }        // the manager's Fallout 4 downloads folder
    public string? ProfileFolder { get; init; }          // MO2: the instance's active profile
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

    public static List<Destination> Detect()
    {
        var list = new List<Destination>();
        var mo2Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModOrganizer");
        if (Directory.Exists(mo2Root))
        {
            foreach (var dir in Directory.EnumerateDirectories(mo2Root).OrderBy(d => d))
            {
                Destination? instance;
                try { instance = Mo2Instance(dir); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { instance = null; }
                if (instance != null) list.Add(instance);
            }
        }
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
        return new Destination
        {
            Label = $"Mod Organizer 2 instance: {Path.GetFileName(dir)}",
            Kind = "mo2",
            GameFolder = gamePath,
            ModsFolder = Folder("mod_directory", "mods"),
            DownloadsFolder = Folder("download_directory", "downloads"),
            ProfileFolder = Path.Combine(Folder("profiles_directory", "profiles"), profile),
        };
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
            GameFolder = RegistryGameFolder(),
            DownloadsFolder = downloads,
            ModsFolder = VortexStagingFolder(vortexRoot) ?? Path.Combine(vortexRoot, "fallout4", "mods"),
        };
    }

    /// <summary>Vortex's Fallout 4 staging folder as its settings name it (the value of settings###mods###installPath###fallout4 in
    /// its state.v2 database, newest file first, with {USERDATA} and {game} filled in), or null when it is not found as plain text.</summary>
    private static string? VortexStagingFolder(string vortexRoot)
    {
        var state = Path.Combine(vortexRoot, "state.v2");
        if (!Directory.Exists(state)) return null;
        var key = System.Text.Encoding.UTF8.GetBytes("settings###mods###installPath###fallout4");
        foreach (var file in new DirectoryInfo(state).EnumerateFiles().Where(f => f.Extension is ".ldb" or ".log").OrderByDescending(f => f.LastWriteTimeUtc))
        {
            byte[] data;
            try
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                data = new byte[stream.Length];
                stream.ReadExactly(data);
            }
            catch (IOException)
            {
                continue;
            }
            string? value = null;
            for (var at = data.AsSpan().IndexOf(key); at >= 0;)
            {
                // The value is stored as a JSON string a few bytes after its key.
                var start = at + key.Length;
                var quote = Array.IndexOf(data, (byte)'"', start, Math.Min(8, data.Length - start));
                if (quote >= 0)
                {
                    var end = quote + 1;
                    while (end < data.Length && data[end] != '"') end += data[end] == '\\' ? 2 : 1;
                    try
                    {
                        if (end < data.Length) value = System.Text.Json.JsonSerializer.Deserialize<string>(data.AsSpan(quote, end - quote + 1)) ?? value;
                    }
                    catch (System.Text.Json.JsonException)
                    {
                    }
                }
                var next = data.AsSpan(start).IndexOf(key);
                at = next < 0 ? -1 : start + next;
            }
            if (!string.IsNullOrWhiteSpace(value)) return value.Replace("{USERDATA}", vortexRoot, StringComparison.OrdinalIgnoreCase).Replace("{game}", "fallout4", StringComparison.OrdinalIgnoreCase);
        }
        return null;
    }

    /// <summary>The destination a folder holds: a Mod Organizer 2 instance (the folder with ModOrganizer.ini) or Vortex's data
    /// folder. Null when it is neither.</summary>
    public static Destination? FromFolder(string folder)
    {
        var dir = Path.GetFullPath(folder).TrimEnd('\\', '/');
        return Mo2Instance(dir) ?? Vortex(dir);
    }

    /// <summary>Whether ALR (Automated Loading Screen Replacer) is installed where the packs go: in one of the MO2 instance's
    /// mods, or in the game's Data folder.</summary>
    public static bool HasAlr(Destination destination)
    {
        try
        {
            if (destination.GameFolder != null && File.Exists(Path.Combine(destination.GameFolder, "Data", "ALR.esp"))) return true;
            if (destination.Kind == "mo2" && destination.ModsFolder != null && Directory.Exists(destination.ModsFolder))
            {
                return Directory.EnumerateDirectories(destination.ModsFolder).Any(mod => File.Exists(Path.Combine(mod, "ALR.esp")));
            }
        }
        catch (IOException) { /* an unreadable folder counts as no ALR */ }
        catch (UnauthorizedAccessException) { /* an unreadable folder counts as no ALR */ }
        return false;
    }

    /// <summary>The picture folder ALR reads when it differs from the packs' own copy: under Mod Organizer 2, ALR reads only the
    /// folder next to its DLL, so the pictures go into the mod that holds F4SE\Plugins\ALR.dll. Null elsewhere.</summary>
    public static string? AlrImageFolder(Destination destination)
    {
        if (destination.Kind != "mo2" || destination.ModsFolder == null || !Directory.Exists(destination.ModsFolder)) return null;
        try
        {
            var mod = Directory.EnumerateDirectories(destination.ModsFolder).FirstOrDefault(m => File.Exists(Path.Combine(m, "F4SE", "Plugins", "ALR.dll")));
            return mod == null ? null : Path.Combine(mod, Writer.AlrImageFolder);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
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
    private static string Unwrap(string value)
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
