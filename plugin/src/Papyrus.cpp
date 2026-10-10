#include "Papyrus.h"

#include "ApplyNow.h"
#include "Distributor.h"
#include "Engine.h"
#include "LoadScreens.h"
#include "MainMenu.h"
#include "MenuRotation.h"
#include "Packs.h"
#include "Settings.h"

namespace ArtMixer::Papyrus
{
	namespace
	{
		constexpr auto kScript = "ArtMixer"sv;
		// Entries a Papyrus array takes for sure; the menu fixes arrive in rounds of at most this many.
		constexpr std::size_t kMaxPapyrusArray = 128;

		std::int32_t GetVersion(std::monostate)
		{
			return static_cast<std::int32_t>(Version::MAJOR * 10000 + Version::MINOR * 100 + Version::PATCH);
		}

		bool IsReady(std::monostate)
		{
			return Engine::Available() && !Packs::All().empty();
		}

		void SetPercent(std::monostate, RE::BSFixedString a_category, std::int32_t a_slot, std::int32_t a_value)
		{
			Settings::SetPercent(a_category.c_str() ? a_category.c_str() : "", a_slot, a_value);
			Distributor::QueueReapply();
		}

		std::int32_t GetPercent(std::monostate, RE::BSFixedString a_category, std::int32_t a_slot)
		{
			return Settings::Percent(a_category.c_str() ? a_category.c_str() : "", a_slot);
		}

		bool GetNewLayoutEachLoad(std::monostate)
		{
			return Settings::Get().newLayoutEachLoad;
		}

		bool SaveSettings(std::monostate)
		{
			return Settings::Save();
		}

		bool AskApplyAll(std::monostate, bool a_leaving)
		{
			return ApplyNow::Ask(a_leaving);
		}

		std::int32_t GetApplyAnswer(std::monostate)
		{
			return ApplyNow::Answer();
		}

		void ReloadNow(std::monostate)
		{
			ApplyNow::Reload();
		}

		std::int32_t GetSeed(std::monostate)
		{
			return Settings::Seed();
		}

		void SetMenuVideo(std::monostate, std::int32_t a_index)
		{
			if (a_index == 0) {
				MainMenu::ResetPicks();
			} else if (a_index != MainMenu::Selected()) {
				MainMenu::Select(a_index);
			}
		}

		std::int32_t GetMenuVideo(std::monostate)
		{
			return MainMenu::AnyPicked() ? std::max(1, MainMenu::Selected()) : 0;
		}

		RE::BSFixedString GetPackSignature(std::monostate)
		{
			return RE::BSFixedString(Packs::Signature());
		}

		// The main menu list a category names, or nothing for the other categories.
		[[nodiscard]] std::optional<MainMenu::List> MenuListOf(std::string_view a_category)
		{
			if (a_category == "menuthemes") return MainMenu::List::Themes;
			if (a_category == "menubackgrounds") return MainMenu::List::Backgrounds;
			if (a_category == "menumusic") return MainMenu::List::Music;
			return std::nullopt;
		}

		std::int32_t GetSlotCount(std::monostate, RE::BSFixedString a_category)
		{
			const auto category = Settings::NormalizeCategory(a_category.c_str() ? a_category.c_str() : "");
			if (const auto list = MenuListOf(category)) return MainMenu::ListCount(*list);
			return static_cast<std::int32_t>(Packs::SlotsFor(category).size());
		}

		std::int32_t GetStoredSlotCount(std::monostate, RE::BSFixedString a_category)
		{
			return Settings::StoredSlots(a_category.c_str() ? a_category.c_str() : "");
		}

		std::vector<std::string> GetCategories(std::monostate)
		{
			return Packs::Sections();
		}

		// Menu values that differ from the settings in effect, or from the defaults for a reset: each shown slot and each list pick.
		std::vector<std::pair<std::string, int>> MenuFixes(bool a_defaults)
		{
			const auto menu = Settings::MenuValues();
			std::vector<std::pair<std::string, int>> fixes;
			const auto check = [&](std::string a_id, int a_wanted) {
				const auto it = menu.find(a_id);
				if (it != menu.end() && it->second != a_wanted) fixes.emplace_back(std::move(a_id), a_wanted);
			};
			for (const auto& section : Packs::Sections()) {
				const auto category = Settings::NormalizeCategory(section);
				const auto menuList = MenuListOf(category);
				const int shown = menuList ? MainMenu::ListCount(*menuList) : static_cast<int>(Packs::SlotsFor(category).size());
				const auto applied = [&](int a_slot) { return a_defaults ? 0 : Settings::Percent(category, a_slot); };
				if (menuList) {
					check("iPick:" + section, a_defaults ? 0 : MainMenu::PickSlot(*menuList));
					continue;
				}
				if (category == "pipboyscreen" || category == "pipboyskin" || category == "pipboymap") {
					int pick = 0;
					for (int slot = 1; slot <= shown && pick == 0; ++slot) {
						if (applied(slot) == 100) pick = slot;
					}
					check("iPick:" + section, pick);
					continue;
				}
				if (category == "other") {
					// Each Other replacers pack's switch is on when its share is the whole.
					for (int slot = 1; slot <= shown; ++slot) check(Settings::MenuKey(section, slot), applied(slot) == 100 ? 1 : 0);
					continue;
				}
				for (int slot = 1; slot <= shown; ++slot) check(Settings::MenuKey(section, slot), applied(slot));
			}
			return fixes;
		}

