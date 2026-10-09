Scriptname ArtMixer:Config extends Quest
{Keeps the Art Mixer MCM pages consistent, holds menu changes until the player applies them, and hands applied settings to the F4SE plugin.}

string Property ModName = "ArtMixer" AutoReadOnly

; An Apply all prompt is open.
bool Busy = false
; A startup pass is running.
bool Registering = false
; Reset all settings also returns the main menu video to vanilla once applied.
bool PendingMenuReset = false

Event OnInit()
	Register()
EndEvent

Event OnQuestInit()
	Register()
EndEvent

Function Register()
	If Registering
		Return
	EndIf
	Registering = true
	Busy = false
	RegisterForExternalEvent("OnMCMMenuOpen|" + ModName, "OnMCMMenuOpen")
	RegisterForExternalEvent("OnMCMMenuClose|" + ModName, "OnMCMMenuClose")
	RegisterForExternalEvent("OnMCMClose", "OnMCMClose")
	RegisterForExternalEvent("OnMCMSettingChange|" + ModName, "OnMCMSettingChange")
	Debug.Trace("ArtMixer: Config registered; plugin ready " + ArtMixer.IsReady() + ", plugin version " + ArtMixer.GetVersion())
	CheckPackSignature()
	CleanHiddenSlots()
	; Puts the settings in effect into the menu, dropping changes that were never applied.
	RevertToApplied()
	Debug.Trace("ArtMixer: startup pass done")
	Registering = false
EndFunction

; Number of slots a category shows; anything above it is a leftover from an earlier pack set.
int Function VisibleSlots(string asCategory)
	If !ArtMixer.IsReady()
		Return 0
	EndIf
	Return ArtMixer.GetSlotCount(asCategory)
EndFunction

; Number of slots to walk for a category: the shown ones and any left over with values.
int Function SlotSpan(string asCategory)
	int shown = VisibleSlots(asCategory)
	int stored = ArtMixer.GetStoredSlotCount(asCategory)
	If stored > shown
		Return stored
	EndIf
	Return shown
EndFunction

; Zeroes applied slots above a category's pack count and picks beyond the list, so leftovers never count toward the cap.
Function CleanHiddenSlots()
	If !ArtMixer.IsReady()
		Return
	EndIf
	bool changed = false
	string[] cats = ArtMixer.GetCategories()
	int c = 0
	While c < cats.Length
		int visible = VisibleSlots(cats[c])
		If IsPick(cats[c])
			If AppliedPick(cats[c]) > visible
				SetAppliedPick(cats[c], 0)
				changed = true
			EndIf
		Else
			int slot = visible + 1
			int stored = ArtMixer.GetStoredSlotCount(cats[c])
			While slot <= stored
				If ArtMixer.GetPercent(cats[c], slot) != 0
					ArtMixer.SetPercent(cats[c], slot, 0)
					Debug.Trace("ArtMixer: cleared leftover slot " + slot + " of " + cats[c])
					changed = true
				EndIf
				slot += 1
			EndWhile
		EndIf
		c += 1
	EndWhile
	If changed
		ArtMixer.SaveSettings()
	EndIf
EndFunction

; Zeroes every slider when the installed pack set differs from the one these settings were made for.
Function CheckPackSignature()
	If !ArtMixer.IsReady()
		Return
	EndIf
	string current = ArtMixer.GetPackSignature()
	string stored = MCM.GetModSettingString(ModName, "sPackSignature:ArtMixer_Main")
	If stored == current
		Return
	EndIf
	If stored != ""
		ResetToDefaults()
		PendingMenuReset = true
		If !Commit()
			Debug.Notification("Art Mixer: packs changed, settings reset, not stored.")
			Return
		EndIf
		MCM.SetModSettingString(ModName, "sPackSignature:ArtMixer_Main", current)
		; Art already loaded keeps the shares it loaded under, so the reset reloads the game as Apply all does.
		Debug.Notification("Art Mixer: packs changed, settings reset; reloading.")
		Debug.Trace("ArtMixer: pack signature changed from " + stored + " to " + current + "; settings reset and the game reloads")
		ArtMixer.ReloadNow()
		Return
	EndIf
	MCM.SetModSettingString(ModName, "sPackSignature:ArtMixer_Main", current)
EndFunction

; Every menu value to its default: every slider 0 (all vanilla), Pip-Boy and main menu picks to Vanilla, new layout on every load off, seed 1,
; the Main menu page's boxes as the plugin found their mods and its switches off.
Function ResetToDefaults()
	SetMenuFixes(true)
	SetMenuFlags(true)
	MCM.SetModSettingBool(ModName, "bNewLayoutEachLoad:ArtMixer_Main", false)
	MCM.SetModSettingInt(ModName, "iSeed:ArtMixer_Main", 1)
	MCM.RefreshMenu()
