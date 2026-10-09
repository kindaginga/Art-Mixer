Scriptname ArtMixer:PlayerAlias extends ReferenceAlias
{Runs the Art Mixer setup when the alias fills and on every game load.}

Event OnInit()
	Run()
EndEvent

Event OnPlayerLoadGame()
	Run()
EndEvent

Function Run()
	Debug.Trace("ArtMixer: loader event")
	ArtMixer:Config config = Game.GetFormFromFile(0x800, "ArtMixer.esl") as ArtMixer:Config
	If config
		config.Register()
	Else
		Debug.Trace("ArtMixer: loader could not find the Config quest")
	EndIf
EndFunction
