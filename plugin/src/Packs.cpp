#include "Packs.h"

#include "Catalog.h"
#include "GameArchives.h"
#include "Settings.h"

namespace ADT::Packs
{
	namespace
	{
		std::vector<Pack> s_packs;
		const Pack* s_vanilla{ nullptr };
		std::string s_signature;
		std::vector<Restore> s_restores;
		std::unordered_map<std::string, std::vector<std::string>> s_swapOwners;  // "form|material" -> the materials the game and converted mods show there
		std::vector<std::string> s_sections;                                     // MCM section names of the share categories, in menu order
		std::unordered_map<std::string, std::vector<const Pack*>> s_slots;       // category -> the packs its slots name, in slot order

		[[nodiscard]] std::optional<float> Number(const nlohmann::json& a_row, const char* a_key)
		{
			const auto it = a_row.find(a_key);
			return it != a_row.end() && it->is_number() ? std::optional<float>(it->get<float>()) : std::nullopt;
		}

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
		s_restores.clear();
		s_sections.clear();
		s_slots.clear();
		const auto path = std::filesystem::path("Data") / "ADT" / "packs.json";
		std::ifstream file(path);
		if (!file) {
			logger::warn("packs: no {} (no packs converted yet)", path.string());
			return false;
		}
		// Parses into locals and publishes only a whole file, so a broken file leaves the state of a missing one.
		std::vector<Pack> packs;
		std::vector<Restore> restores;
		std::vector<std::string> sections;
		std::vector<std::vector<std::string>> slotIds;  // per section, the ids of the packs its slots name
		std::string signature;
		std::unordered_map<std::string, std::string> gameTextures;  // game texture (relative to Textures, lowercase) -> the archive it is read from
		std::unordered_map<std::string, std::string> gameModels;    // game model (relative to Meshes, lowercase) -> the archive it is read from
		try {
			const auto json = nlohmann::json::parse(file);
			signature = Str(json, "signature");
			for (const auto* list : { "gameTextures", "gameModels" }) {
				for (const auto& [name, archive] : json.value(list, nlohmann::json::object()).items()) {
					if (!archive.is_string()) continue;
					auto key = Settings::Lower(name);
					std::replace(key.begin(), key.end(), '/', '\\');
					(std::string_view(list) == "gameTextures" ? gameTextures : gameModels).emplace(std::move(key), archive.get<std::string>());
				}
			}
			for (const auto& row : json.value("categories", nlohmann::json::array())) {
				const auto section = Str(row, "section");
				if (section.empty()) continue;
				sections.push_back(section);
				auto& ids = slotIds.emplace_back();
				for (const auto& id : row.value("packs", nlohmann::json::array())) {
					if (id.is_string()) ids.push_back(Settings::Lower(id.get<std::string>()));
				}
			}
			for (const auto& row : json.value("restores", nlohmann::json::array())) {
				Restore restore{ Str(row, "kind"), Str(row, "form"), Str(row, "from"), Str(row, "mod"), Str(row, "game"), Int(row, "index"), Number(row, "gameColor"), Number(row, "modColor") };
				if (!restore.kind.empty() && !restore.form.empty() && !restore.game.empty()) restores.push_back(std::move(restore));
			}

			for (const auto& row : json.value("packs", nlohmann::json::array())) {
				Pack pack;
				pack.id = Settings::Lower(Str(row, "id"));
				pack.name = Str(row, "name");
				if (pack.name.empty()) pack.name = pack.id;
				pack.vanilla = pack.id == "vanilla";
				pack.page = Settings::NormalizeCategory(Str(row, "page"));
				pack.video = Str(row, "video");
				std::replace(pack.video.begin(), pack.video.end(), '/', '\\');
				for (const auto& movie : row.value("movies", nlohmann::json::array())) {
					Movie entry{ Settings::Lower(Str(movie, "path")), Str(movie, "file"), Settings::NormalizeCategory(Str(movie, "category")) };
					std::replace(entry.path.begin(), entry.path.end(), '/', '\\');
					if (!entry.path.empty() && !entry.file.empty()) pack.movies.push_back(std::move(entry));
				}
				for (const auto& screen : row.value("screens", nlohmann::json::array())) {
					auto key = Settings::Lower(Str(screen, "screen"));
					ScreenCard card{ Str(screen, "card"), Settings::Lower(Str(screen, "model")), Settings::Lower(Str(screen, "gameModel")), Settings::Lower(Str(screen, "swap")), Number(screen, "color") };
					std::replace(card.gameModel.begin(), card.gameModel.end(), '/', '\\');
					if (!key.empty() && !card.card.empty()) pack.screens.emplace(std::move(key), std::move(card));
				}
				pack.music = Str(row, "music");
				std::replace(pack.music.begin(), pack.music.end(), '/', '\\');
				std::size_t count = 0;
				for (const auto& img : row.value("images", nlohmann::json::array())) {
					Image image;
					image.material = Catalog::NormalizeMaterialPath(Str(img, "material"));
					image.slot = Catalog::NormalizeMaterialPath(Str(img, "slot"));
					image.width = Int(img, "width");
					image.height = Int(img, "height");
					if (const auto drop = img.find("dropSwap"); drop != img.end() && drop->is_boolean() && drop->get<bool>()) image.swap.emplace(nullptr);
					if (const auto form = img.find("swapForm"); form != img.end() && form->is_string()) {
						image.swapForm = form->get<std::string>();
						if (image.swapForm.empty()) image.swap.emplace(nullptr);
					}
					image.name = Str(img, "name");
					image.card = Str(img, "card");
					if (const auto b = img.find("bounds"); b != img.end() && b->is_array() && b->size() == 6) {
						std::array<std::int16_t, 6> values{};
						for (std::size_t i = 0; i < 6; ++i) values[i] = static_cast<std::int16_t>((*b)[i].get<int>());
						image.bounds = values;
					}
					if (const auto b = img.find("mapBounds"); b != img.end() && b->is_array() && b->size() == 6) {
						std::array<std::int32_t, 6> values{};
						for (std::size_t i = 0; i < 6; ++i) values[i] = (*b)[i].get<std::int32_t>();
						image.mapBounds = values;
					}
					const auto category = Settings::NormalizeCategory(Str(img, "category"));
					if ((image.material.empty() && image.name.empty() && !image.bounds) || category.empty()) continue;
					pack.images[category].push_back(std::move(image));
					++count;
				}
				if (pack.id.empty()) continue;
				logger::info("packs: '{}' ({}) with {} images in {} categories", pack.name, pack.id, count, pack.images.size());
				if (!pack.screens.empty()) logger::info("packs: '{}' shows {} loading screens with a card of their own model", pack.name, pack.screens.size());
				packs.push_back(std::move(pack));
			}
		} catch (const std::exception& e) {
			logger::error("packs: parse failure in {}: {}", path.string(), e.what());
			return false;
		}
		s_packs = std::move(packs);
		s_restores = std::move(restores);
		s_signature = std::move(signature);
		GameArchives::SetSources(gameTextures, gameModels);
		for (std::size_t i = 0; i < sections.size(); ++i) {
			auto& slots = s_slots[Settings::NormalizeCategory(sections[i])];
			for (const auto& id : slotIds[i]) {
				const auto pack = std::find_if(s_packs.begin(), s_packs.end(), [&](const Pack& a_pack) { return a_pack.id == id; });
				if (pack != s_packs.end()) slots.push_back(&*pack);
			}
		}
		s_sections = std::move(sections);
		s_swapOwners.clear();
		for (const auto& restore : s_restores) {
			if (restore.kind != "swap") continue;
			auto& owners = s_swapOwners[Settings::Lower(restore.form) + "|" + Catalog::NormalizeMaterialPath(restore.from)];
			owners.push_back(Catalog::NormalizeMaterialPath(restore.game));
			owners.push_back(Catalog::NormalizeMaterialPath(restore.mod));
		}
		for (const auto& pack : s_packs) {
			if (pack.vanilla) s_vanilla = &pack;
		}
		return true;
	}

