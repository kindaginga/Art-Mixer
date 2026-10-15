using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace ArtMixerConvert;

/// <summary>One file of the Art Mixer mod as the package lists it (path relative to Data, with forward slashes).
/// A replaceable file is one the converter's packs replace where both mods are deployed into one folder.</summary>
public sealed record CoreFile(string Path, long Size, string Sha256, bool Replaceable);

/// <summary>What a check of the mod's own files found: how many were checked, which were missing or changed, which of
/// those were restored and from which archive, and why the rest were not.</summary>
public sealed record CoreCheckResult(int Checked, IReadOnlyList<string> Damaged, IReadOnlyList<string> Restored, string? Archive, string? NotRestoredBecause)
{
    /// <summary>The damaged files that were not restored.</summary>
    public IReadOnlyList<string> Left => Damaged.Except(Restored, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The message for the player, one entry per line; empty when every file was intact.</summary>
    public IReadOnlyList<string> MessageLines
    {
        get
        {
            var lines = new List<string>();
            if (Restored.Count > 0)
            {
                lines.Add($"Missing or changed files of Art Mixer, restored from {System.IO.Path.GetFileName(Archive)}:");
                lines.AddRange(Restored.Select(path => "  " + path));
            }
            if (Left.Count > 0)
            {
                if (lines.Count > 0) lines.Add("");
                lines.Add($"Missing or changed files of Art Mixer, not restored ({NotRestoredBecause}):");
                lines.AddRange(Left.Select(path => "  " + path));
                lines.Add("Reinstall Art Mixer with your mod manager.");
            }
            return lines;
        }
    }

    /// <summary>The message as one text, or null when every file was intact.</summary>
    public string? Message => Damaged.Count == 0 ? null : string.Join("\n", MessageLines);
}

/// <summary>Checks the files of the Art Mixer mod the converter runs from against the list the package wrote, and
/// restores missing or changed files from the archive Mod Organizer 2 installed the mod from.</summary>
public static class CoreCheck
{
    private const string ListName = "ArtMixer/core-files.json";
    private const string NoRecord = "your mod manager has no install archive on record for the mod";

    /// <summary>The mod folder the converter runs from (it sits in Tools\ArtMixer inside it), or null for a
    /// converter outside the mod.</summary>
    public static string? ModFolder()
    {
        var here = AppContext.BaseDirectory.TrimEnd('\\', '/');
        return here.EndsWith("\\Tools\\ArtMixer", StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(Path.Combine(here, "..", "..")) : null;
    }

    /// <summary>Checks the mod the converter runs from; null for a converter outside the mod.</summary>
    public static CoreCheckResult? Run(Report report) => ModFolder() is { } folder ? Run(folder, report) : null;

    /// <summary>Checks the mod in the folder, restores what it can, and writes the outcome to the report.</summary>
    public static CoreCheckResult Run(string folder, Report report)
    {
        var result = Check(folder);
        if (result.Damaged.Count == 0)
        {
            report.Info($"Art Mixer's own files: {result.Checked} checked, all intact.");
            return result;
        }
        report.Warn($"Art Mixer's own files: {result.Damaged.Count} missing or changed, {result.Restored.Count} restored.");
        foreach (var line in result.MessageLines.Where(line => line.Length > 0)) report.Info(line);
        return result;
    }

    private static CoreCheckResult Check(string folder)
    {
        var installed = ReadList(Path.Combine(folder, Local(ListName)));
        var (archivePath, problem) = FindArchive(folder);
        ZipArchive? zip = null;
        try
        {
            List<CoreFile>? archived = null;
            if (archivePath != null)
            {
                var name = Path.GetFileName(archivePath);
                try
                {
                    zip = ZipFile.OpenRead(archivePath);
                    archived = zip.GetEntry("Data/" + ListName) is { } entry ? ParseList(ReadEntry(entry)) : null;
                    if (archived == null) problem = $"the install archive {name} holds no file list";
                }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    problem = $"the install archive {name} cannot be read as a zip archive";
                }
                if (installed != null && archived != null && !Same(installed, archived))
                {
                    problem = $"the install archive {name} is another version of the mod";
                    archived = null;
                }
            }

            var damaged = new List<string>();
            if (installed == null) damaged.Add(ListName);
            var list = installed ?? archived;
            if (list == null) return new CoreCheckResult(0, damaged, [], archivePath, problem ?? NoRecord);
            var packsDeployed = File.Exists(Path.Combine(folder, "ArtMixer", "packs.json"));
            damaged.AddRange(list.Where(file => !Intact(folder, file, packsDeployed)).Select(file => file.Path));
            if (damaged.Count == 0 || zip == null || archived == null) return new CoreCheckResult(list.Count, damaged, [], archivePath, damaged.Count == 0 ? null : problem ?? NoRecord);

            var restored = new List<string>();
            var own = Environment.ProcessPath;
            foreach (var path in damaged)
            {
                var target = Path.Combine(folder, Local(path));
                if (own != null && Path.GetFullPath(target).Equals(Path.GetFullPath(own), StringComparison.OrdinalIgnoreCase))
                {
                    problem = "the converter cannot replace its own file while it runs";
                    continue;
                }
                try
                {
                    Restore(zip, target, path, archived.FirstOrDefault(file => file.Path.Equals(path, StringComparison.OrdinalIgnoreCase))?.Sha256);
                    restored.Add(path);
                }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    problem = $"{path}: {e.Message}";
                }
            }
            return new CoreCheckResult(list.Count, damaged, restored, archivePath, problem);
        }
        finally
        {
            zip?.Dispose();
        }
    }