EndFunction

; Reset button: the menu goes back to defaults, which take effect once applied like any other change.
Function ResetAll()
	ResetToDefaults()
	PendingMenuReset = true
	Debug.Notification("Art Mixer: defaults set, not yet applied.")
EndFunction

; The Pip-Boy and World map pages hold one list per category instead of sliders: iPick is 0 for Vanilla or the slot number of the chosen pack.
bool Function IsPick(string asCategory)
	Return asCategory == "ArtMixer_PipBoyScreen" || asCategory == "ArtMixer_PipBoySkin" || asCategory == "ArtMixer_PipBoyMap" || asCategory == "ArtMixer_MenuThemes" || asCategory == "ArtMixer_MenuBackgrounds" || asCategory == "ArtMixer_MenuMusic"
EndFunction

; Sets the Main menu page's six switches to the values in effect, or to their defaults.
Function SetMenuFlags(bool abDefaults)
	string[] ids = ArtMixer.GetMenuFlagIds(abDefaults)
	int[] values = ArtMixer.GetMenuFlagValues(abDefaults)
	int i = 0
	While i < ids.Length && i < values.Length
		If MCM.GetModSettingBool(ModName, ids[i]) != (values[i] != 0)
			MCM.SetModSettingBool(ModName, ids[i], values[i] != 0)
		EndIf
		i += 1
	EndWhile
EndFunction

; MCM sends this with the mod name and the setting id when the player changes a value: Rotate themes stands alone, so it and the
; other two rotate switches turn each other off.
Function OnMCMSettingChange(string asModName, string asID)
	If asID == "bRotateThemes:ArtMixer_MainMenu"
		If MCM.GetModSettingBool(ModName, asID)
			MCM.SetModSettingBool(ModName, "bRotateBackgrounds:ArtMixer_MainMenu", false)
			MCM.SetModSettingBool(ModName, "bRotateMusic:ArtMixer_MainMenu", false)
			MCM.RefreshMenu()
		EndIf
	ElseIf asID == "bRotateBackgrounds:ArtMixer_MainMenu" || asID == "bRotateMusic:ArtMixer_MainMenu"
		If MCM.GetModSettingBool(ModName, asID)
			MCM.SetModSettingBool(ModName, "bRotateThemes:ArtMixer_MainMenu", false)
			MCM.RefreshMenu()
		EndIf
	EndIf
EndFunction

; The list choice in effect: the slot the plugin has at 100, or 0 for Vanilla.
int Function AppliedPick(string asCategory)
	int slot = 1
	int span = SlotSpan(asCategory)
	While slot <= span
		If ArtMixer.GetPercent(asCategory, slot) == 100
			Return slot
		EndIf
		slot += 1
	EndWhile
	Return 0
EndFunction

; Sends a list choice to the plugin as one slot at 100 and every other slot at 0.
Function SetAppliedPick(string asCategory, int aiPick)
	int slot = 1
	int span = SlotSpan(asCategory)
	If aiPick > span
		span = aiPick
	EndIf
	While slot <= span
		If slot == aiPick
			ArtMixer.SetPercent(asCategory, slot, 100)
		Else
			ArtMixer.SetPercent(asCategory, slot, 0)
		EndIf
		slot += 1
	EndWhile
	Debug.Trace("ArtMixer: " + asCategory + " pick " + aiPick)
EndFunction

; Makes the menu's values the settings in effect; the plugin reads them from MCM's own files.
Function PushAll()
	ArtMixer.LoadMenuSettings()
	Debug.Trace("ArtMixer: menu values taken over; seed " + ArtMixer.GetSeed())
EndFunction

; Sets the menu values the plugin finds different from the settings in effect, or from the defaults; returns how many it set.
int Function SetMenuFixes(bool abDefaults)
	int total = 0
	int rounds = 0
	string[] ids = ArtMixer.GetMenuFixIds(abDefaults)
	While ids.Length > 0 && rounds < 20
		int[] values = ArtMixer.GetMenuFixValues(abDefaults)
		int i = 0
		While i < ids.Length && i < values.Length
			MCM.SetModSettingInt(ModName, ids[i], values[i])
			i += 1
		EndWhile
		total += ids.Length
		rounds += 1
		ids = ArtMixer.GetMenuFixIds(abDefaults)
	EndWhile
	Return total
EndFunction

