Scriptname GShuf:PlayerAlias extends ReferenceAlias
{Runs the Gallery Shuffle setup when the alias fills and on every game load.}

Event OnInit()
	Run()
EndEvent

Event OnPlayerLoadGame()
	Run()
EndEvent

Function Run()
	Debug.Trace("GShuf: loader event")
	GShuf:Config config = Game.GetFormFromFile(0x800, "GalleryShuffle.esl") as GShuf:Config
	If config
		config.Register()
	Else
		Debug.Trace("GShuf: loader could not find the Config quest")
	EndIf
EndFunction
