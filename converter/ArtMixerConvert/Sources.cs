namespace ArtMixerConvert;

/// <summary>One file inside a source, addressed by its path relative to the Data folder.</summary>
public sealed class SourceFile
{
    public required string DataPath { get; init; }
    public required Func<byte[]> Read { get; init; }
    public required Func<(int Width, int Height)?> Dimensions { get; init; }
}

/// <summary>The files of one installed mod, which become one pack.</summary>
public sealed class Source
{
    public required string Name { get; init; }
    public required string Origin { get; init; }
    public Dictionary<string, SourceFile> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class Sources
{
    private static readonly string[] DataRoots = ["materials", "textures", "meshes", "artmixer", "mcm", "scripts", "sound", "music", "strings", "video", "interface"];

    /// <summary>Loads an installed mod's folder (a Mod Organizer 2 mod folder or a Vortex staging folder): its loose files and
    /// the files inside its BA2 archives, exactly as the mod manager installed them. Null for anything that is not such a folder.</summary>
    public static Source? Load(string input, Report report)
    {
        if (!Directory.Exists(input))
        {
            report.Warn($"{Path.GetFileName(input.TrimEnd('\\', '/'))}: not an installed mod folder. Install the mod with your mod manager first.");
            return null;
        }
        var source = new Source { Name = DisplayName(InputStem(input)), Origin = input };
        var dataRoot = FindDataRoot(input) ?? input;
        foreach (var path in Directory.EnumerateFiles(dataRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(dataRoot, path).Replace('/', '\\');
            if (relative.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase)) AddArchive(source, path, relative, report);
            else AddFile(source, path, relative);
        }
        return source.Files.Count > 0 ? source : null;
    }

    /// <summary>Adds one loose file under its path relative to Data.</summary>
    private static void AddFile(Source source, string file, string relative)
    {
        source.Files[relative] = new SourceFile
        {
            DataPath = relative,
            Read = () => File.ReadAllBytes(file),
            Dimensions = () =>
            {
                if (!file.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) return null;
                Span<byte> head = stackalloc byte[128];
                using var stream = File.OpenRead(file);
                var read = stream.Read(head);
                return Dds.ReadDimensions(head[..read]);
            },
        };
    }

    private static void AddArchive(Source source, string path, string label, Report report)
    {
        try
        {
            var archive = new Ba2Archive(path);
            foreach (var inner in archive.Paths)
            {
                var innerPath = inner;
                source.Files[innerPath] = new SourceFile
                {
                    DataPath = innerPath,
                    Read = () => archive.Read(innerPath),
                    Dimensions = () => archive.Dimensions(innerPath),
                };
            }
            report.Info($"{source.Name}: {Report.Count(archive.Count, "file", "files")} in {label}");
        }
        catch (Exception e)
        {
            report.Warn($"{source.Name}: cannot read {label}: {e.Message}");
        }
    }

    /// <summary>Finds the folder that plays the role of Data: itself, a child named Data, or the parent of a Materials/Textures folder.</summary>
    private static string? FindDataRoot(string folder)
    {
        if (Directory.EnumerateDirectories(folder).Any(d => DataRoots.Contains(Path.GetFileName(d).ToLowerInvariant()))) return folder;
        var data = Path.Combine(folder, "Data");
        if (Directory.Exists(data)) return data;
        foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories))
        {
            if (DataRoots.Contains(Path.GetFileName(dir).ToLowerInvariant())) return Path.GetDirectoryName(dir);
        }
        return null;
    }

    public static string Sanitize(string name)
    {
        var chars = name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var id = new string(chars).Trim('-');
        while (id.Contains("--")) id = id.Replace("--", "-");
        return id.Length == 0 ? "pack" : id;
    }

    /// <summary>An installed mod folder's name, dots included.</summary>
    public static string InputStem(string input) => Path.GetFileName(input.TrimEnd('\\', '/'));

    /// <summary>Strips Nexus download suffixes such as "-4258-6-02", "-44748-3-1-0-1692025512", "-13905-2-0a" or "-20374-", and a
    /// browser's duplicate mark such as " (1)".</summary>
    public static string DisplayName(string fileName) => SplitDownloadName(fileName).Name;

    /// <summary>The Nexus page an input comes from: the mod id Mod Organizer 2 recorded for a mod folder, else the page number
    /// in a Nexus download name; null when there is none.</summary>
    public static string? NexusPage(string input)
    {
        var trimmed = input.TrimEnd('\\', '/');
        if (Directory.Exists(trimmed) && MetaValue(trimmed, "modid") is { } id && id.All(char.IsAsciiDigit) && id.TrimStart('0').Length > 0) return id.TrimStart('0');
        return SplitDownloadName(InputStem(input)).Page;
    }

    /// <summary>A value of a Mod Organizer 2 mod folder's meta.ini, or null.</summary>
    public static string? MetaValue(string folder, string key)
    {
        var meta = Path.Combine(folder, "meta.ini");
        if (!File.Exists(meta)) return null;
        try
        {
            var line = File.ReadLines(meta).FirstOrDefault(l => l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
            var value = line?[(key.Length + 1)..].Trim().Trim('"');
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // A Nexus download name is "Name-page-version parts" or "Name page version date code"; a name of neither form has no page.
    private static (string Name, string? Page) SplitDownloadName(string fileName)
    {
        var stem = System.Text.RegularExpressions.Regex.Replace(fileName, @"\s\(\d+\)$", "");
        var dated = System.Text.RegularExpressions.Regex.Match(stem, @"^(.+) (\d+) \S+ \d{4}-\d{2}-\d{2}T\d{2}-\d{2}Z \S+$");
        if (dated.Success) return (dated.Groups[1].Value.Trim(), dated.Groups[2].Value);
        var parts = stem.Split('-');
        var keep = parts.Length;
        static bool IsVersionish(string part) => part.Length > 0 && part.All(c => char.IsDigit(c) || c == '.' || c == ' ') && part.Any(char.IsDigit);
        // A version part may end in a letter ("0a") or be empty (a trailing "-"); the run still has to start at a number, the mod id.
        static bool IsVersionTail(string part) => part.Length == 0 || IsVersionish(part) || (part.Length > 1 && char.IsAsciiLetterLower(part[^1]) && IsVersionish(part[..^1]));
        while (keep > 1 && IsVersionTail(parts[keep - 1].Trim())) keep--;
        while (keep < parts.Length && !IsVersionish(parts[keep].Trim())) keep++;
        var name = string.Join("-", parts.Take(keep)).Trim();
        var page = parts.Length - keep >= 2 ? parts.Skip(keep).FirstOrDefault(part => part.Length > 0 && part.All(char.IsAsciiDigit)) : null;
        var words = name.Split(' ');
        while (words.Length > 1 && IsVersionish(words[^1]) ) words = words[..^1];
        return (string.Join(" ", words).Trim(), page);
    }
}
