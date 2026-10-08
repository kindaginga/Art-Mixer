#include "Distributor.h"

#include "ApplyNow.h"
#include "AutoTest.h"

#include "Catalog.h"
#include "Engine.h"
#include "Inventory.h"
#include "LoadScreens.h"
#include "Packs.h"
#include "Settings.h"

#include <limits>
#include <unordered_set>

namespace ADT::Distributor
{
	namespace
	{
		struct Entry
		{
			RE::NiPointer<RE::NiAVObject> geometry;
			std::string originalName;   // shader property name as loaded from the NIF
			std::string currentMaterial;  // material file currently applied, empty when the original is in place
			const Catalog::Slot* slot{ nullptr };
			RE::NiPoint3 anchor;  // world-space bound center, identical on the template and its scene clones
		};

		std::mutex s_mutex;
		std::vector<Entry> s_entries;
		std::size_t s_pruneAt{ 256 };

		// Drops entries whose template the model database has released (we hold the last reference); caller holds the lock.
		void Prune()
		{
			std::erase_if(s_entries, [](const Entry& a_entry) { return !a_entry.geometry || a_entry.geometry->refCount <= 1; });
		}
		std::atomic<std::size_t> s_modelsSeen{ 0 };
		std::atomic<std::size_t> s_swapped{ 0 };
		std::atomic<std::size_t> s_referencesSeen{ 0 };
		std::atomic<std::size_t> s_referenceSwaps{ 0 };
		std::atomic<int> s_referenceLogs{ 0 };
		std::atomic<int> s_inlineLogs{ 0 };
		std::atomic<int> s_nameLogs{ 0 };
		std::atomic<int> s_backLogs{ 0 };
		std::atomic<int> s_pipboyLogs{ 0 };
		std::atomic<int> s_pipboyGeometryLogs{ 0 };
		std::atomic<bool> s_reapplyQueued{ false };
		std::atomic<std::size_t> s_set3DCalls{ 0 };
		std::atomic<std::size_t> s_packArrivals{ 0 };

		[[nodiscard]] bool IsPrecombined(std::string_view a_model)
		{
			return Settings::Lower(a_model).starts_with("meshes\\precombined\\");
		}

		[[nodiscard]] std::uint64_t Hash(std::string_view a_text)
		{
			std::uint64_t hash = 14695981039346656037ULL;
			for (const unsigned char c : a_text) {
				hash ^= c;
				hash *= 1099511628211ULL;
			}
			hash ^= hash >> 30;
			hash *= 0xBF58476D1CE4E5B9ULL;
			hash ^= hash >> 27;
			hash *= 0x94D049BB133111EBULL;
			hash ^= hash >> 31;
			return hash;
		}

		// The catalog key of a geometry: its material file name, or "@" and its key texture for a shader the model carries inline.
		[[nodiscard]] std::string SlotName(RE::NiAVObject* a_geometry)
		{
			auto name = Engine::ShaderPropertyName(a_geometry);
			return name.empty() ? Engine::InlineKey(a_geometry) : name;
		}

		// The material a geometry shows, the slot of that material and the slot of the swap entry that shows it ("=swap|material").
		struct GeometrySlots
		{
			std::string material;
			const Catalog::Slot* shown{ nullptr };
			const Catalog::Slot* scoped{ nullptr };
		};

		[[nodiscard]] GeometrySlots SlotsOf(const std::string& a_name, const SwapMap& a_swaps)
		{
			const auto normalized = a_name.starts_with('@') ? std::string() : Catalog::NormalizeMaterialPath(a_name);
			const auto it = normalized.empty() ? a_swaps.end() : a_swaps.find(normalized);
			if (it == a_swaps.end()) return { a_name, Catalog::Find(a_name), nullptr };
			// The entry's own slot applies only while the entry shows the game's or a converted mod's material.
			const auto owned = Packs::OwnsSwapEntry(it->second.form, normalized, it->second.material);
			return { it->second.material, Catalog::Find(it->second.material), owned ? Catalog::Find("=" + it->second.form + "|" + normalized) : nullptr };
		}

		std::atomic<int> s_swapLogs{ 0 };

		// The pack's image made for exactly this slot; several variants (a replacer's clean and damaged sets) are picked among by hash.
		[[nodiscard]] const Packs::Image* ExactImage(const Packs::Pack& a_pack, const Catalog::Slot& a_slot, std::uint64_t a_hash)
		{
			const auto it = a_pack.images.find(a_slot.category);
			if (it == a_pack.images.end()) return nullptr;
			std::vector<const Packs::Image*> matching;
			for (const auto& image : it->second) {
				if (image.slot == a_slot.material) matching.push_back(&image);
			}
			return matching.empty() ? nullptr : matching[static_cast<std::size_t>(a_hash % matching.size())];
		}

		// A pack's versions of a marked slot ("#" model, "%" texture, "!" texture set entry and the rest, lowercase) in a category.
		[[nodiscard]] std::vector<const Packs::Image*> PathVersions(const Packs::Pack* a_pack, const std::string& a_category, char a_mark, std::string_view a_path)
		{
			std::vector<const Packs::Image*> matching;
			if (!a_pack) return matching;
			const auto images = a_pack->images.find(a_category);
			if (images == a_pack->images.end()) return matching;
			for (const auto& image : images->second) {
				if (image.slot.size() == a_path.size() + 1 && image.slot[0] == a_mark && image.slot.compare(1, std::string::npos, a_path) == 0) matching.push_back(&image);
			}
			return matching;
		}

		// The rolled pack's versions of a marked slot, or the vanilla pack's when the rolled pack has none.
		[[nodiscard]] std::vector<const Packs::Image*> PathVersionsOrVanilla(const Packs::Pack* a_chosen, const std::string& a_category, char a_mark, std::string_view a_path)
		{
			auto matching = PathVersions(a_chosen, a_category, a_mark, a_path);
			if (matching.empty() && a_chosen && !a_chosen->vanilla) matching = PathVersions(Packs::Vanilla(), a_category, a_mark, a_path);
			return matching;
		}

		// A page's or list's settings, or null when none are stored.
		[[nodiscard]] const Settings::CategorySettings* SettingsOf(const Settings::Values& a_values, const std::string& a_category)
		{
			const auto it = a_values.categories.find(a_category);
			return it == a_values.categories.end() ? nullptr : &it->second;
		}

