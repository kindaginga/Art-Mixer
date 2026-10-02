#include "Inventory.h"

#include <unordered_map>

namespace GShuf::Inventory
{
	namespace
	{
		constexpr std::uint32_t kUniqueID = 'GShf';
		constexpr std::uint32_t kPickedRecord = 'PICK';
		constexpr std::uint32_t kDroppedRecord = 'DROP';
		constexpr std::uint32_t kRecordVersion = 2;
		constexpr std::uint32_t kPlayer = 0x14;

		std::mutex s_lock;
		std::unordered_map<std::uint32_t, std::uint32_t> s_pickedUp;  // base object -> world reference whose pick it shows
		// A reference the player dropped: its base object (the game reuses form ids of deleted references) and the reference whose pick it shows.
		struct Dropped
		{
			std::uint32_t base{ 0 };
			std::uint32_t shows{ 0 };
		};
		std::unordered_map<std::uint32_t, Dropped> s_dropped;
		std::atomic<int> s_logs{ 0 };

		std::uint32_t Resolve(std::uint32_t a_ref, std::uint32_t a_base)
		{
			const auto it = s_dropped.find(a_ref);
			return it == s_dropped.end() || it->second.base != a_base ? a_ref : it->second.shows;
		}

		// The world reference the player last activated, per base object, until its pickup arrives, and pickups that arrived first.
		std::unordered_map<std::uint32_t, std::uint32_t> s_activated;
		std::unordered_map<std::uint32_t, int> s_pendingPickups;
		// Base objects the player dropped whose new world reference has not loaded yet, with the pick it keeps.
		std::unordered_map<std::uint32_t, std::uint32_t> s_pendingDrops;

		void PickedUp(std::uint32_t a_base, std::uint32_t a_ref)
		{
			s_pickedUp[a_base] = Resolve(a_ref, a_base);
			s_dropped.erase(a_ref);
			if (s_logs.fetch_add(1) < 20) logger::info("inventory: picked up {:08X} (base {:08X}), shown as {:08X}", a_ref, a_base, s_pickedUp[a_base]);
		}

		// A pickup event carries the base object but not the world reference; the player's activation of that reference does.
		class ActivateSink final : public RE::BSTEventSink<RE::TESActivateEvent>
		{
		public:
			RE::BSEventNotifyControl ProcessEvent(const RE::TESActivateEvent& a_event, RE::BSTEventSource<RE::TESActivateEvent>*) override
			{
				const auto* actor = a_event.actionRef.get();
				const auto* object = a_event.objectActivated.get();
				const auto* base = object ? object->GetObjectReference() : nullptr;
				if (!actor || actor->GetFormID() != kPlayer || !base || !object->GetParentCell()) return RE::BSEventNotifyControl::kContinue;
				std::scoped_lock lock(s_lock);
				if (const auto it = s_pendingPickups.find(base->GetFormID()); it != s_pendingPickups.end()) {
					if (--it->second <= 0) s_pendingPickups.erase(it);
					PickedUp(base->GetFormID(), object->GetFormID());
				} else {
					s_activated[base->GetFormID()] = object->GetFormID();
				}
				return RE::BSEventNotifyControl::kContinue;
			}
		};

		class ContainerSink final : public RE::BSTEventSink<RE::TESContainerChangedEvent>
		{
		public:
			RE::BSEventNotifyControl ProcessEvent(const RE::TESContainerChangedEvent& a_event, RE::BSTEventSource<RE::TESContainerChangedEvent>*) override
			{
				std::scoped_lock lock(s_lock);
				const auto base = a_event.baseObjectFormID;
				if (a_event.newContainerFormID == kPlayer && a_event.oldContainerFormID == 0) {
					if (a_event.referenceFormID != 0) {
						PickedUp(base, a_event.referenceFormID);
					} else if (const auto it = s_activated.find(base); it != s_activated.end()) {
						PickedUp(base, it->second);
						s_activated.erase(it);
					} else {
						++s_pendingPickups[base];
					}
				} else if (a_event.oldContainerFormID == kPlayer && a_event.newContainerFormID == 0) {
					if (const auto it = s_pickedUp.find(base); it != s_pickedUp.end()) {
						if (a_event.referenceFormID != 0) s_dropped[a_event.referenceFormID] = { base, it->second };
						else s_pendingDrops[base] = it->second;
						if (s_logs.fetch_add(1) < 20) logger::info("inventory: dropped base {:08X} (reference {:08X}), shown as {:08X}", base, a_event.referenceFormID, it->second);
					}
				}
				return RE::BSEventNotifyControl::kContinue;
			}
		};

		ActivateSink s_activateSink;
		ContainerSink s_sink;

		// Writes a record of form id rows, each row the given number of form ids.
		void WriteRows(const F4SE::SerializationInterface* a_intfc, std::uint32_t a_type, const std::vector<std::array<std::uint32_t, 3>>& a_rows, std::size_t a_width)
		{
			if (!a_intfc->OpenRecord(a_type, kRecordVersion)) return;
			const auto count = static_cast<std::uint32_t>(a_rows.size());
			a_intfc->WriteRecordData(&count, sizeof(count));
			for (const auto& row : a_rows) a_intfc->WriteRecordData(row.data(), static_cast<std::uint32_t>(a_width * sizeof(std::uint32_t)));
		}

