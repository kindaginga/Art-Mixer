#pragma once

namespace GShuf::MainMenu
{
	struct Option
	{
		std::string name;
		std::string video;  // relative to the game folder, e.g. Data\Video\GShuf\Packs\x\MainMenuLoop.bk2; empty means vanilla
	};

	// Hooks the game's Bink video open so the main menu video can come from a pack.
	bool Install();
	// Registers the corner picker menu and the main menu open/close sink; needs the UI singleton.
	void OnGameDataReady();
	// Replaces the list the picker offers and the current choice.
	void SetOptions(std::vector<Option> a_options, int a_selected);
	// Builds the option list from the installed packs and restores the saved choice.
	void LoadFromPacks();
	// True once the intro videos are over and the main menu is showing its loop video.
	[[nodiscard]] bool AtMainMenu();
	// Refuses every video except the menu loop, so an automated run reaches the menu without the intro.
	void SetSkipStartupVideos(bool a_skip);
	// Leaves the corner picker unregistered when false (autotest baseline without it).
	void SetPickerEnabled(bool a_enabled);
	// Last cursor position the picker menu was given by the engine (cursor move events), in the engine's cursor units.
	[[nodiscard]] std::pair<int, int> CursorPos();
	// Logs MainMenu's flags and input context and the control map's context stack (diagnostics).
	void LogMenuState(const char* a_tag);
	[[nodiscard]] int Selected();
	[[nodiscard]] int OptionCount();
	// Applies a choice by index and re-shows the main menu so the new video starts.
	void Select(int a_index);
	// Sets the video file directly; empty restores vanilla.
	void SetMenuVideo(const std::string& a_path);
	[[nodiscard]] std::string MenuVideo();
}
