Scriptname GShuf Native Hidden
{Native functions provided by GalleryShuffle.dll (F4SE plugin).}

; Plugin version as major * 10000 + minor * 100 + patch.
int Function GetVersion() global native

; True when the plugin resolved the running game version and at least one pack is installed.
bool Function IsReady() global native

; Sets the share (0 to 100) of one pack slot in a category and re-applies to cached models.
Function SetPercent(string asCategory, int aiSlot, int aiValue) global native

; The share of one pack slot in a category in effect.
int Function GetPercent(string asCategory, int aiSlot) global native

; Enables or disables art on blank canvases.
Function SetBlankCanvases(bool abEnabled) global native

; True when art on blank canvases is in effect.
bool Function GetBlankCanvases() global native

; Sets the layout seed and re-applies to cached models.
Function SetSeed(int aiSeed) global native

; The layout seed the plugin uses now.
int Function GetSeed() global native

; Turns on or off a new layout whenever a save loads.
Function SetNewLayoutEachLoad(bool abEnabled) global native

; True when a new layout whenever a save loads is in effect.
bool Function GetNewLayoutEachLoad() global native

; Stores the settings in effect for the next game start; false when the file could not be written.
bool Function SaveSettings() global native

; Shows the Apply all prompt (the leaving wording when abLeaving); false when no prompt could be shown.
bool Function AskApplyAll(bool abLeaving) global native

; The Apply all prompt's answer: -1 while it is open, 0 for Apply all, 1 for Cancel.
int Function GetApplyAnswer() global native

; Saves as GShufApply, returns to the main menu and loads that save so every setting shows at once.
Function ReloadNow() global native

; Re-applies the current settings to cached models.
Function Reapply() global native

; Selects the main menu video option (0 = vanilla, then packs with a video in pack order).
Function SetMenuVideo(int aiIndex) global native

; The main menu video option currently selected.
int Function GetMenuVideo() global native

; Identifies the installed pack set; it changes whenever the converter rebuilds the packs.
string Function GetPackSignature() global native

; Share category names from packs.json in menu order (GShuf_ plus the category; kinds are GShuf_Other_<kind>, which are not MCM sections, so build MCM ids only with MenuKey); empty for packs from an older converter.
string[] Function GetCategories() global native

; Number of packs that provide art for a category.
int Function GetSlotCount(string asCategory) global native

; Display name of the pack in a category slot (1-based), empty when the slot is unused.
string Function GetSlotName(string asCategory, int aiSlot) global native

; First slot of a category slot's group (1-based): on picture pages, the packs that replace some of the same objects, whose shares and the group's Vanilla add up to 100; loading screens and each Pip-Boy page are one group; every pack on the Other replacers page and its kinds is its own group. 1 when packs.json lists no groups, 0 when the slot is unused.
int Function GetSlotGroup(string asCategory, int aiSlot) global native

; The MCM setting id of a category's slot value ("iSlot" or "iVanilla"); kinds of the Other replacers page share the GShuf_Other section.
string Function MenuKey(string asPrefix, int aiSlot, string asCategory) global native

; MCM ids of menu values that differ from the settings in effect, or from the defaults, at most 128, in the order of GetMenuFixValues.
string[] Function GetMenuFixIds(bool abDefaults) global native

; The values GetMenuFixIds's ids get, in the same order.
int[] Function GetMenuFixValues(bool abDefaults) global native

; True when a menu share or pick differs from the settings in effect.
bool Function MenuHasChanges() global native

; Makes the menu's values the settings in effect.
Function LoadMenuSettings() global native
