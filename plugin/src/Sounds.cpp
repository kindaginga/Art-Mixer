#include "Sounds.h"

#include "Distributor.h"
#include "Packs.h"
#include "Settings.h"

#include <unordered_map>

namespace GShuf::Sounds
{
	namespace
	{
		// BSResource::ID: CRC-32 (initial value 0, no final inversion) of the lowercase file stem and folder, and the extension's first 4 bytes.
		struct ResourceID
		{
			std::uint32_t file{ 0 };
			std::uint32_t ext{ 0 };
			std::uint32_t dir{ 0 };
		};
		static_assert(sizeof(ResourceID) == 12);

		constexpr auto kCrcTable = [] {
			std::array<std::uint32_t, 256> table{};
			for (std::uint32_t i = 0; i < 256; ++i) {
				std::uint32_t c = i;
				for (int k = 0; k < 8; ++k) c = (c & 1) ? (c >> 1) ^ 0xEDB88320U : c >> 1;
				table[i] = c;
			}
			return table;
		}();

		[[nodiscard]] std::uint32_t Crc(std::string_view a_text)
		{
			std::uint32_t c = 0;
			for (const unsigned char b : a_text) c = kCrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
			return c;
		}

		[[nodiscard]] ResourceID IdOf(std::string_view a_path)
		{
			auto path = Settings::Lower(a_path);
			std::replace(path.begin(), path.end(), '/', '\\');
			const auto slash = path.rfind('\\');
			const std::string_view whole(path);
			const auto dir = slash == std::string::npos ? std::string_view() : whole.substr(0, slash);
			const auto name = slash == std::string::npos ? whole : whole.substr(slash + 1);
			const auto dot = name.rfind('.');
			ResourceID id{ Crc(dot == std::string_view::npos ? name : name.substr(0, dot)), 0, Crc(dir) };
			if (dot != std::string_view::npos) std::memcpy(&id.ext, name.data() + dot + 1, std::min<std::size_t>(4, name.size() - dot - 1));
			return id;
		}

		[[nodiscard]] std::uint64_t Key(const ResourceID& a_id)
		{
			return (static_cast<std::uint64_t>(a_id.dir) << 32) | a_id.file;
		}

		// A game sound file some pack replaces: the page its share is on and each pack's file.
		struct Replaced
		{
			std::string slot;
			std::string category;
			std::vector<std::pair<const Packs::Pack*, ResourceID>> files;
		};

		std::unordered_map<std::uint64_t, Replaced> s_replaced;
		std::atomic<int> s_logs{ 0 };
		thread_local bool t_substituted{ false };

		[[nodiscard]] const std::pair<const Packs::Pack*, ResourceID>* Choose(const Replaced& a_replaced)
		{
			// One pack per sound file under the current layout; the pick changes only with the settings.
			const auto* chosen = Distributor::RollCategory(a_replaced.category, "sound|" + a_replaced.slot, [&](const Packs::Pack& a_pack) {
				return std::any_of(a_replaced.files.begin(), a_replaced.files.end(), [&](const auto& a_file) { return a_file.first == &a_pack; });
			});
			if (!chosen) return nullptr;
			std::vector<const std::pair<const Packs::Pack*, ResourceID>*> files;
			const auto collect = [&](const Packs::Pack* a_pack) {
				for (const auto& file : a_replaced.files) {
					if (file.first == a_pack) files.push_back(&file);
				}
			};
			collect(chosen);
			// A pack with no version of the sound plays the vanilla pack's.
			if (files.empty() && !chosen->vanilla) collect(Packs::Vanilla());
			if (files.empty()) return nullptr;
			// Several variants of one sound in a pack (pooled installer options) are picked among by the same layout.
			return files[static_cast<std::size_t>(Distributor::LayoutHash("sound-variant|" + a_replaced.slot) % files.size())];
		}

		// BSXAudio2Audio::CreateGameSound(const BSResource::ID&): every sound the game plays is created here from its file's ID.
		using CreateGameSoundFn = void* (*)(void*, const ResourceID*);
		CreateGameSoundFn s_originalCreate{ nullptr };

