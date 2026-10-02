using System.IO.Compression;

namespace GShufConvert;

public sealed class ConvertOptions
{
    public required string GameFolder { get; init; }
    public required string OutputFolder { get; init; }
    public required IReadOnlyList<string> Inputs { get; init; }
    public bool IncludeVanilla { get; init; } = true;
    public bool SplitOptions { get; init; }
    public bool KeepTemp { get; init; }
    /// <summary>Also writes the optional ALR module: the pictures in Automated Loading Screen Replacer's picture folder.</summary>
    public bool AlrImages { get; init; }
    /// <summary>The picture folder ALR reads, when it is not the output's own F4SE\Plugins\ALR_Image_Dir (under Mod Organizer 2
    /// ALR reads only the folder next to its own DLL, in ALR's mod folder).</summary>
    public string? AlrFolder { get; init; }
    /// <summary>When set, the finished mod folder is also zipped to this file (for Vortex's downloads folder).</summary>
    public string? ZipTo { get; init; }
}

/// <summary>Runs one conversion: scans the inputs, writes the pack mod folder, the MCM pages and the report.</summary>
public static class Conversion
{
    public const int SlotsPerCategory = 10;
    public const string OutputModName = "Gallery Shuffle Packs";

    public static string CatalogPath => Path.Combine(AppContext.BaseDirectory, "vanilla-slots.json");

