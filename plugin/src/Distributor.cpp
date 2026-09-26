#include "Distributor.h"

#include "Catalog.h"
#include "Engine.h"
#include "Packs.h"
#include "Settings.h"

namespace GShuf::Distributor
{
	namespace
	{
		struct Entry
		{
			RE::NiPointer<RE::NiAVObject> geometry;
			std::string model;
			int index{ 0 };
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

		[[nodiscard]] bool SameAspect(int a_w1, int a_h1, int a_w2, int a_h2)
		{
			if (a_w1 <= 0 || a_h1 <= 0 || a_w2 <= 0 || a_h2 <= 0) return false;
			return static_cast<std::int64_t>(a_w1) * a_h2 == static_cast<std::int64_t>(a_w2) * a_h1;
		}

		[[nodiscard]] const Packs::Image* PickImage(const Packs::Pack& a_pack, const Catalog::Slot& a_slot, std::uint64_t a_hash)
		{
			const auto it = a_pack.images.find(a_slot.category);
			if (it == a_pack.images.end() || it->second.empty()) return nullptr;
			const auto& images = it->second;
			for (const auto& image : images) {
				if (!image.slot.empty() && image.slot == a_slot.material) return &image;
			}
			std::vector<const Packs::Image*> matching;
			for (const auto& image : images) {
				if (SameAspect(image.width, image.height, a_slot.width, a_slot.height)) matching.push_back(&image);
			}
			if (matching.empty()) {
				for (const auto& image : images) matching.push_back(&image);
			}
			return matching[static_cast<std::size_t>(a_hash % matching.size())];
		}

		// Returns the material file to apply for a slot under the given identity, or empty to leave the vanilla material in place.
		[[nodiscard]] std::string ChooseFor(const Catalog::Slot& slot, const std::string& a_identity, const Settings::Values& a_values)
		{
			if (slot.blank && !a_values.blankCanvases) return {};
			const auto key = fmt::format("{}|{}|{}", a_identity, slot.material, a_values.seed);
			const auto hash = Hash(key);
			const auto roll = static_cast<int>(hash % 100);
			const auto slots = Packs::SlotsFor(slot.category);
			const auto settings = a_values.categories.find(slot.category);
			const Packs::Pack* chosen = nullptr;
			if (settings != a_values.categories.end()) {
				int cumulative = 0;
				for (std::size_t i = 0; i < slots.size() && i < static_cast<std::size_t>(Settings::kSlotsPerCategory); ++i) {
					cumulative += settings->second.percent[i];
					if (roll < cumulative) {
						chosen = slots[i];
						break;
					}
				}
			}
			if (!chosen) chosen = Packs::Vanilla();
			if (!chosen) return {};
			const auto* image = PickImage(*chosen, slot, Hash(key + "|image"));
			if (!image) return {};
			if (chosen->vanilla && image->slot != slot.material) return {};
			return "Materials\\" + image->material;
		}

		[[nodiscard]] std::string Choose(const Entry& a_entry, const Settings::Values& a_values)
		{
			return ChooseFor(*a_entry.slot, fmt::format("{:.0f}|{:.0f}|{:.0f}", a_entry.anchor.x, a_entry.anchor.y, a_entry.anchor.z), a_values);
		}

		// Applies the chosen material when it differs from what the geometry currently shows.
		void Apply(Entry& a_entry, const Settings::Values& a_values)
		{
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
		// Only precombined meshes are handled on the template; everything else gets a pick per reference in OnReference3D.
		if (!IsPrecombined(a_modelPath ? a_modelPath : "")) return;
		const auto values = Settings::Get();
		std::vector<Entry> found;
		Engine::ForEachGeometry(a_root, [&](RE::NiAVObject* a_geometry, int a_index) {
			const auto name = Engine::ShaderPropertyName(a_geometry);
			if (name.empty()) return;
			const auto* slot = Catalog::Find(name);
			if (!slot) return;
			Entry entry;
			entry.geometry.reset(a_geometry);
			entry.model = a_modelPath;
			entry.index = a_index;
			entry.originalName = name;
			entry.slot = slot;
			entry.anchor = a_geometry->worldBound.center;
			found.push_back(std::move(entry));
		});
		if (found.empty()) return;
		for (auto& entry : found) Apply(entry, values);
		std::scoped_lock lock(s_mutex);
		for (auto& entry : found) s_entries.push_back(std::move(entry));
		if (s_entries.size() >= s_pruneAt) {
			Prune();
			s_pruneAt = s_entries.size() + 256;
		}
	}

	void OnReference3D(RE::TESObjectREFR* a_ref, RE::NiAVObject* a_root)
	{
		if (!Engine::Available() || Catalog::Count() == 0) return;
		const auto values = Settings::Get();
		const auto identity = fmt::format("ref:{:08X}", a_ref->GetFormID());
		bool seen = false;
		Engine::ForEachGeometry(a_root, [&](RE::NiAVObject* a_geometry, int) {
			const auto name = Engine::ShaderPropertyName(a_geometry);
			if (name.empty()) return;
			const auto lowered = Settings::Lower(name);
			const auto* slot = Catalog::Find(name);
			const bool ours = lowered.find("materials\\gshuf\\") != std::string::npos;
			if (!slot && !ours) return;
			seen = true;
			if (ours) {
				// The clone arrived already carrying a pack material: its shader property is shared with another clone.
				if (s_referenceLogs.load() < 20) { ++s_referenceLogs; logger::info("reference {:08X}: geometry already carries {} (shared property)", a_ref->GetFormID(), name); }
				return;
			}
			const auto wanted = ChooseFor(*slot, identity, values);
			if (wanted.empty()) return;
			if (Engine::ApplyMaterial(a_geometry, wanted)) {
				++s_referenceSwaps;
				if (s_referenceLogs.load() < 20) { ++s_referenceLogs; logger::info("reference {:08X}: {} -> {}", a_ref->GetFormID(), name, wanted); }
			}
		});
		if (seen) ++s_referencesSeen;
	}

	void Reapply()
	{
		const auto values = Settings::Get();
		std::scoped_lock lock(s_mutex);
		Prune();
		std::size_t changed = 0;
		for (auto& entry : s_entries) {
			const auto before = entry.currentMaterial;
			Apply(entry, values);
			if (entry.currentMaterial != before) ++changed;
		}
		logger::info("distributor: reapplied with seed {}: {} tracked geometries, {} changed", values.seed, s_entries.size(), changed);
	}

	void QueueReapply()
	{
		F4SE::GetTaskInterface()->AddTask([] { Reapply(); });
	}

	Stats GetStats()
	{
		std::scoped_lock lock(s_mutex);
		return Stats{ s_entries.size(), s_swapped.load(), s_modelsSeen.load(), s_referencesSeen.load(), s_referenceSwaps.load() };
	}
}
