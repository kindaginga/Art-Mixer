using System.Runtime.InteropServices;

namespace GShufConvert;

public static class Program
{
    // Without arguments the converter opens its window; with arguments it runs on the command line.
    private static readonly string LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GalleryShuffle", "converter.log");

    // Startup diagnostics: what the process was started with and any crash, for launches through a mod manager.
    private static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
        }
        catch { /* logging is best effort */ }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("unhandled: " + e.ExceptionObject);
        Log($"start: exe '{Environment.ProcessPath}' base '{AppContext.BaseDirectory}' cwd '{Environment.CurrentDirectory}' args [{string.Join(" | ", args)}]");
        if (args.Length == 0)
        {
            try
            {
                ApplicationConfiguration.Initialize();
                Application.ThreadException += (_, e) => Log("thread exception: " + e.Exception);
                Application.Run(new MainForm());
                Log("window closed");
                return 0;
            }
            catch (Exception e)
            {
                Log("window failed: " + e);
                MessageBox.Show(e.ToString(), "Gallery Shuffle Converter");
                return 1;
            }
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
    }

    private static void Usage()
    {
        Console.WriteLine("Gallery Shuffle Converter. Start it without arguments for the window, or:");
        Console.WriteLine("  GalleryShuffleConverter --game \"<Fallout 4 folder>\" --out \"<mods folder>\\Gallery Shuffle Packs\" <art mod folder or archive> ...");
        Console.WriteLine("  --zip <file>       also zip the output (for Vortex's downloads folder)");
        Console.WriteLine("  --no-vanilla       skip the vanilla pack");
        Console.WriteLine("  --split-options    one pack per FOMOD option instead of one pack per mod");
        Console.WriteLine("  --keep-temp        keep the extracted archives in the temp folder for inspection");
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
