#include "MainMenu.h"

#include "ApplyNow.h"
#include "Distributor.h"
#include "LoadScreens.h"
#include "Packs.h"
#include "Settings.h"

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>

namespace ADT::MainMenu
{
	namespace
	{
		using BinkOpenFn = void* (*)(const char*, std::uint32_t);
		BinkOpenFn s_originalBinkOpen{ nullptr };
		std::mutex s_mutex;
		std::string s_menuVideo;
		std::vector<Option> s_options;              // index 0 is Vanilla; the rest are packs with a menu video, menu music or both
		int s_bgPick{ 0 };                          // index in s_options of the picked background
		int s_musicPick{ 0 };                       // index in s_options of the picked music
		std::atomic<int> s_opens{ 0 };
		std::atomic<bool> s_loopOpen{ false };    // the menu loop video has been opened: the intro is over and the real menu is up
		std::atomic<bool> s_skipStartup{ false };   // autotest: startup videos are refused so the menu comes up at once
		std::atomic<bool> s_entryEnabled{ true };   // the list entry is added to the main menu (the autotest baseline turns it off)
		void AttachToMenu();
		std::vector<std::string> s_optionIds;

		// The executable names the menu theme by one fixed path; the menu plays whatever file that text names when it starts
		// the theme, so a pack's theme is chosen by rewriting the text in place (a shorter path ends at its terminator).
		constexpr std::string_view kThemePath{ "\\Data\\Music\\Special\\MUS_MainTheme.xwm" };
		char* s_themeText{ nullptr };

		// Finds the theme path text in the executable's read-only data; null unless it occurs exactly once.
		char* FindThemeText()
		{
			const auto base = reinterpret_cast<std::uint8_t*>(GetModuleHandleW(nullptr));
			const auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
			const auto nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
			const auto* section = IMAGE_FIRST_SECTION(nt);
			char* found = nullptr;
			int count = 0;
			for (unsigned i = 0; i < nt->FileHeader.NumberOfSections; ++i, ++section) {
				if (std::strncmp(reinterpret_cast<const char*>(section->Name), ".rdata", 6) != 0) continue;
				auto* begin = reinterpret_cast<char*>(base + section->VirtualAddress);
				const std::string_view data(begin, section->Misc.VirtualSize);
				for (auto pos = data.find(kThemePath); pos != std::string_view::npos; pos = data.find(kThemePath, pos + 1)) {
					if (pos + kThemePath.size() < data.size() && data[pos + kThemePath.size()] == '\0' && (pos == 0 || data[pos - 1] == '\0')) {
						found = begin + pos;
						++count;
					}
				}
			}
			if (count != 1) logger::warn("mainmenu: menu theme path found {} times in the executable; theme packs disabled", count);
			return count == 1 ? found : nullptr;
		}

		// Points the menu theme at a file relative to Data\Music, or back at the game's own theme when empty.
		void SetTheme(const std::string& a_music)
		{
			if (!s_themeText) return;
			const auto text = a_music.empty() ? std::string(kThemePath) : "\\Data\\Music\\" + a_music;
			if (text.size() > kThemePath.size()) {
				logger::warn("mainmenu: theme path '{}' is longer than the game's own; the vanilla theme stays", text);
				return;
			}
			DWORD old = 0;
			if (!VirtualProtect(s_themeText, kThemePath.size() + 1, PAGE_READWRITE, &old)) return;
			std::memset(s_themeText, 0, kThemePath.size() + 1);
			std::memcpy(s_themeText, text.data(), text.size());
			VirtualProtect(s_themeText, kThemePath.size() + 1, old, &old);
			logger::info("mainmenu: menu theme -> '{}'", text);
		}

		// The three lists of the MENU ART selector.
		enum class List
		{
			Themes,
			Backgrounds,
			Music
		};

		// Indices into s_options a list shows, Vanilla first: themes have a video and music, backgrounds a video, music a theme (caller holds s_mutex).
		[[nodiscard]] std::vector<int> ListEntries(List a_list)
		{
			std::vector<int> out;
			for (int i = 0; i < static_cast<int>(s_options.size()); ++i) {
				const bool video = !s_options[i].video.empty();
				const bool music = !s_options[i].music.empty();
				const bool shown = a_list == List::Themes ? video && music : a_list == List::Backgrounds ? video : music;
				if (i == 0 || shown) out.push_back(i);
			}
			return out;
		}

		// Points the menu video and the menu music at the current picks (caller holds s_mutex).
		void ApplyPicks()
		{
			const auto count = static_cast<int>(s_options.size());
			s_menuVideo = s_bgPick > 0 && s_bgPick < count ? s_options[s_bgPick].video : count > 0 ? s_options[0].video : std::string();
			SetTheme(s_musicPick > 0 && s_musicPick < count ? s_options[s_musicPick].music : count > 0 ? s_options[0].music : std::string());
		}

		// The pack id of an option, "" for Vanilla (caller holds s_mutex).
		[[nodiscard]] std::string OptionId(int a_index)
		{
			return a_index >= 0 && a_index < static_cast<int>(s_optionIds.size()) ? s_optionIds[a_index] : std::string();
		}

		// The chosen pack id lives in the plugin's own state file, since the main menu runs no Papyrus.
		[[nodiscard]] std::filesystem::path StatePath()
		{
			return std::filesystem::path("Data") / "ADT" / "state.json";
		}

		void SaveState()
		{
			try {
				nlohmann::json json;
				std::ifstream in(StatePath());
				if (in) { try { json = nlohmann::json::parse(in); } catch (...) { json = nlohmann::json::object(); } }
				if (!json.is_object()) json = nlohmann::json::object();
				json["menuVideo"] = OptionId(s_bgPick);
				json["menuMusic"] = OptionId(s_musicPick);
				std::filesystem::create_directories(StatePath().parent_path());
				std::ofstream out(StatePath());
				out << json.dump(1);
			} catch (const std::exception& e) {
				logger::warn("mainmenu: cannot save state: {}", e.what());
			}
		}