		// The share a pack's one control gives its objects: the slider or switch on the pack's main page; 0 for a pack past its page's slots.
		[[nodiscard]] int ShareOf(const Packs::Pack& a_pack, const Settings::Values& a_values)
		{
			const auto& main = a_pack.page;
			if (main.empty()) return 0;
			// The pack's slot index in its main page's list; a pack the list leaves out has no share.
			const auto& slots = Packs::SlotsFor(main);
			const auto found = std::find(slots.begin(), slots.end(), &a_pack);
			if (found == slots.end()) return 0;
			const auto j = static_cast<std::size_t>(found - slots.begin());
			const auto* page = SettingsOf(a_values, main);
			return page && j < page->percent.size() ? page->percent[j] : 0;
		}

		// The pack a roll lands on among the packs that have a version of the object (every pack without a test), each by its share;
		// the vanilla pack in the remainder. Shares of such packs above 100 in total are scaled down to 100. Loading screens and the
		// Pip-Boy lists keep a share per listed pack; everywhere else a pack's one slider sets all of its objects.
		[[nodiscard]] const Packs::Pack* RollPack(const std::string& a_category, std::uint64_t a_hash, const Settings::Values& a_values, const Covers& a_covers = {})
		{
			const bool listed = a_category == "loadingscreens" || a_category.starts_with("pipboy");
			const auto packs = listed ? Packs::SlotsFor(a_category) : Packs::PacksWith(a_category);
			const auto* settings = listed ? SettingsOf(a_values, a_category) : nullptr;
			std::vector<std::pair<const Packs::Pack*, int>> candidates;
			int total = 0;
			for (std::size_t i = 0; i < packs.size(); ++i) {
				const int share = listed ? (settings && i < settings->percent.size() ? settings->percent[i] : 0) : ShareOf(*packs[i], a_values);
				if (share <= 0 || (a_covers && !a_covers(*packs[i]))) continue;
				candidates.emplace_back(packs[i], share);
				total += share;
			}
			const auto roll = static_cast<int>(a_hash % static_cast<std::uint64_t>(std::max(total, 100)));
			int cumulative = 0;
			for (const auto& [pack, share] : candidates) {
				cumulative += share;
				if (roll < cumulative) return pack;
			}
			return Packs::Vanilla();
		}

		// True when a pack has a version of this very slot.
		[[nodiscard]] bool HasVersion(const Packs::Pack& a_pack, const Catalog::Slot& a_slot)
		{
			const auto it = a_pack.images.find(a_slot.category);
			return it != a_pack.images.end() && std::any_of(it->second.begin(), it->second.end(), [&](const Packs::Image& a_image) { return a_image.slot == a_slot.material; });
		}

		struct ObjectChoice
		{
			const Packs::Image* object{ nullptr };         // the rolled pack's version of this very object (a plugin gave it a model or a name)
			const Packs::Image* file{ nullptr };           // the rolled pack's model for the object's model file ("#")
			const Packs::Image* vanillaObject{ nullptr };  // the game's own model, swap, name and bounds of the object (vanilla pack)
			const Packs::Image* vanillaFile{ nullptr };    // the vanilla pack's copy of the object's model file
		};

		// One roll per reference picks the pack for everything that replaces the object, so its model and its name agree.
		[[nodiscard]] ObjectChoice ChooseObject(std::uint32_t a_refFormID, const Catalog::Slot* a_objectSlot, const Catalog::Slot* a_fileSlot)
		{
			if (!a_objectSlot && !a_fileSlot) return {};
			const auto values = Settings::Get();
			const auto key = fmt::format("ref:{}|model|{}", Catalog::ReferenceKey(a_refFormID), values.seed);
			const auto* chosen = RollPack(a_objectSlot ? a_objectSlot->category : a_fileSlot->category, Hash(key), values, [&](const Packs::Pack& a_pack) {
				return (a_objectSlot && HasVersion(a_pack, *a_objectSlot)) || (a_fileSlot && HasVersion(a_pack, *a_fileSlot));
			});
			if (!chosen) return {};
			const auto imageHash = Hash(key + "|image");
			ObjectChoice choice;
			if (!chosen->vanilla) {
				choice.object = a_objectSlot ? ExactImage(*chosen, *a_objectSlot, imageHash) : nullptr;
				choice.file = a_fileSlot ? ExactImage(*chosen, *a_fileSlot, imageHash) : nullptr;
			}
			// Each field the rolled version leaves out comes from the vanilla pack's version.
			if (const auto* vanilla = Packs::Vanilla(); vanilla) {
				choice.vanillaObject = a_objectSlot ? ExactImage(*vanilla, *a_objectSlot, imageHash) : nullptr;
				choice.vanillaFile = a_fileSlot ? ExactImage(*vanilla, *a_fileSlot, imageHash) : nullptr;
			}
			return choice;
		}

		// True when some pack names this object so; such a name on a reference is one this plugin set.
		// Returns a_name in the place of the game's name a_game inside the object's own name a_own, keeping another mod's tags around it; a_name alone when a_own does not hold a_game.
		[[nodiscard]] std::string Tagged(std::string_view a_own, std::string_view a_game, std::string_view a_name)
		{
			const auto at = a_game.empty() ? std::string_view::npos : a_own.find(a_game);
			if (at == std::string_view::npos) return std::string(a_name);
			return std::string(a_own.substr(0, at)).append(a_name).append(a_own.substr(at + a_game.size()));
		}

		[[nodiscard]] bool IsPackName(const Catalog::Slot& a_slot, std::string_view a_name, std::string_view a_own = {}, std::string_view a_game = {})
		{
			for (const auto& pack : Packs::All()) {
				const auto it = pack.images.find(a_slot.category);
				if (it == pack.images.end()) continue;
				for (const auto& image : it->second) {
					if (image.slot != a_slot.material || image.name.empty()) continue;
					if (image.name == a_name || Tagged(a_own, a_game, image.name) == a_name) return true;
				}
			}
			return false;
		}

