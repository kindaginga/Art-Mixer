#include "MenuRotation.h"

#include "MainMenu.h"
#include "Settings.h"

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>

#include <fstream>
#include <regex>
#include <set>

namespace ArtMixer::MenuRotation
{
	namespace
	{
		constexpr auto kDll = "RandomMainMenu.dll";
		constexpr std::string_view kEntryPrefix{ "mainmenuloop_adt-" };
		constexpr std::string_view kMusicEntryPrefix{ "mus_maintheme_adt-" };
		constexpr std::string_view kImmersivePrefix{ "mainmenuloop_im" };
		constexpr std::string_view kImmersiveMusicPrefix{ "mus_maintheme_im" };
		// The three draw settings, then the two that keep Random Main Menu from leaving the game's own video and music in place.
		constexpr std::array<const char*, 5> kRandomKeys{ "bEnableRandomMainMenu", "bEnableRandomMusicMenu", "bEnableMusicAssociated", "bDisableDefaultMainMenu", "bDisableDefaultMusicMenu" };
		constexpr std::array<const char*, 6> kFlagIds{ "bRandomMainMenu:ArtMixer_MainMenu", "bImmersiveMainMenus:ArtMixer_MainMenu", "bImmersiveOnly:ArtMixer_MainMenu", "bRotateThemes:ArtMixer_MainMenu", "bRotateBackgrounds:ArtMixer_MainMenu", "bRotateMusic:ArtMixer_MainMenu" };
		// The menus' names by number, as Immersive Main Menus' installer names them (the 1080p and ultrawide builds share the numbers).
		constexpr std::array<const char*, 19> kMenuNames{ "", "East Boston Ext.", "Gwinnet Restaurant 1", "Gwinnet Restaurant 2", "Vault 111 Ext.", "Vault 111 Corridor", "Vault 111 Cryogenic Array", "Crater of Atom", "FEV Lab", "Longneck Lukowski's Cannery", "Starlight Interstellar Theatre", "Cliff's Edge Hotel", "Diamond City", "Esplanade", "Lexington", "Lexington River", "Sanctuary Creek", "Red Rocket Night", "Sanctuary Radio" };

		std::vector<ImmersiveMenu> s_menus;
		bool s_fileOnDisk{ false };
		std::atomic<bool> s_pluginsLoaded{ false };
		std::atomic<bool> s_loaded{ false };
		std::atomic<bool> s_failed{ false };
		std::atomic<bool> s_messageShown{ false };
		std::uintptr_t s_moduleBegin{ 0 };
		std::uintptr_t s_moduleEnd{ 0 };

		// One of the two calls in the game's menu start that Random Main Menu patches: the one building the menu video's
		// parameters and the one building the menu music path.
		struct PatchedCall
		{
			std::uintptr_t site{ 0 };      // the call instruction in the game's code
			std::uintptr_t random{ 0 };    // Random Main Menu's replacement
			std::uintptr_t original{ 0 };  // the game's own callee, read from the executable on disk
		};
		PatchedCall s_videoCall;
		PatchedCall s_musicCall;
		std::atomic<bool> s_callsHooked{ false };
		std::atomic<bool> s_randomVideo{ false };  // the video call goes to Random Main Menu
		std::atomic<bool> s_randomMusic{ false };  // the music call goes to Random Main Menu
		using VideoParamsFn = void* (*)(void*, const char*, std::uint8_t, void*, std::uint32_t);
		using MusicPathFn = const char* (*)(char*, std::uint32_t, const char*, const char*);

		std::mutex s_mutex;
		std::vector<std::string> s_hiddenPrefixes;  // lowercase name prefixes Random Main Menu is kept from seeing
		std::atomic<bool> s_filtering{ false };
		std::set<HANDLE> s_handles;  // listings opened by Random Main Menu in a menu folder while names are hidden
		std::atomic<int> s_hiddenCount{ 0 };

		using FindFirstFileWFn = HANDLE(WINAPI*)(LPCWSTR, LPWIN32_FIND_DATAW);
		using FindFirstFileExWFn = HANDLE(WINAPI*)(LPCWSTR, FINDEX_INFO_LEVELS, LPVOID, FINDEX_SEARCH_OPS, LPVOID, DWORD);
		using FindNextFileWFn = BOOL(WINAPI*)(HANDLE, LPWIN32_FIND_DATAW);
		using FindCloseFn = BOOL(WINAPI*)(HANDLE);
		FindFirstFileWFn s_findFirst{ nullptr };
		FindFirstFileExWFn s_findFirstEx{ nullptr };
		FindNextFileWFn s_findNext{ nullptr };
		FindCloseFn s_findClose{ nullptr };

		[[nodiscard]] std::filesystem::path PluginsFolder() { return std::filesystem::path("Data") / "F4SE" / "Plugins"; }
		[[nodiscard]] std::filesystem::path VideoFolder() { return std::filesystem::path("Data") / "Video"; }
		[[nodiscard]] std::filesystem::path MusicFolder() { return std::filesystem::path("Data") / "Music" / "Special"; }

		// Closes the message box; the button needs no action.
		class Notice : public RE::IMessageBoxCallback
		{
		public:
			void operator()(std::uint8_t) override {}
		};

		// True while Random Main Menu's module is loaded; before the modules are known, while its file is on disk.
		[[nodiscard]] bool RandomPresent()
		{
			return s_pluginsLoaded.load() ? s_loaded.load() : s_fileOnDisk;
		}

		// A stored Main menu page value, or the default when the settings do not name it.
		[[nodiscard]] bool Stored(const char* a_key, bool a_default)
		{
			const auto value = Settings::MainMenuFlag(a_key);
			return value ? *value != 0 : a_default;
		}

