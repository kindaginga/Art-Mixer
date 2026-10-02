using Mutagen.Bethesda;
using Mutagen.Bethesda.Fallout4;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Noggog;

// Writes GalleryShuffle.esl: the start-game-enabled quest at 0x800 carrying the GShuf:Config script, the loader quest at 0x801,
// and the loading screen card (static 0x802, framings 0x803 for pictures and 0x804 for magazine back covers) the plugin points the game's loading screens at.
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

// The card whose model the plugin sets per loading screen to one the converter made (a flat sheet facing -Y that fits 37.78 by 21.25 units).
mod.Statics.Add(new Static(new FormKey(modKey, 0x802), Fallout4Release.Fallout4)
{
    EditorID = "GShuf_LoadCard",
    Model = new Model { File = "GShuf\\LoadCards\\card.nif" },
    ObjectBounds = new ObjectBounds { First = new P3Int16(-19, -1, -11), Second = new P3Int16(19, 1, 11) },
});
// Framing: 50 units in front of the loading screen camera with the zoom locked, as ALR places its full-screen cards; unturned because the card already faces the camera.
mod.Transforms.Add(new Transform(new FormKey(modKey, 0x803), Fallout4Release.Fallout4)
{
    EditorID = "GShuf_LoadCardTransform",
    Position = new P3Float(0, 50, 0),
    Rotation = new P3Float(0, 0, 0),
    Scale = 1,
    ZoomMin = 0,
    ZoomMax = 0,
});
// Back cover framing: the game's magazine loading screen placement (LS_GrognakMagazine) turned half a turn, so the back faces the camera.
mod.Transforms.Add(new Transform(new FormKey(modKey, 0x804), Fallout4Release.Fallout4)
{
    EditorID = "GShuf_LoadCardBackTransform",
    Position = new P3Float(9, 65, 1),
    Rotation = new P3Float(0, 0, 3.7357025f - MathF.PI),
    Scale = 1,
    ZoomMin = -0.5f,
    ZoomMax = 1,
});

// One card and one picture framing per loading screen record (the game has 448 to 454), so each record keeps its own picture:
// statics 0x900 to 0xAFF and framings 0xB00 to 0xCFF, the framing a copy of 0x803 whose scale the plugin sets per picture.
const int loadCards = 512;
for (var i = 0; i < loadCards; i++)
{
    mod.Statics.Add(new Static(new FormKey(modKey, (uint)(0x900 + i)), Fallout4Release.Fallout4)
    {
        EditorID = $"GShuf_LoadCard{i:000}",
        Model = new Model { File = "GShuf\\LoadCards\\card.nif" },
        ObjectBounds = new ObjectBounds { First = new P3Int16(-19, -1, -11), Second = new P3Int16(19, 1, 11) },
    });
    mod.Transforms.Add(new Transform(new FormKey(modKey, (uint)(0xB00 + i)), Fallout4Release.Fallout4)
    {
        EditorID = $"GShuf_LoadCardTransform{i:000}",
        Position = new P3Float(0, 50, 0),
        Rotation = new P3Float(0, 0, 0),
        Scale = 1,
        ZoomMin = 0,
        ZoomMax = 0,
    });
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
mod.WriteToBinary(output, new BinaryWriteParameters { ModKey = ModKeyOption.NoCheck });
Console.WriteLine($"wrote {Path.GetFullPath(output)} ({new FileInfo(output).Length} bytes)");
