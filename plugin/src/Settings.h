#pragma once

namespace GShuf::Settings
{
	inline constexpr int kSlotsPerCategory = 10;
	inline constexpr std::string_view kModName = "GalleryShuffle"sv;

	struct CategorySettings
	{
		std::array<int, kSlotsPerCategory> percent{};
	};

	struct Values
	{
		bool blankCanvases{ false };
		bool newLayoutEachLoad{ false };  // a new layout seed whenever a save loads
		int seed{ 1 };
		std::map<std::string, CategorySettings> categories;
		std::array<bool, kSlotsPerCategory> otherByKind{};  // per Other replacers pack slot: its "Set by kind of object" switch
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
	void SetBlankCanvases(bool a_enabled);
	void SetNewLayoutEachLoad(bool a_enabled);
	void SetSeed(int a_seed);
	[[nodiscard]] int Seed();
	// MCM section names carry this prefix (lowercase here); category names drop it.
	inline constexpr std::string_view kSectionPrefix{ "gshuf_" };
	// Lowercase category name without the MCM section prefix.
	[[nodiscard]] std::string NormalizeCategory(std::string_view a_category);
	[[nodiscard]] std::string Lower(std::string_view a_text);
	// MCM setting id "<prefix><slot>:<Section>", or "<prefix><slot>_<kind>:GShuf_Other" for an other_<kind> category.
	[[nodiscard]] std::string MenuKey(std::string_view a_prefix, int a_slot, std::string_view a_category);
}