		// True when a frame of the current call comes from Random Main Menu's module.
		[[nodiscard]] bool FromRandomMainMenu()
		{
			if (!s_moduleBegin) return false;
			void* frames[32]{};
			const auto count = RtlCaptureStackBackTrace(1, 32, frames, nullptr);
			for (USHORT i = 0; i < count; ++i) {
				const auto address = reinterpret_cast<std::uintptr_t>(frames[i]);
				if (address >= s_moduleBegin && address < s_moduleEnd) return true;
			}
			return false;
		}

		[[nodiscard]] std::string Narrow(const wchar_t* a_text)
		{
			std::string out;
			for (; a_text && *a_text; ++a_text) out += *a_text < 128 ? static_cast<char>(std::tolower(static_cast<int>(*a_text))) : '?';
			return out;
		}

		// True for a listing pattern inside Data\Video or Data\Music\Special.
		[[nodiscard]] bool MenuFolder(const wchar_t* a_pattern)
		{
			auto text = Narrow(a_pattern);
			std::replace(text.begin(), text.end(), '/', '\\');
			return text.find("video\\") != std::string::npos || text.find("music\\special\\") != std::string::npos;
		}

		[[nodiscard]] bool HiddenName(const wchar_t* a_name)
		{
			const auto name = Narrow(a_name);
			std::scoped_lock lock(s_mutex);
			return std::any_of(s_hiddenPrefixes.begin(), s_hiddenPrefixes.end(), [&](const std::string& a_prefix) { return name.starts_with(a_prefix); });
		}

		// Moves a listing Random Main Menu opened past the hidden names; INVALID_HANDLE_VALUE when nothing is left.
		HANDLE Filtered(HANDLE a_handle, LPWIN32_FIND_DATAW a_data)
		{
			while (HiddenName(a_data->cFileName)) {
				++s_hiddenCount;
				if (!s_findNext(a_handle, a_data)) {
					s_findClose(a_handle);
					SetLastError(ERROR_FILE_NOT_FOUND);
					return INVALID_HANDLE_VALUE;
				}
			}
			std::scoped_lock lock(s_mutex);
			s_handles.insert(a_handle);
			return a_handle;
		}

		HANDLE WINAPI HookedFindFirstFileW(LPCWSTR a_pattern, LPWIN32_FIND_DATAW a_data)
		{
			auto handle = s_findFirst(a_pattern, a_data);
			if (handle == INVALID_HANDLE_VALUE || !s_filtering.load() || !a_data || !MenuFolder(a_pattern) || !FromRandomMainMenu()) return handle;
			return Filtered(handle, a_data);
		}

		HANDLE WINAPI HookedFindFirstFileExW(LPCWSTR a_pattern, FINDEX_INFO_LEVELS a_level, LPVOID a_data, FINDEX_SEARCH_OPS a_ops, LPVOID a_filter, DWORD a_flags)
		{
			auto handle = s_findFirstEx(a_pattern, a_level, a_data, a_ops, a_filter, a_flags);
			if (handle == INVALID_HANDLE_VALUE || !s_filtering.load() || !a_data || !MenuFolder(a_pattern) || !FromRandomMainMenu()) return handle;
			return Filtered(handle, static_cast<LPWIN32_FIND_DATAW>(a_data));
		}

		BOOL WINAPI HookedFindNextFileW(HANDLE a_handle, LPWIN32_FIND_DATAW a_data)
		{
			auto result = s_findNext(a_handle, a_data);
			if (!result || !s_filtering.load() || !a_data) return result;
			{
				std::scoped_lock lock(s_mutex);
				if (!s_handles.contains(a_handle)) return result;
			}
			while (result && HiddenName(a_data->cFileName)) {
				++s_hiddenCount;
				result = s_findNext(a_handle, a_data);
			}
			return result;
		}

		BOOL WINAPI HookedFindClose(HANDLE a_handle)
		{
			{
				std::scoped_lock lock(s_mutex);
				s_handles.erase(a_handle);
			}
			return s_findClose(a_handle);
		}

		// Replaces every import of a function in a module's import table; the first original found is returned. Counts the slots patched.
		int PatchImport(HMODULE a_module, const char* a_function, void* a_replacement, void** a_original)
		{
			const auto base = reinterpret_cast<std::uint8_t*>(a_module);
			const auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
			const auto nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
			const auto& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
			if (!dir.VirtualAddress) return 0;
			int patched = 0;
			auto* desc = reinterpret_cast<const IMAGE_IMPORT_DESCRIPTOR*>(base + dir.VirtualAddress);
			for (; desc->Name; ++desc) {
				if (!desc->OriginalFirstThunk) continue;
				auto* names = reinterpret_cast<const IMAGE_THUNK_DATA64*>(base + desc->OriginalFirstThunk);
				auto* thunks = reinterpret_cast<IMAGE_THUNK_DATA64*>(base + desc->FirstThunk);
				for (; names->u1.AddressOfData; ++names, ++thunks) {
					if (names->u1.Ordinal & IMAGE_ORDINAL_FLAG64) continue;
					const auto* byName = reinterpret_cast<const IMAGE_IMPORT_BY_NAME*>(base + names->u1.AddressOfData);
					if (strcmp(byName->Name, a_function) != 0) continue;
					DWORD old = 0;
					if (!VirtualProtect(&thunks->u1.Function, sizeof(thunks->u1.Function), PAGE_READWRITE, &old)) continue;
					if (!*a_original) *a_original = reinterpret_cast<void*>(thunks->u1.Function);
					thunks->u1.Function = reinterpret_cast<ULONGLONG>(a_replacement);
					VirtualProtect(&thunks->u1.Function, sizeof(thunks->u1.Function), old, &old);
					++patched;
				}
			}
			return patched;
		}

