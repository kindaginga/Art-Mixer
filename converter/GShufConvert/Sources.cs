using System.Xml;
using SharpCompress.Archives;
using SharpCompress.Common;

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
    private static readonly string[] DataRoots = ["materials", "textures", "meshes", "gshuf", "mcm", "scripts", "sound", "strings"];

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
            foreach (var (option, folders) in FomodOptions(fomod, report))
            {
                var source = new Source { Name = option == "" ? name : $"{name} - {option}", Origin = input };
                foreach (var sub in folders)
                {
                    var dir = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(fomod))!, sub.Replace('/', '\\'));
                    if (Directory.Exists(dir)) AddFolder(source, dir, report);
                    else report.Warn($"{name}: FOMOD folder missing: {sub}");
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
        using var reader = ArchiveFactory.OpenArchive(archive);
        var count = 0;
        foreach (var entry in reader.Entries.Where(e => !e.IsDirectory))
        {
            entry.WriteToDirectory(folder, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
            count++;
        }
        report.Info($"extracted {count} files from {Path.GetFileName(archive)}");
    }

    /// <summary>Option name to the list of folders (relative to the archive root) it installs; "" is the always-installed set.</summary>
    private static List<(string Option, List<string> Folders)> FomodOptions(string configPath, Report report)
    {
        var result = new List<(string, List<string>)>();
        try
        {
            var doc = new XmlDocument();
            doc.Load(configPath);
            var required = new List<string>();
            foreach (XmlElement folder in doc.SelectNodes("//requiredInstallFiles//folder")!)
            {
                required.Add(folder.GetAttribute("source"));
            }
            foreach (XmlElement plugin in doc.SelectNodes("//plugin")!)
            {
                var folders = new List<string>(required);
                foreach (XmlElement folder in plugin.SelectNodes(".//folder")!) folders.Add(folder.GetAttribute("source"));
                foreach (XmlElement file in plugin.SelectNodes(".//file")!) folders.Add(file.GetAttribute("source"));
                if (folders.Count > required.Count) result.Add((plugin.GetAttribute("name"), folders));
            }
            if (result.Count == 0 && required.Count > 0) result.Add(("", required));
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
                    report.Info($"{source.Name}: {archive.Count} files in {relative}");
                }
                catch (Exception e)
                {
                    report.Warn($"{source.Name}: cannot read {relative}: {e.Message}");
                }
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
        while (keep > 1 && parts[keep - 1].Trim().All(char.IsDigit) && parts[keep - 1].Trim().Length > 0) keep--;
        return string.Join("-", parts.Take(keep)).Trim();
    }
}