		// Returns the material file to apply for a slot under the given identity, or empty to leave the vanilla material in place.
		// a_shown receives the pack whose version is returned (the vanilla pack for the game's own look), or null when nothing is returned.
		[[nodiscard]] std::string ChooseFor(const Catalog::Slot& slot, const std::string& a_identity, const Settings::Values& a_values, const Catalog::Slot* a_scoped = nullptr, const Packs::Pack** a_shown = nullptr)
		{
			const auto key = fmt::format("{}|{}|{}", a_identity, slot.material, a_values.seed);
			const auto imageHash = Hash(key + "|image");
			const auto versionOf = [&](const Packs::Pack& a_pack) {
				// A version for the swap entry that shows this material comes first.
				const Packs::Image* found = a_scoped ? ExactImage(a_pack, *a_scoped, imageHash) : nullptr;
				if (!found && &slot != a_scoped) found = ExactImage(a_pack, slot, imageHash);
				return found;
			};
			const auto* chosen = RollPack(slot.category, Hash(key), a_values, [&](const Packs::Pack& a_pack) { return versionOf(a_pack) != nullptr; });
			const Packs::Image* image = chosen && !chosen->vanilla ? versionOf(*chosen) : nullptr;
			const Packs::Pack* shown = image ? chosen : nullptr;
			// A vanilla roll, or a pack with nothing for the frame, shows the vanilla pack's copy of the frame's own picture.
			if (const auto* vanilla = Packs::Vanilla(); !image && vanilla) {
				if (a_scoped) image = ExactImage(*vanilla, *a_scoped, imageHash);
				if (!image) image = ExactImage(*vanilla, slot, imageHash);
				if (image) shown = vanilla;
			}
			if (a_shown) *a_shown = shown;
			return image ? "Materials\\" + image->material : std::string();
		}

		[[nodiscard]] std::string Choose(const Entry& a_entry, const Settings::Values& a_values)
		{
			return ChooseFor(*a_entry.slot, fmt::format("{:.0f}|{:.0f}|{:.0f}", a_entry.anchor.x, a_entry.anchor.y, a_entry.anchor.z), a_values);
		}

		// Applies the chosen material when it differs from what the geometry currently shows.
		void Apply(Entry& a_entry, const Settings::Values& a_values)
		{
			// A magazine back follows its own magazine's cover, which only the reference path knows.
			if (a_entry.slot && a_entry.slot->category == "magazinebacks") return;
			const auto wanted = Choose(a_entry, a_values);
			if (wanted == a_entry.currentMaterial) return;
			const auto target = wanted.empty() ? a_entry.originalName : wanted;
			if (Engine::ApplyMaterial(a_entry.geometry.get(), target)) {
				a_entry.currentMaterial = wanted;
				++s_swapped;
			}
		}
	}

	void OnModelLoaded(const char* a_modelPath, RE::NiAVObject* a_root)
	{
		if (!Engine::Available() || Catalog::Count() == 0) return;
		++s_modelsSeen;
		// Precombined meshes and the Pip-Boy (the player's 3D never passes the reference hook) are handled on the template;
		// everything else gets a pick per reference in OnReference3D.
		const auto modelLower = Settings::Lower(a_modelPath ? a_modelPath : "");
		const bool pipboy = modelLower.find("pipboy") != std::string::npos;
		if (pipboy && s_pipboyLogs.fetch_add(1) < 20) logger::info("distributor: pip-boy model loaded: {}", a_modelPath ? a_modelPath : "");
		if (!IsPrecombined(modelLower) && !pipboy) return;
		const auto values = Settings::Get();
		std::vector<Entry> found;
		Engine::ForEachGeometry(a_root, [&](RE::NiAVObject* a_geometry, int a_index) {
			const auto name = SlotName(a_geometry);
			if (name.empty()) return;
			const auto* slot = Catalog::Find(name);
			if (!slot) return;
			// An inline shader can only be tracked for later changes when its vanilla look exists as a material file.
			if (name.starts_with('@') && slot->restore.empty()) return;
			Entry entry;
			entry.geometry.reset(a_geometry);
			if (pipboy && s_pipboyGeometryLogs.fetch_add(1) < 20) { logger::info("distributor: pip-boy geometry #{} in {}: {} ({})", a_index, a_modelPath, name, slot->category); }
			entry.originalName = slot->restore.empty() ? name : slot->restore;
			entry.slot = slot;
			entry.anchor = a_geometry->worldBound.center;
			found.push_back(std::move(entry));
		});
		if (found.empty()) return;
		for (auto& entry : found) Apply(entry, values);
		if (pipboy) {
			// One line per Pip-Boy screen part, so the log shows what a pick put on it.
			for (const auto& entry : found) {
				if (entry.slot->category == "pipboyscreen") logger::info("distributor: pip-boy screen {} in {} shows {}", entry.slot->material, a_modelPath ? a_modelPath : "", entry.currentMaterial.empty() ? "its own material" : entry.currentMaterial);
			}
		}
		std::scoped_lock lock(s_mutex);
		for (auto& entry : found) s_entries.push_back(std::move(entry));
		if (s_entries.size() >= s_pruneAt) {
			Prune();
			s_pruneAt = s_entries.size() + 256;
		}
	}

	namespace
	{
		// References whose model the game loads with their picks in place; the pass after their 3D arrives leaves their art alone.
		std::mutex s_loadedLock;
		std::unordered_set<std::uint32_t> s_loadedWithPicks;
		std::atomic<int> s_refSwapLogs{ 0 };
		// Material swaps made for sets of picks, by their entries, shared by every reference with the same set.
		std::mutex s_swapLock;
		std::unordered_map<std::string, RE::BGSMaterialSwap*> s_madeSwaps;
		std::atomic<int> s_madeSwapLogs{ 0 };
		// The color remap index the game's own swap entries carry for none.
		constexpr float kNoColorRemap = std::numeric_limits<float>::max();

		using SwapEntries = std::map<std::string, std::pair<std::string, float>>;  // material -> the material shown and its color remap

