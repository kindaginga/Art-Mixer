Scriptname ADT:PlayerAlias extends ReferenceAlias
{Runs the Art Distribution Tool setup when the alias fills and on every game load.}

Event OnInit()
	Run()
EndEvent

Event OnPlayerLoadGame()
	Run()
EndEvent

Function Run()
	Debug.Trace("ADT: loader event")
	ADT:Config config = Game.GetFormFromFile(0x800, "ArtDistributionTool.esl") as ADT:Config
	If config
		config.Register()
	Else
		Debug.Trace("ADT: loader could not find the Config quest")
	EndIf
EndFunction
