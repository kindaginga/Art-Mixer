#include "Packs.h"

#include "Catalog.h"
#include "Settings.h"

namespace GShuf::Packs
{
	namespace
	{
		std::vector<Pack> s_packs;
		const Pack* s_vanilla{ nullptr };
	}

	bool Load()
	{
		s_packs.clear();
		s_vanilla = nullptr;
		const auto path = std::filesystem::path("Data") / "GShuf" / "packs.json";
		std::ifstream file(path);
		if (!file) {
			logger::warn("packs: no {} (no packs converted yet)", path.string());
			return false;
		}
		try {
			const auto json = nlohmann::json::parse(file);
			for (const auto& row : json.value("packs", nlohmann::json::array())) {
				Pack pack;
				pack.id = Settings::Lower(row.value("id", ""));
				pack.name = row.value("name", pack.id);
				pack.vanilla = pack.id == "vanilla";
				std::size_t count = 0;
				for (const auto& img : row.value("images", nlohmann::json::array())) {
					Image image;
					image.material = Catalog::NormalizeMaterialPath(img.value("material", ""));
					image.slot = Catalog::NormalizeMaterialPath(img.value("slot", ""));
					image.width = img.value("width", 0);
					image.height = img.value("height", 0);
					const auto category = Settings::NormalizeCategory(img.value("category", ""));
					if (image.material.empty() || category.empty()) continue;
					pack.images[category].push_back(std::move(image));
					++count;
				}
				if (pack.id.empty()) continue;
				logger::info("packs: '{}' ({}) with {} images in {} categories", pack.name, pack.id, count, pack.images.size());
				s_packs.push_back(std::move(pack));
			}
		} catch (const std::exception& e) {
			logger::error("packs: parse failure in {}: {}", path.string(), e.what());
			s_packs.clear();
			return false;
		}
		for (const auto& pack : s_packs) {
			if (pack.vanilla) s_vanilla = &pack;
		}
		return true;
	}

	const std::vector<Pack>& All()
	{
		return s_packs;
	}

	const Pack* Vanilla()
	{
		return s_vanilla;
	}

	std::vector<const Pack*> SlotsFor(std::string_view a_category)
	{
		std::vector<const Pack*> slots;
		const auto category = Settings::NormalizeCategory(a_category);
		for (const auto& pack : s_packs) {
			if (pack.vanilla) continue;
			const auto it = pack.images.find(category);
			if (it != pack.images.end() && !it->second.empty()) slots.push_back(&pack);
		}
		return slots;
	}
}
