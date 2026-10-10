#pragma once

namespace ArtMixer::LoadScreens
{
	// Finds the game's loading screen records and Art Mixer's card forms, then prepares the first loading screen.
	void OnGameDataReady();
	// Prepares the next loading screen once one closes (game thread).
	void OnLoadingMenuClosed();
	// Rolls the coming loading screen again under the current settings (game thread, after a settings change).
	void Reassign();
	// True while the coming loading screen shows a pack cover, so the game's own loading menu is served for it.
	[[nodiscard]] bool NextIsCover();
	// True while ALR.esp is loaded and the Leave box is off: the game's own loading menu files are served on every load.
	[[nodiscard]] bool ServesGameFiles();
	// The Leave box as an MCM id ("bLeaveToALR:ArtMixer_LoadingScreens") with its value, in effect or by default.
	[[nodiscard]] std::vector<std::pair<std::string, int>> FlagValues(bool a_defaults);
	// Whether ALR.esp is loaded; nothing before the game data is loaded.
	[[nodiscard]] std::optional<bool> ALRLoaded();
	// Test runs: each loading screen shows the next of these cards (as packs.json names them) instead of a rolled one.
	void SetTestCards(std::vector<std::string> a_cards);
}