		// Hooks the folder listing calls in Random Main Menu's own import table and in the C++ runtime's, which lists folders for it.
		void HookListings(HMODULE a_module)
		{
			void* first = nullptr;
			void* firstEx = nullptr;
			void* next = nullptr;
			void* close = nullptr;
			int patched = 0;
			for (HMODULE module : { a_module, GetModuleHandleW(L"msvcp140.dll") }) {
				if (!module) continue;
				patched += PatchImport(module, "FindFirstFileW", reinterpret_cast<void*>(&HookedFindFirstFileW), &first);
				patched += PatchImport(module, "FindFirstFileExW", reinterpret_cast<void*>(&HookedFindFirstFileExW), &firstEx);
				patched += PatchImport(module, "FindNextFileW", reinterpret_cast<void*>(&HookedFindNextFileW), &next);
				patched += PatchImport(module, "FindClose", reinterpret_cast<void*>(&HookedFindClose), &close);
			}
			const auto kernel = GetModuleHandleW(L"kernel32.dll");
			// A call not imported by either module keeps the system's own, so every hook has an original to call.
			s_findFirst = reinterpret_cast<FindFirstFileWFn>(first ? first : reinterpret_cast<void*>(GetProcAddress(kernel, "FindFirstFileW")));
			s_findFirstEx = reinterpret_cast<FindFirstFileExWFn>(firstEx ? firstEx : reinterpret_cast<void*>(GetProcAddress(kernel, "FindFirstFileExW")));
			s_findNext = reinterpret_cast<FindNextFileWFn>(next ? next : reinterpret_cast<void*>(GetProcAddress(kernel, "FindNextFileW")));
			s_findClose = reinterpret_cast<FindCloseFn>(close ? close : reinterpret_cast<void*>(GetProcAddress(kernel, "FindClose")));
			logger::info("mainmenu: Random Main Menu folder listings hooked ({} import slots: FindFirstFileW {}, FindFirstFileExW {}, FindNextFileW {}, FindClose {})", patched, first != nullptr, firstEx != nullptr, next != nullptr, close != nullptr);
		}

		[[nodiscard]] std::string ReadText(const std::filesystem::path& a_path)
		{
			std::ifstream file(a_path, std::ios::binary);
			if (!file) return {};
			return std::string(std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>());
		}

		[[nodiscard]] bool WriteText(const std::filesystem::path& a_path, const std::string& a_text)
		{
			std::ofstream file(a_path, std::ios::binary | std::ios::trunc);
			file << a_text;
			file.close();
			return static_cast<bool>(file);
		}

		// The value of "key = value" in Random Main Menu's settings text, or nothing when the key is absent.
		[[nodiscard]] std::optional<std::string> IniValue(const std::string& a_text, const char* a_key)
		{
			const std::regex pattern("(?:^|\\n)[ \\t]*" + std::string(a_key) + "[ \\t]*=[ \\t]*([^\\r\\n]*)", std::regex::icase);
			std::smatch match;
			if (!std::regex_search(a_text, match, pattern)) return std::nullopt;
			auto value = match[1].str();
			value.erase(value.find_last_not_of(" \t") + 1);
			return value;
		}

		// Sets "key = value" in Random Main Menu's settings text, keeping the line's spacing; appends the line when the key is absent.
		void SetIniValue(std::string& a_text, const char* a_key, const std::string& a_value)
		{
			const std::regex pattern("((?:^|\\n)[ \\t]*" + std::string(a_key) + "[ \\t]*=[ \\t]*)[^\\r\\n]*", std::regex::icase);
			std::smatch match;
			if (std::regex_search(a_text, match, pattern)) {
				// Spliced by position: a format string would read "$1" followed by a digit as another group.
				const auto at = static_cast<std::size_t>(match.position(0));
				a_text = a_text.substr(0, at) + match[1].str() + a_value + a_text.substr(at + static_cast<std::size_t>(match.length(0)));
				return;
			}
			const auto newline = a_text.find("\r\n") != std::string::npos ? "\r\n" : "\n";
			if (!a_text.empty() && a_text.back() != '\n') a_text += newline;
			a_text += std::string(a_key) + " = " + a_value + newline;
		}

		// Sets Random Main Menu's draw settings, recording the player's own values once so they can be put back: the game's own
		// video and music stay the default while the matching rotation is off.
		void SetRandomSettings(bool a_video, bool a_music, bool a_associated)
		{
			const auto path = PluginsFolder() / "RandomMainMenu.ini";
			auto text = ReadText(path);
			if (text.empty()) {
				// Random Main Menu writes its file at its first draw; until then its own defaults stand in, in its own layout.
				text = "\xEF\xBB\xBF[Main]\r\nbRandomOnlyOnStartup = 0\r\nbDisableDefaultMainMenu = 1\r\nbDisableDefaultMusicMenu = 1\r\n\r\n; if this option is enabled it will play the background music associated with the video\r\nbEnableMusicAssociated = 1\r\nbEnableRandomMainMenu = 1\r\nbEnableRandomMusicMenu = 0\r\n\r\n; settings used internally by the plugin\r\niLastMainMenu = 0\r\niLastMusicMenu = 0\r\n";
				logger::info("mainmenu: Random Main Menu has not written {} yet; it is written with its defaults", path.string());
			}
			auto state = MainMenu::LoadState();
			auto originals = state.value("randomMainMenuOriginal", nlohmann::json::object());
			const std::array<bool, 5> wanted{ a_video, a_music, a_associated, a_video, a_music };
			const auto before = text;
			for (std::size_t i = 0; i < kRandomKeys.size(); ++i) {
				const auto current = IniValue(text, kRandomKeys[i]);
				if (!originals.contains(kRandomKeys[i])) originals[kRandomKeys[i]] = current.value_or("");
				SetIniValue(text, kRandomKeys[i], wanted[i] ? "1" : "0");
			}
			if (text != before && !WriteText(path, text)) {
				logger::error("mainmenu: could not write {}", path.string());
				return;
			}
			state["randomMainMenuOriginal"] = originals;
			MainMenu::StoreState(state);
			logger::info("mainmenu: Random Main Menu settings: random video {}, random music {}, music with the video {}, default video kept {}, default music kept {}{}", a_video ? 1 : 0, a_music ? 1 : 0, a_associated ? 1 : 0, a_video ? 0 : 1, a_music ? 0 : 1, text != before ? "" : " (unchanged)");
		}

