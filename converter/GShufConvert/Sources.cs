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
    private static readonly string[] DataRoots = ["materials", "textures", "meshes", "gshuf", "mcm", "scripts", "sound", "strings", "video", "interface"];

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
            folder = Path.Combine(tempRoot, Sanitize(Path.GetFileNameWithoutExtension(input)));
            Extract(input, folder, report);
        }
        else
        {
            report.Warn($"input not found: {input}");
            return [];
        }

        var name = DisplayName(Path.GetFileNameWithoutExtension(input.TrimEnd('\\', '/')));
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
                    var path = Path.Combine(archiveRoot, entry.Replace('/', '\\'));
                    if (Directory.Exists(path)) AddFolder(source, path, report);
                    else if (File.Exists(path) && path.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase)) AddArchive(source, path, Path.GetFileName(path), report);
                    else if (!File.Exists(path)) report.Warn($"{name}: FOMOD source missing: {entry}");
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
        report.Info($"extracted {count} files from {Path.GetFileName(archive)}");
    }

    /// <summary>Option name to the sources (folders or files, relative to the archive root) it installs; "" is the always-installed set.</summary>
    private static List<(string Option, List<string> Sources)> FomodOptions(string configPath, Report report)
    {
        var result = new List<(string, List<string>)>();
        try
        {
            var doc = new XmlDocument();
            doc.Load(configPath);
            static List<string> SourcesOf(XmlNode node)
            {
                var list = new List<string>();
                foreach (XmlElement e in node.SelectNodes(".//folder")!) list.Add(e.GetAttribute("source"));
                foreach (XmlElement e in node.SelectNodes(".//file")!) list.Add(e.GetAttribute("source"));
                return list.Where(x => x.Length > 0).ToList();
            }
            var required = new List<string>();
            foreach (XmlElement node in doc.SelectNodes("//requiredInstallFiles")!) required.AddRange(SourcesOf(node));

            // Conditional patterns install extra sources when flags set by chosen plugins match.
            var patterns = new List<(List<(string Flag, string Value)> Deps, List<string> Sources)>();
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
                var sources = new List<string>(required);
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
                if (sources.Count > required.Count) result.Add((pluginName, sources.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
            }
            foreach (var (deps, patternSources) in patterns)
            {
                if (deps.Count == 0) required.AddRange(patternSources);
            }
            if (required.Count > 0) result.Insert(0, ("", required.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        }
        catch (Exception e)
        {
            report.Warn($"cannot parse {configPath}: {e.Message}");
        }
        return result;
    }

    /// <summary>Adds every loose file under a Data-like root, and every file inside BA2 archives found there.</summary>
    private static void AddFolder(Source source, string folder, Report report)
    {
        var dataRoot = FindDataRoot(folder) ?? folder;
        foreach (var path in Directory.EnumerateFiles(dataRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(dataRoot, path).Replace('/', '\\');
            if (relative.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase))
            {
                AddArchive(source, path, relative, report);
                continue;
            }
            var file = path;
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
            report.Info($"{source.Name}: {archive.Count} files in {label}");
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
        var chars = name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var id = new string(chars).Trim('-');
        while (id.Contains("--")) id = id.Replace("--", "-");
        return id.Length == 0 ? "pack" : id;
    }

    /// <summary>Strips Nexus download suffixes such as "-4258-6-02" or "-44748-3-1-0-1692025512".</summary>
    public static string DisplayName(string fileName)
    {
        var parts = fileName.Split('-');
        var keep = parts.Length;
        static bool IsVersionish(string part) => part.Length > 0 && part.All(c => char.IsDigit(c) || c == '.' || c == ' ') && part.Any(char.IsDigit);
        while (keep > 1 && IsVersionish(parts[keep - 1].Trim())) keep--;
        var name = string.Join("-", parts.Take(keep)).Trim();
        var words = name.Split(' ');
        while (words.Length > 1 && IsVersionish(words[^1]) ) words = words[..^1];
        return string.Join(" ", words).Trim();
    }
}
