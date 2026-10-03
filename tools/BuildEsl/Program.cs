using Mutagen.Bethesda;
using Mutagen.Bethesda.Fallout4;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Noggog;

// Writes ArtDistributionTool.esl: the start-game-enabled quest at 0x800 carrying the ADT:Config script, the loader quest at 0x801,
// and the loading screen cards (statics 0x802 and 0x900 to 0xAFF) the plugin points the game's magazine loading screens at.
var output = args.Length > 0 ? args[0] : Path.Combine("..", "..", "mod", "Data", "ArtDistributionTool.esl");
var modKey = ModKey.FromFileName("ArtDistributionTool.esl");
var mod = new Fallout4Mod(modKey, Fallout4Release.Fallout4);
mod.ModHeader.Flags |= Fallout4ModHeader.HeaderFlag.Small;
mod.ModHeader.Author = "Art Distribution Tool";
mod.ModHeader.Description = "Art Distribution Tool core plugin: MCM logic quest and load-event quest.";

var quest = new Quest(new FormKey(modKey, 0x800), Fallout4Release.Fallout4)
{
    EditorID = "ADT_Config",
    Name = "Art Distribution Tool",
    Data = new QuestData { Flags = Quest.Flag.StartGameEnabled | Quest.Flag.RunOnce },
    VirtualMachineAdapter = new QuestAdapter(),
};
quest.VirtualMachineAdapter.Scripts.Add(new ScriptEntry { Name = "ADT:Config", Flags = ScriptEntry.Flag.Local });
mod.Quests.Add(quest);

// A second quest at 0x801 holds a player alias whose script runs the Config setup on every game load
// (OnPlayerLoadGame); a quest of its own so that saves which already carry 0x800 start it fresh.
var loader = new Quest(new FormKey(modKey, 0x801), Fallout4Release.Fallout4)
{
    EditorID = "ADT_Loader",
    Name = "Art Distribution Tool loader",
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
    Scripts = { new ScriptEntry { Name = "ADT:PlayerAlias", Flags = ScriptEntry.Flag.Local } },
});
mod.Quests.Add(loader);

// The card whose model the plugin sets to a cover card the converter made (the game's loading screen magazine with a pack's cover) in test runs.
mod.Statics.Add(new Static(new FormKey(modKey, 0x802), Fallout4Release.Fallout4)
{
    EditorID = "ADT_LoadCard",
    Model = new Model { File = "ADT\\LoadCards\\card.nif" },
    ObjectBounds = new ObjectBounds { First = new P3Int16(-19, -1, -11), Second = new P3Int16(19, 1, 11) },
});
// One card per distinct cover or vanilla card shown at once (the game has 454 loading screens): statics 0x900 to 0xAFF.
const int loadCards = 512;
for (var i = 0; i < loadCards; i++)
{
    mod.Statics.Add(new Static(new FormKey(modKey, (uint)(0x900 + i)), Fallout4Release.Fallout4)
    {
        EditorID = $"ADT_LoadCard{i:000}",
        Model = new Model { File = "ADT\\LoadCards\\card.nif" },
        ObjectBounds = new ObjectBounds { First = new P3Int16(-19, -1, -11), Second = new P3Int16(19, 1, 11) },
    });
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
mod.WriteToBinary(output, new BinaryWriteParameters { ModKey = ModKeyOption.NoCheck });
Console.WriteLine($"wrote {Path.GetFullPath(output)} ({new FileInfo(output).Length} bytes)");
