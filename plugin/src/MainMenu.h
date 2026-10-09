#pragma once

namespace ADT::MainMenu
{
	struct Option
	{
		std::string name;
		std::string video;  // relative to the game folder, e.g. Data\Video\ADT\Packs\x\MainMenuLoop.bk2; empty means vanilla
		std::string music;  // menu theme relative to Data\Music, e.g. ADT\1a2b3c4d.xwm; empty means vanilla
		bool immersive{ false };   // an Immersive Main Menus menu, shown only while its box is on
		std::string musicSource;   // the file the music is copied from before it plays (Immersive Main Menus names are too long for the theme path), empty for none
	};

	// The three lists of the MENU ART selector and the Main menu page.
	enum class List
	{
		Themes,
		Backgrounds,
		Music
	};

	// A pack's menu files for Random Main Menu: Vanilla and every pack with a menu video or theme.
	struct MenuFile
	{
		std::string id;
		std::string name;
		std::string video;  // relative to the game folder, empty for none
		std::string music;  // relative to Data\Music, empty for none
	};

	// Hooks the game's Bink video open so the main menu video can come from a pack, and finds the menu theme's path in the
	// executable so the theme can come from a pack too.
	bool Install();
	// Registers the main menu open/close sink and adds the plugin's entry to the main menu list; needs the UI singleton.
	void OnGameDataReady();
	// Replaces the menu packs (index 0 Vanilla) and picks one for both the background and the music.
	void SetOptions(std::vector<Option> a_options, int a_selected);
	// The file an interface movie request opens instead (a pack's replacement of that movie), or empty (any thread).
	std::string RedirectMovie(std::string_view a_url);
	// Builds the menu packs from the installed packs and the Immersive Main Menus menus found; the picks come with LoadPicks.
	void LoadFromPacks();
	// Restores the picks from the settings in effect, or from the plugin's own state when the settings name none.
	void LoadPicks();
	// Takes the three lists' picks from the settings in effect: a changed theme sets both, otherwise the background and music picks stand.
	void ApplyFromSettings();
	// Shows or hides the Immersive Main Menus menus in the lists; a pick on a hidden menu goes back to Vanilla.
	void SetImmersiveShown(bool a_shown);
	// The number of entries a list shows past Vanilla.
	[[nodiscard]] int ListCount(List a_list);
	// The ids of a list's entries past Vanilla, in list order (pack ids, then "imm-<id>" for Immersive Main Menus menus).
	[[nodiscard]] std::vector<std::string> ListIds(List a_list);
	// The names of a list's entries past Vanilla, in list order.
	[[nodiscard]] std::vector<std::string> ListNames(List a_list);
	// The list position of the pick (0 Vanilla); Themes only while the background and the music are the same entry.
	[[nodiscard]] int PickSlot(List a_list);
	// Vanilla and every pack with a menu video or theme, as Random Main Menu entries are written from them.
	[[nodiscard]] std::vector<MenuFile> PackMenuFiles();
	// The plugin's own state file (Data\ADT\state.json) as an object, empty when missing.
	[[nodiscard]] nlohmann::json LoadState();
	void StoreState(const nlohmann::json& a_state);
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
	// Opens the BACKGROUNDS list and presses the row of the option at that list position (game thread).
	void PressOption(int a_option);
	// The position of the picked background in the BACKGROUNDS list (0 is Vanilla).
	[[nodiscard]] int Selected();
	// The number of options in the BACKGROUNDS list, Vanilla included.
	[[nodiscard]] int OptionCount();
	// Picks the background at a BACKGROUNDS list position, keeps the music pick, and re-creates the main menu so it shows.
	void Select(int a_index);
	// True when the background or the music pick is not Vanilla.
	[[nodiscard]] bool AnyPicked();
	// Sets both picks back to Vanilla when either is not.
	void ResetPicks();
}
