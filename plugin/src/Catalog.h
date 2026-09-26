#pragma once

namespace GShuf::Catalog
{
	struct Slot
	{
		std::string material;  // normalized path relative to Materials
		std::string category;
		int width{ 0 };
		int height{ 0 };
		bool blank{ false };
	};

	// Loads Data\GShuf\vanilla-slots.json.
	bool Load();
	[[nodiscard]] const Slot* Find(std::string_view a_shaderPropertyName);
	[[nodiscard]] std::size_t Count();
	// Lowercases, converts slashes and strips leading data\ and materials\ segments.
	[[nodiscard]] std::string NormalizeMaterialPath(std::string_view a_path);
}
