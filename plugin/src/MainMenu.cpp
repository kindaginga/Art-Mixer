#include "MainMenu.h"

#include "Packs.h"
#include "Settings.h"

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>

namespace GShuf::MainMenu
{
	namespace
	{
		using BinkOpenFn = void* (*)(const char*, std::uint32_t);
		BinkOpenFn s_originalBinkOpen{ nullptr };
		std::mutex s_mutex;
		std::string s_menuVideo;
		std::vector<Option> s_options;
		int s_selected{ 0 };
		std::atomic<int> s_opens{ 0 };
		std::atomic<int> s_inputLogs{ 0 };
		std::atomic<int> s_messageLogs{ 0 };
		std::atomic<int> s_cursorX{ -1 };
		std::atomic<int> s_cursorY{ -1 };
		std::atomic<int> s_cursorLogs{ 0 };
		std::atomic<bool> s_loopOpen{ false };    // the menu loop video has been opened: the intro is over and the real menu is up
		std::atomic<bool> s_registered{ false };  // the picker menu is registered with the UI
		std::atomic<bool> s_skipStartup{ false };
		std::atomic<bool> s_pickerEnabled{ true };
		std::atomic<bool> s_rebuilding{ false };  // a pick is re-creating MainMenu  // autotest: startup videos are refused so the menu comes up at once
		constexpr auto kPickerMenu = "GShufPicker"sv;
		void ShowPicker(bool a_show);
		std::vector<std::string> s_optionIds;

		// The chosen pack id lives in the plugin's own state file, since the main menu runs no Papyrus.
		[[nodiscard]] std::filesystem::path StatePath()
		{
			return std::filesystem::path("Data") / "GShuf" / "state.json";
		}

		void SaveState()
		{
			try {
				nlohmann::json json;
				std::ifstream in(StatePath());
				if (in) { try { json = nlohmann::json::parse(in); } catch (...) { json = nlohmann::json::object(); } }
				if (!json.is_object()) json = nlohmann::json::object();
				json["menuVideo"] = (s_selected >= 0 && s_selected < static_cast<int>(s_optionIds.size())) ? s_optionIds[s_selected] : "";
				std::filesystem::create_directories(StatePath().parent_path());
				std::ofstream out(StatePath());
				out << json.dump(1);
			} catch (const std::exception& e) {
				logger::warn("mainmenu: cannot save state: {}", e.what());
			}
		}