		// The saved background pack id and, when saved, the music pack id; a state from before separate music has no music id.
		[[nodiscard]] std::pair<std::string, std::optional<std::string>> LoadStatePicks()
		{
			try {
				std::ifstream in(StatePath());
				if (!in) return {};
				const auto json = nlohmann::json::parse(in);
				const auto video = json.find("menuVideo");
				const auto music = json.find("menuMusic");
				return { video != json.end() && video->is_string() ? video->get<std::string>() : std::string(), music != json.end() && music->is_string() ? std::optional<std::string>(music->get<std::string>()) : std::nullopt };
			} catch (...) {
				return {};
			}
		}

		void* HookedBinkOpen(const char* a_name, std::uint32_t a_flags)
		{
			std::string name = a_name ? a_name : "";
			const auto lowered = Settings::Lower(name);
			std::string redirect;
			if (lowered.ends_with("mainmenuloop.bk2")) {
				std::scoped_lock lock(s_mutex);
				redirect = s_menuVideo;
			}
			if (s_opens.fetch_add(1) < 20) logger::info("mainmenu: BinkOpen('{}', {:#x}){}", name, a_flags, redirect.empty() ? "" : " -> " + redirect);
			if (s_skipStartup.load() && !lowered.ends_with("mainmenuloop.bk2")) {
				logger::info("mainmenu: startup video '{}' skipped for the autotest", name);
				return nullptr;
			}
			auto* result = s_originalBinkOpen(redirect.empty() ? a_name : redirect.c_str(), a_flags);
			if (!result && !redirect.empty()) {
				logger::warn("mainmenu: '{}' failed to open, falling back to '{}'", redirect, name);
				result = s_originalBinkOpen(a_name, a_flags);
			}
			if (lowered.ends_with("mainmenuloop.bk2")) {
				// The loop starts when the intro videos are done, so the entry goes into the real menu only.
				s_loopOpen.store(true);
				F4SE::GetTaskInterface()->AddTask([] {
					auto* ui = RE::UI::GetSingleton();
					if (ui && ui->GetMenuOpen("MainMenu")) AttachToMenu();
					if (ui && ui->GetMenuOpen("MainMenu")) ApplyNow::OnMainMenuReady();
				});
			}
			return result;
		}

		// Finds the import address table slot of a function in the game executable and replaces it.
		bool HookImport(const char* a_dll, const char* a_function, void* a_replacement, void** a_original)
		{
			const auto base = reinterpret_cast<std::uint8_t*>(GetModuleHandleW(nullptr));
			const auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
			const auto nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
			const auto& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
			if (!dir.VirtualAddress) return false;
			auto* desc = reinterpret_cast<const IMAGE_IMPORT_DESCRIPTOR*>(base + dir.VirtualAddress);
			for (; desc->Name; ++desc) {
				const auto* dll = reinterpret_cast<const char*>(base + desc->Name);
				if (_stricmp(dll, a_dll) != 0) continue;
				auto* names = reinterpret_cast<const IMAGE_THUNK_DATA64*>(base + desc->OriginalFirstThunk);
				auto* thunks = reinterpret_cast<IMAGE_THUNK_DATA64*>(base + desc->FirstThunk);
				for (; names->u1.AddressOfData; ++names, ++thunks) {
					if (names->u1.Ordinal & IMAGE_ORDINAL_FLAG64) continue;
					const auto* byName = reinterpret_cast<const IMAGE_IMPORT_BY_NAME*>(base + names->u1.AddressOfData);
					if (strcmp(byName->Name, a_function) != 0) continue;
					DWORD old = 0;
					if (!VirtualProtect(&thunks->u1.Function, sizeof(thunks->u1.Function), PAGE_READWRITE, &old)) return false;
					*a_original = reinterpret_cast<void*>(thunks->u1.Function);
					thunks->u1.Function = reinterpret_cast<ULONGLONG>(a_replacement);
					VirtualProtect(&thunks->u1.Function, sizeof(thunks->u1.Function), old, &old);
					return true;
				}
			}
			return false;
		}

		// The entry this plugin adds to the main menu list; the menu's own press handler ignores an index it does not know.
		constexpr int kEntryIndex = 99;
		constexpr auto kListPath = "root1.Menu_mc.MainPanel_mc.List_mc";
		constexpr auto kCodeObjPath = "root1.Menu_mc.BGSCodeObj";
		RE::Scaleform::GFx::Movie* s_listenedMovie{ nullptr };  // the MainMenu movie whose list carries this plugin's listeners
		std::atomic<int> s_entryLogs{ 0 };
		bool s_selectorOpen{ false };                             // the list shows the options instead of the game's entries (game thread)
		std::vector<RE::Scaleform::GFx::Value> s_savedEntries;  // the game's entries while the selector shows
		std::atomic<bool> s_resetPending{ false };              // a pick asked the engine to re-create the menu; set until the menu closes

		constexpr auto kEntryText = "MENU ART";
		constexpr int kBackIndex = 98;     // the selector's BACK row
		constexpr int kMoreIndex = 97;     // the selector's MORE row, the next page of a long list
		constexpr int kOptionBase = 200;   // a list's option rows: kOptionBase + row on the page
		constexpr int kListBase = 300;     // the list choice rows: kListBase + list

		// The main menu's list object, or an undefined value when the menu or its list is not up.
		[[nodiscard]] RE::Scaleform::GFx::Value MenuList(RE::Scaleform::GFx::Movie** a_movie = nullptr)
		{
			RE::Scaleform::GFx::Value list;
			auto* ui = RE::UI::GetSingleton();
			auto main = ui ? ui->GetMenu("MainMenu") : nullptr;
			if (!main || !main->uiMovie) return list;
			if (a_movie) *a_movie = main->uiMovie.get();
			if (!main->uiMovie->GetVariable(&list, kListPath) || !list.IsObject()) return RE::Scaleform::GFx::Value();
			return list;
		}

