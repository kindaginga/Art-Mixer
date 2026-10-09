#pragma once

// Random Main Menu (Nexus 74187) and Immersive Main Menus (Nexus 82077) support: the packs' menu files offered to Random Main
// Menu under the names it draws from, its settings driven by the Main menu page, Immersive Main Menus' menus as picks, and the
// names Random Main Menu is kept from seeing.
namespace ArtMixer::MenuRotation
{
	// One Immersive Main Menus menu found in Data\Video: its id ("im11uw"), its name, and its files relative to the game folder.
	struct ImmersiveMenu
	{
		std::string id;
		std::string name;
		std::string video;  // Data\Video\mainmenuloop_im11uw.bk2
		std::string music;  // Data\Music\Special\mus_maintheme_im11uw.xwm, empty when the menu has no music file
	};

	// The Main menu page's boxes and switches as they are in effect: each box only while its mod is found, each switch only
	// while the Random Main Menu box is on and the Immersive Main Menus only box is off.
	struct Flags
	{
		bool randomMainMenu{ false };
		bool immersiveMainMenus{ false };
		bool immersiveOnly{ false };
		bool rotateThemes{ false };
		bool rotateBackgrounds{ false };
		bool rotateMusic{ false };
	};

	// Looks for Random Main Menu's file and Immersive Main Menus' files (plugin load, before the menu packs are built).
	void Detect();
	// The Immersive Main Menus menus found, in number order.
	[[nodiscard]] const std::vector<ImmersiveMenu>& Menus();
	// After every F4SE plugin loaded: finds Random Main Menu's module, takes over the two menu calls it patched (menu video
	// parameters and menu music path, each sent to Random Main Menu only while its rotation is wanted, otherwise to the game's own
	// code with the pick), hooks its folder listings, and applies the flags.
	void OnPluginsLoaded();
	// True once Random Main Menu's module is loaded.
	[[nodiscard]] bool RandomMainMenuLoaded();
	// The flags in effect, from the settings and what was found.
	[[nodiscard]] Flags Effective();
	// The flags the Main menu page shows by default: each box on when its mod is found, every switch off.
	[[nodiscard]] Flags Defaults();
	// The ids and values of the six Main menu page switches, as MCM ids ("bRandomMainMenu:ArtMixer_MainMenu"), in effect or by default.
	[[nodiscard]] std::vector<std::pair<std::string, int>> FlagValues(bool a_defaults);
	// Writes or removes the packs' Random Main Menu entries, sets Random Main Menu's settings, and sets the names hidden from it,
	// all from the flags in effect; also brings the MCM lists up to date with the Immersive Main Menus menus shown.
	void Apply();
	// Shows the message about a Random Main Menu file that did not load, once, when the main menu is up.
	void OnMainMenuReady();
	// True while Random Main Menu may choose the menu video: Rotate themes or Rotate backgrounds on, or Immersive Main Menus only.
	[[nodiscard]] bool VideoRotation();
	// The number of names kept from Random Main Menu's folder listings so far.
	[[nodiscard]] int HiddenCount();
	// The video a Random Main Menu entry of a pack stands for ("mainmenuloop_adt-<id>.bk2" -> the pack's video), or empty.
	[[nodiscard]] std::string EntryVideo(std::string_view a_requested);
}