		[[nodiscard]] std::string LoadStateMenuVideo()
		{
			try {
				std::ifstream in(StatePath());
				if (!in) return {};
				const auto json = nlohmann::json::parse(in);
				const auto it = json.find("menuVideo");
				return it != json.end() && it->is_string() ? it->get<std::string>() : std::string();
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
				// The loop starts when the intro videos are done, so the picker appears over the real menu only.
				s_loopOpen.store(true);
				F4SE::GetTaskInterface()->AddTask([] {
					auto* ui = RE::UI::GetSingleton();
					if (s_registered.load() && ui && ui->GetMenuOpen("MainMenu")) ShowPicker(true);
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

		// The drop-up list drawn over the main menu; its SWF calls BGSCodeObj.onSelect(index).
		class PickerMenu : public RE::GameMenuBase
		{
		public:
			PickerMenu()
			{
				// No cursor flags: the engine routes its mouse events to one cursor-using menu only, and that must stay MainMenu
				// so its list keeps working; the picker takes its own clicks from the cursor position below.
				menuFlags.set(RE::UI_MENU_FLAGS::kDontHideCursorWhenTopmost);
				depthPriority = RE::UI_DEPTH_PRIORITY::kMessage;
				// Copies the render setup of the real main menu so the picker draws in the same layer.
				if (auto* ui = RE::UI::GetSingleton(); ui) {
					if (auto main = ui->GetMenu("MainMenu"); main) {
						customRendererName = main->customRendererName;
						logger::info("mainmenu: MainMenu flags {:#x} depth {} renderer '{}'", main->menuFlags.underlying(), static_cast<int>(main->depthPriority.underlying()), main->customRendererName.c_str() ? main->customRendererName.c_str() : "");
					}
				}
				auto* manager = RE::BSScaleformManager::GetSingleton();
				const bool loaded = manager && manager->LoadMovieEx(*this, "Interface/GShufPicker.swf", "root1");
				logger::info("mainmenu: picker movie loaded {}, menuObj object {}", loaded, menuObj.IsObject());
				// LoadMovieEx leaves the SWF without BGSCodeObj, so the bridge object is built here: this menu is the function handler.
				if (loaded && uiMovie && menuObj.IsObject()) {
					RE::Scaleform::GFx::Value codeObj, onSelect, check;
					uiMovie->CreateObject(&codeObj);
					uiMovie->CreateFunction(&onSelect, this, reinterpret_cast<void*>(0));
					const bool setFn = codeObj.SetMember("onSelect", onSelect);
					const bool setObj = menuObj.SetMember("BGSCodeObj", codeObj);
					const bool readable = menuObj.GetMember("BGSCodeObj", &check) && check.IsObject();
					logger::info("mainmenu: picker BGSCodeObj set {} (onSelect {}), readable {}", setObj, setFn, readable);
				}
				if (loaded) PushOptions();
			}

			void MapCodeObjectFunctions() override
			{
				MapCodeMethodToASFunction("onSelect", 0);
			}

			// Keys and gamepad buttons belong to the main menu underneath; mouse events go to the engine's own handler so clicks reach the movie.
			// The engine cursor position as stage coordinates of the picker movie; false when unknown.
			bool CursorStage(double& a_x, double& a_y) const
			{
				const auto [px, py] = CursorPos();
				RECT client{};
				HWND window = FindWindowW(nullptr, L"Fallout4");
				if (px < 0 || !window || !GetClientRect(window, &client) || client.right <= 0 || client.bottom <= 0) return false;
				a_x = px * 1280.0 / client.right;
				a_y = py * 720.0 / client.bottom;
				return true;
			}

			// True when the engine cursor is over the box or its open list; the SWF does the hit test in stage coordinates.
			bool CursorOverPicker() const
			{
				double x = 0.0, y = 0.0;
				if (!menuObj.IsObject() || !CursorStage(x, y)) return false;
				RE::Scaleform::GFx::Value args[2]{ RE::Scaleform::GFx::Value(x), RE::Scaleform::GFx::Value(y) };
				RE::Scaleform::GFx::Value result;
				return const_cast<RE::Scaleform::GFx::Value&>(menuObj).Invoke("HitTest", &result, args, 2) && result.IsBoolean() && result.GetBoolean();
			}

			void CallWithCursor(const char* a_function)
			{
				double x = 0.0, y = 0.0;
				if (!menuObj.IsObject() || !CursorStage(x, y)) return;
				RE::Scaleform::GFx::Value args[2]{ RE::Scaleform::GFx::Value(x), RE::Scaleform::GFx::Value(y) };
				menuObj.Invoke(a_function, nullptr, args, 2);
			}

			// Keys and gamepad buttons belong to the main menu underneath. Cursor positions are recorded and drive the row
			// highlight; a left button press is taken only when the cursor is over the box, and the SWF acts on it directly.
			bool ShouldHandleEvent(const RE::InputEvent* a_event) override
			{
				if (!a_event || a_event->device != RE::INPUT_DEVICE::kMouse) return false;
				if (a_event->eventType == RE::INPUT_EVENT_TYPE::kCursorMove) {
					const auto* move = static_cast<const RE::CursorMoveEvent*>(a_event);
					const bool changed = move->cursorPosX != s_cursorX.load() || move->cursorPosY != s_cursorY.load();
					s_cursorX.store(move->cursorPosX);
					s_cursorY.store(move->cursorPosY);
					if (changed) CallWithCursor("Hover");
					return false;
				}
				if (a_event->eventType != RE::INPUT_EVENT_TYPE::kButton) return false;
				const auto* button = static_cast<const RE::ButtonEvent*>(a_event);
				// A held button repeats its event every frame; only the first press of the left button counts.
				const bool press = button->idCode == 0 && button->QJustPressed();
				const bool over = press && CursorOverPicker();
				if (press && s_inputLogs.fetch_add(1) < 40) logger::info("mainmenu: picker left press, over the box {}", over);
				return over;
			}

			void HandleEvent(const RE::ButtonEvent* a_event) override
			{
				if (!a_event || a_event->device != RE::INPUT_DEVICE::kMouse || a_event->idCode != 0 || !a_event->QJustPressed()) return;
				CallWithCursor("Click");
			}

			// Scaleform mouse events are posted to the top menu and swallowed by the base handler; passing them on keeps
			// MainMenu's own list clickable. The picker takes its clicks through the button event path above instead.
			RE::UI_MESSAGE_RESULTS ProcessMessage(RE::UIMessage& a_message) override
			{
				const auto type = *a_message.type;
				if (s_messageLogs.fetch_add(1) < 24) logger::info("mainmenu: picker message type {}", static_cast<int>(type));
				if (type == RE::UI_MESSAGE_TYPE::kScaleformEvent) return RE::UI_MESSAGE_RESULTS::kPassOn;
				return RE::GameMenuBase::ProcessMessage(a_message);
			}

			void Call(const Params& a_params) override
			{
				// userData carries the function id given at creation; the ActionScript arguments follow.
				const auto id = reinterpret_cast<std::uintptr_t>(a_params.userData);
				const auto* arg = a_params.argCount >= 1 ? &a_params.args[0] : nullptr;
				if (!arg || !(arg->IsNumber() || arg->IsInt() || arg->IsUInt())) {
					logger::info("mainmenu: picker call id {} with {} arguments, ignored", id, a_params.argCount);
					return;
				}
				const auto index = arg->IsNumber() ? static_cast<int>(arg->GetNumber()) : arg->IsInt() ? arg->GetInt() : static_cast<int>(arg->GetUInt());
				logger::info("mainmenu: picker call id {} index {}", id, index);
				if (id == 0) Select(index);
			}

			void PushOptions()
			{
				if (!uiMovie || !menuObj.IsObject()) return;
				RE::Scaleform::GFx::Value names;
				uiMovie->CreateArray(&names);
				std::scoped_lock lock(s_mutex);
				for (const auto& option : s_options) {
					names.PushBack(RE::Scaleform::GFx::Value(option.name.c_str()));
				}
				RE::Scaleform::GFx::Value args[2]{ names, RE::Scaleform::GFx::Value(s_selected) };
				const bool ok = menuObj.Invoke("SetOptions", nullptr, args, 2);
				logger::info("mainmenu: picker SetOptions with {} names -> {}", s_options.size(), ok);
			}

			static RE::IMenu* Create(const RE::UIMessage&) { return new PickerMenu(); }
		};

		// Neither the press-start page nor a re-created menu has MainMenu's cursor on (its own key-press transition sets it),
		// and the picker never counts as the top menu, so MainMenu is given the cursor flag and the engine refreshes the cursor.
		void ShowMainMenuCursor()
		{
			auto* ui = RE::UI::GetSingleton();
			if (!ui || !ui->GetMenuOpen(kPickerMenu.data())) return;
			auto main = ui->GetMenu("MainMenu");
			if (!main) return;
			LogMenuState("loop open, before cursor flag");
			// A menu re-created by a pick comes back with a LoadingMenu left open at MainMenu's depth, which swallows the list's input.
			if (s_rebuilding.exchange(false)) {
				for (const char* stray : { "LoadingMenu", "HUDMenu" }) {
					if (ui->GetMenuOpen(stray)) logger::info("mainmenu: {} open after the rebuild", stray);
				}
			}
			const bool had = main->menuFlags.all(RE::UI_MENU_FLAGS::kUsesCursor);
			// Both cursor flags, as MainMenu's own key-press transition sets them: the update flag routes mouse events to its movie.
			main->menuFlags.set(RE::UI_MENU_FLAGS::kUsesCursor, RE::UI_MENU_FLAGS::kUpdateUsesCursor);
			ui->RefreshCursor();
			logger::info("mainmenu: MainMenu cursor flag was {}, cursor refreshed; cursor menu open {}", had, ui->GetMenuOpen(RE::CursorMenu::MENU_NAME));
		}

		void ShowPicker(bool a_show)
		{
			if (auto* queue = RE::UIMessageQueue::GetSingleton(); queue) {
				queue->AddMessage(RE::BSFixedString(kPickerMenu.data()), a_show ? RE::UI_MESSAGE_TYPE::kShow : RE::UI_MESSAGE_TYPE::kHide);
			}
			if (!a_show) return;
			std::thread([] {
				std::this_thread::sleep_for(std::chrono::milliseconds(500));
				F4SE::GetTaskInterface()->AddTask([] { ShowMainMenuCursor(); });
			}).detach();
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
						if (s_loopOpen.load()) ShowPicker(true);
					} else {
						s_loopOpen.store(false);
						ShowPicker(false);
					}
				}
				return RE::BSEventNotifyControl::kContinue;
			}
		};
		MenuSink s_sink;
	}