		// The material swap form with these entries (relative to Materials), made once per set of entries.
		[[nodiscard]] RE::BGSMaterialSwap* SwapWith(const SwapEntries& a_entries)
		{
			std::string key;
			for (const auto& [from, to] : a_entries) key += fmt::format("{}>{}>{}|", from, to.first, to.second);
			std::scoped_lock lock(s_swapLock);
			if (const auto it = s_madeSwaps.find(key); it != s_madeSwaps.end()) return it->second;
			auto* factory = RE::ConcreteFormFactory<RE::BGSMaterialSwap>::GetFormFactory();
			auto* swap = factory ? factory->Create() : nullptr;
			if (swap) {
				for (const auto& [from, to] : a_entries) swap->swapMap.insert({ RE::BSFixedString(from), RE::BGSMaterialSwap::Entry{ RE::BSFixedString(to.first), to.second } });
			} else {
				logger::error("distributor: no material swap could be made; objects keep their art until their 3D arrives");
			}
			if (swap && s_madeSwapLogs.fetch_add(1) < 20) logger::info("distributor: material swap {} made with {} entries", fmt::ptr(swap), a_entries.size());
			s_madeSwaps.emplace(std::move(key), swap);
			return swap;
		}

		// The swap that gives a model its picks for one identity: the entries of the object's own swaps (a later swap's entry
		// replaces an earlier one's), plus each replaced material and named inline shader whose pick shows another material mapped
		// to it (a magazine's back to its cover pack's back), or null when no pick changes anything.
		[[nodiscard]] RE::BGSMaterialSwap* PickSwap(const std::string& a_identity, const Packs::ModelUse& a_use, std::initializer_list<const RE::BGSMaterialSwap*> a_owns)
		{
			const auto values = Settings::Get();
			SwapEntries entries;
			SwapMap swaps;
			for (const auto* own : a_owns) {
				if (!own) continue;
				const auto form = Catalog::FormText(own);
				for (const auto& [from, entry] : own->swapMap) {
					const auto material = Catalog::NormalizeMaterialPath(from.c_str());
					entries[material] = { entry.swapMaterial.c_str(), entry.colorRemappingIndex };
					swaps[material] = { Catalog::NormalizeMaterialPath(entry.swapMaterial.c_str()), form };
				}
			}
			bool changed = false;
			const Packs::Pack* coverPack = nullptr;
			std::vector<std::pair<std::string, const Catalog::Slot*>> backs;
			const auto pick = [&](const std::string& a_from, const std::string& a_name) {
				const auto slots = SlotsOf(a_name, swaps);
				const auto* slot = slots.shown ? slots.shown : slots.scoped;
				if (!slot) return;
				if (slot->category == "magazinebacks") {
					backs.emplace_back(a_from, slot);
					return;
				}
				const Packs::Pack* shown = nullptr;
				const auto wanted = ChooseFor(*slot, a_identity, values, slots.scoped, &shown);
				if (slot->category == "magazines" && shown) coverPack = shown;
				if (wanted.empty()) return;
				const auto target = Catalog::NormalizeMaterialPath(wanted);
				if (target == a_from) return;
				const auto own = entries.find(a_from);
				entries[a_from] = { target, own != entries.end() ? own->second.second : kNoColorRemap };
				changed = true;
			};
			for (const auto& material : a_use.materials) pick(material, material);
			for (const auto& [key, material] : a_use.inlineShaders) pick(material, "@" + key);
			// Materials the object's own swaps replace pick by the material those swaps show.
			for (const auto& [material, entry] : entries) {
				if (std::find(a_use.materials.begin(), a_use.materials.end(), material) == a_use.materials.end()) pick(material, material);
			}
			for (const auto& [from, slot] : backs) {
				const auto* image = coverPack && !coverPack->vanilla ? ExactImage(*coverPack, *slot, Hash(fmt::format("{}|{}|{}", a_identity, slot->material, values.seed))) : nullptr;
				if (!image) continue;
				entries[from] = { Catalog::NormalizeMaterialPath(image->material), kNoColorRemap };
				changed = true;
			}
			return changed ? SwapWith(entries) : nullptr;
		}

		[[nodiscard]] bool LoadedWithPicks(std::uint32_t a_refFormID)
		{
			std::scoped_lock lock(s_loadedLock);
			return s_loadedWithPicks.contains(a_refFormID);
		}
	}

	Engine::ModelChoice ChooseModel(std::uint32_t a_refFormID, std::uint32_t a_baseFormID, const char* a_modelPath, bool a_inWorld)
	{
		if (!a_modelPath || Catalog::Count() == 0 || Packs::All().empty()) return {};
		const auto path = Settings::RelativePath(a_modelPath, "meshes");
		if (auto* probe = AutoTest::SwapProbe(a_baseFormID); probe) return { path, probe };
		const auto* objectSlot = Catalog::FindForm(a_baseFormID);
		const auto* vanilla = Packs::Vanilla();
		const auto* game = objectSlot && vanilla ? ExactImage(*vanilla, *objectSlot, 0) : nullptr;
		// The model file slot is the object's game model when a converted mod gives the object a model, the model it loads now otherwise.
		const auto& file = game && !game->material.empty() && !objectSlot->model.empty() ? objectSlot->model : path;
		const auto identityForm = Inventory::IdentityOf(a_refFormID, a_baseFormID, a_inWorld);
		const auto choice = ChooseObject(identityForm, objectSlot, Catalog::Find("#" + file));
		// The first version with a model: the rolled pack's for the object, for its model file, then the vanilla pack's.
		const Packs::Image* image = nullptr;
		for (const auto* candidate : { choice.object, choice.file, choice.vanillaObject, choice.vanillaFile }) {
			if (candidate && !candidate->material.empty()) {
				image = candidate;
				break;
			}
		}
		// A version that keeps the object's swap loads with the game's own swap.
		std::optional<RE::BGSMaterialSwap*> swap;
		if (image) {
			swap = image->swap;
			if (!swap && choice.vanillaObject) swap = choice.vanillaObject->swap;
		}
		// A pack's own model shows that pack's art as it is.
		const bool vanillaModel = image == choice.vanillaObject || image == choice.vanillaFile;
		if (image && !vanillaModel) return { image->material, swap };
		// The game's model loads with the object's picks as a material swap, from its copy with named inline shaders when it has one.
		const RE::BGSMaterialSwap* own = nullptr;
		if (swap) {
			own = *swap;
		} else if (auto* base = RE::TESForm::GetFormByID(a_baseFormID); base) {
			auto* model = RE::fallout_cast<RE::TESModel*>(base);
			const auto* component = model ? model->GetAsModelMaterialSwap() : nullptr;
			own = component ? component->swapForm : nullptr;
		}
		// The reference's own material swap, whose entries replace the object's.
		const auto* ref = RE::TESForm::GetFormByID<RE::TESObjectREFR>(a_refFormID);
		const auto* extra = ref && ref->extraList ? ref->extraList->GetByType<RE::ExtraMaterialSwap>() : nullptr;
		const RE::BGSMaterialSwap* refOwn = extra ? extra->swap : nullptr;
		// A model packs.json does not list still gets its picks when a swap of the object or the reference shows replaced art.
		static const Packs::ModelUse s_none;
		const auto* use = Packs::ModelFor(image ? file : path);
		if (!use && ((own && !own->swapMap.empty()) || (refOwn && !refOwn->swapMap.empty()))) use = &s_none;
		// Without the swap call hook a reference with a swap of its own keeps the pass after its 3D arrives, which reads that swap.
		if (!use || (refOwn && !Engine::ReferenceSwapsFold())) return image ? Engine::ModelChoice{ image->material, swap } : Engine::ModelChoice{};
		auto* made = PickSwap("ref:" + Catalog::ReferenceKey(identityForm), *use, { own, refOwn });
		if (made && refOwn) Engine::NoteFoldedSwap(made, refOwn);
		if (refOwn && made && s_refSwapLogs.fetch_add(1) < 40) {
			std::string entries;
			for (const auto& [from, entry] : made->swapMap) entries += fmt::format("; {} -> {}", from.c_str(), entry.swapMaterial.c_str());
			logger::info("reference {:08X}: its own material swap {} joins the load-time picks, swap {:08X}{}", a_refFormID, Catalog::FormText(refOwn), made->GetFormID(), entries);
		}
		{
			std::scoped_lock lock(s_loadedLock);
			s_loadedWithPicks.insert(a_refFormID);
		}
		const auto& model = !use->copy.empty() ? use->copy : image ? image->material : path;
		if (made) return { model, made };
		if (model == path) return {};
		return { model, swap };
	}

