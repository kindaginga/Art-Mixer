Scriptname GShuf:Config extends Quest
{Keeps the Gallery Shuffle MCM pages consistent, holds menu changes until the player applies them, and hands applied settings to the F4SE plugin.}

string Property ModName = "GalleryShuffle" AutoReadOnly
int Property SlotCount = 10 AutoReadOnly

; An Apply all prompt is open.
bool Busy = false
; A startup pass is running.
bool Registering = false
; Reset all settings also returns the main menu video to vanilla once applied.
bool PendingMenuReset = false
; A slider change is being rebalanced.
; Slider changes that started rebalancing and have not finished.

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
	RegisterForExternalEvent("OnMCMSettingChange|" + ModName, "OnMCMSettingChange")
	RegisterForExternalEvent("OnMCMMenuOpen|" + ModName, "OnMCMMenuOpen")
	RegisterForExternalEvent("OnMCMMenuClose|" + ModName, "OnMCMMenuClose")
	RegisterForExternalEvent("OnMCMClose", "OnMCMClose")
	Debug.Trace("GShuf: Config registered; plugin ready " + GShuf.IsReady() + ", plugin version " + GShuf.GetVersion())
	CheckPackSignature()
	CleanHiddenSlots()
	; Puts the settings in effect into the menu, dropping changes that were never applied.
	RevertToApplied()
	Debug.Trace("GShuf: startup pass done")
	Registering = false
EndFunction

; Number of slots a category shows; anything above it is a leftover from an earlier pack set.
int Function VisibleSlots(string asCategory)
	If !GShuf.IsReady()
		Return SlotCount
	EndIf
	int n = GShuf.GetSlotCount(asCategory)
	If n > SlotCount
		n = SlotCount
	EndIf
	Return n
EndFunction

; Zeroes applied slots above a category's pack count and picks beyond the list, so leftovers never count toward the cap.
Function CleanHiddenSlots()
	If !GShuf.IsReady()
		Return
	EndIf
	bool changed = false
	string[] cats = Categories()
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
			While slot <= SlotCount
				If GShuf.GetPercent(cats[c], slot) != 0
					GShuf.SetPercent(cats[c], slot, 0)
					Debug.Trace("GShuf: cleared leftover slot " + slot + " of " + cats[c])
					changed = true
				EndIf
				slot += 1
			EndWhile
		EndIf
		c += 1
	EndWhile
	If changed
		GShuf.SaveSettings()
	EndIf
EndFunction

; Zeroes every share when the installed pack set differs from the one these settings were made for.
Function CheckPackSignature()
	If !GShuf.IsReady()
		Return
	EndIf
	string current = GShuf.GetPackSignature()
	string stored = MCM.GetModSettingString(ModName, "sPackSignature:GShuf_Main")
	If stored == current
		Return
	EndIf
	If stored != ""
		ResetToDefaults()
		PendingMenuReset = true
		If !Commit()
			Debug.Notification("Gallery Shuffle: the art packs changed; settings were reset but could not be stored.")
			Return
		EndIf
		Debug.Notification("Gallery Shuffle: the art packs changed, settings were reset.")
	EndIf
	MCM.SetModSettingString(ModName, "sPackSignature:GShuf_Main", current)
EndFunction

; Every menu value to its default: every share 0 (all vanilla), Pip-Boy picks to Vanilla, blank canvases off, new layout on every load off, seed 1.
Function ResetToDefaults()
	SetMenuFixes(true)
	MCM.SetModSettingBool(ModName, "bBlankCanvases:GShuf_Main", false)
	MCM.SetModSettingBool(ModName, "bNewLayoutEachLoad:GShuf_Main", false)
	MCM.SetModSettingInt(ModName, "iSeed:GShuf_Main", 1)
	MCM.RefreshMenu()
EndFunction

; Reset button: the menu goes back to defaults, which take effect once applied like any other change.
Function ResetAll()
	ResetToDefaults()
	PendingMenuReset = true
	Debug.Notification("Gallery Shuffle: settings back to defaults. They take effect once you apply them.")
EndFunction

