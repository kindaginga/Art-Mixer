#include "Papyrus.h"

#include "Distributor.h"
#include "Engine.h"
#include "Packs.h"
#include "Settings.h"

namespace GShuf::Papyrus
{
	namespace
	{
		constexpr auto kScript = "GShuf"sv;

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

		void SetBlankCanvases(std::monostate, bool a_enabled)
		{
			Settings::SetBlankCanvases(a_enabled);
			Distributor::QueueReapply();
		}

		void SetSeed(std::monostate, std::int32_t a_seed)
		{
			Settings::SetSeed(a_seed);
			Distributor::QueueReapply();
		}

		std::int32_t NewSeed(std::monostate)
		{
			const auto seed = Settings::Seed() + 1;
			Settings::SetSeed(seed);
			Distributor::QueueReapply();
			return seed;
		}

		void Reapply(std::monostate)
		{
			Distributor::QueueReapply();
		}

		std::int32_t GetSlotCount(std::monostate, RE::BSFixedString a_category)
		{
			return static_cast<std::int32_t>(Packs::SlotsFor(a_category.c_str() ? a_category.c_str() : "").size());
		}

		RE::BSFixedString GetSlotName(std::monostate, RE::BSFixedString a_category, std::int32_t a_slot)
		{
			const auto slots = Packs::SlotsFor(a_category.c_str() ? a_category.c_str() : "");
			if (a_slot < 1 || static_cast<std::size_t>(a_slot) > slots.size()) return RE::BSFixedString("");
			return RE::BSFixedString(slots[static_cast<std::size_t>(a_slot - 1)]->name);
		}
	}

	bool Register(RE::BSScript::IVirtualMachine* a_vm)
	{
		if (!a_vm) return false;
		a_vm->BindNativeMethod(kScript, "GetVersion"sv, GetVersion, true);
		a_vm->BindNativeMethod(kScript, "IsReady"sv, IsReady, true);
		a_vm->BindNativeMethod(kScript, "SetPercent"sv, SetPercent, true);
		a_vm->BindNativeMethod(kScript, "SetBlankCanvases"sv, SetBlankCanvases, true);
		a_vm->BindNativeMethod(kScript, "SetSeed"sv, SetSeed, true);
		a_vm->BindNativeMethod(kScript, "NewSeed"sv, NewSeed, true);
		a_vm->BindNativeMethod(kScript, "Reapply"sv, Reapply, true);
		a_vm->BindNativeMethod(kScript, "GetSlotCount"sv, GetSlotCount, true);
		a_vm->BindNativeMethod(kScript, "GetSlotName"sv, GetSlotName, true);
		logger::info("papyrus: GShuf natives registered");
		return true;
	}
}