		// One list entry as the forEach scan saw it.
		struct ScannedEntry
		{
			std::size_t position{ 0 };
			std::string text;
			int index{ -1 };
			RE::Scaleform::GFx::Value object;
		};
		std::vector<ScannedEntry> s_scanned;

		// Receives (item, position, array) from Array.forEach and records the item.
		class ScanHandler : public RE::Scaleform::GFx::FunctionHandler
		{
		public:
			void Call(const Params& a_params) override
			{
				if (a_params.argCount < 2 || !a_params.args[0].IsObject()) return;
				ScannedEntry item;
				const auto& at = a_params.args[1];
				item.position = static_cast<std::size_t>(at.IsInt() ? at.GetInt() : at.IsUInt() ? static_cast<int>(at.GetUInt()) : at.IsNumber() ? static_cast<int>(at.GetNumber()) : 0);
				RE::Scaleform::GFx::Value text, index;
				if (a_params.args[0].GetMember("text", &text) && text.IsString()) item.text = text.GetString();
				if (a_params.args[0].GetMember("index", &index)) item.index = index.IsInt() ? index.GetInt() : index.IsUInt() ? static_cast<int>(index.GetUInt()) : index.IsNumber() ? static_cast<int>(index.GetNumber()) : -1;
				item.object = a_params.args[0];
				s_scanned.push_back(std::move(item));
			}
		};
		ScanHandler* s_scanHandler{ nullptr };

		// The list's entries in order, read through Array.forEach (CommonLib wraps no element accessor).
		[[nodiscard]] std::vector<ScannedEntry> ScanEntries(RE::Scaleform::GFx::Movie* a_movie, RE::Scaleform::GFx::Value& a_entries)
		{
			s_scanned.clear();
			if (!s_scanHandler) s_scanHandler = new ScanHandler();
			RE::Scaleform::GFx::Value fn;
			a_movie->CreateFunction(&fn, s_scanHandler, nullptr);
			a_entries.Invoke("forEach", nullptr, &fn, 1);
			return std::move(s_scanned);
		}

		// Puts this plugin's entry into the list before $CREW (else before $QUIT, else last) unless it is already there, and keeps its label current.
		void EnsureEntry()
		{
			RE::Scaleform::GFx::Movie* movie = nullptr;
			auto list = MenuList(&movie);
			RE::Scaleform::GFx::Value entries;
			if (!list.IsObject() || !list.GetMember("entryList", &entries) || !entries.IsArray()) return;
			if (s_selectorOpen) return;
			const std::string label = kEntryText;
			auto scanned = ScanEntries(movie, entries);
			const auto count = scanned.size();
			std::size_t crew = count, quit = count;
			for (auto& item : scanned) {
				if (item.index == kEntryIndex) {
					if (item.text != label) {
						item.object.SetMember("text", RE::Scaleform::GFx::Value(label.c_str()));
						list.Invoke("InvalidateData");
					}
					return;
				}
				if (item.text == "$CREW") crew = item.position;
				if (item.text == "$QUIT") quit = item.position;
			}
			const auto position = crew < count ? crew : quit;
			RE::Scaleform::GFx::Value entry;
			movie->CreateObject(&entry);
			entry.SetMember("text", RE::Scaleform::GFx::Value(label.c_str()));
			entry.SetMember("index", RE::Scaleform::GFx::Value(kEntryIndex));
			RE::Scaleform::GFx::Value args[3]{ RE::Scaleform::GFx::Value(static_cast<std::int32_t>(position)), RE::Scaleform::GFx::Value(0), entry };
			const bool spliced = entries.Invoke("splice", nullptr, args, 3);
			const bool refreshed = list.Invoke("InvalidateData");
			if (s_entryLogs.fetch_add(1) < 20) logger::info("mainmenu: list entry '{}' inserted at position {} of {} ({}); splice {}, refresh {}", label, position + 1, count + 1, crew < count ? "before $CREW" : quit < count ? "before $QUIT" : "last", spliced, refreshed);
		}
		void PlayMenuSound(const char* a_function)
		{
			auto* ui = RE::UI::GetSingleton();
			auto main = ui ? ui->GetMenu("MainMenu") : nullptr;
			RE::Scaleform::GFx::Value codeObj;
			if (main && main->uiMovie && main->uiMovie->GetVariable(&codeObj, kCodeObjPath) && codeObj.IsObject()) codeObj.Invoke(a_function);
		}

		// Replaces the list's entries with the given rows (text, index) and shows the first.
		void ShowRows(RE::Scaleform::GFx::Value& a_list, RE::Scaleform::GFx::Value& a_entries, std::size_t a_count, const std::vector<RE::Scaleform::GFx::Value>& a_rows)
		{
			RE::Scaleform::GFx::Value cut[2]{ RE::Scaleform::GFx::Value(0), RE::Scaleform::GFx::Value(static_cast<std::int32_t>(a_count)) };
			a_entries.Invoke("splice", nullptr, cut, 2);
			for (const auto& row : a_rows) a_entries.PushBack(row);
			a_list.SetMember("selectedIndex", RE::Scaleform::GFx::Value(0));
			a_list.Invoke("InvalidateData");
		}

		[[nodiscard]] double NumberOf(RE::Scaleform::GFx::Value& a_object, const char* a_member, double a_fallback);