; Share category names in menu order, GShuf_ plus the category; kinds are GShuf_Other_<kind>, not MCM sections, so MCM ids come only from GShuf.MenuKey.
string[] Function Categories()
	If GShuf.IsReady()
		; The categories the installed packs name, in menu order.
		string[] named = GShuf.GetCategories()
		If named != None && named.Length > 0
			Return named
		EndIf
	EndIf
	; The fixed list for packs from an older converter.
	string[] list = new string[9]
	list[0] = "GShuf_Paintings"
	list[1] = "GShuf_Posters"
	list[2] = "GShuf_Signage"
	list[3] = "GShuf_Magazines"
	list[4] = "GShuf_Quest"
	list[5] = "GShuf_Other"
	list[6] = "GShuf_PipBoyScreen"
	list[7] = "GShuf_PipBoySkin"
	list[8] = "GShuf_LoadingScreens"
	Return list
EndFunction

; The Pip-Boy pages hold one list each instead of shares: iPick is 0 for Vanilla or the slot number of the chosen pack.
bool Function IsPick(string asCategory)
	Return asCategory == "GShuf_PipBoyScreen" || asCategory == "GShuf_PipBoySkin"
EndFunction

; The list choice in effect: the slot the plugin has at 100, or 0 for Vanilla.
int Function AppliedPick(string asCategory)
	int slot = 1
	While slot <= SlotCount
		If GShuf.GetPercent(asCategory, slot) == 100
			Return slot
		EndIf
		slot += 1
	EndWhile
	Return 0
EndFunction

; Sends a list choice to the plugin as one slot at 100 and every other slot at 0.
Function SetAppliedPick(string asCategory, int aiPick)
	int slot = 1
	While slot <= SlotCount
		If slot == aiPick
			GShuf.SetPercent(asCategory, slot, 100)
		Else
			GShuf.SetPercent(asCategory, slot, 0)
		EndIf
		slot += 1
	EndWhile
	Debug.Trace("GShuf: " + asCategory + " pick " + aiPick)
EndFunction

; Makes the menu's values the settings in effect; the plugin reads them from MCM's own files.
Function PushAll()
	GShuf.LoadMenuSettings()
	Debug.Trace("GShuf: menu values taken over; seed " + GShuf.GetSeed())
EndFunction

; Sets the menu values the plugin finds different from the settings in effect, or from the defaults; returns how many it set.
int Function SetMenuFixes(bool abDefaults)
	int total = 0
	int rounds = 0
	string[] ids = GShuf.GetMenuFixIds(abDefaults)
	While ids.Length > 0 && rounds < 20
		int[] values = GShuf.GetMenuFixValues(abDefaults)
		int i = 0
		While i < ids.Length && i < values.Length
			MCM.SetModSettingInt(ModName, ids[i], values[i])
			i += 1
		EndWhile
		total += ids.Length
		rounds += 1
		ids = GShuf.GetMenuFixIds(abDefaults)
	EndWhile
	Return total
EndFunction

; Sets a menu value when it differs from the given one.
Function SetMenuInt(string asId, int aiValue)
	If MCM.GetModSettingInt(ModName, asId) != aiValue
		MCM.SetModSettingInt(ModName, asId, aiValue)
	EndIf
EndFunction

; True when GalleryShuffle.dll bound its natives.
bool Function PluginLoaded()
	Return GShuf.GetVersion() != 0
EndFunction

; Copies the settings in effect into the menu, dropping every change that was not applied.
Function RevertToApplied()
	If !PluginLoaded()
		Return
	EndIf
	int changed = SetMenuFixes(false)
	If MCM.GetModSettingBool(ModName, "bBlankCanvases:GShuf_Main") != GShuf.GetBlankCanvases()
		MCM.SetModSettingBool(ModName, "bBlankCanvases:GShuf_Main", GShuf.GetBlankCanvases())
	EndIf
	If MCM.GetModSettingBool(ModName, "bNewLayoutEachLoad:GShuf_Main") != GShuf.GetNewLayoutEachLoad()
		MCM.SetModSettingBool(ModName, "bNewLayoutEachLoad:GShuf_Main", GShuf.GetNewLayoutEachLoad())
	EndIf
	SetMenuInt("iSeed:GShuf_Main", GShuf.GetSeed())
	PendingMenuReset = false
	MCM.RefreshMenu()
	Debug.Trace("GShuf: menu back to the settings in effect, " + changed + " values set")
