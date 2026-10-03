#pragma once

// Scripted test runs. Only the test build (ADT_AUTOTEST) has them; in the plugin players get, these do nothing.
namespace ADT::AutoTest
{
#ifdef ADT_AUTOTEST
	// Reads [AutoTest] from Data\F4SE\Plugins\ArtDistributionTool.ini; returns true when a scripted run is configured.
	bool Configure();
	// True when a scripted run lists the feature ("textures", "loadscreens", "inventory", "pickmodels") in [AutoTest] sSkip.
	[[nodiscard]] bool Skips(std::string_view a_feature);
	void OnGameDataReady();
	// Starts the scripted stages once a save has loaded; a failed or unfinished load keeps the prompts being confirmed.
	void OnPostLoadGame(bool a_loaded);
#else
	inline bool Configure() { return false; }
	[[nodiscard]] inline bool Skips(std::string_view) { return false; }
	inline void OnGameDataReady() {}
	inline void OnPostLoadGame(bool) {}
#endif
}
