Scriptname GShuf:Config extends Quest
{Keeps the Gallery Shuffle MCM sliders consistent and forwards changes to the F4SE plugin.}

string Property ModName = "GalleryShuffle" AutoReadOnly
int Property SlotCount = 10 AutoReadOnly

Event OnInit()
	Register()
EndEvent

Event OnQuestInit()
	Register()
EndEvent

Function Register()
	RegisterForExternalEvent("OnMCMSettingChange|" + ModName, "OnMCMSettingChange")
	RegisterForExternalEvent("OnMCMMenuOpen|" + ModName, "OnMCMMenuOpen")
	Debug.Trace("GShuf: Config registered; plugin ready " + GShuf.IsReady() + ", plugin version " + GShuf.GetVersion())
	CheckPackSignature()
	CleanHiddenSlots()
	PushAll()
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

; Zeroes slots above a category's pack count and picks beyond the list, so leftovers never count toward the cap.
Function CleanHiddenSlots()
	If !GShuf.IsReady()
		Return
	EndIf
	string[] cats = Categories()
	int c = 0
	While c < cats.Length
		int visible = VisibleSlots(cats[c])
		If IsPick(cats[c])
			If MCM.GetModSettingInt(ModName, "iPick:" + cats[c]) > visible
				MCM.SetModSettingInt(ModName, "iPick:" + cats[c], 0)
			EndIf
		Else
			int slot = visible + 1
			While slot <= SlotCount
				If MCM.GetModSettingInt(ModName, "iSlot" + slot + ":" + cats[c]) != 0
					MCM.SetModSettingInt(ModName, "iSlot" + slot + ":" + cats[c], 0)
					Debug.Trace("GShuf: cleared leftover slot " + slot + " of " + cats[c])
				EndIf
				slot += 1
			EndWhile
			MCM.SetModSettingInt(ModName, "iVanilla:" + cats[c], 100 - SlotTotal(cats[c]))
		EndIf
		c += 1
	EndWhile
EndFunction

; Zeroes every share when the installed pack set differs from the one these settings were made for.
Function CheckPackSignature()
	If !GShuf.IsReady()
		Return
	EndIf
	string current = GShuf.GetPackSignature()
	string stored = MCM.GetModSettingString(ModName, "sPackSignature:Main")
	If stored == current
		Return
	EndIf
	If stored != ""
		ResetAll()
		Debug.Notification("Gallery Shuffle: the art packs changed, settings were reset.")
	EndIf
	MCM.SetModSettingString(ModName, "sPackSignature:Main", current)
EndFunction

; Reset button and pack-change reset: every share to 0 (all vanilla), Pip-Boy picks to Vanilla, blank canvases off, seed 1.
Function ResetAll()
	string[] cats = Categories()
	int c = 0
	While c < cats.Length
		If IsPick(cats[c])
			MCM.SetModSettingInt(ModName, "iPick:" + cats[c], 0)
		Else
			int slot = 1
			While slot <= SlotCount
				MCM.SetModSettingInt(ModName, "iSlot" + slot + ":" + cats[c], 0)
				slot += 1
			EndWhile
			MCM.SetModSettingInt(ModName, "iVanilla:" + cats[c], 100)
		EndIf
		c += 1
	EndWhile
	MCM.SetModSettingBool(ModName, "bBlankCanvases:Main", false)
	MCM.SetModSettingInt(ModName, "iSeed:Main", 1)
	GShuf.SetMenuVideo(0)
	MCM.RefreshMenu()
	PushAll()
EndFunction

string[] Function Categories()
	string[] list = new string[7]
	list[0] = "Paintings"
	list[1] = "Posters"
	list[2] = "Signage"
	list[3] = "Magazines"
	list[4] = "Quest"
	list[5] = "PipBoyScreen"
	list[6] = "PipBoySkin"
	Return list
EndFunction

; The Pip-Boy pages hold one list each instead of shares: iPick is 0 for Vanilla or the slot number of the chosen pack.
bool Function IsPick(string asCategory)
	Return asCategory == "PipBoyScreen" || asCategory == "PipBoySkin"
EndFunction

; Sends a list choice to the plugin as one slot at 100 and every other slot at 0.
Function PushPick(string asCategory)
	int pick = MCM.GetModSettingInt(ModName, "iPick:" + asCategory)
	int slot = 1
	While slot <= SlotCount
		If slot == pick
			GShuf.SetPercent(asCategory, slot, 100)
		Else
			GShuf.SetPercent(asCategory, slot, 0)
		EndIf
		slot += 1
	EndWhile
	Debug.Trace("GShuf: " + asCategory + " pick " + pick)
