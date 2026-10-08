#include "PCH.h"

#include "GameArchives.h"
#include "Engine.h"

#include <libdeflate.h>

#include <list>
#include "Settings.h"

namespace ADT::GameArchives
{
	namespace
	{
		using Data = std::shared_ptr<const std::vector<std::uint8_t>>;
		using ErrorCode = RE::BSResource::ErrorCode;

		constexpr std::string_view kMarker = "\\adt\\game\\";

#pragma pack(push, 1)
		struct Header
		{
			char magic[4];
			std::uint32_t version;
			char type[4];
			std::uint32_t count;
			std::uint64_t names;
		};
		struct Record
		{
			std::uint32_t nameHash;
			char extension[4];
			std::uint32_t directoryHash;
			std::uint8_t unknown;
			std::uint8_t chunks;
			std::uint16_t chunkSize;
			std::uint16_t height;
			std::uint16_t width;
			std::uint8_t mips;
			std::uint8_t format;
			std::uint16_t flags;
		};
		struct ChunkRecord
		{
			std::uint64_t offset;
			std::uint32_t packed;
			std::uint32_t unpacked;
			std::uint16_t startMip;
			std::uint16_t endMip;
			std::uint32_t align;
		};
		struct GeneralRecord
		{
			std::uint32_t nameHash;
			char extension[4];
			std::uint32_t directoryHash;
			std::uint32_t unknown;
			std::uint64_t offset;
			std::uint32_t packed;
			std::uint32_t unpacked;
			std::uint32_t align;
		};
#pragma pack(pop)
		static_assert(sizeof(Header) == 24 && sizeof(Record) == 24 && sizeof(ChunkRecord) == 24 && sizeof(GeneralRecord) == 36);

		struct Entry
		{
			bool general{ false };  // a whole file of a general archive; otherwise a texture of a texture archive
			std::uint64_t offset{ 0 };
			std::uint32_t packed{ 0 };
			std::uint32_t unpacked{ 0 };
			Record record{};
			std::vector<ChunkRecord> chunks;
		};

		struct Archive
		{
			std::filesystem::path path;
			std::unordered_map<std::string, Entry> entries;  // by path relative to Data, lowercase with backslashes
		};

		// A file found in a game archive.
		struct Located
		{
			std::shared_ptr<const Archive> archive;
			const Entry* entry{ nullptr };
		};

		std::mutex s_lock;
		std::unordered_map<std::string, std::string> s_sources;                      // file (relative to Data) -> archive file name, from packs.json
		std::unordered_map<std::string, std::shared_ptr<const Archive>> s_archives;  // by lowercase file name; null for one that could not be read
		std::vector<std::shared_ptr<const Archive>> s_all;                           // every game texture and model archive, last file name first
		bool s_allIndexed{ false };
		std::atomic<int> s_logs{ 0 };
		std::atomic<int> s_modelLogs{ 0 };

		// Decodes a zlib stream (RFC 1950) into a buffer of known size with libdeflate, one decompressor per thread.
		[[nodiscard]] bool Inflate(const std::uint8_t* a_in, std::size_t a_inSize, std::uint8_t* a_out, std::size_t a_outSize)
		{
			thread_local const std::unique_ptr<libdeflate_decompressor, decltype(&libdeflate_free_decompressor)> decompressor{ libdeflate_alloc_decompressor(), &libdeflate_free_decompressor };
			if (!decompressor) return false;
			std::size_t written = 0;
			return libdeflate_zlib_decompress(decompressor.get(), a_in, a_inSize, a_out, a_outSize, &written) == LIBDEFLATE_SUCCESS && written == a_outSize;
		}

		// Game files read recently (by path relative to Data), kept up to kCacheBytes; the least recently used goes first.
		constexpr std::size_t kCacheBytes = std::size_t{ 256 } * 1024 * 1024;
		std::mutex s_cacheLock;
		std::list<std::pair<std::string, Data>> s_cacheOrder;  // most recently used first
		std::unordered_map<std::string, std::list<std::pair<std::string, Data>>::iterator> s_cacheIndex;
		std::size_t s_cacheSize{ 0 };

		// The file's bytes when they are still in memory, or null.
		[[nodiscard]] Data Cached(const std::string& a_name)
		{
			std::scoped_lock lock(s_cacheLock);
			const auto it = s_cacheIndex.find(a_name);
			if (it == s_cacheIndex.end()) return nullptr;
			s_cacheOrder.splice(s_cacheOrder.begin(), s_cacheOrder, it->second);
			return it->second->second;
		}

