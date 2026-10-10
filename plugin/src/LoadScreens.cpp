#include "LoadScreens.h"

#include "Catalog.h"
#include "Distributor.h"
#include "MenuRotation.h"
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
			RE::TESForm* originalModel{ nullptr };  // the game's own model: the form found at game data ready, or with ALR.esp loaded the form packs.json names
			RE::TESForm* foundModel{ nullptr };     // the form found at game data ready, which is Automated Loading Screen Replacer's while it is loaded
			RE::TESForm* originalTransform{ nullptr };
			std::array<std::int16_t, 2> originalRotation{};
			std::array<float, 2> originalZoom{};
			RE::TESForm* foundTransform{ nullptr };  // the framing and motion found at game data ready, kept for the Leave box
			std::array<std::int16_t, 2> foundRotation{};
			std::array<float, 2> foundZoom{};
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
		bool s_alrLoaded{ false };               // ALR.esp (Automated Loading Screen Replacer) is in the load order
		bool s_dataReady{ false };               // the game data is loaded, so s_alrLoaded is known
		std::size_t s_load{ 0 };                 // index of the next loading screen, the roll's key
		std::atomic<bool> s_nextIsCover{ false };  // the next loading screen shows a pack cover, so it gets the game's own loading menu
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

		// True while ALR.esp is loaded and the Leave box on the Loading screens page is on: the loading screens are left to it.
		[[nodiscard]] bool LeaveToALR()
		{
			return s_alrLoaded && Settings::LoadingScreensFlag("bleavetoalr").value_or(0) != 0;
		}

		// Points a record back at what Automated Loading Screen Replacer left in it (caller holds the lock).
		void LeaveRecord(Record& a_record)
		{
			*a_record.model = a_record.foundModel;
			if (a_record.transform) *a_record.transform = a_record.foundTransform;
			if (a_record.rotation) std::copy(a_record.foundRotation.begin(), a_record.foundRotation.end(), a_record.rotation);
			if (a_record.zoom) std::copy(a_record.foundZoom.begin(), a_record.foundZoom.end(), a_record.zoom);
		}

		// Rolls the next loading screen under the load's index: the pack whose cover shows, or the screens' own look, by the Loading
		// screens shares and the layout seed; every record then shows that one outcome, so a share is a true percentage of loads, and
		// a load that is Art Mixer's gets the game's own loading menu (ruling 2026-10-09: no mod overrides the sliders). With the Leave
		// box on, every record stays as Automated Loading Screen Replacer set it (game thread).
		void Assign()
		{
			std::scoped_lock lock(s_mutex);
			if (!s_active || !s_testCards.empty() || s_cards.empty()) return;
			if (LeaveToALR()) {
				for (auto& record : s_records) LeaveRecord(record);
				s_nextIsCover.store(false);
				logger::info("loading screens: load {} left to Automated Loading Screen Replacer", s_load);
				return;
			}
			const auto key = fmt::format("loadscreen|{}", s_load);
			const auto* pack = Distributor::RollCategory("loadingscreens", key);
			std::vector<const Packs::Image*> covers;
			if (pack && !pack->vanilla) {
				for (const auto& [category, images] : pack->images) {
					for (const auto& image : images) {
						if (!image.card.empty()) covers.push_back(&image);
					}
				}
			}
			if (covers.empty()) {
				// The screens' own look: the vanilla card of a converted model where a pack ships that model's files, else the record untouched.
				std::size_t vanillaCards = 0;
				std::unordered_map<std::string, std::size_t> used;
				for (auto& record : s_records) {
					if (const auto* own = OwnCard(record); own) {
						const auto cardKey = fmt::format("{}|{}", Settings::Lower(own->card), own->color ? fmt::format("{}", *own->color) : std::string());
						auto it = used.find(cardKey);
						if (it == used.end() && used.size() < s_cards.size()) {
							it = used.emplace(cardKey, used.size()).first;
							SetCard(s_cards[it->second], own->card, own->color);
						}
						if (it != used.end()) {
							const Motion motion{ record.originalRotation, record.originalZoom };
							Show(record, s_cards[it->second], static_cast<RE::BGSTransform*>(record.originalTransform), motion);
							++vanillaCards;
							continue;
						}
					}
					RestoreRecord(record);
				}
				s_nextIsCover.store(false);
				logger::info("loading screens: load {} shows the screens' own look ({} with the vanilla card of their model)", s_load, vanillaCards);
				return;
			}
			const auto* cover = covers[static_cast<std::size_t>(Distributor::LayoutHash("loadscreen-image|" + key) % covers.size())];
			SetCard(s_cards[0], cover->card, std::nullopt);
			for (auto& record : s_records) ShowCover(record, s_cards[0]);
			s_nextIsCover.store(true);
			logger::info("loading screens: load {} shows {} from '{}'", s_load, cover->card, pack->name);
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
			s_nextIsCover.store(true);
			logger::info("loading screens: next shows {} from '{}'", cover->card, pack ? pack->name : "");
		}
	}

	void OnGameDataReady()
	{
		auto* data = RE::TESDataHandler::GetSingleton();
		if (!data) return;
		// Ruling 2026-10-09: no other mod overrides the sliders; with ALR.esp loaded the Leave box decides, off by default.
		const auto* alr = data->LookupModByName("ALR.esp");
		s_alrLoaded = alr && alr->GetCompileIndex() != 0xFF;
		s_dataReady = true;
		MenuRotation::RefreshMenuConfig();
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
			record.foundModel = *record.model;
			record.originalModel = record.foundModel;
			record.originalTransform = record.transform ? *record.transform : nullptr;
			std::copy(record.rotation, record.rotation + 2, record.originalRotation.begin());
			std::copy(record.zoom, record.zoom + 2, record.originalZoom.begin());
			record.foundTransform = record.originalTransform;
			record.foundRotation = record.originalRotation;
			record.foundZoom = record.originalZoom;
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
		if (s_alrLoaded) {
			// Automated Loading Screen Replacer rewrote the records before this ran, so each screen's own look comes from packs.json.
			std::size_t resolved = 0;
			for (auto& record : s_records) {
				const auto* look = Packs::GameScreen(record.key);
				auto* form = look ? RE::TESForm::GetFormByID(Catalog::FormIDOf(look->model)) : nullptr;
				if (!form) continue;
				record.originalModel = form;
				record.originalTransform = look->transform.empty() ? nullptr : RE::TESForm::GetFormByID(Catalog::FormIDOf(look->transform));
				if (look->rotation) record.originalRotation = *look->rotation;
				if (look->zoom) record.originalZoom = *look->zoom;
				++resolved;
			}
			logger::info("loading screens: ALR.esp is loaded; the Leave box is {}; {} of {} records point back at the game's own model", LeaveToALR() ? "on" : "off", resolved, s_records.size());
			if (resolved < s_records.size()) logger::warn("loading screens: {} records have no game model in packs.json (converted before 1.0.0's Leave box), so their own look is Automated Loading Screen Replacer's picture; convert again", s_records.size() - resolved);
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

	bool NextIsCover()
	{
		return s_nextIsCover.load();
	}

	bool ServesGameFiles()
	{
		return s_alrLoaded && !LeaveToALR();
	}

	std::vector<std::pair<std::string, int>> FlagValues(bool a_defaults)
	{
		return { { "bLeaveToALR:ArtMixer_LoadingScreens", !a_defaults && LeaveToALR() ? 1 : 0 } };
	}

	std::optional<bool> ALRLoaded()
	{
		return s_dataReady ? std::optional<bool>(s_alrLoaded) : std::nullopt;
	}

	void SetTestCards(std::vector<std::string> a_cards)
	{
		std::scoped_lock lock(s_mutex);
		s_testCards = std::move(a_cards);
	}

	void OnLoadingMenuClosed()
	{
		if (!s_active) return;
		F4SE::GetTaskInterface()->AddTask([] {
			if (!s_testCards.empty()) {
				Prepare();
				return;
			}
			{
				std::scoped_lock lock(s_mutex);
				++s_load;
			}
			Assign();
		});
	}
}