EndFunction

; Sum of the shown pack slots in a category, optionally leaving one slot out.
int Function SlotTotal(string asCategory, int aiSkipSlot = 0)
	int total = 0
	int last = VisibleSlots(asCategory)
	int slot = 1
	While slot <= last
		If slot != aiSkipSlot
			total += MCM.GetModSettingInt(ModName, "iSlot" + slot + ":" + asCategory)
		EndIf
		slot += 1
	EndWhile
	Return total
EndFunction

; Sends every stored setting to the plugin so its state matches the MCM files.
Function PushAll()
	If !GShuf.IsReady()
		Return
	EndIf
	string[] cats = Categories()
	int c = 0
	While c < cats.Length
		If IsPick(cats[c])
			PushPick(cats[c])
		Else
			int slot = 1
			While slot <= SlotCount
				GShuf.SetPercent(cats[c], slot, MCM.GetModSettingInt(ModName, "iSlot" + slot + ":" + cats[c]))
				slot += 1
			EndWhile
			MCM.SetModSettingInt(ModName, "iVanilla:" + cats[c], 100 - SlotTotal(cats[c]))
		EndIf
		c += 1
	EndWhile
	GShuf.SetBlankCanvases(MCM.GetModSettingBool(ModName, "bBlankCanvases:Main"))
	GShuf.SetSeed(MCM.GetModSettingInt(ModName, "iSeed:Main"))
	Debug.Trace("GShuf: pushed " + (cats.Length * SlotCount) + " slot values; paintings slot 1 = " + MCM.GetModSettingInt(ModName, "iSlot1:Paintings") + ", seed " + MCM.GetModSettingInt(ModName, "iSeed:Main"))
EndFunction

Function OnMCMMenuOpen(string asModName)
	string[] cats = Categories()
	int c = 0
	While c < cats.Length
		If !IsPick(cats[c])
			MCM.SetModSettingInt(ModName, "iVanilla:" + cats[c], 100 - SlotTotal(cats[c]))
		EndIf
		c += 1
	EndWhile
	MCM.RefreshMenu()
EndFunction

Function OnMCMSettingChange(string asModName, string asId)
	If asId == "bBlankCanvases:Main"
		GShuf.SetBlankCanvases(MCM.GetModSettingBool(ModName, asId))
		Return
	ElseIf asId == "iSeed:Main"
		GShuf.SetSeed(MCM.GetModSettingInt(ModName, asId))
		Return
	EndIf
	string[] cats = Categories()
	int c = 0
	While c < cats.Length
		If asId == "iPick:" + cats[c]
			PushPick(cats[c])
			Return
		EndIf
		If asId == "iVanilla:" + cats[c]
			MCM.SetModSettingInt(ModName, asId, 100 - SlotTotal(cats[c]))
			MCM.RefreshMenu()
			Return
		EndIf
		int slot = 1
		While slot <= SlotCount
			If asId == "iSlot" + slot + ":" + cats[c]
				SlotChanged(cats[c], slot, asId)
				Return
			EndIf
			slot += 1
		EndWhile
		c += 1
	EndWhile
EndFunction

; Clamps a changed slot so the category never exceeds 100, updates the Vanilla remainder, and informs the plugin.
Function SlotChanged(string asCategory, int aiSlot, string asId)
	int value = MCM.GetModSettingInt(ModName, asId)
	int room = 100 - SlotTotal(asCategory, aiSlot)
	If value > room
		value = room
		MCM.SetModSettingInt(ModName, asId, value)
	EndIf
	MCM.SetModSettingInt(ModName, "iVanilla:" + asCategory, 100 - SlotTotal(asCategory))
	MCM.RefreshMenu()
	GShuf.SetPercent(asCategory, aiSlot, value)
	Debug.Trace("GShuf: " + asId + " set to " + value + " (room " + room + ")")
EndFunction

; Shuffle button: picks a new layout with the same shares.
Function Shuffle()
	int seed = GShuf.NewSeed()
	MCM.SetModSettingInt(ModName, "iSeed:Main", seed)
	Debug.Notification("Gallery Shuffle: new layout set. It appears when an area loads.")
EndFunction
