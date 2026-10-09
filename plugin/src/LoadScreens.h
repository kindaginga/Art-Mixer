#pragma once

namespace ArtMixer::LoadScreens
{
	// Finds the game's loading screen records and Art Mixer's card forms, then prepares the first loading screen.
	void OnGameDataReady();
	// Prepares the next loading screen once one closes (game thread).
	void OnLoadingMenuClosed();
	// Gives every loading screen record the cover the current settings pick for it (game thread, after a settings change).
	void Reassign();
	// Test runs: each loading screen shows the next of these cards (as packs.json names them) instead of a rolled one.
	void SetTestCards(std::vector<std::string> a_cards);
}