	void OnReference3D(RE::TESObjectREFR* a_ref, RE::NiAVObject* a_root)
	{
		++s_set3DCalls;
		AutoTest::LogArrival(a_ref, a_root);
		if (!Engine::Available() || Catalog::Count() == 0 || Packs::All().empty()) return;
		// An object a pack can rename gets its name (or loses a pack name) on the game thread, whatever its 3D carries.
		const bool inWorld = a_ref->GetParentCell() != nullptr;
		if (const auto* base = a_ref->GetObjectReference(); base && Catalog::FindForm(base->GetFormID())) {
			const auto refID = a_ref->GetFormID();
			const auto baseID = base->GetFormID();
			F4SE::GetTaskInterface()->AddTask([refID, baseID, inWorld] { ApplyName(refID, baseID, inWorld); });
		}
		// A reference whose model loaded with its picks needs no pass over its art.
		if (LoadedWithPicks(a_ref->GetFormID())) return;
		// The material swaps of the object and of the reference: game material -> the material shown in its place and the swap.
		SwapMap swaps;
		const auto addSwaps = [&](const RE::BGSMaterialSwap* a_swap) {
			const auto form = Catalog::FormText(a_swap);
			Engine::ForEachMaterialSubstitution(a_swap, [&](std::string_view a_from, std::string_view a_to) { swaps[Catalog::NormalizeMaterialPath(a_from)] = { Catalog::NormalizeMaterialPath(a_to), form }; });
		};
		if (auto* base = a_ref->GetObjectReference(); base) {
			// An object with pack versions loads the model its version names through the model hook, with that version's swap.
			auto* model = Catalog::FindForm(base->GetFormID()) ? Engine::EffectiveModel(a_ref) : RE::fallout_cast<RE::TESModel*>(base);
			if (auto* component = model ? model->GetAsModelMaterialSwap() : nullptr; component) addSwaps(component->swapForm);
		}
		if (a_ref->extraList) {
			if (const auto* extra = a_ref->extraList->GetByType<RE::ExtraMaterialSwap>(); extra) addSwaps(extra->swap);
		}
		// Only roots that carry catalogued art are worth a deferred pass; the check itself touches no engine state.
		bool hasArt = false;
		const bool tracing = Engine::ModelTracing();
		Engine::ForEachGeometry(a_root, [&](RE::NiAVObject* a_geometry, int a_index) {
			const auto name = SlotName(a_geometry);
			const auto slots = SlotsOf(name, swaps);
			const bool known = slots.shown || slots.scoped;
			if (tracing && a_index < 12) logger::info("distributor: 3D of {:08X} geometry #{} shows '{}'{}", a_ref->GetFormID(), a_index, name, known ? " (a slot)" : "");
			if (known) hasArt = true;
		});
		if (!hasArt) return;
		RE::NiPointer<RE::NiAVObject> root{ a_root };
		const auto* object = a_ref->GetObjectReference();
		const auto formID = Inventory::IdentityOf(a_ref->GetFormID(), object ? object->GetFormID() : 0, inWorld);
		if (Engine::ModelTracing()) logger::info("distributor: 3D of {:08X} shows the pick of {:08X}", a_ref->GetFormID(), formID);
		F4SE::GetTaskInterface()->AddTask([root, formID, swaps = std::move(swaps)] {
			const auto start = std::chrono::steady_clock::now();
			ApplyToReference(formID, root.get(), swaps);
			Engine::Perf::AddGameTask(start, std::chrono::steady_clock::now(), fmt::format("reference {:08X}", formID));
		});
	}

	std::uint64_t LayoutHash(std::string_view a_key)
	{
		return Hash(fmt::format("{}|{}", a_key, Settings::Get().seed));
	}

	const Packs::Pack* RollCategory(std::string_view a_category, std::string_view a_key, const Covers& a_covers)
	{
		const auto values = Settings::Get();
		return RollPack(Settings::NormalizeCategory(a_category), Hash(fmt::format("{}|{}", a_key, values.seed)), values, a_covers);
	}

	namespace
	{
		// A model component of a game object whose model a pack replaces in a pick category, with its vanilla state.
		struct PickModel
		{
			RE::TESModel* model{ nullptr };
			std::string slot;
			std::string category;
			std::string original;
			void* textures{ nullptr };
			void* materials{ nullptr };
			std::int8_t numTextures{ 0 };
			std::int8_t numTexturesSRGB{ 0 };
			std::int8_t numMaterials{ 0 };
		};

		std::vector<PickModel> s_pickModels;
		bool s_pickModelsBuilt{ false };

