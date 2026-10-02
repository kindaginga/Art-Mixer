using System.Xml;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace GShufConvert;

/// <summary>One file inside a source, addressed by its path relative to the Data folder.</summary>
public sealed class SourceFile
{
    public required string DataPath { get; init; }
    public required Func<byte[]> Read { get; init; }
    public required Func<(int Width, int Height)?> Dimensions { get; init; }
}

/// <summary>A group of files that becomes one pack: a mod folder, an archive, or one FOMOD option.</summary>
public sealed class Source
{
    public required string Name { get; init; }
    public required string Origin { get; init; }
    public Dictionary<string, SourceFile> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class Sources
{
    private static readonly string[] DataRoots = ["materials", "textures", "meshes", "gshuf", "mcm", "scripts", "sound", "music", "strings", "video", "interface"];

    /// <summary>Loads a mod folder or archive and splits it into packs (one per FOMOD option group when present).</summary>
    public static List<Source> Load(string input, string tempRoot, Report report)
    {
        string folder;
        if (Directory.Exists(input))
        {
            folder = input;
        }
        else if (File.Exists(input))
        {
            var stem = Sanitize(Path.GetFileNameWithoutExtension(input));
            folder = Path.Combine(tempRoot, stem);
            for (var n = 2; Directory.Exists(folder); n++) folder = Path.Combine(tempRoot, $"{stem}-{n}");
            Extract(input, folder, report);
        }
        else
        {
            report.Warn($"input not found: {input}");
            return [];
        }

        var name = DisplayName(InputStem(input));
        var fomod = Directory.EnumerateFiles(folder, "ModuleConfig.xml", SearchOption.AllDirectories)
            .FirstOrDefault(p => Path.GetFileName(Path.GetDirectoryName(p) ?? "").Equals("fomod", StringComparison.OrdinalIgnoreCase));
        var sources = new List<Source>();
        if (fomod != null)
        {
            var archiveRoot = Path.GetDirectoryName(Path.GetDirectoryName(fomod))!;
            foreach (var (option, entries) in FomodOptions(fomod, report))
            {
                var source = new Source { Name = option == "" ? name : $"{name} - {option}", Origin = input };
                report.Info($"{source.Name}: FOMOD sources: {string.Join("; ", entries)}");
                foreach (var entry in entries)
                {
                    var path = Path.Combine(archiveRoot, entry.Source.Replace('/', '\\'));
                    if (Directory.Exists(path)) AddFolder(source, path, report, entry.Destination);
                    else if (File.Exists(path) && path.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase)) AddArchive(source, path, Path.GetFileName(path), report);
                    else if (File.Exists(path)) AddFile(source, path, entry.Destination.Length > 0 ? DataRelative(entry.Destination) : Path.GetFileName(path));
                    else report.Warn($"{name}: FOMOD source missing: {entry.Source}");
                }
                if (source.Files.Count > 0) sources.Add(source);
            }
            if (sources.Count > 0) return sources;
            report.Warn($"{name}: FOMOD config yielded no files; scanning the whole archive instead");
        }
        var whole = new Source { Name = name, Origin = input };
        AddFolder(whole, folder, report);
        if (whole.Files.Count > 0) sources.Add(whole);
        return sources;
    }