	namespace
	{
		// The color remapping index most entries of the game's material swaps hold, which is the value of an entry without one.
		[[nodiscard]] float UnsetSwapColor()
		{
			static const float s_value = [] {
				std::map<float, std::size_t> counts;
				if (auto* data = RE::TESDataHandler::GetSingleton(); data) {
					for (auto* swap : data->GetFormArray<RE::BGSMaterialSwap>()) {
						if (!swap) continue;
						for (const auto& entry : swap->swapMap) ++counts[entry.second.colorRemappingIndex];
					}
				}
				const auto most = std::max_element(counts.begin(), counts.end(), [](const auto& a, const auto& b) { return a.second < b.second; });
				const auto value = most == counts.end() ? 0.0F : most->first;
				logger::info("packs: material swap entries: {} distinct color remapping indexes, {} held by {} entries", counts.size(), value, most == counts.end() ? 0 : most->second);
				return value;
			}();
			return s_value;
		}

		// Puts back each material swap entry a converted mod's plugin changed that the running game still shows (texture sets follow their rolls).
		void RestoreGameRecords()
		{
			std::size_t swaps = 0, unchanged = 0, missing = 0;
			for (const auto& restore : s_restores) {
				auto* form = RE::TESForm::GetFormByID(Catalog::FormIDOf(restore.form));
				if (restore.kind == "swap") {
					auto* swap = form && form->Is(RE::ENUM_FORM_ID::kMSWP) ? static_cast<RE::BGSMaterialSwap*>(form) : nullptr;
					if (!swap) {
						++missing;
						continue;
					}
					const auto from = Catalog::NormalizeMaterialPath(restore.from);
					const auto mod = Catalog::NormalizeMaterialPath(restore.mod);
					bool changed = false;
					for (auto& entry : swap->swapMap) {
						if (Catalog::NormalizeMaterialPath(entry.first.c_str()) != from || Catalog::NormalizeMaterialPath(entry.second.swapMaterial.c_str()) != mod) continue;
						entry.second.swapMaterial = restore.game;
						if (restore.gameColor) entry.second.colorRemappingIndex = *restore.gameColor;
						else if (restore.modColor) entry.second.colorRemappingIndex = UnsetSwapColor();
						changed = true;
					}
					if (changed) {
						++swaps;
						logger::info("packs: material swap {} shows {} in place of {} again (was {})", restore.form, restore.game, restore.from, restore.mod);
					} else {
						++unchanged;
					}
				}
			}
			if (swaps + unchanged + missing > 0) logger::info("packs: {} material swap entries put back to the game's; {} already the game's, {} not loaded", swaps, unchanged, missing);
		}
	}

