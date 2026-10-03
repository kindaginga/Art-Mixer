#pragma once

namespace ADT::MainMenu
{
	struct Option
	{
		std::string name;
		std::string video;  // relative to the game folder, e.g. Data\Video\ADT\Packs\x\MainMenuLoop.bk2; empty means vanilla
		std::string music;  // menu theme relative to Data\Music, e.g. ADT\1a2b3c4d.xwm; empty means vanilla
	};

	// Hooks the game's Bink video open so the main menu video can come from a pack, and finds the menu theme's path in the
	// executable so the theme can come from a pack too.
	bool Install();
	// Registers the main menu open/close sink and adds the plugin's entry to the main menu list; needs the UI singleton.
	void OnGameDataReady();
	// Replaces the options the selector lists and the current choice.
	void SetOptions(std::vector<Option> a_options, int a_selected);
	// The file an interface movie request opens instead (a pack's replacement of that movie), or empty (any thread).
	std::string RedirectMovie(std::string_view a_url);
	// Builds the option list from the installed packs and restores the saved choice.
	void LoadFromPacks();
	// True once the intro videos are over and the main menu is showing its loop video.
	[[nodiscard]] bool AtMainMenu();
	// Refuses every video except the menu loop, so an automated run reaches the menu without the intro.
	void SetSkipStartupVideos(bool a_skip);
	// Leaves the main menu entry out when false (autotest baseline without it).
	void SetEntryEnabled(bool a_enabled);
	// The engine's cursor position in its own units.
	[[nodiscard]] std::pair<int, int> CursorPos();
	// Logs MainMenu's flags and input context and the control map's context stack (diagnostics).
	void LogMenuState(const char* a_tag);
	// The main menu list's entries and row clips, for the log; a message when the list is missing.
	[[nodiscard]] std::string EntryState();
	// Acts as a press of the MENU ART entry, which opens the selector (game thread).
	void PressEntry();
	// Acts as a press of the selector's row for an option (game thread).
	void PressOption(int a_option);
	[[nodiscard]] int Selected();
	[[nodiscard]] int OptionCount();
	// Applies a choice by index and re-shows the main menu so the new video starts.
	void Select(int a_index);
}