    private static void Extract(string archive, string folder, Report report)
    {
        Directory.CreateDirectory(folder);
        using var opened = ArchiveFactory.OpenArchive(archive);
        var options = new ExtractionOptions { ExtractFullPath = true, Overwrite = true };
        var count = 0;
        if (opened.IsSolid || opened.Type == ArchiveType.SevenZip)
        {
            // Solid archives decompress from the start for every random access, so they are read once, sequentially.
            using var reader = opened.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                if (reader.Entry.IsDirectory) continue;
                reader.WriteEntryToDirectory(folder, options);
                count++;
            }
        }
        else
        {
            foreach (var entry in opened.Entries.Where(e => !e.IsDirectory))
            {
                entry.WriteToDirectory(folder, options);
                count++;
            }
        }
        report.Info($"extracted {Report.Count(count, "file", "files")} from {Path.GetFileName(archive)}");
    }

    /// <summary>A folder or file an installer copies: its path in the archive and where it lands, relative to Data (empty: the Data root).</summary>
    private sealed record FomodEntry(string Source, string Destination)
    {
        public override string ToString() => Destination.Length > 0 ? $"{Source} -> {Destination}" : Source;
    }

    /// <summary>Option name to the entries (folders or files, relative to the archive root) it installs; "" is the always-installed set.</summary>
    private static List<(string Option, List<FomodEntry> Sources)> FomodOptions(string configPath, Report report)
    {
        var result = new List<(string, List<FomodEntry>)>();
        try
        {
            var doc = new XmlDocument();
            doc.Load(configPath);
            static List<FomodEntry> SourcesOf(XmlNode node)
            {
                var list = new List<FomodEntry>();
                foreach (XmlElement e in node.SelectNodes(".//folder")!) list.Add(new FomodEntry(e.GetAttribute("source"), e.GetAttribute("destination")));
                foreach (XmlElement e in node.SelectNodes(".//file")!) list.Add(new FomodEntry(e.GetAttribute("source"), e.GetAttribute("destination")));
                return list.Where(x => x.Source.Length > 0).ToList();
            }
            static List<FomodEntry> Distinct(IEnumerable<FomodEntry> entries) =>
                entries.DistinctBy(e => (e.Source.ToLowerInvariant(), e.Destination.ToLowerInvariant())).ToList();
            var required = new List<FomodEntry>();
            foreach (XmlElement node in doc.SelectNodes("//requiredInstallFiles")!) required.AddRange(SourcesOf(node));

            // Conditional patterns install extra sources when flags set by chosen plugins match.
            var patterns = new List<(List<(string Flag, string Value)> Deps, List<FomodEntry> Sources)>();
            foreach (XmlElement pattern in doc.SelectNodes("//conditionalFileInstalls//pattern")!)
            {
                var deps = new List<(string, string)>();
                foreach (XmlElement dep in pattern.SelectNodes(".//flagDependency")!) deps.Add((dep.GetAttribute("flag"), dep.GetAttribute("value")));
                var files = pattern.SelectSingleNode("files");
                if (files != null) patterns.Add((deps, SourcesOf(files)));
            }

            var pluginNames = doc.SelectNodes("//plugin")!.Cast<XmlElement>().Select(p => p.GetAttribute("name")).ToList();
            static bool Generic(string name) => name.Trim().ToLowerInvariant() is "yes" or "no" or "install" or "default" or "none" or "on" or "off";
            foreach (XmlElement plugin in doc.SelectNodes("//plugin")!)
            {
                // Options install the always-required files too; later cross-option deduplication keeps each picture in one pack.
                var sources = new List<FomodEntry>(required);
                var files = plugin.SelectSingleNode("files");
                if (files != null) sources.AddRange(SourcesOf(files));
                var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (XmlElement flag in plugin.SelectNodes("conditionFlags/flag")!) flags[flag.GetAttribute("name")] = flag.InnerText.Trim();
                foreach (var (deps, patternSources) in patterns)
                {
                    // A pattern belongs to this option when the option sets at least one of its flags and contradicts none.
                    var owned = deps.Where(d => flags.ContainsKey(d.Flag)).ToList();
                    if (owned.Count > 0 && owned.All(d => flags[d.Flag].Equals(d.Value, StringComparison.OrdinalIgnoreCase))) sources.AddRange(patternSources);
                }
                var pluginName = plugin.GetAttribute("name");
                var groupName = (plugin.ParentNode?.ParentNode as XmlElement)?.GetAttribute("name") ?? "";
                if ((Generic(pluginName) || pluginNames.Count(n => n == pluginName) > 1) && groupName.Length > 0) pluginName = $"{groupName} - {pluginName}";
                if (sources.Count > required.Count) result.Add((pluginName, Distinct(sources)));
            }
            foreach (var (deps, patternSources) in patterns)
            {
                if (deps.Count == 0) required.AddRange(patternSources);
            }
            if (required.Count > 0) result.Insert(0, ("", Distinct(required)));
        }
        catch (Exception e)
        {
            report.Warn($"cannot parse {configPath}: {e.Message}");
        }
        return result;
    }

    /// <summary>Adds every loose file under a Data-like root, and every file inside BA2 archives found there. With an installer
    /// destination the folder's contents land under that path; without one the folder's own Data root is found.</summary>
    private static void AddFolder(Source source, string folder, Report report, string destination = "")
    {
        var prefix = DataRelative(destination);
        var dataRoot = prefix.Length > 0 ? folder : FindDataRoot(folder) ?? folder;
        foreach (var path in Directory.EnumerateFiles(dataRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(dataRoot, path).Replace('/', '\\');
            if (prefix.Length > 0) relative = prefix + "\\" + relative;
            if (relative.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase))
            {
                AddArchive(source, path, relative, report);
                continue;
            }
            AddFile(source, path, relative);
        }
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

    /// <summary>An installer destination as a path relative to Data: backslashes, no leading "Data", no outer separators.</summary>
    private static string DataRelative(string destination)
    {
        var path = destination.Replace('/', '\\').Trim('\\');
        if (path.Equals("data", StringComparison.OrdinalIgnoreCase)) return "";
        if (path.StartsWith("data\\", StringComparison.OrdinalIgnoreCase)) path = path[5..];
        return path;
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

    /// <summary>An input's name without the archive extension; a folder keeps its whole name, dots included.</summary>
    public static string InputStem(string input)
    {
        var trimmed = input.TrimEnd('\\', '/');
        return Directory.Exists(trimmed) ? Path.GetFileName(trimmed) : Path.GetFileNameWithoutExtension(trimmed);
    }

    /// <summary>Strips Nexus download suffixes such as "-4258-6-02", "-44748-3-1-0-1692025512", "-13905-2-0a" or "-20374-", and a
    /// browser's duplicate mark such as " (1)".</summary>
    public static string DisplayName(string fileName)
    {
        var parts = System.Text.RegularExpressions.Regex.Replace(fileName, @"\s\(\d+\)$", "").Split('-');
        var keep = parts.Length;
        static bool IsVersionish(string part) => part.Length > 0 && part.All(c => char.IsDigit(c) || c == '.' || c == ' ') && part.Any(char.IsDigit);
        // A version part may end in a letter ("0a") or be empty (a trailing "-"); the run still has to start at a number, the mod id.
        static bool IsVersionTail(string part) => part.Length == 0 || IsVersionish(part) || (part.Length > 1 && char.IsAsciiLetterLower(part[^1]) && IsVersionish(part[..^1]));
        while (keep > 1 && IsVersionTail(parts[keep - 1].Trim())) keep--;
        while (keep < parts.Length && !IsVersionish(parts[keep].Trim())) keep++;
        var name = string.Join("-", parts.Take(keep)).Trim();
        var words = name.Split(' ');
        while (words.Length > 1 && IsVersionish(words[^1]) ) words = words[..^1];
        return string.Join(" ", words).Trim();
    }
}