		// Keeps the file's bytes in memory, dropping the least recently used files beyond the budget.
		void Remember(const std::string& a_name, const Data& a_data)
		{
			if (!a_data || a_data->size() > kCacheBytes) return;
			std::scoped_lock lock(s_cacheLock);
			if (s_cacheIndex.contains(a_name)) return;
			s_cacheOrder.emplace_front(a_name, a_data);
			s_cacheIndex.emplace(a_name, s_cacheOrder.begin());
			s_cacheSize += a_data->size();
			while (s_cacheSize > kCacheBytes && !s_cacheOrder.empty()) {
				s_cacheSize -= s_cacheOrder.back().second->size();
				s_cacheIndex.erase(s_cacheOrder.back().first);
				s_cacheOrder.pop_back();
			}
		}

		[[nodiscard]] bool BlockCompressed(std::uint8_t a_format)
		{
			return (a_format >= 70 && a_format <= 84) || (a_format >= 94 && a_format <= 99);
		}

		[[nodiscard]] std::uint32_t BlockBytes(std::uint8_t a_format)
		{
			return a_format == 70 || a_format == 71 || a_format == 72 || a_format == 79 || a_format == 80 || a_format == 81 ? 8u : 16u;
		}

		[[nodiscard]] std::uint32_t BytesPerPixel(std::uint8_t a_format)
		{
			if (a_format >= 61 && a_format <= 65) return 1;
			if (a_format == 49 || a_format == 50 || a_format == 51 || a_format == 54 || (a_format >= 56 && a_format <= 60)) return 2;
			return 4;
		}

		// The 148-byte DDS header with the DX10 extension, as the converter writes it for a texture taken out of an archive.
		void WriteHeader(std::uint8_t* a_header, const Record& a_record)
		{
			const auto put = [&](std::size_t a_at, std::uint32_t a_value) { std::memcpy(a_header + a_at, &a_value, sizeof(a_value)); };
			const bool compressed = BlockCompressed(a_record.format);
			const bool cube = (a_record.flags & 0xFF) != 0;
			const std::uint32_t mips = a_record.mips;
			std::memset(a_header, 0, 148);
			put(0, 0x20534444);
			put(4, 124);
			put(8, 0x1u | 0x2u | 0x4u | 0x1000u | (mips > 1 ? 0x20000u : 0u) | (compressed ? 0x80000u : 0x8u));
			put(12, a_record.height);
			put(16, a_record.width);
			put(20, compressed ? std::max(1u, (a_record.width + 3u) / 4u) * std::max(1u, (a_record.height + 3u) / 4u) * BlockBytes(a_record.format) : a_record.width * BytesPerPixel(a_record.format));
			put(28, std::max(1u, mips));
			put(76, 32);
			put(80, 0x4);
			put(84, 0x30315844);
			put(108, 0x1000u | (mips > 1 ? 0x400008u : 0u) | (cube ? 0x8u : 0u));
			put(112, cube ? 0xFE00u : 0u);
			put(128, a_record.format);
			put(132, 3);
			put(136, cube ? 0x4u : 0u);
			put(140, 1);
		}

		template <class T>
		[[nodiscard]] bool Get(std::ifstream& a_file, T& a_value)
		{
			a_file.read(reinterpret_cast<char*>(&a_value), sizeof(T));
			return a_file.good();
		}

		[[nodiscard]] std::shared_ptr<const Archive> ReadIndex(const std::filesystem::path& a_path)
		{
			std::ifstream file(a_path, std::ios::binary);
			Header header{};
			if (!file || !Get(file, header) || std::memcmp(header.magic, "BTDX", 4) != 0) return nullptr;
			const bool general = std::memcmp(header.type, "GNRL", 4) == 0;
			if (!general && std::memcmp(header.type, "DX10", 4) != 0) return nullptr;
			if (header.version == 2 || header.version == 3) file.seekg(8, std::ios::cur);
			std::vector<Entry> records(header.count);
			for (auto& entry : records) {
				if (general) {
					GeneralRecord record{};
					if (!Get(file, record)) return nullptr;
					entry.general = true;
					entry.offset = record.offset;
					entry.packed = record.packed;
					entry.unpacked = record.unpacked;
					continue;
				}
				if (!Get(file, entry.record)) return nullptr;
				entry.chunks.resize(entry.record.chunks);
				for (auto& chunk : entry.chunks) {
					if (!Get(file, chunk)) return nullptr;
				}
			}
			file.seekg(static_cast<std::streamoff>(header.names));
			auto archive = std::make_shared<Archive>();
			archive->path = a_path;
			archive->entries.reserve(header.count);
			for (auto& entry : records) {
				std::uint16_t length = 0;
				if (!Get(file, length)) return nullptr;
				std::string name(length, '\0');
				file.read(name.data(), length);
				if (!file.good()) return nullptr;
				name = Settings::Lower(name);
				std::replace(name.begin(), name.end(), '/', '\\');
				archive->entries.try_emplace(std::move(name), std::move(entry));
			}
			return archive;
		}

