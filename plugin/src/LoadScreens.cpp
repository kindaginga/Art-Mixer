#include "LoadScreens.h"

#include "Catalog.h"
#include "Distributor.h"
#include "Packs.h"
#include "Settings.h"

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>

namespace ArtMixer::LoadScreens
{
	namespace
	{
		// A loading screen record's model and framing, found in its LoadNIFData by the pointed form's type: the model first,
		// then the transform after the embedded camera path (offset 0x40 on the runtimes measured). The rotation range the
		// menu turns the model through (ONAM, degrees) follows the model at 0x8, the zoom range (ZNAM) closes the block at 0x48.
		struct Record
		{
			std::string key;  // the record's plugin and local form id, stable across load orders
			RE::TESForm** model{ nullptr };
			RE::TESForm** transform{ nullptr };
			std::int16_t* rotation{ nullptr };
			float* zoom{ nullptr };
			RE::TESForm* originalModel{ nullptr };
			RE::TESForm* originalTransform{ nullptr };
			std::array<std::int16_t, 2> originalRotation{};
			std::array<float, 2> originalZoom{};
		};

		// How the magazine turns and zooms on the game's magazine screens.
		struct Motion
		{
			std::array<std::int16_t, 2> rotation;
			std::array<float, 2> zoom;
		};
		constexpr Motion kMagazineMotion{ { -180, 180 }, { -0.5F, 2.0F } };

		std::mutex s_mutex;
		std::vector<Record> s_records;
		RE::TESObjectSTAT* s_card{ nullptr };
		// One card per distinct card model (ArtMixer.esl 0x900 onward).
		std::vector<RE::TESObjectSTAT*> s_cards;
		RE::BGSTransform* s_magazineTransform{ nullptr };
		bool s_active{ false };
		std::vector<std::string> s_testCards;
		std::size_t s_testCard{ 0 };
		float s_cardColor{ 0.0F };  // the card statics' own color remapping index

		// Sets a card static's model and color remapping index (its own when the card names none).
		void SetCard(RE::TESObjectSTAT* a_card, const std::string& a_model, std::optional<float> a_color)
		{
			static_cast<RE::TESModel*>(a_card)->SetModel(a_model.c_str());
			static_cast<RE::BGSModelMaterialSwap*>(a_card)->colorRemappingIndex = a_color.value_or(s_cardColor);
		}

		// The vanilla card of a record while it shows the game's form, and that form shows the game's model and swap or is a converted object; or null.
		[[nodiscard]] const Packs::ScreenCard* OwnCard(const Record& a_record)
		{
			const auto* card = Packs::VanillaScreenCard(a_record.key);
			if (!card || !a_record.originalModel || a_record.originalModel->GetFormID() != Catalog::FormIDOf(card->model)) return nullptr;
			if (Catalog::FindForm(a_record.originalModel->GetFormID())) return card;
			auto* model = RE::fallout_cast<RE::TESModel*>(a_record.originalModel);
			const char* path = model ? model->GetModel() : nullptr;
			if (!path) return card;
			const auto shown = Settings::RelativePath(path, "meshes");
			const auto* swapComponent = model->GetAsModelMaterialSwap();
			const auto swap = swapComponent && swapComponent->swapForm ? Catalog::FormText(swapComponent->swapForm) : std::string();
			return shown == card->gameModel && swap == card->swap ? card : nullptr;
		}

		[[nodiscard]] bool Readable(const void* a_ptr, std::size_t a_size)
		{
			MEMORY_BASIC_INFORMATION mbi{};
			if (!a_ptr || !VirtualQuery(a_ptr, &mbi, sizeof(mbi))) return false;
			if (mbi.State != MEM_COMMIT || (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD))) return false;
			return reinterpret_cast<std::uintptr_t>(a_ptr) + a_size <= reinterpret_cast<std::uintptr_t>(mbi.BaseAddress) + mbi.RegionSize;
		}

