#pragma once

namespace GShuf::LoadScreens
{
	// Finds the game's loading screen records and Gallery Shuffle's card forms, then prepares the first loading screen.
	void OnGameDataReady();
	// Prepares the next loading screen once one closes (game thread).
	void OnLoadingMenuClosed();
	// Test runs: each loading screen that shows a card uses the next of these framings (x, y, z, rotation x, y, z, scale).
	// Gives every loading screen record the picture the current settings pick for it (game thread, after a settings change).
	void Reassign();
	void SetTestFramings(std::vector<std::array<float, 7>> a_framings);
	// Test runs: each loading screen shows the next of these cards (as packs.json names them) instead of a rolled one.
	void SetTestCards(std::vector<std::string> a_cards);
}