		// The drawn height of a row clip's text, or 0 when the clip or its field is out of reach; the entry field shrinks text that is too long.
		[[nodiscard]] double RowTextHeight(RE::Scaleform::GFx::Value& a_list, std::uint32_t a_row)
		{
			RE::Scaleform::GFx::Value at(a_row), clip, field;
			if (!a_list.Invoke("GetClipByIndex", &clip, &at, 1) || !clip.IsObject() || !clip.GetMember("textField", &field) || !field.IsObject()) return 0.0;
			return NumberOf(field, "textHeight", 0.0);
		}

		// Cuts each selector row's name with "..." ahead of its suffix until the row draws at the height of the BACK row instead of shrinking.
		void FitSelectorRows(RE::Scaleform::GFx::Value& a_list, const std::vector<RE::Scaleform::GFx::Value>& a_rows, const std::vector<std::string>& a_names, const std::vector<std::string>& a_suffixes)
		{
			const auto full = RowTextHeight(a_list, static_cast<std::uint32_t>(a_rows.size() - 1));
			if (full <= 0.0) return;
			for (std::size_t i = 0; i + 1 < a_rows.size(); ++i) {
				std::string name = a_names[i];
				bool cut = false;
				for (int tries = 0; tries < 60 && name.size() > 3 && RowTextHeight(a_list, static_cast<std::uint32_t>(i)) < full - 0.5; ++tries) {
					name.pop_back();
					while (!name.empty() && name.back() == ' ') name.pop_back();
					cut = true;
					const_cast<RE::Scaleform::GFx::Value&>(a_rows[i]).SetMember("text", RE::Scaleform::GFx::Value((name + "..." + a_suffixes[i]).c_str()));
					a_list.Invoke("InvalidateData");
				}
				if (cut && s_entryLogs.load() <= 20) logger::info("mainmenu: selector row {} cut to '{}' (text height {:.1f}, full {:.1f})", i + 1, name + "..." + a_suffixes[i], RowTextHeight(a_list, static_cast<std::uint32_t>(i)), full);
			}
		}

		[[nodiscard]] std::pair<int, int> RowClips(RE::Scaleform::GFx::Value& a_list);
		void Pick(int a_background, int a_music);

		bool s_inList{ false };     // the selector shows a list's options instead of the list choice (game thread)
		List s_list{ List::Themes };  // the list the selector shows while s_inList (game thread)
		int s_page{ 0 };            // the page of that list on show (game thread)
		int s_pages{ 1 };           // the number of pages of that list (game thread)
		int s_pageStart{ 0 };       // the list position of the page's first row (game thread)
		int s_pageCount{ 0 };       // the number of option rows on the page (game thread)

		// Replaces whatever the list shows with the given rows, cutting names that do not fit; a_suffixes are kept whole.
		void ShowSelectorRows(const std::vector<std::string>& a_names, const std::vector<std::string>& a_suffixes, const std::vector<std::int32_t>& a_indices)
		{
			RE::Scaleform::GFx::Movie* movie = nullptr;
			auto list = MenuList(&movie);
			RE::Scaleform::GFx::Value entries;
			if (!list.IsObject() || !list.GetMember("entryList", &entries) || !entries.IsArray()) return;
			const auto shown = ScanEntries(movie, entries).size();
			std::vector<RE::Scaleform::GFx::Value> rows;
			for (std::size_t i = 0; i < a_names.size(); ++i) {
				RE::Scaleform::GFx::Value row;
				movie->CreateObject(&row);
				row.SetMember("text", RE::Scaleform::GFx::Value((a_names[i] + a_suffixes[i]).c_str()));
				row.SetMember("index", RE::Scaleform::GFx::Value(a_indices[i]));
				rows.push_back(row);
			}
			s_selectorOpen = true;
			ShowRows(list, entries, shown, rows);
			FitSelectorRows(list, rows, a_names, a_suffixes);
		}

		// Shows the list choice: THEMES, BACKGROUNDS, MUSIC and BACK.
		void ShowChoice()
		{
			s_inList = false;
			ShowSelectorRows({ "THEMES", "BACKGROUNDS", "MUSIC", "BACK" }, { "", "", "", "" }, { kListBase + static_cast<std::int32_t>(List::Themes), kListBase + static_cast<std::int32_t>(List::Backgrounds), kListBase + static_cast<std::int32_t>(List::Music), kBackIndex });
			logger::info("mainmenu: selector shows the list choice");
		}

		// Shows one page of a list: its options with the current pick marked, MORE when the list needs more pages, and BACK.
		void ShowList(List a_list, int a_page)
		{
			RE::Scaleform::GFx::Movie* movie = nullptr;
			auto menuList = MenuList(&movie);
			if (!menuList.IsObject()) return;
			const int clips = std::max(3, RowClips(menuList).first);
			std::vector<std::string> names, suffixes;
			std::vector<std::int32_t> indices;
			{
				std::scoped_lock lock(s_mutex);
				const auto entries = ListEntries(a_list);
				const int count = static_cast<int>(entries.size());
				const int perPage = std::max(1, count + 1 <= clips ? count : clips - 2);
				s_pages = std::max(1, (count + perPage - 1) / perPage);
				s_page = ((a_page % s_pages) + s_pages) % s_pages;
				s_pageStart = s_page * perPage;
				s_pageCount = std::min(perPage, count - s_pageStart);
				for (int row = 0; row < s_pageCount; ++row) {
					const int option = entries[s_pageStart + row];
					std::string name = s_options[option].name;
					for (auto& c : name) c = static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
					const bool marked = a_list == List::Themes ? option == s_bgPick && option == s_musicPick : a_list == List::Backgrounds ? option == s_bgPick : option == s_musicPick;
					names.push_back(name);
					suffixes.push_back(marked ? " *" : "");
					indices.push_back(kOptionBase + row);
				}
			}
			if (s_pages > 1) {
				names.push_back("MORE");
				suffixes.push_back(fmt::format(" ({}/{})", s_page + 1, s_pages));
				indices.push_back(kMoreIndex);
			}
			names.push_back("BACK");
			suffixes.push_back("");
			indices.push_back(kBackIndex);
			s_inList = true;
			s_list = a_list;
			ShowSelectorRows(names, suffixes, indices);
			logger::info("mainmenu: selector shows list {} page {} of {} ({} options, {} row clips)", static_cast<int>(a_list), s_page + 1, s_pages, s_pageCount, clips);
		}

