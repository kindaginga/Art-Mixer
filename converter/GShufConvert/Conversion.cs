using System.IO.Compression;
using System.Security.Cryptography;

namespace GShufConvert;

public sealed class ConvertOptions
{
    public required string GameFolder { get; init; }
    public required string OutputFolder { get; init; }
    public required IReadOnlyList<string> Inputs { get; init; }
    public bool IncludeVanilla { get; init; } = true;
    public bool SplitOptions { get; init; }
    public bool KeepTemp { get; init; }
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
        report.Info($"catalog: {catalog.Slots.Count} vanilla art slots");
        var gameFiles = new GameFiles(options.GameFolder, report);

        var output = options.OutputFolder;
        if (Directory.Exists(output))
        {
            foreach (var stale in new[] { "Materials\\GShuf\\Packs", "Textures\\GShuf\\Packs", "GShuf", "MCM" })
            {
                var path = Path.Combine(output, stale);
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
        }
        Directory.CreateDirectory(output);
        var temp = Path.Combine(Path.GetTempPath(), "GShufConvert");
        if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        Directory.CreateDirectory(temp);

        var writer = new Writer(output, catalog, gameFiles, report);
        var packs = new List<PackOutput>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vanilla" };
        foreach (var input in options.Inputs)
        {
            report.Info($"--- {input}");
            var scanned = new List<(Source Source, List<FoundImage> Images)>();
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in Sources.Load(input, temp, report))
            {
                // A picture that an earlier option group of the same mod already provides (same bytes) is not repeated in later groups.
                var images = Scanner.Scan(source, catalog, report).Where(i => emitted.Add(Convert.ToHexString(SHA1.HashData(source.Files[i.Diffuse].Read())) + "|" + (i.Slot ?? ""))).ToList();
                if (images.Count == 0)
                {
                    report.Info($"{source.Name}: no art found (no vanilla-path textures, no materials with textures, no gshuf-pack.json); skipped");
                    continue;
                }
                scanned.Add((source, images));
            }
            // One pack per mod: FOMOD options pool together (duplicates were already dropped by content), so alternative
            // sets of the same frames become variants the plugin picks among. Split mode keeps one pack per option.
            var small = options.SplitOptions ? scanned.Where(x => x.Images.Count < 4).ToList() : scanned.ToList();
            if (scanned.Count > 1 && small.Count > 1)
            {
                var modName = Sources.DisplayName(Path.GetFileNameWithoutExtension(input.TrimEnd('\\', '/')));
                var merged = new Source { Name = options.SplitOptions ? $"{modName} - Individual pictures" : modName, Origin = input };
                var mergedImages = new List<FoundImage>();
                foreach (var (source, images) in small)
                {
                    var prefix = Sources.Sanitize(source.Name) + "|";
                    foreach (var image in images)
                    {
                        merged.Files[prefix + image.Diffuse] = source.Files[image.Diffuse];
                        if (image.Normal != null && source.Files.TryGetValue(image.Normal, out var normal)) merged.Files[prefix + image.Normal] = normal;
                        if (image.MaterialSource != null && source.Files.TryGetValue(image.MaterialSource, out var material)) merged.Files[prefix + image.MaterialSource] = material;
                        mergedImages.Add(new FoundImage
                        {
                            Category = image.Category, Slot = image.Slot, Width = image.Width, Height = image.Height,
                            Diffuse = prefix + image.Diffuse,
                            Normal = image.Normal != null ? prefix + image.Normal : null,
                            MaterialSource = image.MaterialSource != null ? prefix + image.MaterialSource : null,
                        });
                    }
                }
                report.Info($"{modName}: pooled {small.Count} options ({mergedImages.Count} pictures) into '{merged.Name}'");
                scanned = scanned.Except(small).ToList();
                scanned.Add((merged, mergedImages));
            }
            if (scanned.Count == 0) report.Warn($"{Path.GetFileName(input)}: nothing usable found; is this an art mod?");
            foreach (var (source, images) in scanned)
            {
                var pack = writer.WritePack(source, images, usedIds);
                if (pack.Images.Count > 0) packs.Add(pack);
            }
        }
        if (options.IncludeVanilla) packs.Add(writer.WriteVanillaPack());
        writer.WritePacksJson(packs);
        writer.WriteMcm(packs, SlotsPerCategory);
        File.Copy(CatalogPath, Path.Combine(output, "GShuf", "vanilla-slots.json"), overwrite: true);
        writer.WriteReport();
        if (!options.KeepTemp) { try { Directory.Delete(temp, recursive: true); } catch { /* temp cleanup is best effort */ } }

        if (options.ZipTo != null)
        {
            if (File.Exists(options.ZipTo)) File.Delete(options.ZipTo);
            ZipFile.CreateFromDirectory(output, options.ZipTo, CompressionLevel.Fastest, includeBaseDirectory: false);
            report.Info($"zipped to {options.ZipTo}");
        }
        report.Info($"done: {packs.Count} packs written to {output} ({report.Warnings} warnings)");
        return packs.Count;
    }
}
