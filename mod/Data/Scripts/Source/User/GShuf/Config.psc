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
	PushAll()
EndFunction

string[] Function Categories()
	string[] list = new string[5]
	list[0] = "Paintings"
	list[1] = "Posters"
	list[2] = "Signage"
	list[3] = "Magazines"
	list[4] = "Quest"
	Return list
EndFunction

; Sum of the pack slots in a category, optionally leaving one slot out.
int Function SlotTotal(string asCategory, int aiSkipSlot = 0)
	int total = 0
	int slot = 1
	While slot <= SlotCount
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
		int slot = 1
		While slot <= SlotCount
			GShuf.SetPercent(cats[c], slot, MCM.GetModSettingInt(ModName, "iSlot" + slot + ":" + cats[c]))
			slot += 1
		EndWhile
		MCM.SetModSettingInt(ModName, "iVanilla:" + cats[c], 100 - SlotTotal(cats[c]))
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
		MCM.SetModSettingInt(ModName, "iVanilla:" + cats[c], 100 - SlotTotal(cats[c]))
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