		// Swaps the game's entries for the list choice; the game's entries are kept for BACK.
		void OpenSelector()
		{
			RE::Scaleform::GFx::Movie* movie = nullptr;
			auto list = MenuList(&movie);
			RE::Scaleform::GFx::Value entries;
			if (!list.IsObject() || !list.GetMember("entryList", &entries) || !entries.IsArray()) return;
			auto scanned = ScanEntries(movie, entries);
			s_savedEntries.clear();
			for (auto& item : scanned) s_savedEntries.push_back(item.object);
			logger::info("mainmenu: selector opened in place of {} entries", scanned.size());
			ShowChoice();
		}

		// Puts the game's entries back.
		void CloseSelector()
		{
			RE::Scaleform::GFx::Movie* movie = nullptr;
			auto list = MenuList(&movie);
			RE::Scaleform::GFx::Value entries;
			s_selectorOpen = false;
			s_inList = false;
			if (!list.IsObject() || !list.GetMember("entryList", &entries) || !entries.IsArray()) return;
			// Leaves the list as it is when no game entries are kept.
			if (s_savedEntries.empty()) {
				logger::info("mainmenu: selector close skipped; no game entries kept to restore");
				return;
			}
			const auto rows = ScanEntries(movie, entries).size();
			ShowRows(list, entries, rows, s_savedEntries);
			logger::info("mainmenu: selector closed; {} entries restored", s_savedEntries.size());
			s_savedEntries.clear();
		}

		// Acts on a pressed row of this plugin's: the entry opens the selector, a list row opens that list, MORE turns the page,
		// BACK goes up one level, an option row picks that option.
		void OnRowPressed(int a_index)
		{
			// Ignores every press while the engine re-creates the menu for a pick.
			if (s_resetPending.load()) {
				logger::info("mainmenu: row {} pressed while the menu reloads for a pick; ignored", a_index);
				return;
			}
			if (a_index == kEntryIndex) {
				PlayMenuSound("PlayOKSound");
				OpenSelector();
			} else if (a_index == kBackIndex) {
				PlayMenuSound("PlayCancelSound");
				if (s_inList) {
					ShowChoice();
				} else {
					CloseSelector();
				}
			} else if (a_index >= kListBase && a_index <= kListBase + static_cast<int>(List::Music)) {
				PlayMenuSound("PlayOKSound");
				ShowList(static_cast<List>(a_index - kListBase), 0);
			} else if (a_index == kMoreIndex) {
				PlayMenuSound("PlayOKSound");
				ShowList(s_list, s_page + 1);
			} else if (a_index >= kOptionBase && a_index < kOptionBase + s_pageCount && s_inList) {
				int background = 0, music = 0;
				{
					std::scoped_lock lock(s_mutex);
					const auto entries = ListEntries(s_list);
					const int position = s_pageStart + (a_index - kOptionBase);
					if (position >= static_cast<int>(entries.size())) return;
					const int option = entries[position];
					background = s_list == List::Music ? s_bgPick : option;
					music = s_list == List::Backgrounds ? s_musicPick : option;
					if (background == s_bgPick && music == s_musicPick) background = -1;
				}
				PlayMenuSound("PlayOKSound");
				if (background < 0) {
					CloseSelector();
					return;
				}
				logger::info("mainmenu: selector row pressed -> list {}, row {}", static_cast<int>(s_list), a_index - kOptionBase);
				s_selectorOpen = false;
				s_inList = false;
				s_savedEntries.clear();
				Pick(background, music);
			}
		}

		// Receives the list's events: userData 0 is listItemsCreated (the list was rebuilt), 1 is itemPress.
		class ListHandler : public RE::Scaleform::GFx::FunctionHandler
		{
		public:
			void Call(const Params& a_params) override
			{
				const auto id = reinterpret_cast<std::uintptr_t>(a_params.userData);
				if (id == 0) {
					EnsureEntry();
					return;
				}
				auto list = MenuList();
				RE::Scaleform::GFx::Value entry, index;
				if (!list.IsObject() || !list.GetMember("selectedEntry", &entry) || !entry.IsObject() || !entry.GetMember("index", &index)) return;
				const int pressed = index.IsInt() ? index.GetInt() : index.IsNumber() ? static_cast<int>(index.GetNumber()) : -1;
				OnRowPressed(pressed);
			}
		};
		ListHandler* s_listHandler{ nullptr };

		// The number of row clips the list draws, and how many are visible, through the list's own GetClipByIndex.
		[[nodiscard]] std::pair<int, int> RowClips(RE::Scaleform::GFx::Value& a_list)
		{
			int count = 0, visible = 0;
			for (; count < 64; ++count) {
				RE::Scaleform::GFx::Value at(static_cast<std::uint32_t>(count)), clip, shown;
				if (!a_list.Invoke("GetClipByIndex", &clip, &at, 1) || !clip.IsObject()) break;
				if (clip.GetMember("visible", &shown) && shown.IsBoolean() && shown.GetBoolean()) ++visible;
			}
			return { count, visible };
		}
		// A number member as double, or the fallback.
		[[nodiscard]] double NumberOf(RE::Scaleform::GFx::Value& a_object, const char* a_member, double a_fallback)
		{
			RE::Scaleform::GFx::Value v;
			if (!a_object.GetMember(a_member, &v)) return a_fallback;
			return v.IsNumber() ? v.GetNumber() : v.IsInt() ? v.GetInt() : v.IsUInt() ? v.GetUInt() : a_fallback;
		}