		// Puts Random Main Menu's three draw settings back to the player's own values, when they were recorded.
		void RestoreRandomSettings()
		{
			auto state = MainMenu::LoadState();
			if (!state.contains("randomMainMenuOriginal")) return;
			const auto path = PluginsFolder() / "RandomMainMenu.ini";
			auto text = ReadText(path);
			const auto originals = state["randomMainMenuOriginal"];
			if (!text.empty()) {
				const auto before = text;
				for (const auto& key : kRandomKeys) {
					if (originals.contains(key) && originals[key].is_string() && !originals[key].get<std::string>().empty()) SetIniValue(text, key, originals[key].get<std::string>());
				}
				if (text != before && !WriteText(path, text)) {
					logger::error("mainmenu: could not write {}", path.string());
					return;
				}
			}
			state.erase("randomMainMenuOriginal");
			MainMenu::StoreState(state);
			logger::info("mainmenu: Random Main Menu settings put back to the player's own");
		}

		// Every Random Main Menu entry of this plugin's on disk: file names in lowercase by folder.
		[[nodiscard]] std::vector<std::filesystem::path> Entries(const std::filesystem::path& a_folder, std::string_view a_prefix)
		{
			std::vector<std::filesystem::path> out;
			std::error_code error;
			for (const auto& entry : std::filesystem::directory_iterator(a_folder, error)) {
				if (Settings::Lower(entry.path().filename().string()).starts_with(a_prefix)) out.push_back(entry.path());
			}
			return out;
		}

		// Writes the packs' entries for Random Main Menu, or removes them: a video entry is an empty file the video hook answers
		// with the pack's own video; a music entry is a copy, since the game plays the file Random Main Menu names.
		void WriteEntries(bool a_wanted)
		{
			std::set<std::string> keep;
			int videos = 0, music = 0;
			std::error_code error;
			if (a_wanted) {
				std::filesystem::create_directories(VideoFolder(), error);
				std::filesystem::create_directories(MusicFolder(), error);
				for (const auto& file : MainMenu::PackMenuFiles()) {
					if (!file.video.empty()) {
						const auto stub = VideoFolder() / (std::string(kEntryPrefix) + file.id + ".bk2");
						keep.insert(Settings::Lower(stub.filename().string()));
						if (!std::filesystem::exists(stub, error) && !WriteText(stub, "")) logger::error("mainmenu: could not write {}", stub.string());
						++videos;
					}
					if (!file.music.empty()) {
						const auto copy = MusicFolder() / (std::string(kMusicEntryPrefix) + file.id + ".xwm");
						const auto source = std::filesystem::path("Data") / "Music" / file.music;
						keep.insert(Settings::Lower(copy.filename().string()));
						if (!std::filesystem::exists(copy, error) || std::filesystem::file_size(copy, error) != std::filesystem::file_size(source, error)) {
							if (!std::filesystem::copy_file(source, copy, std::filesystem::copy_options::overwrite_existing, error)) logger::error("mainmenu: could not copy {} to {}: {}", source.string(), copy.string(), error.message());
						}
						++music;
					}
				}
			}
			int removed = 0;
			for (const auto& folder : { std::pair(VideoFolder(), kEntryPrefix), std::pair(MusicFolder(), kMusicEntryPrefix) }) {
				for (const auto& path : Entries(folder.first, folder.second)) {
					if (keep.contains(Settings::Lower(path.filename().string()))) continue;
					if (std::filesystem::remove(path, error)) ++removed;
					else logger::error("mainmenu: could not remove {}: {}", path.string(), error.message());
				}
			}
			logger::info("mainmenu: Random Main Menu entries: {} videos, {} music{}", videos, music, removed ? fmt::format(", {} old entries removed", removed) : "");
		}

		// Brings the Main menu page's three lists in the MCM config up to date with the entries shown, so a menu that joins or leaves the lists appears there too.
		void UpdateMenuConfig()
		{
			const auto path = std::filesystem::path("Data") / "MCM" / "Config" / std::string(Settings::kModName) / "config.json";
			nlohmann::json config;
			try {
				std::ifstream in(path);
				if (!in) return;
				config = nlohmann::json::parse(in);
			} catch (const std::exception& e) {
				logger::warn("mainmenu: {} not read: {}", path.string(), e.what());
				return;
			}
			bool changed = false;
			const std::array<std::pair<const char*, MainMenu::List>, 3> lists{ { { "iPick:ArtMixer_MenuThemes", MainMenu::List::Themes }, { "iPick:ArtMixer_MenuBackgrounds", MainMenu::List::Backgrounds }, { "iPick:ArtMixer_MenuMusic", MainMenu::List::Music } } };
			if (!config.contains("pages") || !config["pages"].is_array()) return;
			for (auto& page : config["pages"]) {
				if (!page.contains("content") || !page["content"].is_array()) continue;
				for (auto& control : page["content"]) {
					const auto id = control.value("id", std::string());
					for (const auto& [wanted, list] : lists) {
						if (id != wanted) continue;
						nlohmann::json options = nlohmann::json::array({ "Vanilla" });
						for (const auto& name : MainMenu::ListNames(list)) options.push_back(name);
						if (control["valueOptions"]["options"] != options) {
							control["valueOptions"]["options"] = options;
							changed = true;
						}
					}
				}
			}
			if (!changed) return;
			if (WriteText(path, config.dump(2) + "\n")) logger::info("mainmenu: MCM lists brought up to date in {}", path.string());
			else logger::error("mainmenu: could not write {}", path.string());
		}
	}

