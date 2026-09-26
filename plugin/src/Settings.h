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
		int seed{ 1 };
		std::map<std::string, CategorySettings> categories;
	};

	// Reads the MCM defaults and the player's overrides from disk.
	void Load();
	[[nodiscard]] Values Get();
	void SetPercent(std::string_view a_category, int a_slot, int a_value);
	void SetBlankCanvases(bool a_enabled);
	void SetSeed(int a_seed);
	[[nodiscard]] int Seed();
	[[nodiscard]] std::string NormalizeCategory(std::string_view a_category);
	[[nodiscard]] std::string Lower(std::string_view a_text);
}
