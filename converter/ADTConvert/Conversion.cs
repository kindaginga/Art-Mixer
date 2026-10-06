using System.IO.Compression;

namespace ADTConvert;

public sealed class ConvertOptions
{
    public required string GameFolder { get; init; }
    public required string OutputFolder { get; init; }
    /// <summary>The folders of the installed mods to convert.</summary>
    public required IReadOnlyList<string> Inputs { get; init; }
    /// <summary>When set, the finished mod folder is also zipped to this file (for Vortex's downloads folder).</summary>
    public string? ZipTo { get; init; }
    /// <summary>The folders of the installed art mods the mod manager enables that are not converted.</summary>
    public IReadOnlyList<string> EnabledNotConverted { get; init; } = [];
    /// <summary>What the window lists as each input's contents, by folder; recorded with the converted mods.</summary>
    public IReadOnlyDictionary<string, string> Summaries { get; init; } = new Dictionary<string, string>();
    /// <summary>The list group the window shows each input in, by folder; recorded with the converted mods.</summary>
    public IReadOnlyDictionary<string, string> Groups { get; init; } = new Dictionary<string, string>();
}

/// <summary>A mod the packs were converted from, as packs.json records it: its name, its folder and its contents line.</summary>
public sealed record ConvertedMod(string Name, string Folder, string Contains, string Group = "");

/// <summary>What a conversion wrote: the number of packs, one warning for each enabled mod that is not converted and shares objects with the converted mods,
/// and the message about the mod's own files when some were missing or changed.</summary>
public sealed record ConversionResult(int Packs, IReadOnlyList<string> SharedWarnings, string? OwnFilesMessage = null);

/// <summary>Runs one conversion: scans the installed mods, writes the pack mod folder, the MCM pages and the report.</summary>
public static class Conversion
{
    // Slots one page or list may have: the Papyrus array limit, as the plugin's kMaxSlots.
    public const int SlotsPerCategory = 128;
    public const string OutputModName = "Art Distribution Tool Packs";

    public static string CatalogPath => Path.Combine(AppContext.BaseDirectory, "vanilla-slots.json");