		// Gives the list one row more than the game built it with, so the added entry pushes nothing out of view. The list's own
		// SetNumListItems is protected, so the clip is built here as that method builds them, and the rows are spaced to fit the border.
		void GrowList(RE::Scaleform::GFx::Movie* a_movie, RE::Scaleform::GFx::Value& a_list)
		{
			const int rows = static_cast<int>(NumberOf(a_list, "numListItems", 0));
			const auto [clips, visible] = RowClips(a_list);
			if (rows <= 0 || clips != rows) {
				logger::info("mainmenu: list rows {} with {} clips; left as is", rows, clips);
				return;
			}
			RE::Scaleform::GFx::Value holder, first, clip, at(static_cast<std::uint32_t>(0));
			if (!a_list.Invoke("GetClipByIndex", &first, &at, 1) || !first.IsObject() || !first.GetMember("parent", &holder) || !holder.IsObject()) {
				logger::warn("mainmenu: the list's row holder or first row is out of reach; the list keeps {} rows", rows);
				return;
			}
			a_movie->CreateObject(&clip, "MainMenuListEntry");
			if (!clip.IsObject()) {
				logger::warn("mainmenu: no MainMenuListEntry could be made; the list keeps {} rows", rows);
				return;
			}
			RE::Scaleform::GFx::Value platform, over, press;
			if (first.GetMember("iPlatform", &platform)) clip.SetMember("iPlatform", platform);
			clip.SetMember("clipIndex", RE::Scaleform::GFx::Value(static_cast<std::uint32_t>(rows)));
			bool wired = a_list.GetMember("onEntryRollover", &over) && a_list.GetMember("onEntryPress", &press);
			if (wired) {
				RE::Scaleform::GFx::Value hover[2]{ RE::Scaleform::GFx::Value("mouseOver"), over };
				RE::Scaleform::GFx::Value click[2]{ RE::Scaleform::GFx::Value("click"), press };
				wired = clip.Invoke("addEventListener", nullptr, hover, 2) && clip.Invoke("addEventListener", nullptr, click, 2);
			}
			const bool added = holder.Invoke("addChild", nullptr, &clip, 1);
			a_list.SetMember("numListItems", RE::Scaleform::GFx::Value(static_cast<std::uint32_t>(rows + 1)));
			// Rows are stacked clip height plus spacing from the border's top and shown while they fit its height.
			RE::Scaleform::GFx::Value border;
			const double clipHeight = NumberOf(first, "height", 0.0);
			const double spacing = NumberOf(a_list, "verticalSpacing", 0.0);
			double borderHeight = 0.0;
			if (a_list.GetMember("border", &border) && border.IsObject()) borderHeight = NumberOf(border, "height", 0.0);
			const double needed = (rows + 1) * clipHeight + rows * spacing;
			double newSpacing = spacing;
			if (clipHeight > 0.0 && borderHeight > 0.0 && needed > borderHeight) {
				newSpacing = (borderHeight - (rows + 1) * clipHeight) / rows;
				if (newSpacing >= 0.0) {
					a_list.SetMember("verticalSpacing", RE::Scaleform::GFx::Value(newSpacing));
				} else {
					newSpacing = spacing;
					border.SetMember("height", RE::Scaleform::GFx::Value(needed));
					borderHeight = needed;
				}
			}
			logger::info("mainmenu: list rows {} -> {} (clip added {}, handlers wired {}); clip height {:.1f}, spacing {:.1f} -> {:.1f}, border height {:.1f}", rows, rows + 1, added, wired, clipHeight, spacing, newSpacing, borderHeight);
		}
		// Hooks the list of the current MainMenu movie once and inserts the entry; a re-created menu is a new movie and gets hooked again.
		void AttachToMenu()
		{
			if (!s_entryEnabled.load()) return;
			RE::Scaleform::GFx::Movie* movie = nullptr;
			auto list = MenuList(&movie);
			if (!list.IsObject() || !movie) return;
			if (s_listenedMovie != movie) {
				if (!s_listHandler) s_listHandler = new ListHandler();
				RE::Scaleform::GFx::Value onCreated, onPress;
				movie->CreateFunction(&onCreated, s_listHandler, reinterpret_cast<void*>(0));
				movie->CreateFunction(&onPress, s_listHandler, reinterpret_cast<void*>(1));
				RE::Scaleform::GFx::Value created[2]{ RE::Scaleform::GFx::Value("BSScrollingList::listItemsCreated"), onCreated };
				RE::Scaleform::GFx::Value press[2]{ RE::Scaleform::GFx::Value("BSScrollingList::itemPress"), onPress };
				const bool a = list.Invoke("addEventListener", nullptr, created, 2);
				const bool b = list.Invoke("addEventListener", nullptr, press, 2);
				s_listenedMovie = movie;
				s_selectorOpen = false;
				s_inList = false;
				s_savedEntries.clear();
				logger::info("mainmenu: list listeners attached (items created {}, press {})", a, b);
				GrowList(movie, list);
			}
			EnsureEntry();
			const auto [clips, visible] = RowClips(list);
			if (s_entryLogs.load() <= 20) logger::info("mainmenu: list draws {} row clips, {} visible", clips, visible);
		}

