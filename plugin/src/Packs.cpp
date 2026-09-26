#include "Packs.h"

#include "Catalog.h"
#include "Settings.h"

namespace GShuf::Packs
{
	namespace
	{
		std::vector<Pack> s_packs;
		const Pack* s_vanilla{ nullptr };
		std::string s_signature;

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

	bool Load()
	{
		s_packs.clear();
		s_vanilla = nullptr;
		s_signature.clear();
		const auto path = std::filesystem::path("Data") / "GShuf" / "packs.json";
		std::ifstream file(path);
		if (!file) {
			logger::warn("packs: no {} (no packs converted yet)", path.string());
			return false;
		}
		try {
			const auto json = nlohmann::json::parse(file);
			s_signature = Str(json, "signature");
			for (const auto& row : json.value("packs", nlohmann::json::array())) {
				Pack pack;
				pack.id = Settings::Lower(Str(row, "id"));
				pack.name = Str(row, "name");
				if (pack.name.empty()) pack.name = pack.id;
				pack.vanilla = pack.id == "vanilla";
				pack.video = Str(row, "video");
				std::replace(pack.video.begin(), pack.video.end(), '/', '\\');
				std::size_t count = 0;
				for (const auto& img : row.value("images", nlohmann::json::array())) {
					Image image;
					image.material = Catalog::NormalizeMaterialPath(Str(img, "material"));
					image.slot = Catalog::NormalizeMaterialPath(Str(img, "slot"));
					image.width = Int(img, "width");
					image.height = Int(img, "height");
					const auto category = Settings::NormalizeCategory(Str(img, "category"));
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

	std::string Signature()
	{
		if (!s_signature.empty()) return s_signature;
		std::string ids;
		for (const auto& pack : s_packs) ids += pack.id + ";";
		return ids.empty() ? "none" : ids;
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