EndFunction

; True when a menu value differs from the settings in effect.
bool Function HasPendingChanges()
	If PendingMenuReset && GShuf.GetMenuVideo() != 0
		Return true
	EndIf
	If MCM.GetModSettingBool(ModName, "bBlankCanvases:GShuf_Main") != GShuf.GetBlankCanvases()
		Return true
	EndIf
	If MCM.GetModSettingBool(ModName, "bNewLayoutEachLoad:GShuf_Main") != GShuf.GetNewLayoutEachLoad()
		Return true
	EndIf
	If MCM.GetModSettingInt(ModName, "iSeed:GShuf_Main") != GShuf.GetSeed()
		Return true
	EndIf
	Return GShuf.MenuHasChanges()
EndFunction

; Shows the Apply all prompt and waits for the answer: 0 Apply all, 1 Cancel, -1 when no prompt could be shown.
int Function Ask(bool abLeaving)
	If !GShuf.AskApplyAll(abLeaving)
		Return -1
	EndIf
	int answer = GShuf.GetApplyAnswer()
	While answer < 0
		Utility.WaitMenuMode(0.1)
		answer = GShuf.GetApplyAnswer()
	EndWhile
	Return answer
EndFunction

; Makes the menu's values the settings in effect and stores them for the next game start; false when they could not be stored.
bool Function Commit()
	PushAll()
	bool stored = GShuf.SaveSettings()
	If PendingMenuReset
		GShuf.SetMenuVideo(0)
		PendingMenuReset = false
	EndIf
	Return stored
EndFunction

; Applies the menu and reloads the game, or says the changes last only this session when they could not be stored.
Function Apply()
	If Commit()
		GShuf.ReloadNow()
	Else
		Debug.Notification("Gallery Shuffle: changes applied for this session only; GalleryShuffle_Applied.ini could not be written.")
	EndIf
EndFunction

; Apply all button: after the player confirms, the changes take effect and the game reloads; Cancel keeps them in the menu.
Function ApplyAll()
	If Busy
		Return
	EndIf
	Busy = true
	If !PluginLoaded()
		Debug.Notification("Gallery Shuffle: GalleryShuffle.dll is not loaded, so changes cannot be applied.")
	ElseIf !HasPendingChanges()
		Debug.Notification("Gallery Shuffle: there are no changes to apply.")
	ElseIf Ask(false) == 0
		Apply()
	EndIf
	Busy = false
EndFunction

; Leaving Gallery Shuffle or the menu with changes not applied: Apply all applies them and reloads, Cancel puts the settings back.
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
			Debug.Notification("Gallery Shuffle: changes canceled; the settings are back to how they were.")
		EndIf
	EndIf
	Busy = false
EndFunction

; MCM sends this with no arguments when the player opens Gallery Shuffle.
Function OnMCMMenuOpen()
	MCM.RefreshMenu()
EndFunction

; MCM sends this with no arguments when the player selects another mod after Gallery Shuffle.
Function OnMCMMenuClose()
	Leaving()
EndFunction

; MCM sends this with no arguments when the player leaves the menu for the pause menu.
Function OnMCMClose()
	Leaving()
EndFunction

Function OnMCMSettingChange(string asModName, string asId)
	; Every slider is independent and MCM stores its value itself; a change waits for Apply all.
	Debug.Trace("GShuf: " + asId + " changed to " + MCM.GetModSettingInt(ModName, asId))
EndFunction

; Shuffle button: a new layout with the same shares, which takes effect once applied.
Function Shuffle()
	MCM.SetModSettingInt(ModName, "iSeed:GShuf_Main", MCM.GetModSettingInt(ModName, "iSeed:GShuf_Main") + 1)
	MCM.RefreshMenu()
	Debug.Notification("Gallery Shuffle: new layout chosen. It takes effect once you apply your changes.")
EndFunction
