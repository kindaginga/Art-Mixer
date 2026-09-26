#include "Catalog.h"

#include "Settings.h"

namespace GShuf::Catalog
{
	namespace
	{
		std::unordered_map<std::string, Slot> s_slots;
	}

	std::string NormalizeMaterialPath(std::string_view a_path)
	{
		auto path = Settings::Lower(a_path);
		std::replace(path.begin(), path.end(), '/', '\\');
		while (!path.empty() && path.front() == '\\') path.erase(0, 1);
		if (path.starts_with("data\\")) path.erase(0, 5);
		if (path.starts_with("materials\\")) path.erase(0, 10);
		return path;
	}

	bool Load()
	{
		s_slots.clear();
		const auto path = std::filesystem::path("Data") / "GShuf" / "vanilla-slots.json";
		std::ifstream file(path);
		if (!file) {
			logger::error("catalog: cannot open {}", path.string());
			return false;
		}
		try {
			const auto json = nlohmann::json::parse(file);
			for (const auto& row : json) {
				Slot slot;
				slot.material = NormalizeMaterialPath(row.value("material", ""));
				slot.category = Settings::NormalizeCategory(row.value("category", ""));
				slot.width = row.value("width", 0);
				slot.height = row.value("height", 0);
				slot.blank = Settings::Lower(row.value("diffuse", "")).find("paintingcanvas") != std::string::npos;
				if (slot.material.empty() || slot.category.empty()) continue;
				s_slots[slot.material] = std::move(slot);
			}
		} catch (const std::exception& e) {
			logger::error("catalog: parse failure in {}: {}", path.string(), e.what());
			s_slots.clear();
			return false;
		}
		logger::info("catalog: {} vanilla slots loaded", s_slots.size());
		return true;
	}

	const Slot* Find(std::string_view a_shaderPropertyName)
	{
		const auto it = s_slots.find(NormalizeMaterialPath(a_shaderPropertyName));
		return it == s_slots.end() ? nullptr : &it->second;
	}

	std::size_t Count()
	{
		return s_slots.size();
	}
}