		// The archive of that file name in Data, indexed on first use; null when it cannot be read (s_lock held).
		[[nodiscard]] std::shared_ptr<const Archive> ArchiveNamed(const std::string& a_name)
		{
			const auto key = Settings::Lower(a_name);
			if (const auto it = s_archives.find(key); it != s_archives.end()) return it->second;
			auto archive = ReadIndex(std::filesystem::path("Data") / a_name);
			s_archives.emplace(key, archive);
			return archive;
		}

		// Indexes every texture and model archive of the game (base game, DLC, Creation Club) in Data (s_lock held).
		void IndexAll()
		{
			if (s_allIndexed) return;
			s_allIndexed = true;
			std::vector<std::string> names;
			std::error_code error;
			for (const auto& item : std::filesystem::directory_iterator("Data", error)) {
				const auto name = item.path().filename().string();
				const auto lower = Settings::Lower(name);
				if (!lower.ends_with(".ba2")) continue;
				if (!lower.starts_with("fallout4 - ") && !lower.starts_with("dlc") && !lower.starts_with("cc")) continue;
				if (lower.find(" - textures") == std::string::npos && lower.find(" - meshes") == std::string::npos && lower.find(" - main") == std::string::npos) continue;
				names.push_back(name);
			}
			std::sort(names.begin(), names.end(), [](const auto& a_left, const auto& a_right) { return Settings::Lower(a_left) > Settings::Lower(a_right); });
			for (const auto& name : names) {
				if (auto archive = ArchiveNamed(name)) s_all.push_back(std::move(archive));
			}
			logger::info("game archives: indexed all {} texture and model archives of the game", s_all.size());
		}

		// The game's own file (lowercase path relative to Data) in the archive packs.json names for it, else in any game archive.
		[[nodiscard]] Located Find(const std::string& a_file)
		{
			std::scoped_lock lock(s_lock);
			if (const auto source = s_sources.find(a_file); source != s_sources.end()) {
				if (const auto archive = ArchiveNamed(source->second)) {
					if (const auto entry = archive->entries.find(a_file); entry != archive->entries.end()) return { archive, &entry->second };
				}
			}
			IndexAll();
			for (const auto& archive : s_all) {
				if (const auto entry = archive->entries.find(a_file); entry != archive->entries.end()) {
					if (s_logs.fetch_add(1) < 60) logger::warn("game archives: {} is not where packs.json places it; read from {}", a_file, archive->path.filename().string());
					return { archive, &entry->second };
				}
			}
			if (s_logs.fetch_add(1) < 60) logger::warn("game archives: no game archive has {}", a_file);
			return {};
		}

		[[nodiscard]] std::uint32_t Size(const Entry& a_entry)
		{
			if (a_entry.general) return a_entry.unpacked;
			std::uint32_t size = 148;
			for (const auto& chunk : a_entry.chunks) size += chunk.unpacked;
			return size;
		}

		[[nodiscard]] Data Assemble(const Located& a_file)
		{
			std::ifstream file(a_file.archive->path, std::ios::binary);
			if (!file) return nullptr;
			auto data = std::make_shared<std::vector<std::uint8_t>>(Size(*a_file.entry));
			std::vector<std::uint8_t> raw;
			if (a_file.entry->general) {
				file.seekg(static_cast<std::streamoff>(a_file.entry->offset));
				if (a_file.entry->packed == 0) {
					file.read(reinterpret_cast<char*>(data->data()), a_file.entry->unpacked);
					return file.good() ? data : nullptr;
				}
				raw.resize(a_file.entry->packed);
				file.read(reinterpret_cast<char*>(raw.data()), a_file.entry->packed);
				return file.good() && Inflate(raw.data(), raw.size(), data->data(), a_file.entry->unpacked) ? data : nullptr;
			}
			WriteHeader(data->data(), a_file.entry->record);
			std::size_t at = 148;
			for (const auto& chunk : a_file.entry->chunks) {
				file.seekg(static_cast<std::streamoff>(chunk.offset));
				if (chunk.packed == 0) {
					file.read(reinterpret_cast<char*>(data->data() + at), chunk.unpacked);
					if (!file.good()) return nullptr;
				} else {
					raw.resize(chunk.packed);
					file.read(reinterpret_cast<char*>(raw.data()), chunk.packed);
					if (!file.good() || !Inflate(raw.data(), raw.size(), data->data() + at, chunk.unpacked)) return nullptr;
				}
				at += chunk.unpacked;
			}
			return data;
		}

