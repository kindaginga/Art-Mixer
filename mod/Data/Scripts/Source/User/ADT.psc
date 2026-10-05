Scriptname ADT Native Hidden
{Native functions provided by ArtDistributionTool.dll (F4SE plugin).}

; Plugin version as major * 10000 + minor * 100 + patch.
int Function GetVersion() global native

; True when the plugin resolved the running game version and at least one pack is installed.
bool Function IsReady() global native

; Sets the percent (0 to 100) of one pack slot in a category and re-applies to cached models.
Function SetPercent(string asCategory, int aiSlot, int aiValue) global native

; The percent of one pack slot in a category in effect.
int Function GetPercent(string asCategory, int aiSlot) global native

; The layout seed the plugin uses now.
int Function GetSeed() global native

; True when a new layout whenever a save loads is in effect.
bool Function GetNewLayoutEachLoad() global native

; Stores the settings in effect for the next game start; false when the file could not be written.
bool Function SaveSettings() global native

; Shows the Apply all prompt (the leaving wording when abLeaving); false when no prompt could be shown.
bool Function AskApplyAll(bool abLeaving) global native

; The Apply all prompt's answer: -1 while it is open, 0 for Apply all, 1 for Cancel.
int Function GetApplyAnswer() global native

; Saves as ADTApply, returns to the main menu and loads that save so every setting shows at once.
Function ReloadNow() global native

; Selects the main menu video option (0 = vanilla, then packs with a video in pack order).
Function SetMenuVideo(int aiIndex) global native

; The main menu video option currently selected.
int Function GetMenuVideo() global native

; Identifies the installed pack set; it changes whenever the converter rebuilds the packs.
string Function GetPackSignature() global native

; Category names from packs.json in menu order: ADT_ plus the category; kinds of the Other replacers page are ADT_Other_<kind>.
string[] Function GetCategories() global native

; Number of packs a category's page or list shows.
int Function GetSlotCount(string asCategory) global native

; Number of slots a category has values for in effect, shown or left over from an earlier pack set.
int Function GetStoredSlotCount(string asCategory) global native

; MCM ids of menu values that differ from the settings in effect, or from the defaults, at most 128, in the order of GetMenuFixValues.
string[] Function GetMenuFixIds(bool abDefaults) global native

; The values GetMenuFixIds's ids get, in the same order.
int[] Function GetMenuFixValues(bool abDefaults) global native

; True when a menu slider or pick differs from the settings in effect.
bool Function MenuHasChanges() global native

; Makes the menu's values the settings in effect.
Function LoadMenuSettings() global native
