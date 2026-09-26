#pragma once

namespace GShuf::Packs
{
	struct Image
	{
		std::string material;  // path relative to Materials, as written by the converter
		std::string slot;      // vanilla slot this image replaces, empty for standalone art
		int width{ 0 };
		int height{ 0 };
	};

	struct Pack
	{
		std::string id;
		std::string name;
		bool vanilla{ false };
		std::map<std::string, std::vector<Image>> images;  // by category
	};

	// Loads Data\GShuf\packs.json written by the converter.
	bool Load();
	[[nodiscard]] const std::vector<Pack>& All();
	[[nodiscard]] const Pack* Vanilla();
	// Packs that have images for the category, in slot order (1-based slots).
	[[nodiscard]] std::vector<const Pack*> SlotsFor(std::string_view a_category);
}