	bool Install()
	{
		void* original = nullptr;
		if (!HookImport("bink2w64.dll", "BinkOpen", reinterpret_cast<void*>(&HookedBinkOpen), &original)) {
			logger::error("mainmenu: BinkOpen import not found; main menu video packs disabled");
			return false;
		}
		s_originalBinkOpen = reinterpret_cast<BinkOpenFn>(original);
		logger::info("mainmenu: BinkOpen hooked (original {})", original);
		return true;
	}

	void OnGameDataReady()
	{
		auto* ui = RE::UI::GetSingleton();
		if (!ui) {
			logger::error("mainmenu: UI singleton unavailable; picker disabled");
			return;
		}
		ui->RegisterSink<RE::MenuOpenCloseEvent>(&s_sink);
		if (!s_pickerEnabled.load()) {
			logger::info("mainmenu: picker disabled by the autotest");
			return;
		}
		ui->RegisterMenu(kPickerMenu.data(), &PickerMenu::Create);
		s_registered.store(true);
		const bool mainOpen = ui->GetMenuOpen("MainMenu");
		logger::info("mainmenu: picker menu registered; MainMenu already open {}, loop video open {}", mainOpen, s_loopOpen.load());
		if (mainOpen && s_loopOpen.load()) ShowPicker(true);
	}

