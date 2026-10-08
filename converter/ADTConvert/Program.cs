using System.Reflection;
using System.Runtime.InteropServices;

namespace ADTConvert;

public static class Program
{
    // The name the converter shows in its window, its error box and its help text.
    internal const string Title = "Art Distribution Tool";
    /// <summary>The version and the commit the build comes from, "1.0.0+ae01711" ("-modified" when built from changed files).</summary>
    internal static string Build => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown build";

    // Without arguments the converter opens its window; with arguments it runs on the command line.
    private static readonly string LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArtDistributionTool", "converter.log");

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
                MessageBox.Show(e.ToString(), Title);
                return 1;
            }
        }

        AttachConsole(-1);
        string? game = null;
        string? output = null;
        string? zipTo = null;
        var inputs = new List<string>();
        var listInstalled = false;
        string? mo2Instance = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--mo2-instance" or "--game" or "--out" or "--zip" && i + 1 == args.Length)
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
            Conversion.Run(new ConvertOptions { GameFolder = game, OutputFolder = output, Inputs = inputs, ZipTo = zipTo }, report);
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
        Console.WriteLine("  game folder: " + Place(destination.GameFolder));
        Console.WriteLine("  mod folders: " + Place(destination.ModsFolder));
        if (destination.ProfileFolder != null) Console.WriteLine("  profile:     " + Place(destination.ProfileFolder));
        var scan = InstalledMods.Find(destination, new Report { Sink = Console.WriteLine });
        Console.WriteLine($"{Report.Count(scan.Mods.Count, "art mod", "art mods")} found in {Report.Count(scan.ModFolders, "installed mod", "installed mods")}:");
        foreach (var mod in scan.Mods) Console.WriteLine($"  {mod.Name} | {mod.Group} | {(mod.Status.Length > 0 ? mod.Status : "installed")} | {mod.Summary} | {mod.Location}");
        return 0;
    }

    private static void Usage()
    {
        Console.WriteLine(Title + ". Start it without arguments for the window, or:");
        Console.WriteLine("  \"Art Distribution Tool.exe\" --game \"<Fallout 4 folder>\" --out \"<mods folder>\\Art Distribution Tool Packs\" <installed mod folder> ...");
        Console.WriteLine("  \"Art Distribution Tool.exe\" --list-installed [--mo2-instance <name> | <MO2 instance folder or Vortex data folder>]");
        Console.WriteLine("                     list the installed mods there that hold art, without converting anything");
        Console.WriteLine("  --zip <file>       also zip the output (for Vortex's downloads folder)");
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