    public static ConversionResult Run(ConvertOptions options, Report report)
    {
        if (!File.Exists(Path.Combine(options.GameFolder, "Fallout4.exe"))) throw new InvalidOperationException($"Fallout4.exe not found in {options.GameFolder}");
        var ownFiles = CoreCheck.Run(report);
        if (!File.Exists(CatalogPath)) throw new InvalidOperationException($"vanilla-slots.json missing next to the converter ({CatalogPath})");
        if (options.Inputs.Count == 0) throw new InvalidOperationException("no art mods given");

        var catalog = Catalog.Load(CatalogPath);
        report.Info($"catalog: {Report.Count(catalog.Slots.Count, "vanilla art slot", "vanilla art slots")}");
        var gameFiles = new GameFiles(options.GameFolder, report);

        var output = options.OutputFolder;
        if (Directory.Exists(output))
        {
            foreach (var stale in new[] { "Materials\\ADT\\Packs", "Textures\\ADT\\Packs", "Meshes\\ADT\\Packs", "Video\\ADT\\Packs", "Music\\ADT", "Interface\\ADT\\Packs", "Meshes\\ADT\\LoadCards", "Materials\\ADT\\LoadCards", "Textures\\ADT\\LoadCards", "ADT", "MCM\\Config\\ArtDistributionTool" })
            {
                var path = Path.Combine(output, stale);
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
        }
        Directory.CreateDirectory(output);
        var writer = new Writer(output, catalog, gameFiles, report);
        var (packs, sharedWarnings) = WritePacks(options, report, catalog, gameFiles, writer);
        writer.WriteReport();

        if (options.ZipTo != null)
        {
            if (File.Exists(options.ZipTo)) File.Delete(options.ZipTo);
            ZipFile.CreateFromDirectory(output, options.ZipTo, CompressionLevel.Fastest, includeBaseDirectory: false);
            report.Info($"zipped to {options.ZipTo}");
        }
        report.Info($"done: {Report.Count(packs.Count, "pack", "packs")} written to {output} ({Report.Count(report.Warnings, "warning", "warnings")})");
        return new ConversionResult(packs.Count, sharedWarnings, ownFiles?.Message);
    }

    /// <summary>The mods the packs in an output folder were converted from and when, from its packs.json; null when the folder
    /// holds no packs or its packs.json names no mods.</summary>
    public static (string Generated, List<ConvertedMod> Mods)? ReadConverted(string outputFolder)
    {
        try
        {
            var path = Path.Combine(outputFolder, "ADT", "packs.json");
            if (outputFolder.Length == 0 || !File.Exists(path)) return null;
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
            if (!document.RootElement.TryGetProperty("mods", out var rows)) return null;
            var mods = rows.EnumerateArray().Select(row => new ConvertedMod(row.GetProperty("name").GetString() ?? "", row.GetProperty("folder").GetString() ?? "", row.GetProperty("contains").GetString() ?? "", row.TryGetProperty("group", out var group) ? group.GetString() ?? "" : "")).ToList();
            var generated = document.RootElement.TryGetProperty("generated", out var stamp) ? stamp.GetString() ?? "" : "";
            return mods.Count > 0 ? (generated, mods) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    // Warns about each enabled mod that is not converted and replaces objects the converted mods also replace, naming the mod and the count.
    private static List<string> WarnShared(ConvertOptions options, List<(Source Source, List<FoundImage> Images)> found, Catalog catalog, GameFiles gameFiles, Report report)
    {
        static IEnumerable<string> Slots(IEnumerable<FoundImage> images) => images.Select(i => i.Slot).OfType<string>().Where(s => s.Length > 0);
        var converted = Slots(found.SelectMany(f => f.Images)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        foreach (var folder in options.EnabledNotConverted)
        {
            var quiet = new Report();
            if (Sources.Load(folder, quiet) is not { } source) continue;
            var shared = Slots(Scanner.Scan(source, catalog, gameFiles, quiet, [])).Where(converted.Contains).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (shared == 0) continue;
            var warning = $"{source.Name}: enabled and not converted. It shares {Report.Count(shared, "object", "objects")} with the mods being converted.";
            report.Warn(warning);
            warnings.Add(warning);
        }
        return warnings;
    }

    // Leaves out a mod when another converted mod also has everything it would put in its pack (art, menu video, menu theme and
    // interface files), byte for byte and for the same object: a cut-down copy of the same mod. Of mods with the same content the first is kept.
    private static List<(Source Source, List<FoundImage> Images)> WithoutDuplicates(List<(Source Source, List<FoundImage> Images)> found, Dictionary<FoundImage, string> keys, GameFiles gameFiles, Report report)
    {
        static string Hash(Source source, string path) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(source.Files[path].Read()));
        var sets = found.Select(f =>
        {
            var set = f.Images.Select(i => keys[i]).ToHashSet(StringComparer.Ordinal);
            if (Scanner.FindMenuVideo(f.Source) is { } video) set.Add("video|" + Hash(f.Source, video));
            if (Scanner.FindMenuMusic(f.Source) is { } music) set.Add("music|" + Hash(f.Source, music));
            foreach (var file in Scanner.FindInterfaceFiles(f.Source, gameFiles)) set.Add($"interface|{file.GamePath}|{Hash(f.Source, file.Source)}");
            return set;
        }).ToList();
        var kept = new List<(Source Source, List<FoundImage> Images)>();
        for (var b = 0; b < found.Count; b++)
        {
            var source = found[b].Source;
            var covers = sets[b].Count > 0
                ? Enumerable.Range(0, found.Count).Where(a => a != b && sets[b].IsSubsetOf(sets[a]) && (sets[a].Count > sets[b].Count || a < b)).OrderByDescending(a => sets[a].Count).ThenBy(a => a).ToList()
                : [];
            if (covers.Count == 0)
            {
                kept.Add(found[b]);
                continue;
            }
            report.Warn($"{source.Name}: left out. '{found[covers[0]].Source.Name}' already has the same {Report.Count(sets[b].Count, "item", "items")}, file for file, so this mod would add nothing.");
        }
        return kept;
    }

    // Joins each patch to its host: a mod from the same Nexus page as a larger mod of another input, replacing at least one of
    // the same objects, becomes part of that mod's pack. Where both replace an object, the smaller mod's version is used.
    private static List<(Source Source, List<FoundImage> Images)> WithPatches(List<(Source Source, List<FoundImage> Images)> found, GameFiles gameFiles, Report report)
    {
        var pages = found.Select(f => Sources.NexusPage(f.Source.Origin)).ToList();
        var slots = found.Select(f => f.Images.Select(i => i.Slot).OfType<string>().Where(s => s.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase)).ToList();
        bool Larger(int a, int b) => found[a].Images.Count > found[b].Images.Count || (found[a].Images.Count == found[b].Images.Count && a < b);
        var host = Enumerable.Range(0, found.Count).Select(b => pages[b] == null ? b : Enumerable.Range(0, found.Count)
            .Where(a => a != b && pages[a] == pages[b] && !found[a].Source.Origin.Equals(found[b].Source.Origin, StringComparison.OrdinalIgnoreCase) && Larger(a, b) && slots[a].Overlaps(slots[b]))
            .OrderByDescending(a => found[a].Images.Count).ThenBy(a => a).DefaultIfEmpty(b).First()).ToList();
        int Root(int i)
        {
            while (host[i] != i) i = host[i];
            return i;
        }
        var joined = new List<(Source Source, List<FoundImage> Images)>();
        for (var h = 0; h < found.Count; h++)
        {
            if (host[h] != h) continue;
            var members = Enumerable.Range(0, found.Count).Where(i => i != h && Root(i) == h).OrderBy(i => found[i].Images.Count).ThenByDescending(i => i).ToList();
            if (members.Count == 0)
            {
                joined.Add(found[h]);
                continue;
            }
            members.Add(h);
            var merged = new Source { Name = found[h].Source.Name, Origin = found[h].Source.Origin };
            var images = new List<FoundImage>();
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in members)
            {
                var (source, own) = found[m];
                if (Scanner.FindMenuVideo(merged) == null && Scanner.FindMenuVideo(source) is { } video) merged.Files[video] = source.Files[video];
                if (Scanner.FindMenuMusic(merged) == null && Scanner.FindMenuMusic(source) is { } music) merged.Files[music] = source.Files[music];
                foreach (var file in Scanner.FindInterfaceFiles(source, gameFiles)) merged.Files.TryAdd(file.Source, source.Files[file.Source]);
                foreach (var image in own.Where(i => string.IsNullOrEmpty(i.Slot) || !taken.Contains(i.Slot)))
                    images.Add(image.Prefixed($"{m}-{Sources.Sanitize(source.Name)}|", source, merged));
                taken.UnionWith(slots[m]);
                if (m != h) report.Info($"{source.Name}: joined to the pack '{merged.Name}' as its patch. Both come from Nexus page {pages[m]}, and it replaces {Report.Count(slots[m].Count(slots[host[m]].Contains), "object", "objects")} that '{found[host[m]].Source.Name}' also replaces; there the patch's version is used.");
            }
            joined.Add((merged, images));
        }
        return joined;
    }

    // Scans every input and writes the packs, the vanilla pack, the loading screen cards, packs.json, the MCM pages and the report.
    private static (List<PackOutput> Packs, List<string> SharedWarnings) WritePacks(ConvertOptions options, Report report, Catalog catalog, GameFiles gameFiles, Writer writer)
    {
        var packs = new List<PackOutput>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vanilla" };
        var restores = new List<Scanner.Restore>();
        var found = new List<(Source Source, List<FoundImage> Images)>();
        var keys = new Dictionary<FoundImage, string>(ReferenceEqualityComparer.Instance);
        var mods = new List<ConvertedMod>();
        foreach (var input in options.Inputs)
        {
            report.Info($"--- {input}{(Sources.NexusPage(input) is { } page ? $" (Nexus page {page})" : "")}");
            if (Sources.Load(input, report) is not { } source) continue;
            writer.NoteOverrides(source);
            // The same replacement from two plugins of one mod (an .esl and an .esp copy) counts once.
            var images = new List<FoundImage>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var image in Scanner.Scan(source, catalog, gameFiles, report, restores))
            {
                var key = image.ContentKey(source);
                if (!seen.Add(key)) continue;
                keys[image] = key;
                images.Add(image);
            }
            if (images.Count == 0 && Scanner.FindMenuVideo(source) == null && Scanner.FindMenuMusic(source) == null && Scanner.FindInterfaceFiles(source, gameFiles).Count == 0)
            {
                report.Warn($"{source.Name}: nothing to convert: no art that replaces the game's own (a standalone art mod shows its art through its own plugin; keep it enabled)");
                continue;
            }
            found.Add((source, images));
            mods.Add(new ConvertedMod(source.Name, input, options.Summaries.GetValueOrDefault(input) ?? Report.Count(images.Count, "item", "items"), options.Groups.GetValueOrDefault(input) ?? ""));
        }
        var sharedWarnings = WarnShared(options, found, catalog, gameFiles, report);
        foreach (var (source, images) in WithPatches(WithoutDuplicates(found, keys, gameFiles, report), gameFiles, report))
        {
            var pack = writer.WritePack(source, images, usedIds, Scanner.FindMenuVideo(source), Scanner.FindMenuMusic(source), Scanner.FindInterfaceFiles(source, gameFiles));
            if (pack.Images.Count > 0 || pack.Video != null || pack.Music != null || pack.Movies.Count > 0) packs.Add(pack);
        }
        packs.Add(writer.WriteVanillaPack(packs));
        writer.SplitMagazineBacks(packs);
        writer.WriteLoadCards(packs);
        writer.WritePacksJson(packs, restores, SlotsPerCategory, mods);
        writer.WriteMcm(packs, SlotsPerCategory);
        writer.WriteCatalog(CatalogPath);
        return (packs, sharedWarnings);
    }
}