		// The game file a requested path names through textures\adt\game\ or meshes\adt\game\, as its lowercase path relative to
		// Data with backslashes; empty for any other path.
		[[nodiscard]] std::string Alias(const char* a_path)
		{
			if (!a_path) return {};
			const std::string_view path(a_path);
			const auto same = [](char a_char, char a_wanted) {
				const char lower = a_char == '/' ? '\\' : static_cast<char>(std::tolower(static_cast<unsigned char>(a_char)));
				return lower == a_wanted;
			};
			for (std::size_t start = 0; start + kMarker.size() < path.size(); ++start) {
				std::size_t matched = 0;
				while (matched < kMarker.size() && same(path[start + matched], kMarker[matched])) ++matched;
				if (matched != kMarker.size()) continue;
				const auto head = Settings::Lower(path.substr(0, start));
				const std::string_view root = head.ends_with("textures") ? "textures\\" : head.ends_with("meshes") ? "meshes\\" : "";
				if (root.empty()) return {};
				auto rest = Settings::Lower(path.substr(start + kMarker.size()));
				std::replace(rest.begin(), rest.end(), '/', '\\');
				return std::string(root) + rest;
			}
			return {};
		}

		// A game file in memory (a texture as a DDS file), read from its archive when the game opens or reads the stream.
		class GameStream final : public RE::BSResource::Stream
		{
		public:
			GameStream(Located a_file, std::string a_name, std::string a_request, std::uint64_t a_position, Data a_data) :
				RE::BSResource::Stream(Size(*a_file.entry), false), _file(std::move(a_file)), _name(std::move(a_name)), _request(std::move(a_request)), _position(a_position),
				_data(std::move(a_data))
			{}

			ErrorCode DoOpen() override
			{
				std::scoped_lock lock(_lock);
				_position = 0;
				return Load() ? ErrorCode::kNone : ErrorCode::kFileError;
			}

			void DoClose() override
			{
				std::scoped_lock lock(_lock);
				_data.reset();
			}

			ErrorCode DoGetInfo(RE::BSResource::Info& a_info) override
			{
				a_info = {};
				a_info.fileSize = totalSize;
				return ErrorCode::kNone;
			}

			void DoClone(RE::BSTSmartPointer<RE::BSResource::Stream>& a_result) const override
			{
				std::scoped_lock lock(_lock);
				(new GameStream(_file, _name, _request, _position, _data))->HandTo(a_result);
			}

			ErrorCode DoRead(void* a_buffer, std::uint64_t a_bytes, std::uint64_t& a_read) const override
			{
				std::scoped_lock lock(_lock);
				a_read = 0;
				if (!Load()) return ErrorCode::kFileError;
				const std::uint64_t size = _data->size();
				const std::uint64_t take = _position >= size ? 0 : std::min(a_bytes, size - _position);
				if (take > 0) std::memcpy(a_buffer, _data->data() + _position, take);
				_position += take;
				a_read = take;
				// The copy in memory goes once its end has been read.
				if (_position >= size) _data.reset();
				return ErrorCode::kNone;
			}

			ErrorCode DoReadAt(void* a_buffer, std::uint64_t a_bytes, std::uint64_t a_position, std::uint64_t& a_read) const override
			{
				std::scoped_lock lock(_lock);
				a_read = 0;
				if (!Load()) return ErrorCode::kFileError;
				const std::uint64_t size = _data->size();
				const std::uint64_t take = a_position >= size ? 0 : std::min(a_bytes, size - a_position);
				if (take > 0) std::memcpy(a_buffer, _data->data() + a_position, take);
				a_read = take;
				return ErrorCode::kNone;
			}

			bool DoGetName(RE::BSFixedString& a_result) const override
			{
				a_result = _request.c_str();
				return true;
			}