		// Categories chosen from a list (one pack for the whole game) rather than shared.
		[[nodiscard]] bool IsPickCategory(std::string_view a_category)
		{
			return a_category == "pipboyskin" || a_category == "pipboyscreen" || a_category == "pipboymap";
		}

		void BuildPickModels()
		{
			s_pickModelsBuilt = true;
			std::unordered_map<std::string, std::string> slots;
			for (const auto& pack : Packs::All()) {
				for (const auto& [category, images] : pack.images) {
					if (!IsPickCategory(category)) continue;
					for (const auto& image : images) {
						if (image.slot.starts_with('#')) slots.emplace(Settings::Lower(image.slot.substr(1)), category);
					}
				}
			}
			if (slots.empty()) return;
			auto* handler = RE::TESDataHandler::GetSingleton();
			if (!handler) return;
			const auto add = [&](RE::TESModel* a_model) {
				if (!a_model || !a_model->GetModel()) return;
				const auto it = slots.find(Settings::RelativePath(a_model->GetModel(), "meshes"));
				if (it == slots.end()) return;
				s_pickModels.push_back({ a_model, it->first, it->second, a_model->GetModel(), a_model->textures, a_model->materials, a_model->numTextures, a_model->numTexturesSRGB, a_model->numMaterials });
			};
			for (auto* addon : handler->GetFormArray<RE::TESObjectARMA>()) {
				if (!addon) continue;
				for (int i = 0; i < 2; ++i) {
					add(&addon->bipedModel[i]);
					add(&addon->bipedModelFacebones[i]);
					add(&addon->bipedModel1stPerson[i]);
				}
			}
			for (auto* armor : handler->GetFormArray<RE::TESObjectARMO>()) {
				if (!armor) continue;
				for (int i = 0; i < 2; ++i) add(&armor->worldModel[i]);
			}
			for (const auto type : { RE::ENUM_FORM_ID::kSTAT, RE::ENUM_FORM_ID::kMISC, RE::ENUM_FORM_ID::kACTI, RE::ENUM_FORM_ID::kFURN, RE::ENUM_FORM_ID::kANIO }) {
				for (auto* form : handler->formArrays[std::to_underlying(type)]) {
					add(RE::fallout_cast<RE::TESModel*>(form));
				}
			}
			logger::info("distributor: {} model components of game objects follow a pick ({} models)", s_pickModels.size(), slots.size());
		}
	}

	void ApplyPickModels()
	{
		if (!s_pickModelsBuilt) BuildPickModels();
		for (auto& entry : s_pickModels) {
			const auto matching = PathVersionsOrVanilla(RollCategory(entry.category, "pick-model|" + entry.slot), entry.category, '#', entry.slot);
			std::string wanted;
			// Several variants of one model (pooled installer options) are picked among by hash.
			if (!matching.empty()) wanted = matching[static_cast<std::size_t>(Hash(fmt::format("pick-model|{}|image|{}", entry.slot, Settings::Get().seed)) % matching.size())]->material;
			const auto& target = wanted.empty() ? entry.original : wanted;
			const char* current = entry.model->GetModel();
			if (current && Settings::Lower(current) == Settings::Lower(target)) continue;
			entry.model->SetModel(target.c_str());
			// The vanilla file's precomputed texture and material lists belong to it alone.
			entry.model->textures = wanted.empty() ? static_cast<decltype(entry.model->textures)>(entry.textures) : nullptr;
			entry.model->materials = wanted.empty() ? static_cast<decltype(entry.model->materials)>(entry.materials) : nullptr;
			entry.model->numTextures = wanted.empty() ? entry.numTextures : 0;
			entry.model->numTexturesSRGB = wanted.empty() ? entry.numTexturesSRGB : 0;
			entry.model->numMaterials = wanted.empty() ? entry.numMaterials : 0;
			logger::info("distributor: model {} now {}", entry.slot, target);
		}
	}

	std::string ChooseTexture(std::string_view a_relative)
	{
		// Texture (relative to Textures, lowercase) of every "%" slot a pack replaces -> its category.
		static const auto s_categories = [] {
			std::unordered_map<std::string, std::string> categories;
			for (const auto& pack : Packs::All()) {
				for (const auto& [category, images] : pack.images) {
					for (const auto& image : images) {
						if (image.slot.starts_with('%')) categories.emplace(Settings::Lower(image.slot.substr(1)), category);
					}
				}
			}
			return categories;
		}();
		if (s_categories.empty()) return {};
		const auto it = s_categories.find(std::string(a_relative));
		if (it == s_categories.end()) return {};
		const auto* chosen = RollCategory(it->second, "texture|" + it->first, [&](const Packs::Pack& a_pack) { return !PathVersions(&a_pack, it->second, '%', it->first).empty(); });
		const auto matching = PathVersionsOrVanilla(chosen, it->second, '%', it->first);
		if (matching.empty()) return {};
		// Several variants of one texture (a mod's installer options pooled into one pack) are picked among by hash.
		return matching[static_cast<std::size_t>(Hash(fmt::format("texture|{}|image|{}", it->first, Settings::Get().seed)) % matching.size())]->material;
	}

	void ApplyWorldMap()
	{
		// The world map texture (relative to Textures, lowercase) and the game's cells for it, from the vanilla pack.
		std::string texture;
		std::optional<std::array<std::int32_t, 6>> bounds;
		if (const auto* vanilla = Packs::Vanilla()) {
			if (const auto it = vanilla->images.find("pipboymap"); it != vanilla->images.end()) {
				for (const auto& image : it->second) {
					if (!image.slot.starts_with('%') || !image.mapBounds) continue;
					texture = Settings::Lower(image.slot.substr(1));
					bounds = image.mapBounds;
					break;
				}
			}
		}
		if (texture.empty()) return;
		auto* world = RE::TESForm::GetFormByID<RE::TESWorldSpace>(0x3C);
		if (!world) {
			logger::warn("distributor: the Commonwealth world space 0000003C is missing; the world map keeps its cells");
			return;
		}
		// The chosen pack's world map, with its own cells when its plugin gave the map new ones.
		const auto material = ChooseTexture(texture);
		std::string shown = "the game's";
		for (const auto& pack : Packs::All()) {
			if (pack.vanilla || material.empty()) continue;
			const auto it = pack.images.find("pipboymap");
			if (it == pack.images.end()) continue;
			for (const auto& image : it->second) {
				if (image.material != material) continue;
				shown = pack.name + "'s";
				if (image.mapBounds) bounds = image.mapBounds;
			}
		}
		auto& map = world->worldMapData;
		const auto& b = *bounds;
		map.usableWidth = static_cast<std::uint32_t>(b[0]);
		map.usableHeight = static_cast<std::uint32_t>(b[1]);
		map.nwCellX = static_cast<std::int16_t>(b[2]);
		map.nwCellY = static_cast<std::int16_t>(b[3]);
		map.seCellX = static_cast<std::int16_t>(b[4]);
		map.seCellY = static_cast<std::int16_t>(b[5]);
		logger::info("distributor: world map shows {} map, cells NW {},{} SE {},{}", shown, map.nwCellX, map.nwCellY, map.seCellX, map.seCellY);
	}

