#include "MainMenu.h"

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
		constexpr auto kPickerMenu = "GShufPicker"sv;

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
			auto* result = s_originalBinkOpen(redirect.empty() ? a_name : redirect.c_str(), a_flags);
			if (!result && !redirect.empty()) {
				logger::warn("mainmenu: '{}' failed to open, falling back to '{}'", redirect, name);
				result = s_originalBinkOpen(a_name, a_flags);
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
				menuFlags.set(RE::UI_MENU_FLAGS::kUsesCursor, RE::UI_MENU_FLAGS::kDontHideCursorWhenTopmost, RE::UI_MENU_FLAGS::kUpdateUsesCursor);
				depthPriority = RE::UI_DEPTH_PRIORITY::kGameMessage;
				auto* manager = RE::BSScaleformManager::GetSingleton();
				const bool loaded = manager && manager->LoadMovieEx(*this, "Interface/GShufPicker.swf", "root1");
				logger::info("mainmenu: picker movie loaded {}, menuObj object {}", loaded, menuObj.IsObject());
				if (loaded) PushOptions();
			}

			void MapCodeObjectFunctions() override
			{
				MapCodeMethodToASFunction("onSelect", 0);
			}

			void Call(const Params& a_params) override
			{
				// The first argument is the mapped function id, the rest are the ActionScript arguments.
				if (a_params.argCount < 2 || !a_params.args[0].IsNumber() || !a_params.args[1].IsNumber()) return;
				const auto id = static_cast<int>(a_params.args[0].GetNumber());
				const auto index = static_cast<int>(a_params.args[1].GetNumber());
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

		class MenuSink : public RE::BSTEventSink<RE::MenuOpenCloseEvent>
		{
		public:
			RE::BSEventNotifyControl ProcessEvent(const RE::MenuOpenCloseEvent& a_event, RE::BSTEventSource<RE::MenuOpenCloseEvent>*) override
			{
				const auto name = a_event.menuName.c_str() ? a_event.menuName.c_str() : "";
				if (std::string_view(name) == "MainMenu") {
					logger::info("mainmenu: MainMenu {}", a_event.opening ? "opened" : "closed");
					if (auto* queue = RE::UIMessageQueue::GetSingleton(); queue) {
						queue->AddMessage(RE::BSFixedString(kPickerMenu.data()), a_event.opening ? RE::UI_MESSAGE_TYPE::kShow : RE::UI_MESSAGE_TYPE::kHide);
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
		ui->RegisterMenu(kPickerMenu.data(), &PickerMenu::Create);
		logger::info("mainmenu: picker menu registered");
	}

	void SetOptions(std::vector<Option> a_options, int a_selected)
	{
		std::scoped_lock lock(s_mutex);
		s_options = std::move(a_options);
		s_selected = a_selected;
		s_menuVideo = (a_selected >= 0 && a_selected < static_cast<int>(s_options.size())) ? s_options[a_selected].video : std::string();
	}

	void Select(int a_index)
	{
		{
			std::scoped_lock lock(s_mutex);
			if (a_index < 0 || a_index >= static_cast<int>(s_options.size())) return;
			s_selected = a_index;
			s_menuVideo = s_options[a_index].video;
			logger::info("mainmenu: selected {} -> '{}'", s_options[a_index].name, s_menuVideo);
		}
		// The main menu opens its video once per show, so it is re-shown to pick up the new file.
		if (auto* queue = RE::UIMessageQueue::GetSingleton(); queue) {
			queue->AddMessage(RE::BSFixedString("MainMenu"), RE::UI_MESSAGE_TYPE::kHide);
			queue->AddMessage(RE::BSFixedString("MainMenu"), RE::UI_MESSAGE_TYPE::kShow);
		}
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