			ErrorCode DoWrite(const void*, std::uint64_t, std::uint64_t& a_written) const override
			{
				a_written = 0;
				return ErrorCode::kUnsupported;
			}

			ErrorCode DoSeek(std::int64_t a_offset, RE::BSResource::SeekMode a_mode, std::uint64_t& a_position) const override
			{
				std::scoped_lock lock(_lock);
				const auto size = static_cast<std::int64_t>(totalSize);
				const std::int64_t base = a_mode == RE::BSResource::SeekMode::kSet ? 0 : a_mode == RE::BSResource::SeekMode::kCurrent ? static_cast<std::int64_t>(_position) : size;
				_position = static_cast<std::uint64_t>(std::clamp<std::int64_t>(base + a_offset, 0, size));
				a_position = _position;
				return ErrorCode::kNone;
			}

			// Puts the stream into the game's pointer as its first reference, counted where this runtime counts.
			void HandTo(RE::BSTSmartPointer<RE::BSResource::Stream>& a_result)
			{
				static const bool s_countAt10 = REL::Module::get().version() >= REL::Version(1, 11, 0, 0);
				if (s_countAt10) _engine = kRefCountBeg;
				else flags = kRefCountBeg;
				*reinterpret_cast<RE::BSResource::Stream**>(&a_result) = this;
			}

		private:
			// Reads the texture out of its archive unless it is in memory (_lock held).
			bool Load() const
			{
				if (_data) return true;
				if (_data = Cached(_name); _data) {
					Engine::Perf::Add(Engine::Perf::Kind::kCacheHit, 0.0, _name, _data->size());
					return true;
				}
				const auto start = std::chrono::steady_clock::now();
				_data = Assemble(_file);
				Remember(_name, _data);
				const auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - start).count();
				Engine::Perf::Add(Engine::Perf::Kind::kArchiveRead, std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count(), _name, _data ? _data->size() : 0);
				if (!_data) logger::error("game archives: {} could not be read from {}", _name, _file.archive->path.filename().string());
				else if ((_file.entry->general ? s_modelLogs : s_logs).fetch_add(1) < 40) logger::info("game archives: {} read from {} ({} bytes, {} ms)", _name, _file.archive->path.filename().string(), _data->size(), ms);
				return _data != nullptr;
			}

			std::uint64_t _engine{ 0 };  // 10: the reference count and open flags on 1.11.169 and 1.11.240 (0C on 1.10.163 and 1.10.984)
			Located _file;
			std::string _name;     // the game file, relative to Data
			std::string _request;  // the path the game asked for, which it reads back as the stream's name
			mutable std::mutex _lock;
			mutable std::uint64_t _position;
			mutable Data _data;
		};

		class Source final : public RE::BSResource::Location
		{
		public:
			Source() { mounted = false; }

			ErrorCode DoCreateStream(const char* a_path, RE::BSTSmartPointer<RE::BSResource::Stream>& a_result, RE::BSResource::Location*& a_location, bool a_writable, RE::BSResource::LocationTraverser*) override
			{
				auto relative = Alias(a_path);
				if (relative.empty() || a_writable) return ErrorCode::kNotExist;
				auto file = Find(relative);
				if (!file.entry) return ErrorCode::kNotExist;
				(new GameStream(std::move(file), std::move(relative), a_path, 0, nullptr))->HandTo(a_result);
				a_location = this;
				return ErrorCode::kNone;
			}

			ErrorCode DoTraversePrefix(const char*, RE::BSResource::LocationTraverser&) override { return ErrorCode::kNone; }

			ErrorCode DoGetInfo(const char* a_path, RE::BSResource::Info& a_result, RE::BSResource::LocationTraverser*) override
			{
				return Info(a_path, a_result);
			}

			ErrorCode DoGetInfo(const char* a_path, RE::BSResource::Info& a_result, RE::BSResource::Location*& a_location) override
			{
				const auto code = Info(a_path, a_result);
				if (code == ErrorCode::kNone) a_location = this;
				return code;
			}

			[[nodiscard]] const char* DoGetName() const override { return "ADTGameArchives"; }