	void Detect()
	{
		std::error_code error;
		s_fileOnDisk = std::filesystem::exists(PluginsFolder() / kDll, error);
		s_menus.clear();
		const std::regex pattern("^mainmenuloop_im(\\d+)([a-z0-9]*)\\.bk2$", std::regex::icase);
		for (const auto& entry : std::filesystem::directory_iterator(VideoFolder(), error)) {
			const auto name = Settings::Lower(entry.path().filename().string());
			std::smatch match;
			if (!std::regex_match(name, match, pattern)) continue;
			const auto number = std::stoi(match[1].str());
			ImmersiveMenu menu;
			menu.id = "im" + match[1].str() + match[2].str();
			menu.name = number > 0 && number < static_cast<int>(kMenuNames.size()) ? kMenuNames[number] : fmt::format("Immersive Main Menu {}", number);
			menu.video = (VideoFolder() / entry.path().filename()).string();
			const auto music = MusicFolder() / ("mus_maintheme_" + menu.id + ".xwm");
			if (std::filesystem::exists(music, error)) menu.music = music.string();
			s_menus.push_back(std::move(menu));
		}
		std::sort(s_menus.begin(), s_menus.end(), [](const ImmersiveMenu& a, const ImmersiveMenu& b) {
			const auto number = [](const ImmersiveMenu& m) { return std::stoi(m.id.substr(2, m.id.find_first_not_of("0123456789", 2) - 2)); };
			return std::pair(number(a), a.id) < std::pair(number(b), b.id);
		});
		std::string names;
		for (const auto& menu : s_menus) names += (names.empty() ? "" : ", ") + menu.id + (menu.music.empty() ? " (no music)" : "");
		logger::info("mainmenu: Random Main Menu file {}; Immersive Main Menus: {} menus{}", s_fileOnDisk ? "on disk" : "not installed", s_menus.size(), names.empty() ? "" : " (" + names + ")");
	}

	const std::vector<ImmersiveMenu>& Menus()
	{
		return s_menus;
	}

	namespace
	{
		// Every call in the game's code that leaves the game image for a stub jumping into Random Main Menu, with the stub's target.
		[[nodiscard]] std::vector<std::pair<std::uintptr_t, std::uintptr_t>> PatchedSites()
		{
			std::vector<std::pair<std::uintptr_t, std::uintptr_t>> sites;
			const auto text = REL::Module::get().segment(REL::Segment::text);
			const auto base = REL::Module::get().base();
			const auto image = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + reinterpret_cast<const IMAGE_DOS_HEADER*>(base)->e_lfanew)->OptionalHeader.SizeOfImage;
			const auto code = reinterpret_cast<const std::uint8_t*>(text.address());
			for (std::size_t i = 0; i + 5 <= text.size(); ++i) {
				if (code[i] != 0xE8) continue;
				const auto rel = *reinterpret_cast<const std::int32_t*>(code + i + 1);
				const auto target = reinterpret_cast<std::uintptr_t>(code + i + 5) + rel;
				if (target >= base && target < base + image) continue;
				MEMORY_BASIC_INFORMATION info{};
				if (!VirtualQuery(reinterpret_cast<const void*>(target), &info, sizeof(info)) || info.State != MEM_COMMIT || (info.Protect & PAGE_NOACCESS) || (info.Protect & PAGE_GUARD)) continue;
				if (reinterpret_cast<std::uintptr_t>(info.BaseAddress) + info.RegionSize < target + 14) continue;
				const auto stub = reinterpret_cast<const std::uint8_t*>(target);
				if (stub[0] != 0xFF || stub[1] != 0x25 || *reinterpret_cast<const std::uint32_t*>(stub + 2) != 0) continue;
				const auto hook = *reinterpret_cast<const std::uintptr_t*>(stub + 6);
				if (hook < s_moduleBegin || hook >= s_moduleEnd) continue;
				sites.emplace_back(reinterpret_cast<std::uintptr_t>(code + i), hook);
			}
			return sites;
		}

		// True when the bytes a_pattern sit within the 16 bytes before the call at a_site.
		[[nodiscard]] bool Preceded(std::uintptr_t a_site, std::initializer_list<std::uint8_t> a_pattern)
		{
			const auto code = reinterpret_cast<const std::uint8_t*>(a_site - 16);
			for (std::size_t i = 0; i + a_pattern.size() <= 16; ++i) {
				if (std::equal(a_pattern.begin(), a_pattern.end(), code + i)) return true;
			}
			return false;
		}

		// A function's bytes; -1 marks a byte that differs between copies (an import's address).
		using Pattern = std::vector<int>;
		// The game's menu video parameter constructor: stores the five arguments into the parameter block and returns it, in the
		// 1.11 order and in the 1.10.163 order.
		const Pattern kVideoParams{ 0x8B, 0x44, 0x24, 0x28, 0x89, 0x01, 0x48, 0x8B, 0xC1, 0x48, 0x89, 0x51, 0x08, 0x4C, 0x89, 0x49, 0x10, 0x44, 0x88, 0x41, 0x18, 0xC3 };
		const Pattern kVideoParams163{ 0x8B, 0x44, 0x24, 0x28, 0x48, 0x89, 0x51, 0x08, 0x4C, 0x89, 0x49, 0x10, 0x89, 0x01, 0x48, 0x8B, 0xC1, 0x44, 0x88, 0x41, 0x18, 0xC3 };
		// The game's path helper: returns the path from its "music\" part when it has one, else the prefix and the path joined in the buffer.
		const Pattern kMusicPath{ 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57, 0x48, 0x83, 0xEC, 0x20, 0x4D, 0x8B, 0xF1, 0x44, 0x8B, 0xEA, 0x4D, 0x8B, 0xF8, 0x4C, 0x8B, 0xE1, 0x49, 0x8B, 0xF8, 0x33, 0xED, 0x48, 0xC7, 0xC6, 0xFF, 0xFF, 0xFF, 0xFF, 0x48, 0xFF, 0xC6, 0x41, 0x38, 0x2C, 0x31, 0x75, 0xF7, 0x41, 0x0F, 0xBE, 0x09, 0xFF, 0x15, -1, -1, -1, -1, 0x41, 0x0F, 0xBE, 0x0F, 0x0F, 0xB7, 0xD8, 0xFF, 0x15, -1, -1, -1, -1, 0x3B, 0xC3, 0x75, 0x18 };