    public static int Run(ConvertOptions options, Report report)
    {
        if (!File.Exists(Path.Combine(options.GameFolder, "Fallout4.exe"))) throw new InvalidOperationException($"Fallout4.exe not found in {options.GameFolder}");
        if (!File.Exists(CatalogPath)) throw new InvalidOperationException($"vanilla-slots.json missing next to the converter ({CatalogPath})");
        if (options.Inputs.Count == 0) throw new InvalidOperationException("no art mods given");

        var catalog = Catalog.Load(CatalogPath);
        report.Info($"catalog: {Report.Count(catalog.Slots.Count, "vanilla art slot", "vanilla art slots")}");
        var gameFiles = new GameFiles(options.GameFolder, report);

        var output = options.OutputFolder;
        if (Directory.Exists(output))
        {
            foreach (var stale in new[] { "Materials\\GShuf\\Packs", "Textures\\GShuf\\Packs", "Meshes\\GShuf\\Packs", "Video\\GShuf\\Packs", "Music\\GShuf", "Sound\\GShuf\\Packs", "Interface\\GShuf\\Packs", "Meshes\\GShuf\\LoadCards", "Materials\\GShuf\\LoadCards", "Textures\\GShuf\\LoadCards", "GShuf", "MCM\\Config\\GalleryShuffle" })
            {
                var path = Path.Combine(output, stale);
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
        }
        Directory.CreateDirectory(output);
        // ALR's picture folder is shared with the player's own pictures, so only files with Gallery Shuffle's prefix go, from the
        // output's copy of the folder and from the folder the pictures go to this time.
        var alrFolder = options.AlrFolder ?? Path.Combine(output, Writer.AlrImageFolder);
        foreach (var folder in new[] { Path.Combine(output, Writer.AlrImageFolder), alrFolder }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, Writer.AlrFilePrefix + "*")) File.Delete(file);
        }
        // Extracts into a folder of its own under the converter's temp folder, marked in use by a lock file held until the end.
        var tempParent = Path.Combine(Path.GetTempPath(), "GShufConvert");
        Directory.CreateDirectory(tempParent);
        SweepTemp(tempParent);
        var runId = Guid.NewGuid().ToString("N")[..12];
        using var runLock = new FileStream(Path.Combine(tempParent, runId + ".lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        var temp = Directory.CreateDirectory(Path.Combine(tempParent, runId)).FullName;
        var writer = new Writer(output, catalog, gameFiles, report);
        List<PackOutput> packs;
        try
        {
            packs = WritePacks(options, report, catalog, gameFiles, writer, alrFolder, temp);
        }
        finally
        {
            if (options.KeepTemp) report.Info($"extracted archives kept in {temp}");
            else DeleteTemp(temp, report);
        }
        writer.WriteReport();

        if (options.ZipTo != null)
        {
            if (File.Exists(options.ZipTo)) File.Delete(options.ZipTo);
            ZipFile.CreateFromDirectory(output, options.ZipTo, CompressionLevel.Fastest, includeBaseDirectory: false);
            report.Info($"zipped to {options.ZipTo}");
        }
        report.Info($"done: {Report.Count(packs.Count, "pack", "packs")} written to {output} ({Report.Count(report.Warnings, "warning", "warnings")})");
        return packs.Count;
    }

    // Leaves out a mod when another converted mod also has everything it would put in its pack (art, menu video, menu theme and
    // interface files), byte for byte and for the same object: a cut-down download of the same mod. Of mods with the same content the first is kept.
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

    // Scans every input and writes the packs, the vanilla pack, the loading screen cards, packs.json, the MCM pages and the report.
    private static List<PackOutput> WritePacks(ConvertOptions options, Report report, Catalog catalog, GameFiles gameFiles, Writer writer, string alrFolder, string temp)
    {
        var packs = new List<PackOutput>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vanilla" };
        var restores = new List<Scanner.Restore>();
        var found = new List<(Source Source, List<FoundImage> Images)>();
        var keys = new Dictionary<FoundImage, string>(ReferenceEqualityComparer.Instance);
        foreach (var input in options.Inputs)
        {
            report.Info($"--- {input}");
            var scanned = new List<(Source Source, List<FoundImage> Images)>();
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in Sources.Load(input, temp, report))
            {
                writer.NoteOverrides(source);
                var interfaceFiles = Scanner.FindInterfaceFiles(source, gameFiles);
                // Something an earlier option group of the same mod already provides (same bytes) is not repeated in later groups.
                var images = new List<FoundImage>();
                foreach (var image in Scanner.Scan(source, catalog, gameFiles, report, restores))
                {
                    var key = image.ContentKey(source);
                    if (!emitted.Add(key)) continue;
                    keys[image] = key;
                    images.Add(image);
                }
                if (images.Count == 0 && Scanner.FindMenuVideo(source) == null && Scanner.FindMenuMusic(source) == null && interfaceFiles.Count == 0)
                {
                    report.Info($"{source.Name}: no art found (no vanilla-path textures or materials, no new materials with textures, no gshuf-pack.json); skipped");
                    continue;
                }
                scanned.Add((source, images));
            }
            // One pack per mod: FOMOD options pool together (duplicates were already dropped by content), so alternative
            // sets of the same frames become variants the plugin picks among. Split mode keeps one pack per option.
            var small = options.SplitOptions ? scanned.Where(x => x.Images.Count < 4 && Scanner.FindMenuVideo(x.Source) == null && Scanner.FindMenuMusic(x.Source) == null).ToList() : scanned.ToList();
            if (scanned.Count > 1 && small.Count > 1)
            {
                var modName = Sources.DisplayName(Sources.InputStem(input));
                var merged = new Source { Name = options.SplitOptions ? $"{modName} - Individual pictures" : modName, Origin = input };
                var mergedImages = new List<FoundImage>();
                foreach (var (source, images) in small)
                {
                    var video = Scanner.FindMenuVideo(source);
                    if (video != null && !merged.Files.ContainsKey(video)) merged.Files[video] = source.Files[video];
                    var music = Scanner.FindMenuMusic(source);
                    if (music != null && !merged.Files.ContainsKey(music)) merged.Files[music] = source.Files[music];
                    foreach (var file in Scanner.FindInterfaceFiles(source, gameFiles)) merged.Files.TryAdd(file.Source, source.Files[file.Source]);
                    var prefix = Sources.Sanitize(source.Name) + "|";
                    foreach (var image in images)
                    {
                        var copy = image.Prefixed(prefix, source, merged);
                        keys[copy] = keys[image];
                        mergedImages.Add(copy);
                    }
                }
                report.Info($"{modName}: pooled {Report.Count(small.Count, "option", "options")} ({Report.Count(mergedImages.Count, "item", "items")}) into '{merged.Name}'");
                scanned = scanned.Except(small).ToList();
                scanned.Add((merged, mergedImages));
            }
            if (scanned.Count == 0) report.Warn($"{Path.GetFileName(input)}: nothing to convert: no art that replaces the game's own (a standalone art mod shows its art through its own plugin; keep it installed)");
            found.AddRange(scanned);
        }
        foreach (var (source, images) in WithoutDuplicates(found, keys, gameFiles, report))
        {
            var pack = writer.WritePack(source, images, usedIds, Scanner.FindMenuVideo(source), Scanner.FindMenuMusic(source), Scanner.FindInterfaceFiles(source, gameFiles));
            if (pack.Images.Count > 0 || pack.Video != null || pack.Music != null || pack.Movies.Count > 0) packs.Add(pack);
        }
        if (options.IncludeVanilla) packs.Add(writer.WriteVanillaPack(packs));
        writer.SplitMagazineBacks(packs);
        writer.WriteLoadCards(packs);
        if (options.AlrImages) writer.WriteAlrImages(packs, alrFolder);
        writer.SplitOtherKinds(packs);
        writer.WritePacksJson(packs, restores, SlotsPerCategory);
        writer.WriteMcm(packs, SlotsPerCategory);
        writer.WriteCatalog(CatalogPath);
        return packs;
    }

    // Deletes the run folders under the converter's temp folder whose lock file no running conversion holds, with those lock files.
    private static void SweepTemp(string parent)
    {
        foreach (var folder in Directory.EnumerateDirectories(parent))
        {
            try
            {
                var lockPath = folder + ".lock";
                if (File.Exists(lockPath))
                {
                    using (new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    File.Delete(lockPath);
                }
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Skips the folder of a conversion that is still running.
            }
        }
    }

    // Deletes a run's extraction folder, warning when a file in it is held open.
    private static void DeleteTemp(string temp, Report report)
    {
        try
        {
            Directory.Delete(temp, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            report.Warn($"could not delete the extraction folder {temp}: {e.Message}");
        }
    }
}
