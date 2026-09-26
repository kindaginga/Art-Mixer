#include "Catalog.h"

#include "Settings.h"

namespace GShuf::Catalog
{
	namespace
	{
		std::unordered_map<std::string, Slot> s_slots;

		// Reads a string field, treating a missing key or a JSON null as empty.
		[[nodiscard]] std::string Str(const nlohmann::json& a_row, const char* a_key)
		{
			const auto it = a_row.find(a_key);
			return it != a_row.end() && it->is_string() ? it->get<std::string>() : std::string();
		}

		[[nodiscard]] int Int(const nlohmann::json& a_row, const char* a_key)
		{
			const auto it = a_row.find(a_key);
			return it != a_row.end() && it->is_number() ? it->get<int>() : 0;
		}
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
				slot.material = NormalizeMaterialPath(Str(row, "material"));
				slot.category = Settings::NormalizeCategory(Str(row, "category"));
				slot.width = Int(row, "width");
				slot.height = Int(row, "height");
				slot.blank = Settings::Lower(Str(row, "diffuse")).find("paintingcanvas") != std::string::npos || slot.material.find("blank") != std::string::npos;
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