		// Reads rows written by WriteRows; form ids of objects from plugins follow the load order, ids of objects made in the game stay.
		std::vector<std::array<std::uint32_t, 3>> ReadRows(const F4SE::SerializationInterface* a_intfc, std::size_t a_width)
		{
			std::vector<std::array<std::uint32_t, 3>> rows;
			std::uint32_t count = 0;
			if (a_intfc->ReadRecordData(&count, sizeof(count)) != sizeof(count)) return rows;
			for (std::uint32_t i = 0; i < count; ++i) {
				std::array<std::uint32_t, 3> row{};
				const auto size = static_cast<std::uint32_t>(a_width * sizeof(std::uint32_t));
				if (a_intfc->ReadRecordData(row.data(), size) != size) break;
				for (std::size_t k = 0; k < a_width; ++k) row[k] = a_intfc->ResolveFormID(row[k]).value_or(row[k]);
				rows.push_back(row);
			}
			return rows;
		}

		void F4SEAPI OnSave(const F4SE::SerializationInterface* a_intfc)
		{
			std::scoped_lock lock(s_lock);
			std::vector<std::array<std::uint32_t, 3>> picked;
			for (const auto& [base, shows] : s_pickedUp) picked.push_back({ base, shows, 0 });
			WriteRows(a_intfc, kPickedRecord, picked, 2);
			std::vector<std::array<std::uint32_t, 3>> dropped;
			for (const auto& [ref, entry] : s_dropped) dropped.push_back({ ref, entry.base, entry.shows });
			WriteRows(a_intfc, kDroppedRecord, dropped, 3);
		}

		void F4SEAPI OnLoad(const F4SE::SerializationInterface* a_intfc)
		{
			std::scoped_lock lock(s_lock);
			s_pickedUp.clear();
			s_dropped.clear();
			std::uint32_t type = 0, version = 0, length = 0;
			while (a_intfc->GetNextRecordInfo(type, version, length)) {
				if (version != kRecordVersion) continue;
				if (type == kPickedRecord) {
					for (const auto& row : ReadRows(a_intfc, 2)) s_pickedUp[row[0]] = row[1];
				} else if (type == kDroppedRecord) {
					for (const auto& row : ReadRows(a_intfc, 3)) s_dropped[row[0]] = { row[1], row[2] };
				}
			}
			logger::info("inventory: {} carried and {} dropped items keep their world pick", s_pickedUp.size(), s_dropped.size());
		}

		void F4SEAPI OnRevert(const F4SE::SerializationInterface*)
		{
			std::scoped_lock lock(s_lock);
			s_pickedUp.clear();
			s_dropped.clear();
			s_activated.clear();
			s_pendingPickups.clear();
			s_pendingDrops.clear();
		}
	}

	void Install()
	{
		auto* serialization = const_cast<F4SE::SerializationInterface*>(F4SE::GetSerializationInterface());
		if (!serialization) {
			logger::warn("inventory: no serialization interface; carried items keep their pick only until the game is closed");
			return;
		}
		serialization->SetUniqueID(kUniqueID);
		serialization->SetSaveCallback(OnSave);
		serialization->SetLoadCallback(OnLoad);
		serialization->SetRevertCallback(OnRevert);
	}

	void OnGameDataReady()
	{
		auto* containers = RE::TESContainerChangedEvent::GetEventSource();
		auto* activations = RE::TESActivateEvent::GetEventSource();
		if (containers && activations) {
			containers->RegisterSink(&s_sink);
			activations->RegisterSink(&s_activateSink);
			logger::info("inventory: following items the player picks up and drops");
		}
	}

	std::uint32_t IdentityOf(std::uint32_t a_refFormID, std::uint32_t a_baseFormID, bool a_inWorld)
	{
		std::scoped_lock lock(s_lock);
		if (const auto it = s_dropped.find(a_refFormID); it != s_dropped.end() && it->second.base == a_baseFormID) return it->second.shows;
		// A dropped item's new world reference is made by the game (form ids from FF) and loads right after the drop.
		if (a_inWorld && (a_refFormID >> 24) == 0xFF) {
			if (const auto it = s_pendingDrops.find(a_baseFormID); it != s_pendingDrops.end()) {
				const auto shows = it->second;
				s_dropped[a_refFormID] = { a_baseFormID, shows };
				s_pendingDrops.erase(it);
				if (s_logs.fetch_add(1) < 20) logger::info("inventory: {:08X} (base {:08X}) is the dropped item, shown as {:08X}", a_refFormID, a_baseFormID, shows);
				return shows;
			}
		}
		if (!a_inWorld) {
			if (const auto it = s_pickedUp.find(a_baseFormID); it != s_pickedUp.end()) return it->second;
		}
		return a_refFormID;
	}
}
