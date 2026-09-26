using System.Security.Cryptography;
using GShufConvert;

const int slotsPerCategory = 10;

string? game = null;
string? output = null;
var inputs = new List<string>();
var includeVanilla = true;
var keepTemp = false;
var splitOptions = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--game": game = args[++i]; break;
        case "--out": output = args[++i]; break;
        case "--no-vanilla": includeVanilla = false; break;
        case "--keep-temp": keepTemp = true; break;
        case "--split-options": splitOptions = true; break;
        case "--help" or "-h" or "/?": Usage(); return 0;
        default: inputs.Add(args[i]); break;
    }
}
if (game == null || output == null || inputs.Count == 0)
{
    Usage();
    return 2;
}
if (!File.Exists(Path.Combine(game, "Fallout4.exe")))
{
    Console.WriteLine($"Fallout4.exe not found in {game}");
    return 2;
}

var report = new Report();
var catalogPath = Path.Combine(AppContext.BaseDirectory, "vanilla-slots.json");
if (!File.Exists(catalogPath))
{
    Console.WriteLine($"vanilla-slots.json missing next to the converter ({catalogPath})");
    return 2;
}
var catalog = Catalog.Load(catalogPath);
report.Info($"catalog: {catalog.Slots.Count} vanilla art slots");
var gameFiles = new GameFiles(game, report);

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
foreach (var input in inputs)
{
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
    // sets of the same frames become variants the plugin picks among. --split-options keeps one pack per option.
    var small = splitOptions ? scanned.Where(x => x.Images.Count < 4).ToList() : scanned.ToList();
    if (scanned.Count > 1 && small.Count > 1)
    {
        var modName = Sources.DisplayName(Path.GetFileNameWithoutExtension(input.TrimEnd('\\', '/')));
        var merged = new Source { Name = splitOptions ? $"{modName} - Individual pictures" : modName, Origin = input };
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
    foreach (var (source, images) in scanned)
    {
        var pack = writer.WritePack(source, images, usedIds);
        if (pack.Images.Count > 0) packs.Add(pack);
    }
}
if (includeVanilla) packs.Add(writer.WriteVanillaPack());
writer.WritePacksJson(packs);
writer.WriteMcm(packs, slotsPerCategory);
File.Copy(catalogPath, Path.Combine(output, "GShuf", "vanilla-slots.json"), overwrite: true);
writer.WriteReport();
if (!keepTemp) { try { Directory.Delete(temp, recursive: true); } catch { /* temp cleanup is best effort */ } }

report.Info($"done: {packs.Count} packs written to {output} ({report.Warnings} warnings)");
return 0;

static void Usage()
{
    Console.WriteLine("GShufConvert --game \"<Fallout 4 folder>\" --out \"<mods folder>\\Gallery Shuffle Packs\" <art mod folder or archive> ...");
    Console.WriteLine("  Converts art mods (loose folders, .zip/.7z/.rar archives, FOMODs, BA2 archives) into Gallery Shuffle packs,");
    Console.WriteLine("  copies the vanilla art into its own pack, and writes the MCM pages. Run it again after adding or removing mods.");
    Console.WriteLine("  --no-vanilla   skip the vanilla pack");
    Console.WriteLine("  --keep-temp    keep the extracted archives in the temp folder for inspection");
    Console.WriteLine("  --split-options   one pack per FOMOD option instead of one pack per mod");
}