		class MenuSink : public RE::BSTEventSink<RE::MenuOpenCloseEvent>
		{
		public:
			RE::BSEventNotifyControl ProcessEvent(const RE::MenuOpenCloseEvent& a_event, RE::BSTEventSource<RE::MenuOpenCloseEvent>*) override
			{
				const auto name = a_event.menuName.c_str() ? a_event.menuName.c_str() : "";
				if (std::string_view(name) == "MainMenu") {
					logger::info("mainmenu: MainMenu {}", a_event.opening ? "opened" : "closed");
					if (a_event.opening) {
						F4SE::GetTaskInterface()->AddTask([] { AttachToMenu(); });
					} else {
						s_loopOpen.store(false);
						s_resetPending.store(false);
						s_listenedMovie = nullptr;
						s_selectorOpen = false;
						s_inList = false;
						s_savedEntries.clear();
					}
				} else if (std::string_view(name) == "LoadingMenu") {
					// Logs how long each loading screen stays open.
					static std::chrono::steady_clock::time_point s_loadingSince{};
					if (a_event.opening) {
						s_loadingSince = std::chrono::steady_clock::now();
					} else if (s_loadingSince != std::chrono::steady_clock::time_point{}) {
						logger::info("loading screen: {:.1f} s", std::chrono::duration<double>(std::chrono::steady_clock::now() - s_loadingSince).count());
						s_loadingSince = {};
						LoadScreens::OnLoadingMenuClosed();
					}
				}
				return RE::BSEventNotifyControl::kContinue;
			}
		};
		MenuSink s_sink;

		// Sets the background and music picks, stores them and re-creates the main menu so they show.
		void Pick(int a_background, int a_music)
		{
			{
				std::scoped_lock lock(s_mutex);
				const int count = static_cast<int>(s_options.size());
				if (a_background < 0 || a_background >= count || a_music < 0 || a_music >= count) return;
				s_bgPick = a_background;
				s_musicPick = a_music;
				ApplyPicks();
				logger::info("mainmenu: picked background '{}' and music '{}' -> video '{}', music '{}'", s_options[s_bgPick].name, s_options[s_musicPick].name, s_menuVideo, s_options[s_musicPick].music);
				SaveState();
			}
			// The main menu opens its video when it is created. Hiding and showing the menu re-creates it with a dead list, so the
			// engine's own return-to-main-menu sequence is requested instead, the path the game takes when leaving a save.
			// That sequence ends a running game, so from inside one the picks are only stored for the next menu.
			auto* ui = RE::UI::GetSingleton();
			if (!ui || !ui->GetMenuOpen("MainMenu")) {
				logger::info("mainmenu: picks stored; the menu picks them up when it is next created");
				return;
			}
			s_resetPending.store(true);
			F4SE::GetTaskInterface()->AddTask([] {
				if (auto* game = RE::Main::GetSingleton(); game) {
					game->resetGame = true;
					logger::info("mainmenu: game reset requested for the menu change");
				}
			});
		}
	}

	bool Install()
	{
		void* original = nullptr;
		if (!HookImport("bink2w64.dll", "BinkOpen", reinterpret_cast<void*>(&HookedBinkOpen), &original)) {
			logger::error("mainmenu: BinkOpen import not found; main menu video packs disabled");
			return false;
		}
		s_originalBinkOpen = reinterpret_cast<BinkOpenFn>(original);
		s_themeText = FindThemeText();
		logger::info("mainmenu: BinkOpen hooked (original {})", original);
		return true;
	}

	void OnGameDataReady()
	{
		auto* ui = RE::UI::GetSingleton();
		if (!ui) {
			logger::error("mainmenu: UI singleton unavailable; the menu entry is left out");
			return;
		}
		ui->RegisterSink<RE::MenuOpenCloseEvent>(&s_sink);
		if (!s_entryEnabled.load()) {
			logger::info("mainmenu: menu entry disabled by the autotest");
			return;
		}
		const bool mainOpen = ui->GetMenuOpen("MainMenu");
		logger::info("mainmenu: menu entry ready; MainMenu already open {}, loop video open {}", mainOpen, s_loopOpen.load());
		if (mainOpen) AttachToMenu();
	}

	void SetSkipStartupVideos(bool a_skip)
	{
		s_skipStartup.store(a_skip);
	}

	void SetEntryEnabled(bool a_enabled)
	{
		s_entryEnabled.store(a_enabled);
	}

	bool AtMainMenu()
	{
		auto* ui = RE::UI::GetSingleton();
		return s_loopOpen.load() && ui && ui->GetMenuOpen("MainMenu");
	}

	void SetOptions(std::vector<Option> a_options, int a_selected)
	{
		std::scoped_lock lock(s_mutex);
		s_options = std::move(a_options);
		s_optionIds.assign(s_options.size(), "");
		const bool valid = a_selected >= 0 && a_selected < static_cast<int>(s_options.size());
		s_bgPick = valid ? a_selected : 0;
		s_musicPick = valid ? a_selected : 0;
		ApplyPicks();
	}

	std::string EntryState()
	{
		RE::Scaleform::GFx::Movie* movie = nullptr;
		auto list = MenuList(&movie);
		RE::Scaleform::GFx::Value entries;
		if (!list.IsObject() || !list.GetMember("entryList", &entries) || !entries.IsArray()) return "no main menu list";
		const auto scanned = ScanEntries(movie, entries);
		const auto [clips, visible] = RowClips(list);
		std::string texts;
		for (const auto& item : scanned) texts += (texts.empty() ? "" : " | ") + item.text;
		return fmt::format("{} entries ({} row clips, {} visible): {}", scanned.size(), clips, visible, texts);
	}

	void PressEntry()
	{
		OnRowPressed(kEntryIndex);
	}

	void PressOption(int a_option)
	{
		if (!s_inList || s_list != List::Backgrounds) ShowList(List::Backgrounds, 0);
		while (a_option >= s_pageStart + s_pageCount && s_page + 1 < s_pages) ShowList(List::Backgrounds, s_page + 1);
		OnRowPressed(kOptionBase + a_option - s_pageStart);
	}

