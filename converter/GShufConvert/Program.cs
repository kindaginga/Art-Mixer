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
        var alrImages = false;
        string? alrFolder = null;
        var listInstalled = false;
        string? mo2Instance = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--mo2-instance" or "--game" or "--out" or "--zip" or "--alr-dir" && i + 1 == args.Length)
            {
                Console.WriteLine($"{args[i]} needs a value.");
                Usage();
                return 2;
            }
            switch (args[i])
            {
                case "--list-installed": listInstalled = true; break;
                case "--mo2-instance": mo2Instance = args[++i]; break;
                case "--game": game = args[++i]; break;
                case "--out": output = args[++i]; break;
                case "--zip": zipTo = args[++i]; break;
                case "--no-vanilla": includeVanilla = false; break;
                case "--keep-temp": keepTemp = true; break;
                case "--split-options": splitOptions = true; break;
                case "--alr": alrImages = true; break;
                case "--alr-dir": alrImages = true; alrFolder = args[++i]; break;
                case "--help" or "-h" or "/?": Usage(); return 0;
                default: inputs.Add(args[i]); break;
            }
        }
        if (listInstalled)
        {
            try { return ListInstalled(mo2Instance, inputs); }
            catch (Exception e) { Console.WriteLine("failed: " + e.Message); return 1; }
        }
        if (game == null || output == null || inputs.Count == 0)
        {
            Usage();
            return 2;
        }
        var report = new Report { Sink = Console.WriteLine };
        try
        {
            Conversion.Run(new ConvertOptions { GameFolder = game, OutputFolder = output, Inputs = inputs, IncludeVanilla = includeVanilla, KeepTemp = keepTemp, SplitOptions = splitOptions, AlrImages = alrImages, AlrFolder = alrFolder, ZipTo = zipTo }, report);
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine("failed: " + e.Message);
            return 1;
        }
    }

    /// <summary>Prints the art mods installed in a Mod Organizer 2 instance or Vortex: the named instance, the folder given, or
    /// the manager the converter runs from.</summary>
    private static int ListInstalled(string? mo2Instance, List<string> folders)
    {
        Destination? destination;
        if (mo2Instance != null) destination = Managers.Mo2Instance(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModOrganizer", mo2Instance));
        else if (folders.Count > 0) destination = Managers.FromFolder(folders[0]);
        else
        {
            var detected = Managers.Detect();
            destination = Managers.Preferred(detected) ?? (detected.Count == 1 ? detected[0] : null);
            if (destination == null && detected.Count > 1)
            {
                Console.WriteLine($"{detected.Count} mod manager setups found; name one with --mo2-instance <name> or give its folder:");
                foreach (var d in detected) Console.WriteLine("  " + d.Label);
                return 2;
            }
        }
        if (destination == null)
        {
            Console.WriteLine("No Fallout 4 Mod Organizer 2 instance or Vortex found there.");
            return 2;
        }
        string Place(string? folder) => folder == null ? "none" : Directory.Exists(folder) ? folder : folder + " (not found)";
        Console.WriteLine(destination.Label);
        Console.WriteLine("  mod folders: " + Place(destination.ModsFolder));
        Console.WriteLine("  downloads:   " + Place(destination.DownloadsFolder));
        if (destination.ProfileFolder != null) Console.WriteLine("  profile:     " + Place(destination.ProfileFolder));
        var scan = InstalledMods.Find(destination, new Report { Sink = Console.WriteLine });
        Console.WriteLine($"{Report.Count(scan.Mods.Count, "art mod", "art mods")} found in {Report.Count(scan.ModFolders, "mod folder", "mod folders")} and {Report.Count(scan.Downloads, "download", "downloads")}:");
        foreach (var mod in scan.Mods) Console.WriteLine($"  {mod.Name} | {mod.Where}{(mod.Status.Length > 0 ? ", " + mod.Status : "")} | {mod.Summary} | {mod.Location}");
        return 0;
    }

    private static void Usage()
    {
        Console.WriteLine("Gallery Shuffle Converter. Start it without arguments for the window, or:");
        Console.WriteLine("  GalleryShuffleConverter --game \"<Fallout 4 folder>\" --out \"<mods folder>\\Gallery Shuffle Packs\" <art mod folder or archive> ...");
        Console.WriteLine("  GalleryShuffleConverter --list-installed [--mo2-instance <name> | <MO2 instance folder or Vortex data folder>]");
        Console.WriteLine("                     list the mod folders and downloads there that hold art, without converting anything");
        Console.WriteLine("  --zip <file>       also zip the output (for Vortex's downloads folder)");
        Console.WriteLine("  --no-vanilla       skip the vanilla pack");
        Console.WriteLine("  --split-options    each alternative picture set a mod's installer offers becomes its own pack");
        Console.WriteLine("  --alr              also put the pictures in the picture folder of ALR (Automated Loading Screen Replacer)");
        Console.WriteLine("  --alr-dir <folder> the same, into that folder (under Mod Organizer 2: <ALR's mod>\\F4SE\\Plugins\\ALR_Image_Dir)");
        Console.WriteLine("  --keep-temp        keep the extracted archives in the temp folder for inspection");
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