		// The first place the game's loaded code holds a_pattern, and how many places hold it; 0 when none.
		[[nodiscard]] std::pair<std::uintptr_t, int> FindCode(const Pattern& a_pattern)
		{
			const auto text = REL::Module::get().segment(REL::Segment::text);
			const auto code = reinterpret_cast<const std::uint8_t*>(text.address());
			std::uintptr_t first = 0;
			int count = 0;
			for (std::size_t i = 0; i + a_pattern.size() <= text.size(); ++i) {
				bool match = true;
				for (std::size_t j = 0; match && j < a_pattern.size(); ++j) match = a_pattern[j] < 0 || code[i + j] == a_pattern[j];
				if (!match) continue;
				if (!first) first = reinterpret_cast<std::uintptr_t>(code + i);
				++count;
			}
			return { first, count };
		}

		// The game function Random Main Menu's replacement at a_hook calls on: the first pointer its code loads from its own image
		// that lands in the game's code; 0 when it loads none.
		[[nodiscard]] std::uintptr_t StoredOriginal(std::uintptr_t a_hook)
		{
			const auto text = REL::Module::get().segment(REL::Segment::text);
			const auto code = reinterpret_cast<const std::uint8_t*>(a_hook);
			const auto limit = std::min<std::size_t>(1024, s_moduleEnd - a_hook);
			for (std::size_t i = 0; i + 7 <= limit; ++i) {
				std::size_t next = 0;
				if (code[i] == 0xFF && code[i + 1] == 0x15) next = i + 6;
				else if ((code[i] == 0x48 || code[i] == 0x4C) && code[i + 1] == 0x8B && (code[i + 2] & 0xC7) == 0x05) next = i + 7;
				else continue;
				const auto rel = *reinterpret_cast<const std::int32_t*>(code + next - 4);
				const auto slot = a_hook + next + rel;
				if (slot < s_moduleBegin || slot + sizeof(std::uintptr_t) > s_moduleEnd) continue;
				const auto value = *reinterpret_cast<const std::uintptr_t*>(slot);
				if (value >= text.address() && value < text.address() + text.size()) return value;
			}
			return 0;
		}

		// The plugin's own copy of the game's menu video parameter constructor: the five arguments stored into the block.
		void* OwnVideoParams(void* a_params, const char* a_path, std::uint8_t a_flag, void* a_r9, std::uint32_t a_fifth)
		{
			auto* block = static_cast<std::uint8_t*>(a_params);
			std::memcpy(block, &a_fifth, sizeof(a_fifth));
			std::memcpy(block + 0x08, &a_path, sizeof(a_path));
			std::memcpy(block + 0x10, &a_r9, sizeof(a_r9));
			block[0x18] = a_flag;
			return a_params;
		}

		// The plugin's own copy of the game's path helper: the path from its prefix part when the prefix starts it or follows a
		// separator (either separator closing it), else the prefix and the path joined in the buffer.
		const char* OwnMusicPath(char* a_buffer, std::uint32_t a_size, const char* a_path, const char* a_prefix)
		{
			const auto length = std::strlen(a_prefix);
			if (length && _strnicmp(a_path, a_prefix, length) == 0) return a_path;
			for (const char* at = a_path; length && *at; ++at) {
				if ((*at == '\\' || *at == '/') && _strnicmp(at + 1, a_prefix, length - 1) == 0 && (at[length] == '\\' || at[length] == '/')) return at + 1;
			}
			strcpy_s(a_buffer, a_size, a_prefix);
			strcat_s(a_buffer, a_size, a_path);
			return a_buffer;
		}

		// Writes a loaded image, page by page, to a_file; pages that cannot be read stay zero.
		void DumpImage(std::uintptr_t a_begin, std::size_t a_size, const std::filesystem::path& a_file)
		{
			std::ofstream out(a_file, std::ios::binary);
			std::vector<char> zero(4096);
			for (std::size_t at = 0; at < a_size; at += 4096) {
				MEMORY_BASIC_INFORMATION info{};
				const auto page = reinterpret_cast<const char*>(a_begin + at);
				const bool readable = VirtualQuery(page, &info, sizeof(info)) && info.State == MEM_COMMIT && !(info.Protect & PAGE_NOACCESS) && !(info.Protect & PAGE_GUARD);
				out.write(readable ? page : zero.data(), std::min<std::size_t>(4096, a_size - at));
			}
			logger::info("mainmenu: {} bytes of the image at {:#x} written to {}", a_size, a_begin, a_file.string());
		}

