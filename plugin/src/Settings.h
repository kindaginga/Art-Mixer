#pragma once

namespace ArtMixer::Settings
{
	inline constexpr int kMaxSlots = 128;  // slots one page or list may have: the Papyrus array limit
	inline constexpr std::string_view kModName = "ArtMixer"sv;

	struct CategorySettings
	{
		std::vector<int> percent;  // one share per slot, as many slots as the settings files name
	};

	struct Values
	{
		bool newLayoutEachLoad{ false };  // a new layout seed whenever a save loads
		int seed{ 1 };
		std::map<std::string, CategorySettings> categories;
		std::map<std::string, int> mainMenu;  // the Main menu page's boxes and switches by lowercase key, as applied; absent until applied once
		std::map<std::string, int> loadingScreens;  // the Loading screens section's Leave box by lowercase key ("bleavetoalr"), as applied
	};

	// Reads the MCM defaults, then the applied settings; on the first start without them, the menu's values, which it then saves as applied.
	void Load();
	// Writes the settings in effect to the applied settings file; returns false when the file could not be written.
	bool Save();
	// Makes the menu's values (MCM's defaults, then its user file) the settings in effect.
	void LoadMenu();
	// Every whole-number menu value by MCM id ("key:Section"): MCM's defaults overlaid with its user file, which MCM writes on every change.
	[[nodiscard]] std::unordered_map<std::string, int> MenuValues();
	[[nodiscard]] Values Get();
	// The share of one slot of a category in effect (0 for an unknown category or slot).
	[[nodiscard]] int Percent(std::string_view a_category, int a_slot);
	void SetPercent(std::string_view a_category, int a_slot, int a_value);
	// The number of slots a category has values for in effect (0 for an unknown category).
	[[nodiscard]] int StoredSlots(std::string_view a_category);
	void SetSeed(int a_seed);
	[[nodiscard]] int Seed();
	// A list category's pick in effect: the slot at 100, or 0 for Vanilla.
	[[nodiscard]] int Pick(std::string_view a_category);
	// Sets a list category's pick: that slot gets everything, every other slot nothing.
	void SetPick(std::string_view a_category, int a_slot);
	// A Main menu page value as applied (lowercase key such as "brandommainmenu"), or nothing when never applied.
	[[nodiscard]] std::optional<int> MainMenuFlag(std::string_view a_key);
	// A Loading screens page box as applied (lowercase key such as "bleavetoalr"), or nothing when never applied.
	[[nodiscard]] std::optional<int> LoadingScreensFlag(std::string_view a_key);
	// MCM section names carry this prefix (lowercase here); category names drop it.
	inline constexpr std::string_view kSectionPrefix{ "artmixer_" };
	// Lowercase category name without the MCM section prefix.
	[[nodiscard]] std::string NormalizeCategory(std::string_view a_category);
	[[nodiscard]] std::string Lower(std::string_view a_text);
	// A path relative to one of Data's folders ("materials", "textures", "meshes"): lowercase, backslashes, without leading
	// slashes, "data\" or the folder itself.
	[[nodiscard]] std::string RelativePath(std::string_view a_path, std::string_view a_folder);
	// The settings key of a category's slot, named after its pack: "bPack_<id>" on Other replacers, "iPack_<id>" elsewhere; empty past the pack list.
	[[nodiscard]] std::string PackKey(std::string_view a_category, int a_slot);
	// MCM setting id "<PackKey>:<Section>", empty past the pack list.
	[[nodiscard]] std::string MenuKey(std::string_view a_section, int a_slot);
}