		// The pointer-sized field of a block that points at a form the predicate accepts, or null.
		[[nodiscard]] RE::TESForm** FindFormField(std::byte* a_block, std::size_t a_size, const std::function<bool(RE::ENUM_FORM_ID)>& a_accept)
		{
			for (std::size_t offset = 0; offset + 8 <= a_size; offset += 8) {
				auto** field = reinterpret_cast<RE::TESForm**>(a_block + offset);
				auto* form = *field;
				if (!Readable(form, sizeof(RE::TESForm)) || !Readable(*reinterpret_cast<void**>(form), 8)) continue;
				if (a_accept(form->GetFormType())) return field;
			}
			return nullptr;
		}

		void Show(Record& a_record, RE::TESObjectSTAT* a_card, RE::BGSTransform* a_transform, const Motion& a_motion)
		{
			*a_record.model = a_card;
			if (a_record.transform) *a_record.transform = a_transform;
			if (a_record.rotation) std::copy(a_motion.rotation.begin(), a_motion.rotation.end(), a_record.rotation);
			if (a_record.zoom) std::copy(a_motion.zoom.begin(), a_motion.zoom.end(), a_record.zoom);
		}

		// A cover card shows with the framing and motion of the game's magazine screens; without that framing, with the record's own.
		void ShowCover(Record& a_record, RE::TESObjectSTAT* a_card)
		{
			if (s_magazineTransform) {
				Show(a_record, a_card, s_magazineTransform, kMagazineMotion);
				return;
			}
			const Motion motion{ a_record.originalRotation, a_record.originalZoom };
			Show(a_record, a_card, static_cast<RE::BGSTransform*>(a_record.originalTransform), motion);
		}

		// Points a record back at its own model, framing and motion (caller holds the lock).
		void RestoreRecord(Record& a_record)
		{
			*a_record.model = a_record.originalModel;
			if (a_record.transform) *a_record.transform = a_record.originalTransform;
			if (a_record.rotation) std::copy(a_record.originalRotation.begin(), a_record.originalRotation.end(), a_record.rotation);
			if (a_record.zoom) std::copy(a_record.originalZoom.begin(), a_record.originalZoom.end(), a_record.zoom);
		}

		// Gives every loading screen record the cover the current layout picks for it, or its own look when the pick is
		// vanilla; the same settings always give the same covers (game thread).
		void Assign()
		{
			std::scoped_lock lock(s_mutex);
			if (!s_active || !s_testCards.empty() || s_cards.empty()) return;
			// The assignment depends only on the seed and the loading screen shares, so it is redone only when those change.
			const auto values = Settings::Get();
			const auto shares = values.categories.find("loadingscreens");
			auto state = fmt::format("{}", values.seed);
			if (shares != values.categories.end()) {
				for (const auto percent : shares->second.percent) state += fmt::format("|{}", percent);
			}
			static std::string s_assigned;
			if (state == s_assigned) return;
			s_assigned = state;
			std::size_t shown = 0;
			std::size_t vanillaCards = 0;
			std::size_t withoutCard = 0;
			// One card static per card model and color; records showing the same card share it.
			std::unordered_map<std::string, std::size_t> used;
			const auto cardIndex = [&](const std::string& a_model, std::optional<float> a_color) -> std::optional<std::size_t> {
				const auto key = fmt::format("{}|{}", Settings::Lower(a_model), a_color ? fmt::format("{}", *a_color) : std::string());
				if (const auto it = used.find(key); it != used.end()) return it->second;
				if (used.size() >= s_cards.size()) return std::nullopt;
				const auto index = used.size();
				used.emplace(key, index);
				SetCard(s_cards[index], a_model, a_color);
				return index;
			};
			// A screen showing the game's own form: the vanilla card of its model where a converted mod ships that model's files, else its own look.
			const auto showOwn = [&](Record& a_record) {
				if (const auto* own = OwnCard(a_record); own) {
					if (const auto index = cardIndex(own->card, own->color); index) {
						const Motion motion{ a_record.originalRotation, a_record.originalZoom };
						Show(a_record, s_cards[*index], static_cast<RE::BGSTransform*>(a_record.originalTransform), motion);
						++vanillaCards;
						return;
					}
					++withoutCard;
				}
				RestoreRecord(a_record);
			};
			for (auto& record : s_records) {
				const auto* pack = Distributor::RollCategory("loadingscreens", "loadscreen|" + record.key);
				std::vector<const Packs::Image*> covers;
				if (pack && !pack->vanilla) {
					for (const auto& [category, images] : pack->images) {
						for (const auto& image : images) {
							if (!image.card.empty()) covers.push_back(&image);
						}
					}
				}
				if (covers.empty()) {
					showOwn(record);
					continue;
				}
				const auto* cover = covers[static_cast<std::size_t>(Distributor::LayoutHash("loadscreen-image|" + record.key) % covers.size())];
				const auto index = cardIndex(cover->card, std::nullopt);
				if (!index) {
					++withoutCard;
					RestoreRecord(record);
					continue;
				}
				ShowCover(record, s_cards[*index]);
				++shown;
			}
			logger::info("loading screens: {} of {} records show a pack cover under the current settings; {} show the vanilla card of their own model and {} their own look for want of a free card", shown, s_records.size(), vanillaCards, withoutCard);
		}

