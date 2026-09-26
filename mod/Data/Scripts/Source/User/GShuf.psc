Scriptname GShuf Native Hidden
{Native functions provided by GalleryShuffle.dll (F4SE plugin).}

; Plugin version as major * 10000 + minor * 100 + patch.
int Function GetVersion() global native

; True when the plugin resolved the running game version and at least one pack is installed.
bool Function IsReady() global native

; Sets the share (0 to 100) of one pack slot in a category and re-applies to cached models.
Function SetPercent(string asCategory, int aiSlot, int aiValue) global native

; Enables or disables art on blank canvases.
Function SetBlankCanvases(bool abEnabled) global native

; Sets the layout seed and re-applies to cached models.
Function SetSeed(int aiSeed) global native

; Advances the layout seed, re-applies to cached models, and returns the new seed.
int Function NewSeed() global native

; Re-applies the current settings to cached models.
Function Reapply() global native

; Selects the main menu video option (0 = vanilla, then packs with a video in pack order).
Function SetMenuVideo(int aiIndex) global native

; The main menu video option currently selected.
int Function GetMenuVideo() global native

; Identifies the installed pack set; it changes whenever the converter rebuilds the packs.
string Function GetPackSignature() global native

; Number of packs that provide art for a category.
int Function GetSlotCount(string asCategory) global native

; Display name of the pack in a category slot (1-based), empty when the slot is unused.
string Function GetSlotName(string asCategory, int aiSlot) global native