		private:
			ErrorCode Info(const char* a_path, RE::BSResource::Info& a_result)
			{
				const auto relative = Alias(a_path);
				if (relative.empty()) return ErrorCode::kNotExist;
				const auto file = Find(relative);
				if (!file.entry) return ErrorCode::kNotExist;
				a_result = {};
				a_result.fileSize = Size(*file.entry);
				return ErrorCode::kNone;
			}
		};
	}

	namespace
	{
		using CreateStream = ErrorCode (*)(RE::BSResource::Location*, const char*, RE::BSTSmartPointer<RE::BSResource::Stream>&, RE::BSResource::Location*&, bool, RE::BSResource::LocationTraverser*);

		// A file source class of the game and its own stream function, which HookedCreateStream stands in for.
		struct GameSource
		{
			std::uintptr_t table{ 0 };
			CreateStream original{ nullptr };
		};

		Source* s_source{ nullptr };
		std::array<GameSource, 4> s_gameSources{};
		int s_gameSourceCount{ 0 };

		// Answers an ADT\Game request made straight to one of the game's own file sources, as a model load makes it.
		ErrorCode HookedCreateStream(RE::BSResource::Location* a_self, const char* a_path, RE::BSTSmartPointer<RE::BSResource::Stream>& a_result, RE::BSResource::Location*& a_location, bool a_writable, RE::BSResource::LocationTraverser* a_traverser)
		{
			if (s_source && s_source->DoCreateStream(a_path, a_result, a_location, a_writable, a_traverser) == ErrorCode::kNone) return ErrorCode::kNone;
			const auto table = *reinterpret_cast<const std::uintptr_t*>(a_self);
			for (int i = 0; i < s_gameSourceCount; ++i) {
				if (s_gameSources[i].table == table) return s_gameSources[i].original(a_self, a_path, a_result, a_location, a_writable, a_traverser);
			}
			return ErrorCode::kNotExist;
		}

		// Puts HookedCreateStream into the stream slot of each class the game's own file sources have; returns how many classes.
		int HookGameSources(const RE::BSResource::GlobalLocations& a_global)
		{
			const auto rdata = REL::Module::get().segment(REL::Segment::rdata);
			const auto text = REL::Module::get().segment(REL::Segment::text);
			const auto base = REL::Module::get().base();
			for (const auto* entry = a_global.head; entry; entry = entry->next) {
				if (!entry->location || s_gameSourceCount >= static_cast<int>(s_gameSources.size())) continue;
				const auto table = *reinterpret_cast<const std::uintptr_t*>(entry->location);
				bool known = false;
				for (int i = 0; i < s_gameSourceCount; ++i) known = known || s_gameSources[i].table == table;
				if (known || table < rdata.address() || table >= rdata.address() + rdata.size()) continue;
				const auto original = reinterpret_cast<const std::uintptr_t*>(table)[3];
				if (original < text.address() || original >= text.address() + text.size()) {
					logger::error("game archives: the stream function of the game's file source class {:X} points outside the game ({:X}), another plugin hooks it; the game's own models the packs name will be missing", table - base, original);
					continue;
				}
				s_gameSources[s_gameSourceCount++] = { table, reinterpret_cast<CreateStream>(original) };
				REL::safe_write(table + 3 * sizeof(std::uintptr_t), reinterpret_cast<std::uintptr_t>(&HookedCreateStream));
				logger::info("game archives: hooked the stream function {:X} of the game's file source class {:X}", original - base, table - base);
			}
			return s_gameSourceCount;
		}
	}

	void SetSources(const std::unordered_map<std::string, std::string>& a_textures, const std::unordered_map<std::string, std::string>& a_models)
	{
		std::scoped_lock lock(s_lock);
		s_sources.clear();
		for (const auto& [texture, archive] : a_textures) s_sources.emplace("textures\\" + texture, archive);
		for (const auto& [model, archive] : a_models) s_sources.emplace("meshes\\" + model, archive);
	}

	bool Install()
	{
		const auto* global = RE::BSResource::GlobalLocations::GetSingleton();
		if (!global) {
			logger::error("game archives: the game's file source list does not exist; the game's own files the packs name will be missing");
			return false;
		}
		int sources = 0;
		for (const auto* entry = global->head; entry && sources < 64; entry = entry->next) ++sources;
		s_source = new Source();
		if (HookGameSources(*global) == 0) logger::error("game archives: none of the game's file source classes could be hooked; the game's own models the packs name will be missing");
		RE::BSResource::RegisterLocation(s_source, 10);
		std::size_t placed = 0;
		{
			std::scoped_lock lock(s_lock);
			placed = s_sources.size();
		}
		logger::info("game archives: file source registered next to the game's {}; packs.json places {} game textures and models", sources, placed);
		return true;
	}
}