		// Test runs: the next named card on every record, one per loading screen (game thread).
		void Prepare()
		{
			std::scoped_lock lock(s_mutex);
			if (!s_active || s_testCards.empty()) return;
			const Packs::Pack* pack = nullptr;
			const Packs::Image* cover = nullptr;
			// Test runs name the cards to show, one per loading screen.
			const auto wanted = Settings::Lower(s_testCards[s_testCard++ % s_testCards.size()]);
			for (const auto& candidate : Packs::All()) {
				for (const auto& [category, images] : candidate.images) {
					for (const auto& image : images) {
						if (!cover && Settings::Lower(image.card) == wanted) {
							pack = &candidate;
							cover = &image;
						}
					}
				}
			}
			if (!cover) {
				// A vanilla card shows on every record with the framing and motion of the screen it belongs to.
				const auto owner = std::find_if(s_records.begin(), s_records.end(), [&](const Record& a_record) {
					const auto* own = Packs::VanillaScreenCard(a_record.key);
					return own && Settings::Lower(own->card) == wanted;
				});
				if (owner != s_records.end()) {
					SetCard(s_card, wanted, Packs::VanillaScreenCard(owner->key)->color);
					const Motion motion{ owner->originalRotation, owner->originalZoom };
					for (auto& record : s_records) Show(record, s_card, static_cast<RE::BGSTransform*>(owner->originalTransform), motion);
					logger::info("loading screens: next shows the vanilla card {} of {}", wanted, owner->key);
					return;
				}
				logger::warn("loading screens: test card '{}' is not in any pack", wanted);
				for (auto& record : s_records) RestoreRecord(record);
				return;
			}
			SetCard(s_card, cover->card, std::nullopt);
			for (auto& record : s_records) ShowCover(record, s_card);
			logger::info("loading screens: next shows {} from '{}'", cover->card, pack ? pack->name : "");
		}
	}

	void OnGameDataReady()
	{
		auto* data = RE::TESDataHandler::GetSingleton();
		if (!data) return;
		if (const auto* alr = data->LookupModByName("ALR.esp"); alr && alr->GetCompileIndex() != 0xFF) {
			logger::info("loading screens: ALR is active, so its loading screens are left to it");
			return;
		}
		s_card = data->LookupForm<RE::TESObjectSTAT>(0x802, "ArtMixer.esl");
		s_magazineTransform = data->LookupForm<RE::BGSTransform>(0x249731, "Fallout4.esm");
		if (!s_magazineTransform) logger::warn("loading screens: the game's magazine framing (Fallout4.esm 0x249731) is missing, so cover cards keep each screen's own framing");
		if (!s_card) {
			logger::warn("loading screens: the card form is missing from ArtMixer.esl");
			return;
		}
		std::scoped_lock lock(s_mutex);
		std::size_t skipped = 0;
		for (auto* form : data->formArrays[std::to_underlying(RE::ENUM_FORM_ID::kLSCR)]) {
			auto* screen = form ? form->As<RE::TESLoadScreen>() : nullptr;
			auto* block = screen ? reinterpret_cast<std::byte*>(screen->loadNIFData) : nullptr;
			if (!Readable(block, 0x50)) {
				++skipped;
				continue;
			}
			Record record;
			const auto* file = form->GetFile(0);
			const auto local = form->GetFormID() & ((form->GetFormID() >> 24) == 0xFE ? 0xFFFu : 0xFFFFFFu);
			record.key = fmt::format("{}:{:06X}", file ? Settings::Lower(file->GetFilename()) : std::string(), local);
			record.model = FindFormField(block, 0x10, [](RE::ENUM_FORM_ID a_type) { return a_type != RE::ENUM_FORM_ID::kTRNS && a_type != RE::ENUM_FORM_ID::kNONE; });
			record.transform = FindFormField(block, 0x50, [](RE::ENUM_FORM_ID a_type) { return a_type == RE::ENUM_FORM_ID::kTRNS; });
			if (!record.model) {
				++skipped;
				continue;
			}
			record.rotation = reinterpret_cast<std::int16_t*>(block + 0x8);
			record.zoom = reinterpret_cast<float*>(block + 0x48);
			record.originalModel = *record.model;
			record.originalTransform = record.transform ? *record.transform : nullptr;
			std::copy(record.rotation, record.rotation + 2, record.originalRotation.begin());
			std::copy(record.zoom, record.zoom + 2, record.originalZoom.begin());
			s_records.push_back(record);
		}
		// The rotation and zoom ranges sit at fixed offsets; they are written only when every record holds a plausible range there.
		const auto plausible = std::all_of(s_records.begin(), s_records.end(), [](const Record& r) {
			const auto& [rotationMin, rotationMax] = r.originalRotation;
			const auto& [zoomMin, zoomMax] = r.originalZoom;
			return rotationMin >= -360 && rotationMax <= 360 && rotationMin <= rotationMax &&
			       std::isfinite(zoomMin) && std::isfinite(zoomMax) && zoomMin >= -100.0F && zoomMax <= 100.0F && zoomMin <= zoomMax;
		});
		if (!plausible) {
			for (auto& record : s_records) {
				record.rotation = nullptr;
				record.zoom = nullptr;
			}
			logger::warn("loading screens: the rotation and zoom ranges are not where they were measured, so cards keep each screen's own motion");
		}
		for (std::uint32_t i = 0; i < 512; ++i) {
			auto* card = data->LookupForm<RE::TESObjectSTAT>(0x900 + i, "ArtMixer.esl");
			if (!card) break;
			s_cards.push_back(card);
		}
		if (s_cards.empty()) logger::warn("loading screens: the card statics are missing from ArtMixer.esl");
		s_cardColor = static_cast<RE::BGSModelMaterialSwap*>(s_card)->colorRemappingIndex;
		s_active = !s_records.empty();
		logger::info("loading screens: {} records ({} with a framing), {} skipped", s_records.size(), std::count_if(s_records.begin(), s_records.end(), [](const Record& r) { return r.transform != nullptr; }), skipped);
		if (s_active) F4SE::GetTaskInterface()->AddTask([] { s_testCards.empty() ? Assign() : Prepare(); });
	}

	void Reassign()
	{
		if (s_active) Assign();
	}

	void SetTestCards(std::vector<std::string> a_cards)
	{
		std::scoped_lock lock(s_mutex);
		s_testCards = std::move(a_cards);
	}

	void OnLoadingMenuClosed()
	{
		if (s_active && !s_testCards.empty()) F4SE::GetTaskInterface()->AddTask([] { Prepare(); });
	}
}
