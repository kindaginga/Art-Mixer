#pragma once

namespace GShuf::AutoTest
{
	// Reads [AutoTest] from Data\F4SE\Plugins\GalleryShuffle.ini; returns true when a scripted run is configured.
	bool Configure();
	// True when a scripted run lists the feature ("textures", "sounds", "loadscreens", "inventory", "pickmodels") in [AutoTest] sSkip.
	[[nodiscard]] bool Skips(std::string_view a_feature);
	void OnGameDataReady();
	// Starts the scripted stages once a save has loaded; a failed or unfinished load keeps the prompts being confirmed.
	void OnPostLoadGame(bool a_loaded);
}