		// On request (a file named ArtMixer_dump.request next to the plugin log), writes the game's image and Random
		// Main Menu's image next to the log and removes the request.
		void DumpOnRequest()
		{
			const auto logs = logger::log_directory();
			if (!logs) return;
			const auto folder = *logs;
			const auto request = folder / "ArtMixer_dump.request";
			std::error_code error;
			if (!std::filesystem::exists(request, error)) return;
			const auto base = REL::Module::get().base();
			const auto image = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + reinterpret_cast<const IMAGE_DOS_HEADER*>(base)->e_lfanew)->OptionalHeader.SizeOfImage;
			DumpImage(base, image, folder / "ArtMixer_game.bin");
			if (s_moduleBegin) DumpImage(s_moduleBegin, s_moduleEnd - s_moduleBegin, folder / "ArtMixer_randommainmenu.bin");
			std::filesystem::remove(request, error);
		}

		// The game builds the menu video's parameters; Random Main Menu gets the call only while it may choose the video.
		void* HookedVideoParams(void* a_params, const char* a_path, std::uint8_t a_flag, void* a_r9, std::uint32_t a_fifth)
		{
			const bool random = s_randomVideo.load();
			logger::info("mainmenu: menu video call -> {} (the game's video '{}')", random ? "Random Main Menu" : "the game", a_path ? a_path : "");
			return reinterpret_cast<VideoParamsFn>(random ? s_videoCall.random : s_videoCall.original)(a_params, a_path, a_flag, a_r9, a_fifth);
		}

		// The game builds the menu music path from its setting; Random Main Menu gets the call only while it may choose the music.
		const char* HookedMusicPath(char* a_buffer, std::uint32_t a_size, const char* a_path, const char* a_prefix)
		{
			const bool random = s_randomMusic.load();
#ifdef ARTMIXER_AUTOTEST
			if (!random) {
				char scratch[MAX_PATH]{};
				const auto theirs = reinterpret_cast<MusicPathFn>(s_musicCall.random)(scratch, sizeof(scratch), a_path, a_prefix);
				logger::info("autotest: Random Main Menu would have passed the music path '{}'", theirs ? theirs : "");
			}
#endif
			const auto result = reinterpret_cast<MusicPathFn>(random ? s_musicCall.random : s_musicCall.original)(a_buffer, a_size, a_path, a_prefix);
			logger::info("mainmenu: menu music path -> '{}' from {} (the game's path '{}')", result ? result : "", random ? "Random Main Menu" : "the game", a_path ? a_path : "");
			return result;
		}

		// Puts the plugin in front of Random Main Menu at the two calls it patched, so its picks reach the game only while wanted.
		void HookPatchedCalls()
		{
			const auto base = REL::Module::get().base();
			const auto sites = PatchedSites();
			PatchedCall video, music;
			for (const auto& [site, hook] : sites) {
				if (Preceded(site, { 0xBA, 0x04, 0x01, 0x00, 0x00 }) && !music.site) music = { site, hook, 0 };
				else if (Preceded(site, { 0x41, 0xB0, 0x30 }) && !video.site) video = { site, hook, 0 };
			}
			if (sites.size() != 2 || !video.site || !music.site) {
				logger::error("mainmenu: Random Main Menu patched {} game calls, not the menu video and menu music calls the plugin knows; its picks stay in control of the menu", sites.size());
				return;
			}
			// The 1.10 executables are encrypted on disk, so the game's own callees come from the loaded code: by their bytes, else
			// from the pointers Random Main Menu's replacements call them through, else the plugin's own copies run.
			auto [videoFn, videoCount] = FindCode(kVideoParams);
			if (!videoFn) std::tie(videoFn, videoCount) = FindCode(kVideoParams163);
			const auto [musicFn, musicCount] = FindCode(kMusicPath);
			std::string videoRoute, musicRoute;
			if (videoFn) { video.original = videoFn; videoRoute = fmt::format("found by its bytes {} times", videoCount); }
			else if (const auto stored = StoredOriginal(video.random)) { video.original = stored; videoRoute = "read from Random Main Menu's stored pointer"; }
			else { video.original = reinterpret_cast<std::uintptr_t>(&OwnVideoParams); videoRoute = "the plugin's own copy"; }
			if (musicFn) { music.original = musicFn; musicRoute = fmt::format("found by its bytes {} times", musicCount); }
			else if (const auto stored = StoredOriginal(music.random)) { music.original = stored; musicRoute = "read from Random Main Menu's stored pointer"; }
			else { music.original = reinterpret_cast<std::uintptr_t>(&OwnMusicPath); musicRoute = "the plugin's own copy"; }
			auto& trampoline = F4SE::GetTrampoline();
			if (trampoline.free_size() < 28) {
				logger::error("mainmenu: no trampoline space for the menu call hooks ({} bytes free); Random Main Menu's picks stay in control of the menu", trampoline.free_size());
				return;
			}
			s_videoCall = video;
			s_musicCall = music;
			trampoline.write_call<5>(video.site, &HookedVideoParams);
			trampoline.write_call<5>(music.site, &HookedMusicPath);
			s_callsHooked.store(true);
			logger::info("mainmenu: Random Main Menu's menu calls hooked: video at exe+{:#x} (game function {:#x}, {}; Random Main Menu dll+{:#x}), music at exe+{:#x} (game function {:#x}, {}; Random Main Menu dll+{:#x})",
				video.site - base, video.original - base, videoRoute, video.random - s_moduleBegin, music.site - base, music.original - base, musicRoute, music.random - s_moduleBegin);
		}
	}

	void OnPluginsLoaded()
	{
		const auto module = GetModuleHandleA(kDll);
		s_loaded.store(module != nullptr);
		s_pluginsLoaded.store(true);
		if (module) {
			const auto base = reinterpret_cast<std::uint8_t*>(module);
			const auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
			const auto nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
			s_moduleBegin = reinterpret_cast<std::uintptr_t>(base);
			s_moduleEnd = s_moduleBegin + nt->OptionalHeader.SizeOfImage;
			logger::info("mainmenu: Random Main Menu loaded ({} bytes at {:#x})", nt->OptionalHeader.SizeOfImage, s_moduleBegin);
			HookPatchedCalls();
			HookListings(module);
			DumpOnRequest();
		} else if (s_fileOnDisk) {
			s_failed.store(true);
			logger::error("mainmenu: {} is in Data\\F4SE\\Plugins but did not load; the Random Main Menu box stays off", kDll);
		} else {
			logger::info("mainmenu: Random Main Menu not installed");
		}
		Apply();
	}

	bool RandomMainMenuLoaded()
	{
		return s_loaded.load();
	}

	Flags Effective()
	{
		Flags flags;
		// Each box is off until the player ticks it; a box whose mod is not found stays off.
		flags.randomMainMenu = RandomPresent() && Stored("brandommainmenu", false);
		flags.immersiveMainMenus = flags.randomMainMenu && !s_menus.empty() && Stored("bimmersivemainmenus", false);
		flags.immersiveOnly = flags.immersiveMainMenus && Stored("bimmersiveonly", false);
		const bool rotating = flags.randomMainMenu && !flags.immersiveOnly;
		flags.rotateThemes = rotating && Stored("brotatethemes", false);
		// Themes rotates backgrounds with their music, so it stands alone.
		flags.rotateBackgrounds = rotating && !flags.rotateThemes && Stored("brotatebackgrounds", false);
		flags.rotateMusic = rotating && !flags.rotateThemes && Stored("brotatemusic", false);
		return flags;
	}

	Flags Defaults()
	{
		// Every box and switch starts off; Random Main Menu does nothing until the player ticks its box.
		return Flags{};
	}

	std::vector<std::pair<std::string, int>> FlagValues(bool a_defaults)
	{
		const auto flags = a_defaults ? Defaults() : Effective();
		const std::array<bool, 6> values{ flags.randomMainMenu, flags.immersiveMainMenus, flags.immersiveOnly, flags.rotateThemes, flags.rotateBackgrounds, flags.rotateMusic };
		std::vector<std::pair<std::string, int>> out;
		for (std::size_t i = 0; i < kFlagIds.size(); ++i) out.emplace_back(kFlagIds[i], values[i] ? 1 : 0);
		return out;
	}

	void Apply()
	{
		const auto flags = Effective();
		MainMenu::SetImmersiveShown(flags.immersiveMainMenus);
		MainMenu::ApplyFromSettings();
		WriteEntries(flags.randomMainMenu && !flags.immersiveOnly);
		// What Random Main Menu may see: nothing while its box is off or the matching rotation is off, so it has nothing to draw
		// from and the game plays the pick; Immersive Main Menus' names only while that box is on.
		std::string hidden;
		{
			std::scoped_lock lock(s_mutex);
			s_hiddenPrefixes.clear();
			const bool music = flags.rotateThemes || flags.rotateMusic;
			if (!flags.randomMainMenu || !(flags.rotateThemes || flags.rotateBackgrounds || flags.immersiveOnly)) s_hiddenPrefixes.push_back("mainmenuloop_");
			else if (!flags.immersiveMainMenus) s_hiddenPrefixes.push_back(std::string(kImmersivePrefix));
			if (!flags.randomMainMenu || !music) s_hiddenPrefixes.push_back("mus_maintheme_");
			else if (!flags.immersiveMainMenus) s_hiddenPrefixes.push_back(std::string(kImmersiveMusicPrefix));
			s_filtering.store(!s_hiddenPrefixes.empty());
			s_randomVideo.store(flags.randomMainMenu && (flags.rotateThemes || flags.rotateBackgrounds || flags.immersiveOnly));
			s_randomMusic.store(flags.randomMainMenu && music);
			for (const auto& prefix : s_hiddenPrefixes) hidden += (hidden.empty() ? "" : ", ") + prefix + "*";
		}
		if (flags.randomMainMenu && !flags.immersiveOnly) SetRandomSettings(flags.rotateThemes || flags.rotateBackgrounds, flags.rotateMusic, flags.rotateThemes);
		else RestoreRandomSettings();
		UpdateMenuConfig();
		logger::info("mainmenu: Random Main Menu {}; boxes: Random Main Menu {}, Immersive Main Menus {}, Immersive Main Menus only {}; rotate themes {}, backgrounds {}, music {}; names hidden from Random Main Menu: {}; lists: {} themes, {} backgrounds, {} music; menu calls to Random Main Menu: video {}, music {}",
			s_pluginsLoaded.load() ? (s_loaded.load() ? "loaded" : s_failed.load() ? "on disk but not loaded" : "not installed") : (s_fileOnDisk ? "on disk" : "not installed"),
			flags.randomMainMenu, flags.immersiveMainMenus, flags.immersiveOnly, flags.rotateThemes, flags.rotateBackgrounds, flags.rotateMusic,
			hidden.empty() ? "none" : hidden,
			MainMenu::ListCount(MainMenu::List::Themes), MainMenu::ListCount(MainMenu::List::Backgrounds), MainMenu::ListCount(MainMenu::List::Music),
			s_callsHooked.load() ? (s_randomVideo.load() ? "yes" : "no") : "not hooked", s_callsHooked.load() ? (s_randomMusic.load() ? "yes" : "no") : "not hooked");
	}

	void OnMainMenuReady()
	{
		if (!s_failed.load() || s_messageShown.exchange(true)) return;
		if (auto* manager = RE::MessageMenuManager::GetSingleton(); manager) {
			manager->Create("Art Mixer", "Random Main Menu is installed (Data\\F4SE\\Plugins\\RandomMainMenu.dll) but did not load, so its box on the Main menu page stays off. Check that its version matches your game and that Address Library is installed.", new Notice(), RE::WARNING_TYPES::kDefault, "OK");
		}
		logger::info("mainmenu: Random Main Menu load failure message shown; names hidden so far {}", s_hiddenCount.load());
	}

	bool VideoRotation()
	{
		const auto flags = Effective();
		return flags.rotateThemes || flags.rotateBackgrounds || flags.immersiveOnly;
	}

	int HiddenCount()
	{
		return s_hiddenCount.load();
	}

	std::string EntryVideo(std::string_view a_requested)
	{
		auto name = Settings::Lower(a_requested);
		std::replace(name.begin(), name.end(), '/', '\\');
		if (const auto slash = name.rfind('\\'); slash != std::string::npos) name.erase(0, slash + 1);
		if (!name.starts_with(kEntryPrefix) || !name.ends_with(".bk2")) return {};
		const auto id = name.substr(kEntryPrefix.size(), name.size() - kEntryPrefix.size() - 4);
		for (const auto& file : MainMenu::PackMenuFiles()) {
			if (Settings::Lower(file.id) == id) return file.video;
		}
		return {};
	}
}
