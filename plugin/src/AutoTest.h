#pragma once

// Scripted test runs. Only the test build (ARTMIXER_AUTOTEST) has them; in the plugin players get, these do nothing.
namespace ArtMixer::AutoTest
{
#ifdef ARTMIXER_AUTOTEST
	// Reads [AutoTest] from Data\F4SE\Plugins\ArtMixer.ini; returns true when a scripted run is configured.
	bool Configure();
	// True when a scripted run lists the feature ("textures", "loadscreens", "inventory", "pickmodels") in [AutoTest] sSkip.
	[[nodiscard]] bool Skips(std::string_view a_feature);
	void OnGameDataReady();
	// Starts the scripted stages once a save has loaded; a failed or unfinished load keeps the prompts being confirmed.
	void OnPostLoadGame(bool a_loaded);
	// Logs what a placed reference's 3D shows the moment it is created, before any later pass (the game thread or a loader thread).
	void LogArrival(RE::TESObjectREFR* a_ref, RE::NiAVObject* a_root);
	// The material swap [AutoTest] sSwapProbe ("base hex|material|replacement") gives that object through the model hook, or null.
	[[nodiscard]] RE::BGSMaterialSwap* SwapProbe(std::uint32_t a_baseFormID);
#else
	inline bool Configure() { return false; }
	[[nodiscard]] inline bool Skips(std::string_view) { return false; }
	inline void OnGameDataReady() {}
	inline void OnPostLoadGame(bool) {}
	inline void LogArrival(RE::TESObjectREFR*, RE::NiAVObject*) {}
	[[nodiscard]] inline RE::BGSMaterialSwap* SwapProbe(std::uint32_t) { return nullptr; }
#endif
}