		std::vector<std::string> GetMenuFixIds(std::monostate, bool a_defaults)
		{
			std::vector<std::string> ids;
			for (auto& [id, value] : MenuFixes(a_defaults)) {
				if (ids.size() == kMaxPapyrusArray) break;
				ids.push_back(std::move(id));
			}
			return ids;
		}

		std::vector<std::int32_t> GetMenuFixValues(std::monostate, bool a_defaults)
		{
			std::vector<std::int32_t> values;
			for (const auto& [id, value] : MenuFixes(a_defaults)) {
				if (values.size() == kMaxPapyrusArray) break;
				values.push_back(value);
			}
			return values;
		}

		// The Main menu page's six switches and the Loading screens page's two boxes as MCM ids with their values, in effect or by default.
		[[nodiscard]] std::vector<std::pair<std::string, int>> AllFlagValues(bool a_defaults)
		{
			auto flags = MenuRotation::FlagValues(a_defaults);
			for (auto& flag : LoadScreens::FlagValues(a_defaults)) flags.push_back(std::move(flag));
			return flags;
		}

		// The switches as MCM ids, in effect or by default, in the order of GetMenuFlagValues.
		std::vector<std::string> GetMenuFlagIds(std::monostate, bool a_defaults)
		{
			std::vector<std::string> ids;
			for (auto& [id, value] : AllFlagValues(a_defaults)) ids.push_back(std::move(id));
			return ids;
		}

		std::vector<std::int32_t> GetMenuFlagValues(std::monostate, bool a_defaults)
		{
			std::vector<std::int32_t> values;
			for (const auto& [id, value] : AllFlagValues(a_defaults)) values.push_back(value);
			return values;
		}

		// True when a menu share, pick, Main menu page switch or Loading screens page box differs from the settings in effect.
		bool MenuHasChanges(std::monostate)
		{
			if (!MenuFixes(false).empty()) return true;
			const auto menu = Settings::MenuValues();
			for (const auto& [id, value] : AllFlagValues(false)) {
				const auto it = menu.find(id);
				if (it != menu.end() && (it->second != 0) != (value != 0)) return true;
			}
			return false;
		}

		// Makes the menu's values the settings in effect.
		void LoadMenuSettings(std::monostate)
		{
			Settings::LoadMenu();
			MenuRotation::Apply();
			Distributor::QueueReapply();
		}
	}

	bool Register(RE::BSScript::IVirtualMachine* a_vm)
	{
		if (!a_vm) return false;
		a_vm->BindNativeMethod(kScript, "GetVersion"sv, GetVersion, true);
		a_vm->BindNativeMethod(kScript, "IsReady"sv, IsReady, true);
		a_vm->BindNativeMethod(kScript, "SetPercent"sv, SetPercent, true);
		a_vm->BindNativeMethod(kScript, "GetSeed"sv, GetSeed, true);
		a_vm->BindNativeMethod(kScript, "GetPercent"sv, GetPercent, true);
		a_vm->BindNativeMethod(kScript, "GetNewLayoutEachLoad"sv, GetNewLayoutEachLoad, true);
		a_vm->BindNativeMethod(kScript, "SaveSettings"sv, SaveSettings, true);
		a_vm->BindNativeMethod(kScript, "AskApplyAll"sv, AskApplyAll, true);
		a_vm->BindNativeMethod(kScript, "GetApplyAnswer"sv, GetApplyAnswer, true);
		a_vm->BindNativeMethod(kScript, "ReloadNow"sv, ReloadNow, true);
		a_vm->BindNativeMethod(kScript, "SetMenuVideo"sv, SetMenuVideo, true);
		a_vm->BindNativeMethod(kScript, "GetMenuVideo"sv, GetMenuVideo, true);
		a_vm->BindNativeMethod(kScript, "GetPackSignature"sv, GetPackSignature, true);
		a_vm->BindNativeMethod(kScript, "GetSlotCount"sv, GetSlotCount, true);
		a_vm->BindNativeMethod(kScript, "GetStoredSlotCount"sv, GetStoredSlotCount, true);
		a_vm->BindNativeMethod(kScript, "GetCategories"sv, GetCategories, true);
		a_vm->BindNativeMethod(kScript, "GetMenuFixIds"sv, GetMenuFixIds, true);
		a_vm->BindNativeMethod(kScript, "GetMenuFixValues"sv, GetMenuFixValues, true);
		a_vm->BindNativeMethod(kScript, "MenuHasChanges"sv, MenuHasChanges, true);
		a_vm->BindNativeMethod(kScript, "LoadMenuSettings"sv, LoadMenuSettings, true);
		a_vm->BindNativeMethod(kScript, "GetMenuFlagIds"sv, GetMenuFlagIds, true);
		a_vm->BindNativeMethod(kScript, "GetMenuFlagValues"sv, GetMenuFlagValues, true);
		logger::info("papyrus: ArtMixer natives registered");
		return true;
	}
}
