using Mutagen.Bethesda;
using Mutagen.Bethesda.Fallout4;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;

// Writes GalleryShuffle.esl: the start-game-enabled quest at 0x800 carrying the GShuf:Config script, and the loader quest at 0x801.
var output = args.Length > 0 ? args[0] : Path.Combine("..", "..", "mod", "Data", "GalleryShuffle.esl");
var modKey = ModKey.FromFileName("GalleryShuffle.esl");
var mod = new Fallout4Mod(modKey, Fallout4Release.Fallout4);
mod.ModHeader.Flags |= Fallout4ModHeader.HeaderFlag.Small;
mod.ModHeader.Author = "Gallery Shuffle";
mod.ModHeader.Description = "Gallery Shuffle core plugin: MCM logic quest and load-event quest.";

var quest = new Quest(new FormKey(modKey, 0x800), Fallout4Release.Fallout4)
{
    EditorID = "GShuf_Config",
    Name = "Gallery Shuffle",
    Data = new QuestData { Flags = Quest.Flag.StartGameEnabled | Quest.Flag.RunOnce },
    VirtualMachineAdapter = new QuestAdapter(),
};
quest.VirtualMachineAdapter.Scripts.Add(new ScriptEntry { Name = "GShuf:Config", Flags = ScriptEntry.Flag.Local });
mod.Quests.Add(quest);

// A second quest at 0x801 holds a player alias whose script runs the Config setup on every game load
// (OnPlayerLoadGame); a quest of its own so that saves which already carry 0x800 start it fresh.
var loader = new Quest(new FormKey(modKey, 0x801), Fallout4Release.Fallout4)
{
    EditorID = "GShuf_Loader",
    Name = "Gallery Shuffle loader",
    Data = new QuestData { Flags = Quest.Flag.StartGameEnabled | Quest.Flag.RunOnce },
    VirtualMachineAdapter = new QuestAdapter(),
};
var playerRef = new FormKey(ModKey.FromFileName("Fallout4.esm"), 0x14);
loader.Aliases ??= new();
loader.Aliases.Add(new QuestReferenceAlias
{
    ID = 0,
    Name = "Player",
    ForcedReference = new FormLinkNullable<IPlacedGetter>(playerRef),
});
loader.VirtualMachineAdapter.Aliases.Add(new QuestFragmentAlias
{
    Property = new ScriptObjectProperty { Object = loader.ToLink<IFallout4MajorRecordGetter>(), Alias = 0 },
    Version = 6,
    ObjectFormat = 2,
    Scripts = { new ScriptEntry { Name = "GShuf:PlayerAlias", Flags = ScriptEntry.Flag.Local } },
});
mod.Quests.Add(loader);

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
mod.WriteToBinary(output, new BinaryWriteParameters { ModKey = ModKeyOption.NoCheck });
Console.WriteLine($"wrote {Path.GetFullPath(output)} ({new FileInfo(output).Length} bytes)");