	void LoadFromPacks()
	{
		// The Vanilla option plays the vanilla pack's copies of the game's own files when the converter made them.
		const auto* vanilla = Packs::Vanilla();
		std::vector<Option> options{ { "Vanilla", vanilla && !vanilla->video.empty() ? "Data\\Video\\" + vanilla->video : std::string(), vanilla ? vanilla->music : std::string() } };
		std::vector<std::string> ids{ "" };
		for (const auto& pack : Packs::All()) {
			if (pack.vanilla || (pack.video.empty() && pack.music.empty())) continue;
			options.push_back({ pack.name, pack.video.empty() ? std::string() : "Data\\Video\\" + pack.video, pack.music });
			ids.push_back(pack.id);
		}
		const auto [savedVideo, savedMusic] = LoadStatePicks();
		const auto find = [&](const std::string& a_id) {
			for (std::size_t i = 1; i < ids.size(); ++i) {
				if (ids[i] == a_id) return static_cast<int>(i);
			}
			return 0;
		};
		// A state from before separate music names one pack for both, as the single choice did.
		const int saved = find(savedVideo);
		const int background = saved > 0 && !options[saved].video.empty() ? saved : 0;
		int music = savedMusic ? find(*savedMusic) : saved;
		if (music > 0 && options[music].music.empty()) music = 0;
		std::scoped_lock lock(s_mutex);
		s_options = std::move(options);
		s_optionIds = std::move(ids);
		s_bgPick = background;
		s_musicPick = music;
		ApplyPicks();
		logger::info("mainmenu: {} menu packs ({} themes, {} backgrounds, {} music); background '{}', music '{}'", s_options.size() - 1, ListEntries(List::Themes).size() - 1, ListEntries(List::Backgrounds).size() - 1, ListEntries(List::Music).size() - 1, s_options[s_bgPick].name, s_options[s_musicPick].name);
	}

	std::string RedirectMovie(std::string_view a_url)
	{
		const auto candidates = Packs::MoviesFor(a_url);
		if (candidates.empty()) return {};
		const auto& category = candidates.front().second->category;
		const Packs::Pack* chosen = nullptr;
		if (category == "mainmenu") {
			// Replaces the main menu's movie only with the copy of the pack picked in the MENU ART selector.
			std::scoped_lock lock(s_mutex);
			const auto id = OptionId(s_bgPick);
			for (const auto& [pack, movie] : candidates) {
				if (!pack->vanilla && pack->id == id) return movie->file;
			}
			// Leaves the file the mod manager resolved when the pick ships no main menu movie.
			return {};
		} else {
			// A list page's pick, or the share's pick for this file under the current layout.
			auto file = Settings::Lower(a_url);
			std::replace(file.begin(), file.end(), '/', '\\');
			chosen = Distributor::RollCategory(category, "movie|" + file, [&](const Packs::Pack& a_pack) {
				return std::any_of(candidates.begin(), candidates.end(), [&](const auto& a_candidate) { return a_candidate.first == &a_pack; });
			});
		}
		for (const auto& [pack, movie] : candidates) {
			if (pack == chosen) return movie->file;
		}
		// A pack with no copy of the file shows the vanilla pack's.
		for (const auto& [pack, movie] : candidates) {
			if (pack->vanilla) return movie->file;
		}
		return {};
	}

	void LogMenuState(const char* a_tag)
	{
		auto* ui = RE::UI::GetSingleton();
		auto main = ui ? ui->GetMenu("MainMenu") : nullptr;
		std::string stack;
		bool ignoreKeyboard = false;
		if (auto* controls = RE::ControlMap::GetSingleton(); controls) {
			for (const auto& context : controls->contextPriorityStack) stack += std::to_string(static_cast<int>(context.get())) + " ";
			ignoreKeyboard = controls->ignoreKeyboardMouse;
		}
		std::string menus;
		if (ui) {
			for (const auto& entry : ui->menuStack) {
				if (!entry) continue;
				menus += fmt::format("{}(d{} f{:#x} in{} top{} vis{}) ", entry->menuName.c_str() ? entry->menuName.c_str() : "?", static_cast<int>(entry->depthPriority.get()), entry->menuFlags.underlying(), entry->inputEventHandlingEnabled ? 1 : 0, entry->passesTopMenuTest ? 1 : 0, entry->menuCanBeVisible ? 1 : 0);
			}
		}
		if (main) {
			logger::info("mainmenu: state [{}]: flags {:#x}, inputContext {}, passesTop {}, control stack [{}], ignoreKeyboardMouse {}, menu stack [{}]", a_tag, main->menuFlags.underlying(), static_cast<int>(main->inputContext.get()), main->passesTopMenuTest, stack, ignoreKeyboard, menus);
		} else {
			logger::info("mainmenu: state [{}]: MainMenu not open, control stack [{}]", a_tag, stack);
		}
	}

	std::pair<int, int> CursorPos()
	{
		// The engine's own cursor position in window pixels.
		if (auto* cursor = RE::MenuCursor::GetSingleton(); cursor) return { cursor->cursorPosX, cursor->cursorPosY };
		return { -1, -1 };
	}

	int Selected()
	{
		std::scoped_lock lock(s_mutex);
		const auto entries = ListEntries(List::Backgrounds);
		const auto it = std::find(entries.begin(), entries.end(), s_bgPick);
		return it == entries.end() ? 0 : static_cast<int>(it - entries.begin());
	}

	int OptionCount()
	{
		std::scoped_lock lock(s_mutex);
		return static_cast<int>(ListEntries(List::Backgrounds).size());
	}

	void Select(int a_index)
	{
		int background = -1, music = 0;
		{
			std::scoped_lock lock(s_mutex);
			const auto entries = ListEntries(List::Backgrounds);
			if (a_index < 0 || a_index >= static_cast<int>(entries.size())) return;
			background = entries[a_index];
			music = s_musicPick;
		}
		Pick(background, music);
	}

	bool AnyPicked()
	{
		std::scoped_lock lock(s_mutex);
		return s_bgPick != 0 || s_musicPick != 0;
	}

	void ResetPicks()
	{
		if (AnyPicked()) Pick(0, 0);
	}

}