	void ApplyBounds()
	{
		std::size_t widened = 0;
		Catalog::ForEachObjectSlot([&](std::uint32_t a_formID, const Catalog::Slot& a_slot) {
			auto* object = RE::TESForm::GetFormByID<RE::TESBoundObject>(a_formID);
			if (!object) return;
			auto& box = object->boundData;
			const auto before = box;
			for (const auto& pack : Packs::All()) {
				const auto it = pack.images.find(a_slot.category);
				if (it == pack.images.end()) continue;
				for (const auto& image : it->second) {
					if (image.slot != a_slot.material || !image.bounds) continue;
					const auto& b = *image.bounds;
					box.boundMin.x = std::min(box.boundMin.x, b[0]);
					box.boundMin.y = std::min(box.boundMin.y, b[1]);
					box.boundMin.z = std::min(box.boundMin.z, b[2]);
					box.boundMax.x = std::max(box.boundMax.x, b[3]);
					box.boundMax.y = std::max(box.boundMax.y, b[4]);
					box.boundMax.z = std::max(box.boundMax.z, b[5]);
				}
			}
			if (std::memcmp(&before, &box, sizeof(box)) == 0) return;
			++widened;
			logger::info("distributor: bounds of {:08X} ({}, {}, {})..({}, {}, {}) now ({}, {}, {})..({}, {}, {})", a_formID, before.boundMin.x, before.boundMin.y, before.boundMin.z, before.boundMax.x, before.boundMax.y, before.boundMax.z,
				box.boundMin.x, box.boundMin.y, box.boundMin.z, box.boundMax.x, box.boundMax.y, box.boundMax.z);
		});
		logger::info("distributor: {} objects widened to enclose their pack versions", widened);
	}

	void ApplyName(std::uint32_t a_formID, std::uint32_t a_baseFormID, bool a_inWorld)
	{
		auto* ref = RE::TESForm::GetFormByID<RE::TESObjectREFR>(a_formID);
		const auto* slot = Catalog::FindForm(a_baseFormID);
		const auto* base = ref ? ref->GetObjectReference() : nullptr;
		if (!ref || !slot || !base || base->GetFormID() != a_baseFormID) return;
		const auto choice = ChooseObject(Inventory::IdentityOf(a_formID, a_baseFormID, a_inWorld), slot, nullptr);
		const std::string own{ RE::TESFullName::GetFullName(*base) };
		const std::string game = choice.vanillaObject ? choice.vanillaObject->name : std::string();
		// The rolled pack's name for the object inside the own name's tags, else the game's own name while the object shows a converted mod's name.
		auto wanted = choice.object && !choice.object->name.empty() ? Tagged(own, game, choice.object->name) : choice.vanillaObject && IsPackName(*slot, own) ? game : std::string();
		// A name equal to the object's own name is no change.
		if (wanted == own) wanted.clear();
		const bool changed = Engine::SetReferenceName(ref, wanted, [slot, &own, &game](std::string_view a_name) { return IsPackName(*slot, a_name, own, game); });
		if (changed && s_nameLogs.fetch_add(1) < 20) logger::info("reference {:08X} (base {:08X}): name {}", a_formID, a_baseFormID, wanted.empty() ? "back to the object's own" : "'" + wanted + "'");
	}

	void ApplyToReference(std::uint32_t a_formID, RE::NiAVObject* a_root, const SwapMap& a_swaps)
	{
		const auto values = Settings::Get();
		const auto identity = "ref:" + Catalog::ReferenceKey(a_formID);
		bool seen = false;
		// The pack the magazine's cover shows and the back geometries that follow it.
		const Packs::Pack* coverPack = nullptr;
		std::vector<std::pair<RE::NiAVObject*, const Catalog::Slot*>> backs;
		Engine::ForEachGeometry(a_root, [&](RE::NiAVObject* a_geometry, int) {
			const auto name = SlotName(a_geometry);
			if (name.empty()) return;
			const auto lowered = Settings::Lower(name);
			const auto slots = SlotsOf(name, a_swaps);
			if (slots.material != name && s_swapLogs.fetch_add(1) < 20) logger::info("reference {:08X}: its material swap shows {} in place of {}{}", a_formID, slots.material, name, slots.scoped ? " (a slot of that swap entry)" : "");
			const auto* slot = slots.shown ? slots.shown : slots.scoped;
			// A pack material ("materials\adt\packs\...") or a pack texture under an inline shader ("@adt\packs\...").
			const bool ours = lowered.find("adt\\packs\\") != std::string::npos;
			if (!slot && !ours) return;
			seen = true;
			if (ours) {
				++s_packArrivals;
				// The 3D arrived already showing a pack look (the game built it from a copy that has one, as for a picked-up item).
				if (s_referenceLogs.load() < 20) { ++s_referenceLogs; logger::info("reference {:08X}: geometry arrived showing {}", a_formID, name); }
				return;
			}
			if (slot->category == "magazinebacks") {
				backs.emplace_back(a_geometry, slot);
				return;
			}
			const Packs::Pack* shown = nullptr;
			const auto wanted = ChooseFor(*slot, identity, values, slots.scoped, &shown);
			if (slot->category == "magazines" && shown) coverPack = shown;
			if (wanted.empty()) return;
			if (Engine::ApplyMaterial(a_geometry, wanted)) {
				++s_referenceSwaps;
				if (s_referenceLogs.load() < 20) { ++s_referenceLogs; logger::info("reference {:08X}: {} -> {}", a_formID, name, wanted); }
				if (name.starts_with('@') && s_inlineLogs.fetch_add(1) < 20) logger::info("reference {:08X}: model shader {} -> {}", a_formID, name, wanted);
			}
		});
		// Each back shows the cover's pack's version of that back material, or stays the game's own.
		for (const auto& [geometry, slot] : backs) {
			const auto* image = coverPack && !coverPack->vanilla ? ExactImage(*coverPack, *slot, Hash(fmt::format("{}|{}|{}", identity, slot->material, values.seed))) : nullptr;
			if (!image) continue;
			const auto wanted = "Materials\\" + image->material;
			if (Engine::ApplyMaterial(geometry, wanted)) {
				++s_referenceSwaps;
				if (s_backLogs.fetch_add(1) < 20) logger::info("reference {:08X}: back {} follows its cover's pack '{}': {}", a_formID, slot->material, coverPack->name, wanted);
			}
		}
		if (seen) ++s_referencesSeen;
	}