; Sets a menu value when it differs from the given one.
Function SetMenuInt(string asId, int aiValue)
	If MCM.GetModSettingInt(ModName, asId) != aiValue
		MCM.SetModSettingInt(ModName, asId, aiValue)
	EndIf
EndFunction

; True when ArtMixer.dll bound its natives.
bool Function PluginLoaded()
	Return ArtMixer.GetVersion() != 0
EndFunction

; Copies the settings in effect into the menu, dropping every change that was not applied.
Function RevertToApplied()
	If !PluginLoaded()
		Return
	EndIf
	int changed = SetMenuFixes(false)
	If MCM.GetModSettingBool(ModName, "bNewLayoutEachLoad:ArtMixer_Main") != ArtMixer.GetNewLayoutEachLoad()
		MCM.SetModSettingBool(ModName, "bNewLayoutEachLoad:ArtMixer_Main", ArtMixer.GetNewLayoutEachLoad())
	EndIf
	SetMenuInt("iSeed:ArtMixer_Main", ArtMixer.GetSeed())
	SetMenuFlags(false)
	PendingMenuReset = false
	MCM.RefreshMenu()
	Debug.Trace("ArtMixer: menu back to the settings in effect, " + changed + " values set")
EndFunction

; True when a menu value differs from the settings in effect.
bool Function HasPendingChanges()
	If PendingMenuReset && ArtMixer.GetMenuVideo() != 0
		Return true
	EndIf
	If MCM.GetModSettingBool(ModName, "bNewLayoutEachLoad:ArtMixer_Main") != ArtMixer.GetNewLayoutEachLoad()
		Return true
	EndIf
	If MCM.GetModSettingInt(ModName, "iSeed:ArtMixer_Main") != ArtMixer.GetSeed()
		Return true
	EndIf
	Return ArtMixer.MenuHasChanges()
EndFunction

; Shows the Apply all prompt and waits for the answer: 0 Apply all, 1 Cancel, -1 when no prompt could be shown.
int Function Ask(bool abLeaving)
	If !ArtMixer.AskApplyAll(abLeaving)
		Return -1
	EndIf
	int answer = ArtMixer.GetApplyAnswer()
	While answer < 0
		Utility.WaitMenuMode(0.1)
		answer = ArtMixer.GetApplyAnswer()
	EndWhile
	Return answer
EndFunction

; Makes the menu's values the settings in effect and stores them for the next game start; false when they could not be stored.
bool Function Commit()
	PushAll()
	bool stored = ArtMixer.SaveSettings()
	If PendingMenuReset
		ArtMixer.SetMenuVideo(0)
		PendingMenuReset = false
	EndIf
	Return stored
EndFunction

; Applies the menu and reloads the game, or says the changes last only this session when they could not be stored.
Function Apply()
	If Commit()
		ArtMixer.ReloadNow()
	Else
		Debug.Notification("Art Mixer: applied for this session only, settings file not writable.")
	EndIf
EndFunction

; Apply all button: after the player confirms, the changes take effect and the game reloads; Cancel keeps them in the menu.
Function ApplyAll()
	If Busy
		Return
	EndIf
	Busy = true
	If !PluginLoaded()
		Debug.Notification("Art Mixer: ArtMixer.dll is not loaded.")
	ElseIf !HasPendingChanges()
		Debug.Notification("Art Mixer: nothing to apply.")
	ElseIf Ask(false) == 0
		Apply()
	EndIf
	Busy = false
EndFunction

; Leaving Art Mixer or the menu with changes not applied: Apply all applies them and reloads, Cancel puts the settings back.
Function Leaving()
	If Busy
		Return
	EndIf
	Busy = true
	If PluginLoaded() && HasPendingChanges()
		int answer = Ask(true)
		If answer == 0
			Apply()
		Else
			RevertToApplied()
			Debug.Notification("Art Mixer: changes canceled.")
		EndIf
	EndIf
	Busy = false
EndFunction

; MCM sends this with no arguments when the player opens Art Mixer.
Function OnMCMMenuOpen()
	MCM.RefreshMenu()
EndFunction

; MCM sends this with no arguments when the player selects another mod after Art Mixer.
Function OnMCMMenuClose()
	Leaving()
EndFunction

; MCM sends this with no arguments when the player leaves the menu for the pause menu.
Function OnMCMClose()
	Leaving()
EndFunction

; Shuffle button: a new layout with the same slider settings, which takes effect once applied.
Function Shuffle()
	MCM.SetModSettingInt(ModName, "iSeed:ArtMixer_Main", MCM.GetModSettingInt(ModName, "iSeed:ArtMixer_Main") + 1)
	MCM.RefreshMenu()
	Debug.Notification("Art Mixer: new layout, not yet applied.")
EndFunction
