using System.Runtime.InteropServices;
using GShufConvert;

// Without arguments the converter opens its window; with arguments it runs on the command line.
if (args.Length == 0)
{
    ApplicationConfiguration.Initialize();
    Application.Run(new MainForm());
    return 0;
}

AttachConsole(-1);
string? game = null;
string? output = null;
string? zipTo = null;
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
        case "--zip": zipTo = args[++i]; break;
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
var report = new Report { Sink = Console.WriteLine };
try
{
    Conversion.Run(new ConvertOptions { GameFolder = game, OutputFolder = output, Inputs = inputs, IncludeVanilla = includeVanilla, KeepTemp = keepTemp, SplitOptions = splitOptions, ZipTo = zipTo }, report);
    return 0;
}
catch (Exception e)
{
    Console.WriteLine("failed: " + e.Message);
    return 1;
}

static void Usage()
{
    Console.WriteLine("Gallery Shuffle Converter. Start it without arguments for the window, or:");
    Console.WriteLine("  GalleryShuffleConverter --game \"<Fallout 4 folder>\" --out \"<mods folder>\\Gallery Shuffle Packs\" <art mod folder or archive> ...");
    Console.WriteLine("  --zip <file>       also zip the output (for Vortex's downloads folder)");
    Console.WriteLine("  --no-vanilla       skip the vanilla pack");
    Console.WriteLine("  --split-options    one pack per FOMOD option instead of one pack per mod");
    Console.WriteLine("  --keep-temp        keep the extracted archives in the temp folder for inspection");
}

[DllImport("kernel32.dll")]
static extern bool AttachConsole(int processId);