	void OnGameDataReady()
	{
		std::size_t resolved = 0;
		for (auto& pack : s_packs) {
			for (auto& [category, images] : pack.images) {
				for (auto& image : images) {
					if (image.swapForm.empty()) continue;
					auto* form = RE::TESForm::GetFormByID(Catalog::FormIDOf(image.swapForm));
					if (form && form->Is(RE::ENUM_FORM_ID::kMSWP)) {
						image.swap = static_cast<RE::BGSMaterialSwap*>(form);
						++resolved;
					} else {
						logger::warn("packs: '{}' names material swap {} for {}, which is not loaded; that version keeps the object's own", pack.name, image.swapForm, image.slot);
					}
				}
			}
		}
		if (resolved > 0) logger::info("packs: {} versions load with the game's own material swap", resolved);
		RestoreGameRecords();
	}

	const ScreenCard* VanillaScreenCard(std::string_view a_screen)
	{
		if (!s_vanilla) return nullptr;
		const auto it = s_vanilla->screens.find(Settings::Lower(a_screen));
		return it == s_vanilla->screens.end() ? nullptr : &it->second;
	}

	const std::vector<Restore>& Restores()
	{
		return s_restores;
	}

	bool OwnsSwapEntry(std::string_view a_form, std::string_view a_material, std::string_view a_shown)
	{
		const auto it = s_swapOwners.find(Settings::Lower(a_form) + "|" + Catalog::NormalizeMaterialPath(a_material));
		if (it == s_swapOwners.end()) return false;
		const auto shown = Catalog::NormalizeMaterialPath(a_shown);
		return std::find(it->second.begin(), it->second.end(), shown) != it->second.end();
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
		return s_signature;
	}

	std::vector<const Pack*> PacksWith(std::string_view a_category)
	{
		std::vector<const Pack*> packs;
		const auto category = Settings::NormalizeCategory(a_category);
		for (const auto& pack : s_packs) {
			if (pack.vanilla) continue;
			const auto it = pack.images.find(category);
			const bool movies = std::any_of(pack.movies.begin(), pack.movies.end(), [&](const Movie& m) { return m.category == category; });
			if ((it != pack.images.end() && !it->second.empty()) || movies) packs.push_back(&pack);
		}
		return packs;
	}

	const std::vector<const Pack*>& SlotsFor(std::string_view a_category)
	{
		static const std::vector<const Pack*> s_none;
		const auto it = s_slots.find(Settings::NormalizeCategory(a_category));
		return it == s_slots.end() ? s_none : it->second;
	}

	const std::vector<std::string>& Sections()
	{
		return s_sections;
	}

	std::vector<std::pair<const Pack*, const Movie*>> MoviesFor(std::string_view a_path)
	{
		auto path = Settings::Lower(a_path);
		std::replace(path.begin(), path.end(), '/', '\\');
		std::vector<std::pair<const Pack*, const Movie*>> found;
		for (const auto& pack : s_packs) {
			for (const auto& movie : pack.movies) {
				if (movie.path == path) found.emplace_back(&pack, &movie);
			}
		}
		return found;
	}
}
