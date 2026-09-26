using Mutagen.Bethesda;
using Mutagen.Bethesda.Fallout4;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;

// Writes GalleryShuffle.esl: one start-game-enabled quest at 0x800 carrying the GShuf:Config script.
var output = args.Length > 0 ? args[0] : Path.Combine("..", "..", "mod", "Data", "GalleryShuffle.esl");
var modKey = ModKey.FromFileName("GalleryShuffle.esl");
var mod = new Fallout4Mod(modKey, Fallout4Release.Fallout4);
mod.ModHeader.Flags |= Fallout4ModHeader.HeaderFlag.Small;
mod.ModHeader.Author = "Gallery Shuffle";
mod.ModHeader.Description = "Gallery Shuffle core plugin: MCM logic quest.";

var quest = new Quest(new FormKey(modKey, 0x800), Fallout4Release.Fallout4)
{
    EditorID = "GShuf_Config",
    Name = "Gallery Shuffle",
    Data = new QuestData { Flags = Quest.Flag.StartGameEnabled | Quest.Flag.RunOnce },
    VirtualMachineAdapter = new QuestAdapter(),
};
quest.VirtualMachineAdapter.Scripts.Add(new ScriptEntry { Name = "GShuf:Config", Flags = ScriptEntry.Flag.Local });
mod.Quests.Add(quest);

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
mod.WriteToBinary(output, new BinaryWriteParameters { ModKey = ModKeyOption.NoCheck });
Console.WriteLine($"wrote {Path.GetFullPath(output)} ({new FileInfo(output).Length} bytes)");