    /// <summary>Whether an installed file is the one the list names: present, and of the listed size and SHA-256. A replaceable
    /// file only has to be present where the packs are deployed into the same folder.</summary>
    private static bool Intact(string folder, CoreFile file, bool packsDeployed)
    {
        var path = Path.Combine(folder, Local(file.Path));
        try
        {
            if (!File.Exists(path)) return false;
            if (file.Replaceable && packsDeployed) return true;
            if (new FileInfo(path).Length != file.Size) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Writes one file from the archive over the installed one, after checking the archive's copy against the listed SHA-256.</summary>
    private static void Restore(ZipArchive zip, string target, string path, string? sha256)
    {
        var entry = zip.GetEntry("Data/" + path) ?? throw new InvalidDataException("not in the install archive");
        var bytes = ReadEntry(entry);
        if (sha256 != null && !Convert.ToHexString(SHA256.HashData(bytes)).Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("the install archive's copy does not match the file list");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".artmixer-restore";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, target, overwrite: true);
    }

    /// <summary>The archive Mod Organizer 2 recorded for the mod in its meta.ini (a full path, or a file name in the instance's
    /// downloads folder), or the reason there is none.</summary>
    private static (string? Path, string? Problem) FindArchive(string folder)
    {
        var meta = Path.Combine(folder, "meta.ini");
        if (!File.Exists(meta)) return (null, NoRecord);
        string? recorded = null;
        try
        {
            foreach (var line in File.ReadLines(meta))
            {
                if (line.StartsWith("installationFile=", StringComparison.OrdinalIgnoreCase)) recorded = Managers.Unwrap(line["installationFile=".Length..].Trim());
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, NoRecord);
        }
        if (string.IsNullOrEmpty(recorded)) return (null, NoRecord);
        if (!Path.IsPathRooted(recorded))
        {
            var mods = Path.GetDirectoryName(folder.TrimEnd('\\', '/'));
            var instance = Managers.Detect().FirstOrDefault(d => d.Kind == "mo2" && Managers.SameFolder(d.ModsFolder, mods));
            if (instance?.DownloadsFolder == null) return (null, $"the install archive {recorded} was not found");
            recorded = Path.Combine(instance.DownloadsFolder, recorded);
        }
        return File.Exists(recorded) ? (recorded, null) : (null, $"the install archive {recorded} is no longer there");
    }

    private static List<CoreFile>? ReadList(string path)
    {
        try
        {
            return File.Exists(path) ? ParseList(File.ReadAllBytes(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The files a list names; null when the bytes are not a file list.</summary>
    private static List<CoreFile>? ParseList(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var files = new List<CoreFile>();
            foreach (var file in document.RootElement.GetProperty("files").EnumerateArray())
            {
                files.Add(new CoreFile(file.GetProperty("path").GetString()!, file.GetProperty("size").GetInt64(), file.GetProperty("sha256").GetString()!,
                    file.TryGetProperty("replaceable", out var replaceable) && replaceable.ValueKind == JsonValueKind.True));
            }
            return files.Count > 0 && files.All(f => !string.IsNullOrEmpty(f.Path) && !string.IsNullOrEmpty(f.Sha256)) ? files : null;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static bool Same(List<CoreFile> a, List<CoreFile> b)
    {
        static HashSet<string> Keys(List<CoreFile> list) => list.Select(f => $"{f.Path.ToLowerInvariant()}|{f.Size}|{f.Sha256.ToLowerInvariant()}").ToHashSet();
        return a.Count == b.Count && Keys(a).SetEquals(Keys(b));
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string Local(string path) => path.Replace('/', '\\');
}