		void* HookedCreateGameSound(void* a_self, const ResourceID* a_id)
		{
			if (a_id && !s_replaced.empty()) {
				if (const auto it = s_replaced.find(Key(*a_id)); it != s_replaced.end()) {
					if (const auto* replacement = Choose(it->second)) {
						const ResourceID copy = replacement->second;
						// Marks a file from a pack other than the vanilla pack.
						t_substituted = !replacement->first->vanilla;
						if (t_substituted && s_logs.fetch_add(1) < 40) logger::info("sounds: {} plays a pack file", it->second.slot);
						return s_originalCreate(a_self, &copy);
					}
				}
			}
			return s_originalCreate(a_self, a_id);
		}

		// BGSMusicSingleTrack::DoPlay: loop count at +0x4C, loop data (begin, end, count) at +0x58, sound handle at +0x60.
		using DoPlayFn = void* (*)(std::uint8_t*);
		DoPlayFn s_originalDoPlay{ nullptr };
		std::mutex s_tracksLock;
		std::unordered_map<const void*, bool> s_trackReplaced;

		void* HookedDoPlay(std::uint8_t* a_track)
		{
			t_substituted = false;
			const auto before = *reinterpret_cast<const std::uint32_t*>(a_track + 0x60);
			auto* result = s_originalDoPlay(a_track);
			const auto after = *reinterpret_cast<const std::uint32_t*>(a_track + 0x60);
			bool replaced = false;
			{
				std::scoped_lock lock(s_tracksLock);
				if (after != before) s_trackReplaced[a_track] = t_substituted;
				const auto it = s_trackReplaced.find(a_track);
				replaced = it != s_trackReplaced.end() && it->second;
			}
			// Turns off the vanilla loop points while a pack's file plays.
			const auto* loop = *reinterpret_cast<const std::uint8_t* const*>(a_track + 0x58);
			auto& loops = *reinterpret_cast<std::int32_t*>(a_track + 0x4C);
			if (replaced && loop && loops > 0 && loops == *reinterpret_cast<const std::int32_t*>(loop + 8)) loops = 0;
			return result;
		}
	}

	bool Install()
	{
		for (const auto& pack : Packs::All()) {
			for (const auto& [category, images] : pack.images) {
				for (const auto& image : images) {
					if (!image.slot.starts_with('~') || image.material.empty()) continue;
					// The slot is the game's file without its extension; any extension keeps a dotted name whole.
					auto& replaced = s_replaced[Key(IdOf(image.slot.substr(1) + ".wav"))];
					replaced.slot = image.slot.substr(1);
					replaced.category = category;
					replaced.files.emplace_back(&pack, IdOf(image.material));
				}
			}
		}
		if (s_replaced.empty()) {
			logger::info("sounds: no pack replaces a game sound");
			return true;
		}
		const auto base = REL::Module::get().base();
		REL::Relocation<std::uintptr_t> audio{ RE::VTABLE::BSXAudio2Audio[0] };
		s_originalCreate = reinterpret_cast<CreateGameSoundFn>(audio.write_vfunc(3, &HookedCreateGameSound));
		REL::Relocation<std::uintptr_t> track{ RE::VTABLE::BGSMusicSingleTrack[0] };
		s_originalDoPlay = reinterpret_cast<DoPlayFn>(track.write_vfunc(2, &HookedDoPlay));
		logger::info("sounds: {} game sound files replaced by packs; hooked BSXAudio2Audio::CreateGameSound (vtable {:X}, original {:X}) and BGSMusicSingleTrack::DoPlay (vtable {:X}, original {:X})",
			s_replaced.size(), audio.address() - base, reinterpret_cast<std::uintptr_t>(s_originalCreate) - base, track.address() - base, reinterpret_cast<std::uintptr_t>(s_originalDoPlay) - base);
		return s_originalCreate && s_originalDoPlay;
	}
}
