using Microsoft.Win32;

namespace GShufConvert;

/// <summary>A place the converted packs can be delivered to.</summary>
public sealed class Destination
{
    public required string Label { get; init; }
    public required string Kind { get; init; }          // "mo2", "vortex" or "folder"
    public string? GameFolder { get; init; }
    public string? ModsFolder { get; init; }             // MO2: the instance's mods folder
    public string? DownloadsFolder { get; init; }        // Vortex: the Fallout 4 downloads folder
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
                var ini = Path.Combine(dir, "ModOrganizer.ini");
                if (!File.Exists(ini)) continue;
                var values = ReadIni(ini);
                if (!values.TryGetValue("general.gamename", out var game) || !game.Contains("Fallout 4", StringComparison.OrdinalIgnoreCase)) continue;
                var gamePath = values.TryGetValue("general.gamepath", out var gp) ? Unwrap(gp) : null;
                var baseDir = values.TryGetValue("settings.base_directory", out var bd) ? Unwrap(bd) : dir;
                var mods = values.TryGetValue("settings.mod_directory", out var md) ? Unwrap(md).Replace("%BASE_DIR%", baseDir) : Path.Combine(baseDir, "mods");
                list.Add(new Destination
                {
                    Label = $"Mod Organizer 2 instance: {Path.GetFileName(dir)}",
                    Kind = "mo2",
                    GameFolder = gamePath,
                    ModsFolder = mods,
                });
            }
        }
        var vortexDownloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vortex", "downloads", "fallout4");
        if (Directory.Exists(vortexDownloads))
        {
            list.Add(new Destination
            {
                Label = "Vortex (zip into its Fallout 4 downloads, then install it there)",
                Kind = "vortex",
                GameFolder = RegistryGameFolder(),
                DownloadsFolder = vortexDownloads,
            });
        }
        return list;
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

    // MO2 stores paths as @ByteArray(D:\\Games\\Fallout 4) with doubled backslashes.
    private static string Unwrap(string value)
    {
        var v = value;
        if (v.StartsWith("@ByteArray(") && v.EndsWith(")")) v = v[11..^1];
        return v.Replace("\\\\", "\\").Replace('/', '\\').Trim('"');
    }
}
