#include "Catalog.h"

#include "Settings.h"

namespace ArtMixer::Catalog
{
	namespace
	{
		std::unordered_map<std::string, Slot> s_slots;
		// Published once by ResolveForms; the loader thread reads it through the atomic pointer.
		std::unique_ptr<const std::unordered_map<std::uint32_t, const Slot*>> s_formSlotsOwner;
		std::atomic<const std::unordered_map<std::uint32_t, const Slot*>*> s_formSlots{ nullptr };
		// Published once by ResolveForms: lowercase plugin file names by compile index (0 to 255), then light files by light index.
		constexpr std::size_t kLightNames{ 256 };
		std::unique_ptr<const std::vector<std::string>> s_fileNamesOwner;
		std::atomic<const std::vector<std::string>*> s_fileNames{ nullptr };

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
		return Settings::RelativePath(a_path, "materials");
	}

	bool Load()
	{
		s_slots.clear();
		const auto path = std::filesystem::path("Data") / "ArtMixer" / "vanilla-slots.json";
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
				if (const auto restore = Str(row, "restore"); !restore.empty()) slot.restore = "Materials\\" + restore;
				slot.model = Settings::Lower(Str(row, "model"));
				std::replace(slot.model.begin(), slot.model.end(), '/', '\\');
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

	void ResolveForms()
	{
		if (s_formSlots.load(std::memory_order_acquire)) return;
		auto* data = RE::TESDataHandler::GetSingleton();
		if (!data) {
			logger::error("catalog: no data handler; object slots stay unresolved");
			return;
		}
		auto names = std::make_unique<std::vector<std::string>>(kLightNames + 0x1000);
		for (const auto* file : data->files) {
			if (!file || !file->IsActive()) continue;
			if (!file->IsLight()) (*names)[file->GetCompileIndex()] = Settings::Lower(file->GetFilename());
			else if (file->GetSmallFileCompileIndex() < 0x1000) (*names)[kLightNames + file->GetSmallFileCompileIndex()] = Settings::Lower(file->GetFilename());
		}
		s_fileNamesOwner = std::move(names);
		s_fileNames.store(s_fileNamesOwner.get(), std::memory_order_release);
		auto map = std::make_unique<std::unordered_map<std::uint32_t, const Slot*>>();
		std::size_t total = 0;
		for (const auto& [key, slot] : s_slots) {
			if (!key.starts_with('&')) continue;
			++total;
			const auto formID = FormIDOf(std::string_view(key).substr(1));
			if (formID == 0) {
				logger::warn("catalog: object slot {} names a plugin that is not loaded", key);
				continue;
			}
			map->emplace(formID, &slot);
		}
		logger::info("catalog: {} of {} object slots resolved", map->size(), total);
		s_formSlotsOwner = std::move(map);
		s_formSlots.store(s_formSlotsOwner.get(), std::memory_order_release);
	}

	std::string FormText(const RE::TESForm* a_form)
	{
		const auto* file = a_form ? a_form->GetFile(0) : nullptr;
		if (!file) return {};
		const auto id = a_form->GetFormID();
		return fmt::format("{}:{:06x}", Settings::Lower(file->GetFilename()), id & ((id >> 24) == 0xFE ? 0xFFFu : 0xFFFFFFu));
	}

	std::string ReferenceKey(std::uint32_t a_formID)
	{
		const auto index = a_formID >> 24;
		const auto* names = s_fileNames.load(std::memory_order_acquire);
		// Fallout4.esm (always index 00) and references the game made (FF) keep their form id.
		if (index == 0x00 || index == 0xFF || !names) return fmt::format("{:08X}", a_formID);
		const bool light = index == 0xFE;
		const auto& name = (*names)[light ? kLightNames + ((a_formID >> 12) & 0xFFFu) : index];
		if (name.empty()) return fmt::format("{:08X}", a_formID);
		return fmt::format("{}:{:06x}", name, a_formID & (light ? 0xFFFu : 0xFFFFFFu));
	}

	std::uint32_t FormIDOf(std::string_view a_form)
	{
		auto* data = RE::TESDataHandler::GetSingleton();
		const auto colon = a_form.rfind(':');
		if (!data || colon == std::string_view::npos || colon == 0) return 0;
		const auto* mod = data->LookupModByName(a_form.substr(0, colon));
		if (!mod || mod->GetCompileIndex() == 0xFF) return 0;
		const auto local = static_cast<std::uint32_t>(std::strtoul(std::string(a_form.substr(colon + 1)).c_str(), nullptr, 16));
		// Form ids as TESFile::IsFormInMod reads them: FE, the light index and 12 bits for a light plugin, else the index and 24 bits.
		return mod->IsLight() ?
		           0xFE000000u | (static_cast<std::uint32_t>(mod->GetSmallFileCompileIndex()) << 12) | (local & 0xFFFu) :
		           (static_cast<std::uint32_t>(mod->GetCompileIndex()) << 24) | (local & 0xFFFFFFu);
	}

	const Slot* FindForm(std::uint32_t a_formID)
	{
		const auto* map = s_formSlots.load(std::memory_order_acquire);
		if (!map || a_formID == 0) return nullptr;
		const auto it = map->find(a_formID);
		return it == map->end() ? nullptr : it->second;
	}

	void ForEachObjectSlot(const std::function<void(std::uint32_t, const Slot&)>& a_visitor)
	{
		const auto* map = s_formSlots.load(std::memory_order_acquire);
		if (!map) return;
		for (const auto& [formID, slot] : *map) a_visitor(formID, *slot);
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