	void ApplyTextureSets()
	{
		// Every texture set a pack changes: set form -> changed index -> category.
		std::map<std::string, std::map<int, std::string>> sets;
		for (const auto& pack : Packs::All()) {
			for (const auto& [category, images] : pack.images) {
				for (const auto& image : images) {
					if (!image.slot.starts_with('!')) continue;
					const auto bar = image.slot.find('|');
					if (bar != std::string::npos) sets[image.slot.substr(1, bar - 1)].emplace(std::atoi(image.slot.c_str() + bar + 1), category);
				}
			}
		}
		std::size_t entries = 0, written = 0, foreign = 0;
		for (const auto& [form, indexes] : sets) {
			auto* record = RE::TESForm::GetFormByID(Catalog::FormIDOf(form));
			auto* set = record && record->Is(RE::ENUM_FORM_ID::kTXST) ? static_cast<RE::BGSTextureSet*>(record) : nullptr;
			if (!set) continue;
			// One roll per set, so a pack's diffuse and normal map show together.
			const auto* chosen = RollCategory(indexes.begin()->second, "textureset|" + form, [&](const Packs::Pack& a_pack) {
				return std::any_of(indexes.begin(), indexes.end(), [&](const auto& a_index) { return !PathVersions(&a_pack, a_index.second, '!', fmt::format("{}|{}", form, a_index.first)).empty(); });
			});
			const auto variant = LayoutHash("textureset-image|" + form);
			for (const auto& [index, category] : indexes) {
				++entries;
				if (index < 0 || index >= static_cast<int>(std::size(set->textures))) continue;
				const auto entry = fmt::format("{}|{}", form, index);
				// Only a texture the game, a converted mod or a pack put there is replaced.
				const auto current = Engine::TexturePath(set->textures[index].textureName.c_str() ? set->textures[index].textureName.c_str() : "");
				std::string game;
				bool owned = false;
				for (const auto& pack : Packs::All()) {
					for (const auto* image : PathVersions(&pack, category, '!', entry)) owned = owned || Engine::TexturePath(image->material) == current;
				}
				for (const auto& restore : Packs::Restores()) {
					if (restore.kind != "textureSet" || restore.form != form || restore.index != index) continue;
					game = restore.game;
					owned = owned || Engine::TexturePath(restore.mod) == current || Engine::TexturePath(restore.game) == current;
				}
				if (!owned) {
					++foreign;
					continue;
				}
				const auto matching = PathVersionsOrVanilla(chosen, category, '!', entry);
				// Without a version (no vanilla pack) the set shows the game's own texture.
				const auto texture = matching.empty() ? game : matching[static_cast<std::size_t>(variant % matching.size())]->material;
				if (!texture.empty() && Engine::SetTextureSetTexture(set, static_cast<std::size_t>(index), texture)) {
					++written;
					logger::info("distributor: texture set {} texture {} is {} (was {})", form, index, Engine::TexturePath(texture), current);
				}
			}
		}
		if (entries > 0) logger::info("distributor: {} of {} texture set textures written for the current shares; {} hold a texture another mod put there", written, entries, foreign);
	}

	void Reapply()
	{
		s_reapplyQueued.store(false);
		ApplyTextureSets();
		ApplyWorldMap();
		if (!AutoTest::Skips("pickmodels")) ApplyPickModels();
		LoadScreens::Reassign();
		const auto values = Settings::Get();
		// Leaves the loaded objects as they are while a reload is under way.
		if (ApplyNow::Reloading()) {
			logger::info("distributor: reapplied the texture sets, world map, Pip-Boy models and loading screens; the reload under way gives the loaded objects their art");
			return;
		}
		std::vector<Entry> work;
		{
			std::scoped_lock lock(s_mutex);
			Prune();
			work = s_entries;
		}
		// Engine calls happen without the registry lock: the loader thread takes that lock after its own material loads.
		std::size_t changed = 0;
		for (auto& entry : work) {
			const auto before = entry.currentMaterial;
			Apply(entry, values);
			if (entry.currentMaterial != before) ++changed;
		}
		{
			std::scoped_lock lock(s_mutex);
			for (auto& entry : s_entries) {
				for (const auto& done : work) {
					if (done.geometry.get() == entry.geometry.get()) { entry.currentMaterial = done.currentMaterial; break; }
				}
			}
		}
		logger::info("distributor: reapplied with seed {}: {} tracked geometries, {} changed", values.seed, work.size(), changed);
	}

	void QueueReapply()
	{
		// Several setting changes in a row (the MCM script pushes 60 at start) collapse into one pass 300 ms later.
		if (s_reapplyQueued.exchange(true)) return;
		std::thread([] {
			std::this_thread::sleep_for(std::chrono::milliseconds(300));
			F4SE::GetTaskInterface()->AddTask([] {
				const auto start = std::chrono::steady_clock::now();
				Reapply();
				Engine::Perf::AddGameTask(start, std::chrono::steady_clock::now(), "reapply");
			});
		}).detach();
	}

	Stats GetStats()
	{
		std::scoped_lock lock(s_mutex);
		return Stats{ s_entries.size(), s_swapped.load(), s_modelsSeen.load(), s_referencesSeen.load(), s_referenceSwaps.load(), s_set3DCalls.load(), s_packArrivals.load() };
	}
}
