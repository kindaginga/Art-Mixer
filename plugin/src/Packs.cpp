#include "Packs.h"

#include "Catalog.h"
#include "Engine.h"
#include "Settings.h"

namespace GShuf::Packs
{
	namespace
	{
		std::vector<Pack> s_packs;
		const Pack* s_vanilla{ nullptr };
		std::string s_signature;
		std::vector<Restore> s_restores;
		std::unordered_map<std::string, std::vector<std::string>> s_swapOwners;  // "form|material" -> the materials the game and converted mods show there
		std::unordered_map<std::string, std::vector<int>> s_groups;              // category -> each slot's group, as the group's first slot
		std::vector<std::string> s_sections;                                     // MCM section names of the share categories, in menu order

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
		s_groups.clear();
		s_sections.clear();
		const auto path = std::filesystem::path("Data") / "GShuf" / "packs.json";
		std::ifstream file(path);
		if (!file) {
			logger::warn("packs: no {} (no packs converted yet)", path.string());
			return false;
		}
		// Parses into locals and publishes only a whole file, so a broken file leaves the state of a missing one.
		std::vector<Pack> packs;
		std::vector<Restore> restores;
		std::unordered_map<std::string, std::vector<int>> groups;
		std::vector<std::string> sections;
		std::string signature;
		try {
			const auto json = nlohmann::json::parse(file);
			signature = Str(json, "signature");
			if (const auto listed = json.find("sections"); listed != json.end() && listed->is_array()) {
				for (const auto& name : *listed) {
					if (name.is_string() && !name.get_ref<const std::string&>().empty()) sections.push_back(name.get<std::string>());
				}
			}
			if (const auto listed = json.find("groups"); listed != json.end() && listed->is_object()) {
				for (const auto& [category, rows] : listed->items()) {
					auto& heads = groups[Settings::NormalizeCategory(category)];
					for (const auto& row : rows) {
						if (!row.is_array() || row.empty() || !row.front().is_number_integer()) continue;
						const int head = row.front().get<int>();
						for (const auto& slot : row) {
							const int index = slot.is_number_integer() ? slot.get<int>() : 0;
							if (index < 1 || index > Settings::kSlotsPerCategory) continue;
							if (heads.size() < static_cast<std::size_t>(index)) heads.resize(static_cast<std::size_t>(index), 0);
							heads[static_cast<std::size_t>(index - 1)] = head;
						}
					}
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
					if (const auto w = img.find("cardWidth"); w != img.end() && w->is_number()) image.cardWidth = w->get<float>();
					if (const auto b = img.find("bounds"); b != img.end() && b->is_array() && b->size() == 6) {
						std::array<std::int16_t, 6> values{};
						for (std::size_t i = 0; i < 6; ++i) values[i] = static_cast<std::int16_t>((*b)[i].get<int>());
						image.bounds = values;
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
		s_groups = std::move(groups);
		s_sections = std::move(sections);
		s_signature = std::move(signature);
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
		// Counts the texture set textures whose engine file id is the hash of their name with and without the Textures folder.
		void MeasureTextureIDs()
		{
			auto* data = RE::TESDataHandler::GetSingleton();
			if (!data) return;
			std::size_t names = 0, withFolder = 0, withoutFolder = 0;
			for (auto* set : data->GetFormArray<RE::BGSTextureSet>()) {
				if (!set) continue;
				for (std::size_t i = 0; i < std::size(set->textures); ++i) {
					const char* name = set->textures[i].textureName.c_str();
					if (!name || !*name) continue;
					++names;
					const auto path = Engine::TexturePath(name);
					if (set->textureFileIDs[i] == Engine::FileID("textures\\" + path)) ++withFolder;
					else if (set->textureFileIDs[i] == Engine::FileID(path)) ++withoutFolder;
				}
			}
			logger::info("packs: texture set file ids: {} of {} names hash to their id with the Textures folder, {} without it", withFolder, names, withoutFolder);
		}

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
		MeasureTextureIDs();
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
		if (!s_signature.empty()) return s_signature;
		std::string ids;
		for (const auto& pack : s_packs) ids += pack.id + ";";
		return ids.empty() ? "none" : ids;
	}

	// How much of a pack's art a page holds; the Other replacers page counts every kind.
	static std::size_t OnPage(const Pack& a_pack, std::string_view a_page)
	{
		const auto on = [&](const std::string& a_category) { return a_page == "other" ? a_category.starts_with("other_") : a_category == a_page; };
		std::size_t count = 0;
		for (const auto& [category, images] : a_pack.images) {
			if (on(category)) count += images.size();
		}
		for (const auto& movie : a_pack.movies) {
			if (on(movie.category)) ++count;
		}
		return count;
	}

	std::string MainPage(const Pack& a_pack)
	{
		static constexpr std::array<std::string_view, 6> pages{ "paintings", "posters", "signage", "magazines", "quest", "other" };
		std::string best;
		std::size_t most = 0;
		for (const auto page : pages) {
			const auto count = OnPage(a_pack, page);
			if (count > most) {
				most = count;
				best = page;
			}
		}
		return best;
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

	std::vector<const Pack*> SlotsFor(std::string_view a_category)
	{
		std::vector<const Pack*> slots;
		const auto category = Settings::NormalizeCategory(a_category);
		const bool page = category == "paintings" || category == "posters" || category == "signage" || category == "magazines" || category == "quest" || category == "other";
		for (const auto& pack : s_packs) {
			if (pack.vanilla) continue;
			if (page) {
				// A page lists the packs whose one slider sits on it.
				if (MainPage(pack) == category) slots.push_back(&pack);
				continue;
			}
			// A kind lists only the packs of the Other replacers page.
			if (category.starts_with("other_") && MainPage(pack) != "other") continue;
			if (category == "loadingscreens") {
				// Loading screens show a pack's pictures, so every pack with pictures has a share there.
				const bool pictures = std::any_of(pack.images.begin(), pack.images.end(), [](const auto& entry) {
					const bool art = entry.first == "paintings" || entry.first == "posters" || entry.first == "signage" || entry.first == "magazines" || entry.first == "quest";
					return art && std::any_of(entry.second.begin(), entry.second.end(), [](const Image& a_image) {
						return a_image.slot.empty() || std::string_view("#&%~!").find(a_image.slot[0]) == std::string_view::npos;
					});
				});
				const auto own = pack.images.find(category);
				const bool items = own != pack.images.end() && !own->second.empty();
				const bool movies = std::any_of(pack.movies.begin(), pack.movies.end(), [&](const Movie& m) { return m.category == category; });
				if (pictures || items || movies) slots.push_back(&pack);
				continue;
			}
			const auto it = pack.images.find(category);
			const bool movies = std::any_of(pack.movies.begin(), pack.movies.end(), [&](const Movie& m) { return m.category == category; });
			if ((it != pack.images.end() && !it->second.empty()) || movies) slots.push_back(&pack);
		}
		return slots;
	}

	int GroupOf(std::string_view a_category, int a_slot)
	{
		const auto category = Settings::NormalizeCategory(a_category);
		const auto count = static_cast<int>(std::min<std::size_t>(SlotsFor(category).size(), Settings::kSlotsPerCategory));
		if (a_slot < 1 || a_slot > count) return 0;
		const auto it = s_groups.find(category);
		if (it == s_groups.end()) return 1;
		const auto index = static_cast<std::size_t>(a_slot - 1);
		return index < it->second.size() && it->second[index] > 0 ? it->second[index] : a_slot;
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