	void SetSkipStartupVideos(bool a_skip)
	{
		s_skipStartup.store(a_skip);
	}

	void SetPickerEnabled(bool a_enabled)
	{
		s_pickerEnabled.store(a_enabled);
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
		s_selected = a_selected;
		s_menuVideo = (a_selected >= 0 && a_selected < static_cast<int>(s_options.size())) ? s_options[a_selected].video : std::string();
	}

	void LoadFromPacks()
	{
		std::vector<Option> options{ { "Vanilla", "" } };
		std::vector<std::string> ids{ "" };
		for (const auto& pack : Packs::All()) {
			if (pack.video.empty()) continue;
			options.push_back({ pack.name, "Data\\Video\\" + pack.video });
			ids.push_back(pack.id);
		}
		const auto saved = LoadStateMenuVideo();
		int selected = 0;
		for (std::size_t i = 1; i < ids.size(); ++i) {
			if (ids[i] == saved) selected = static_cast<int>(i);
		}
		SetOptions(std::move(options), selected);
		std::scoped_lock lock(s_mutex);
		s_optionIds = std::move(ids);
		logger::info("mainmenu: {} video options, selected {} ('{}')", s_options.size(), s_selected, saved);
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
		// The engine's own cursor position in window pixels; the per-frame cursor events stop reaching the picker when
		// another menu above it takes them (the 1.11 promo banner does), so they are only the fallback.
		if (auto* cursor = RE::MenuCursor::GetSingleton(); cursor) return { cursor->cursorPosX, cursor->cursorPosY };
		return { s_cursorX.load(), s_cursorY.load() };
	}

	int Selected()
	{
		std::scoped_lock lock(s_mutex);
		return s_selected;
	}

	int OptionCount()
	{
		std::scoped_lock lock(s_mutex);
		return static_cast<int>(s_options.size());
	}

	void Select(int a_index)
	{
		{
			std::scoped_lock lock(s_mutex);
			if (a_index < 0 || a_index >= static_cast<int>(s_options.size())) return;
			s_selected = a_index;
			s_menuVideo = s_options[a_index].video;
			logger::info("mainmenu: selected {} -> '{}'", s_options[a_index].name, s_menuVideo);
			SaveState();
		}
		// The main menu opens its video when it is created. Hiding and showing the menu re-creates it with a dead list, so the
		// engine's own return-to-main-menu sequence is requested instead, the path the game takes when leaving a save.
		s_rebuilding.store(true);
		F4SE::GetTaskInterface()->AddTask([] {
			if (auto* game = RE::Main::GetSingleton(); game) {
				game->resetGame = true;
				logger::info("mainmenu: game reset requested for the video switch");
			}
		});
	}

	void SetMenuVideo(const std::string& a_path)
	{
		std::scoped_lock lock(s_mutex);
		s_menuVideo = a_path;
	}

	std::string MenuVideo()
	{
		std::scoped_lock lock(s_mutex);
		return s_menuVideo;
	}
}
