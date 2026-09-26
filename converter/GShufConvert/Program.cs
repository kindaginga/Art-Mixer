using GShufConvert;

const int slotsPerCategory = 10;

string? game = null;
string? output = null;
var inputs = new List<string>();
var includeVanilla = true;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--game": game = args[++i]; break;
        case "--out": output = args[++i]; break;
        case "--no-vanilla": includeVanilla = false; break;
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
    foreach (var source in Sources.Load(input, temp, report))
    {
        var images = Scanner.Scan(source, catalog, report);
        if (images.Count == 0)
        {
            report.Warn($"{source.Name}: no art found (no vanilla-path textures, no materials with textures, no gshuf-pack.json); skipped");
            continue;
        }
        var pack = writer.WritePack(source, images, usedIds);
        if (pack.Images.Count > 0) packs.Add(pack);
    }
}
if (includeVanilla) packs.Add(writer.WriteVanillaPack());
writer.WritePacksJson(packs);
writer.WriteMcm(packs, slotsPerCategory);
File.Copy(catalogPath, Path.Combine(output, "GShuf", "vanilla-slots.json"), overwrite: true);
writer.WriteReport();
try { Directory.Delete(temp, recursive: true); } catch { /* temp cleanup is best effort */ }

report.Info($"done: {packs.Count} packs written to {output} ({report.Warnings} warnings)");
return 0;

static void Usage()
{
    Console.WriteLine("GShufConvert --game \"<Fallout 4 folder>\" --out \"<mods folder>\\Gallery Shuffle Packs\" <art mod folder or archive> ...");
    Console.WriteLine("  Converts art mods (loose folders, .zip/.7z/.rar archives, FOMODs, BA2 archives) into Gallery Shuffle packs,");
    Console.WriteLine("  copies the vanilla art into its own pack, and writes the MCM pages. Run it again after adding or removing mods.");
    Console.WriteLine("  --no-vanilla   skip the vanilla pack");
}
