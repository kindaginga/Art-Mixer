#include "AutoTest.h"

#include "Distributor.h"
#include "Engine.h"
#include "LoadScreens.h"
#include "MainMenu.h"
#include "Settings.h"

#include <cmath>
#include <set>

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>

namespace ADT::AutoTest
{
	namespace
	{
		bool s_enabled{ false };
		std::string s_save;
		std::string s_mode;
		std::string s_menuVideo;
		std::string s_menuAction;  // "click": click CONTINUE; "enter": press Enter; empty: none
		bool s_pickFirst{ true };  // press the plugin's menu entry before the menu action
		bool s_anyKey{ true };     // click once as the "press any button" step
		std::string s_consoleAfterA;  // console command issued in game after stage A
		std::string s_place;          // comma-separated base form ids placed in front of the player at stage A2 (two frames when empty)
		int s_placePitch{ 70 };       // camera pitch after the placement, in degrees down
		bool s_nameCheck{ false };    // after the placement: log the names of the placed items, pick one up, log its inventory stacks
		bool s_inventoryCheck{ false };  // after the placement: pick the placed items up and photograph the inventory 3D view with model logging
		bool s_noContainer{ false };     // the inventory check skips the container menu (for the save and load round trip)
		bool s_skipReload{ false };      // quit after the placement stages instead of running the reload stages B and C
		int s_idleSeconds{ 200 };  // length of the "idle" menu action
		int s_loadCount{ 4 };      // loads of the save in the "loadscreens" mode, the first one included
		std::atomic<bool> s_loadIssued{ false };
		std::atomic<bool> s_finishIssued{ false };
		std::string s_lastBox;  // body text of the open prompt already confirmed (game thread only), cleared once no prompt is open and on every harness command

		void RunOnGameThread(std::function<void()> a_task)
		{
			F4SE::GetTaskInterface()->AddTask(std::move(a_task));
		}

		// Runs a console command on the game thread and waits until it has actually executed (loading screens stall the queue).
		void Console(std::string a_command)
		{
			auto done = std::make_shared<std::atomic<bool>>(false);
			RunOnGameThread([command = std::move(a_command), done] {
				s_lastBox.clear();
				RE::Console::ExecuteCommand(command.c_str());
				logger::info("autotest: issued '{}'", command);
				done->store(true);
			});
			for (int i = 0; i < 1200 && !done->load(); ++i) std::this_thread::sleep_for(std::chrono::milliseconds(100));
			if (!done->load()) logger::warn("autotest: a console command queued 120 s ago has not run");
		}

		[[nodiscard]] bool IsReadable(const void* a_ptr, std::size_t a_size)
		{
			MEMORY_BASIC_INFORMATION mbi{};
			if (!a_ptr || !VirtualQuery(a_ptr, &mbi, sizeof(mbi))) return false;
			if (mbi.State != MEM_COMMIT || (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD))) return false;
			const auto regionEnd = reinterpret_cast<std::uintptr_t>(mbi.BaseAddress) + mbi.RegionSize;
			return reinterpret_cast<std::uintptr_t>(a_ptr) + a_size <= regionEnd;
		}

		// Answers the "mods are loaded" message box with its first button; the MessageBoxData pointer sits at
		// menu+0xE8 on 1.10.x and menu+0xF0 on 1.11.x, so it is located by vtable instead of a fixed offset.
		// The open message box's data with a readable callback, or null (game thread).
		RE::MessageBoxData* OpenMessageBox()
		{
			auto ui = RE::UI::GetSingleton();
			if (!ui) return nullptr;
			auto menu = ui->GetMenu<RE::MessageBoxMenu>();
			if (!menu) return nullptr;
			const auto dataVtable = REL::Relocation<std::uintptr_t>{ RE::VTABLE::MessageBoxData[0] }.address();
			const auto raw = reinterpret_cast<const std::uintptr_t*>(menu.get());
			RE::MessageBoxData* data = nullptr;
			for (int i = 0x18; i < 0x140 && !data; i += 8) {
				if (!IsReadable(raw + i / 8, 8)) break;
				const auto candidate = reinterpret_cast<const std::uintptr_t*>(raw[i / 8]);
				if (IsReadable(candidate, 0x68) && candidate[0] == dataVtable) data = reinterpret_cast<RE::MessageBoxData*>(raw[i / 8]);
			}
			if (!data || !IsReadable(data->callback.get(), 0x10)) return nullptr;
			return data;
		}

		void ConfirmMessageBox(bool a_afterLoad = false)
		{
			if (s_finishIssued.load() && !a_afterLoad) return;
			auto* data = OpenMessageBox();
			if (!data) {
				s_lastBox.clear();
				return;
			}
			auto callback = data->callback.get();
			const std::string body = data->bodyText.c_str() ? data->bodyText.c_str() : "";
			if (body == s_lastBox) return;
			// The 1.11 missing-content prompt offers a Creations menu search with its first button; its second button loads the save.
			const bool declineSearch = body.find("Creations menu") != std::string::npos;
			logger::info("autotest: {} message box '{}'", declineSearch ? "declining the search in" : "confirming", body);
			s_lastBox = body;
			(*callback)(declineSearch ? 1 : 0);
			RE::UIMessageQueue::GetSingleton()->AddMessage(RE::MessageBoxMenu::MENU_NAME, RE::UI_MESSAGE_TYPE::kHide);
		}

		// A place entry as a form id in hex: hex as given, or "Plugin.esp:local id" looked up in that plugin.
		std::string ResolvePlace(const std::string& a_entry)
		{
			const auto colon = a_entry.rfind(':');
			if (colon == std::string::npos) return a_entry;
			auto* handler = RE::TESDataHandler::GetSingleton();
			const auto local = static_cast<std::uint32_t>(std::strtoul(a_entry.substr(colon + 1).c_str(), nullptr, 16));
			auto* form = handler ? handler->LookupForm(local, a_entry.substr(0, colon)) : nullptr;
			logger::info("autotest: {} is {}", a_entry, form ? fmt::format("{:08X}", form->GetFormID()) : std::string("not loaded"));
			return form ? fmt::format("{:08X}", form->GetFormID()) : a_entry;
		}

		// Raises an MCM event the way MCM's menu does: F4SE's root.f4se.SendExternalEvent on the pause menu's (or the HUD's) movie.
		void SendMcmEvent(std::vector<std::string> a_args)
		{
			RunOnGameThread([args = std::move(a_args)] {
				auto* ui = RE::UI::GetSingleton();
				const auto menu = ui ? ui->GetMenu(ui->GetMenuOpen("PauseMenu") ? "PauseMenu" : "HUDMenu") : nullptr;
				if (!menu || !menu->uiMovie) {
					logger::warn("autotest: no menu movie to send '{}' from", args.front());
					return;
				}
				RE::Scaleform::GFx::Value f4se;
				if (!menu->uiMovie->GetVariable(&f4se, "root.f4se") || !f4se.IsObject()) {
					logger::warn("autotest: the menu movie has no root.f4se object");
					return;
				}
				std::vector<RE::Scaleform::GFx::Value> values;
				values.reserve(args.size());
				for (const auto& arg : args) values.emplace_back(arg.c_str());
				const bool sent = f4se.Invoke("SendExternalEvent", nullptr, values.data(), values.size());
				logger::info("autotest: sent MCM event '{}' with {} arguments: {}", args.front(), args.size() - 1, sent);
			});
		}

		// True once the Art Distribution Tool Apply all prompt is open, checked on the game thread for up to a_seconds.
		bool WaitForApplyPrompt(int a_seconds)
		{
			for (int i = 0; i < a_seconds * 5; ++i) {
				auto open = std::make_shared<std::atomic<int>>(-1);
				RunOnGameThread([open] {
					auto* data = OpenMessageBox();
					const char* body = data ? data->bodyText.c_str() : nullptr;
					open->store(body && std::string_view(body).find("Apply all") != std::string_view::npos ? 1 : 0);
				});
				for (int k = 0; k < 50 && open->load() < 0; ++k) std::this_thread::sleep_for(std::chrono::milliseconds(10));
				if (open->load() == 1) return true;
				std::this_thread::sleep_for(std::chrono::milliseconds(200));
			}
			return false;
		}

		// Answers the open prompt with a button (0 Apply all, 1 Cancel), as a click on it would.
		void AnswerApplyPrompt(int a_button)
		{
			RunOnGameThread([a_button] {
				auto* data = OpenMessageBox();
				if (!data) {
					logger::warn("autotest: no prompt to answer");
					return;
				}
				logger::info("autotest: answering the Apply all prompt with {}", a_button == 0 ? "Apply all" : "Cancel");
				(*data->callback.get())(static_cast<std::uint8_t>(a_button));
				RE::UIMessageQueue::GetSingleton()->AddMessage(RE::MessageBoxMenu::MENU_NAME, RE::UI_MESSAGE_TYPE::kHide);
			});
		}

		// A key's value in one section of an ini file, or "(none)".
		std::string IniValue(const std::filesystem::path& a_path, std::string_view a_section, std::string_view a_key)
		{
			std::ifstream file(a_path);
			std::string line;
			bool inSection = false;
			const auto section = "[" + Settings::Lower(a_section) + "]";
			const auto key = Settings::Lower(a_key) + "=";
			while (std::getline(file, line)) {
				auto text = Settings::Lower(line);
				while (!text.empty() && (text.back() == '\r' || text.back() == ' ')) text.pop_back();
				if (text.starts_with('[')) {
					inSection = text == section;
					continue;
				}
				if (inSection && text.starts_with(key)) return text.substr(key.size());
			}
			return "(none)";
		}

		// Logs the Paintings slot 1 share in effect, in the menu's settings file and in the applied settings file.
		void LogApplyState(std::string_view a_stage)
		{
			const auto key = Settings::PackKey("paintings", 1);
			const auto menu = IniValue(std::filesystem::path("Data") / "MCM" / "Settings" / "ArtDistributionTool.ini", "ADT_Paintings", key);
			const auto applied = IniValue(std::filesystem::path("Data") / "F4SE" / "Plugins" / "ArtDistributionTool_Applied.ini", "ADT_paintings", key);
			logger::info("autotest: {}: paintings slot 1 in effect {}, menu file {}, applied file {}", a_stage, Settings::Percent("paintings", 1), menu, applied);
		}

		// Moves a slider the way MCM does: MCM stores the value, and nothing else happens until the changes are applied.
		void MenuSliderChange(int a_value, int a_slot = 1, std::string_view a_section = "ADT_Paintings")
		{
			const auto id = Settings::MenuKey(a_section, a_slot);
			Console(fmt::format("cgf \"MCM.SetModSettingInt\" \"ArtDistributionTool\" \"{}\" {}", id, a_value));
			std::this_thread::sleep_for(std::chrono::seconds(3));
		}

		// Logs the menu file's shares of two slots of one section and one slot of another against the expected values.
		bool CheckShares(std::string_view a_step, std::string_view a_section, std::array<int, 2> a_slots, std::string_view a_otherSection, int a_other, std::array<int, 3> a_expected)
		{
			const auto path = std::filesystem::path("Data") / "MCM" / "Settings" / "ArtDistributionTool.ini";
			// MCM leaves a key out of its file while it holds the default, 0 for a pack slot.
			const auto read = [&](int a_slot, std::string_view a_category) {
				const auto id = Settings::MenuKey(a_category, a_slot);
				const auto colon = id.find(':');
				const auto v = IniValue(path, std::string_view(id).substr(colon + 1), std::string_view(id).substr(0, colon));
				return v == "(none)" ? std::string("0") : v;
			};
			const std::array<std::string, 3> got{ read(a_slots[0], a_section), read(a_slots[1], a_section), read(a_other, a_otherSection) };
			bool ok = true;
			for (std::size_t i = 0; i < got.size(); ++i) ok = ok && got[i] == std::to_string(a_expected[i]);
			logger::info("autotest: sliders {}: {} {}/{} other section {} (expected {}/{} other section {})", a_step, ok ? "PASS" : "FAIL", got[0], got[1], got[2], a_expected[0], a_expected[1], a_expected[2]);
			return ok;
		}

		void MouseRelative(int a_dx, int a_dy)
		{
			INPUT input{};
			input.type = INPUT_MOUSE;
			input.mi.dx = a_dx;
			input.mi.dy = a_dy;
			input.mi.dwFlags = MOUSEEVENTF_MOVE;
			SendInput(1, &input, sizeof(INPUT));
		}

		// The game moves its own cursor from raw mouse deltas, so the cursor is steered by feedback from the engine's
		// own cursor position; the target is a stage coordinate scaled to the game window.
		bool MoveCursorTo(float a_stageX, float a_stageY)
		{
			HWND window = FindWindowW(nullptr, L"Fallout4");
			RECT client{};
			if (!window || !GetClientRect(window, &client)) {
				logger::warn("autotest: game window not found");
				return false;
			}
			const float width = static_cast<float>(client.right - client.left);
			const float height = static_cast<float>(client.bottom - client.top);
			const int targetX = static_cast<int>(a_stageX * width / 1280.0f);
			const int targetY = static_cast<int>(a_stageY * height / 720.0f);
			// Steps are capped because the engine applies its own sensitivity to the deltas; the tolerance is well inside a list row.
			for (int step = 0; step < 160; ++step) {
				const auto [x, y] = MainMenu::CursorPos();
				if (x < 0) {
					MouseRelative(1, 1);
					std::this_thread::sleep_for(std::chrono::milliseconds(60));
					continue;
				}
				const int dx = targetX - x;
				const int dy = targetY - y;
				if (std::abs(dx) <= 12 && std::abs(dy) <= 12) {
					logger::info("autotest: cursor at ({}, {}) for stage ({:.0f}, {:.0f}) after {} steps", x, y, a_stageX, a_stageY, step);
					return true;
				}
				MouseRelative(std::clamp(dx / 3, -50, 50), std::clamp(dy / 3, -50, 50));
				std::this_thread::sleep_for(std::chrono::milliseconds(40));
			}
			const auto [x, y] = MainMenu::CursorPos();
			logger::warn("autotest: cursor stuck at ({}, {}) wanting ({}, {})", x, y, targetX, targetY);
			return false;
		}

		void ClickStage(float a_stageX, float a_stageY);

		void PressScanCode(std::uint16_t a_scanCode)
		{
			INPUT input[2]{};
			input[0].type = INPUT_KEYBOARD;
			input[0].ki.wScan = a_scanCode;
			input[0].ki.dwFlags = KEYEVENTF_SCANCODE;
			input[1] = input[0];
			input[1].ki.dwFlags |= KEYEVENTF_KEYUP;
			SendInput(1, &input[0], sizeof(INPUT));
			std::this_thread::sleep_for(std::chrono::milliseconds(80));
			SendInput(1, &input[1], sizeof(INPUT));
			logger::info("autotest: pressed scan code {:#x}", a_scanCode);
		}

		// Clicks CONTINUE or presses Enter on the main menu list and reports whether the menu reacted (it closes to load).
		void MenuAction()
		{
			if (s_menuAction.empty()) return;
			if (s_menuAction == "click") ClickStage(352.0f, 316.0f);
			else if (s_menuAction == "enter") PressScanCode(0x1C);
			else if (s_menuAction == "anykey-click") {
				ClickStage(640.0f, 300.0f);
				std::this_thread::sleep_for(std::chrono::seconds(3));
				ClickStage(352.0f, 316.0f);
			} else if (s_menuAction == "idle") {
				// No input at all; a screenshot every 30 s shows what the engine plays while the player is away.
				for (int elapsed = 0; elapsed < s_idleSeconds; elapsed += 30) {
					std::this_thread::sleep_for(std::chrono::seconds(30));
					Console("Screenshot");
					logger::info("autotest: idle {} s, at main menu {}", elapsed + 30, MainMenu::AtMainMenu());
				}
				return;
			}
			for (int i = 0; i < 80; ++i) {
				std::this_thread::sleep_for(std::chrono::milliseconds(100));
				if (!MainMenu::AtMainMenu()) break;
			}
			auto* ui = RE::UI::GetSingleton();
			logger::info("autotest: menu action '{}' -> MainMenu open {}, at main menu {}, message box open {}, load menu open {}", s_menuAction, ui && ui->GetMenuOpen("MainMenu"), MainMenu::AtMainMenu(), ui && ui->GetMenuOpen("MessageBoxMenu"), ui && ui->GetMenuOpen("LoadingMenu"));
			Console("Screenshot");
		}

		void ClickStage(float a_stageX, float a_stageY)
		{
			MoveCursorTo(a_stageX, a_stageY);
			INPUT input[2]{};
			input[0].type = INPUT_MOUSE;
			input[0].mi.dwFlags = MOUSEEVENTF_LEFTDOWN;
			input[1].type = INPUT_MOUSE;
			input[1].mi.dwFlags = MOUSEEVENTF_LEFTUP;
			SendInput(1, &input[0], sizeof(INPUT));
			std::this_thread::sleep_for(std::chrono::milliseconds(80));
			SendInput(1, &input[1], sizeof(INPUT));
			logger::info("autotest: clicked at stage ({:.0f}, {:.0f})", a_stageX, a_stageY);
		}

		// Reloads the loaded area through the console and puts the player back where they stood.
		// Waits for the loading menu a teleport opens to close again (up to 10 s to open, 300 s to close), then 5 s for the area to settle.
		void WaitForLoad()
		{
			const auto loading = [] {
				auto* ui = RE::UI::GetSingleton();
				return ui && ui->GetMenuOpen("LoadingMenu");
			};
			const auto start = std::chrono::steady_clock::now();
			for (int i = 0; i < 100 && !loading(); ++i) std::this_thread::sleep_for(std::chrono::milliseconds(100));
			for (int i = 0; i < 3000 && loading(); ++i) std::this_thread::sleep_for(std::chrono::milliseconds(100));
			logger::info("autotest: loading menu closed after {:.1f} s", std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count());
			std::this_thread::sleep_for(std::chrono::seconds(5));
		}

		void ReloadArea(bool a_farFirst)
		{
			struct Pose { float x, y, z, ax, az; };
			auto pose = std::make_shared<Pose>();
			RunOnGameThread([pose] {
				if (auto* player = RE::PlayerCharacter::GetSingleton(); player) {
					const auto& loc = player->data.location;
					const auto& ang = player->data.angle;
					*pose = Pose{ loc.x, loc.y, loc.z, ang.x * 57.29578f, ang.z * 57.29578f };
					logger::info("autotest: pose ({:.1f}, {:.1f}, {:.1f}) angle x {:.1f} z {:.1f}", pose->x, pose->y, pose->z, pose->ax, pose->az);
				}
			});
			std::this_thread::sleep_for(std::chrono::seconds(1));
			const int cellX = static_cast<int>(std::floor(pose->x / 4096.0f));
			const int cellY = static_cast<int>(std::floor(pose->y / 4096.0f));
			if (a_farFirst) {
				Console("cow Commonwealth -20 -20");
				WaitForLoad();
			}
			Console(fmt::format("cow Commonwealth {} {}", cellX, cellY));
			WaitForLoad();
			Console(fmt::format("player.setpos x {:.1f}", pose->x));
			Console(fmt::format("player.setpos y {:.1f}", pose->y));
			Console(fmt::format("player.setpos z {:.1f}", pose->z));
			Console(fmt::format("player.setangle x {:.1f}", pose->ax));
			Console(fmt::format("player.setangle z {:.1f}", pose->az));
			std::this_thread::sleep_for(std::chrono::seconds(6));
		}

		void LogStats(const char* a_stage)
		{
			const auto stats = Distributor::GetStats();
			logger::info("autotest: {}: models seen {}, tracked geometries {}, swaps applied {}, Load3D calls {}, references with art {}, reference swaps {}, arrived showing a pack {}", a_stage, stats.modelsSeen, stats.tracked, stats.swapped, stats.set3DCalls, stats.referencesSeen, stats.referenceSwaps, stats.packArrivals);
		}
	}

	bool Skips(std::string_view a_feature)
	{
		static const auto s_skipped = [] {
			std::string list;
			bool enabled = false;
			std::ifstream file(std::filesystem::path("Data") / "F4SE" / "Plugins" / "ArtDistributionTool.ini");
			std::string line;
			bool inSection = false;
			while (std::getline(file, line)) {
				const auto text = Settings::Lower(line);
				if (text.find('[') != std::string::npos) {
					inSection = text.find("[autotest]") != std::string::npos;
					continue;
				}
				const auto eq = text.find('=');
				if (!inSection || eq == std::string::npos) continue;
				const auto key = text.substr(0, eq);
				auto value = text.substr(eq + 1);
				value.erase(value.find_last_not_of(" \t\r\n") + 1);
				if (key.find("benabled") != std::string::npos) enabled = value == "1";
				if (key.find("sskip") != std::string::npos) list = "," + value + ",";
			}
			if (enabled && !list.empty()) logger::info("autotest: features left out for this run: {}", list);
			return enabled ? list : std::string();
		}();
		return s_skipped.find("," + std::string(a_feature) + ",") != std::string::npos;
	}

	bool Configure()
	{
		const auto path = std::filesystem::path("Data") / "F4SE" / "Plugins" / "ArtDistributionTool.ini";
		std::ifstream file(path);
		if (!file) return false;
		std::string line;
		bool inSection = false;
		bool menuEntry = true;
		bool skipVideos = true;

		while (std::getline(file, line)) {
			const auto text = Settings::Lower(line);
			if (text.find('[') != std::string::npos) {
				inSection = text.find("[autotest]") != std::string::npos;
				continue;
			}
			if (!inSection) continue;
			const auto eq = line.find('=');
			if (eq == std::string::npos) continue;
			const auto key = Settings::Lower(line.substr(0, eq));
			auto value = line.substr(eq + 1);
			value.erase(0, value.find_first_not_of(" \t"));
			value.erase(value.find_last_not_of(" \t\r\n") + 1);
			if (key.find("benabled") != std::string::npos) s_enabled = value == "1";
			if (key.find("sloadsave") != std::string::npos) s_save = value;
			if (key.find("smode") != std::string::npos) s_mode = Settings::Lower(value);
			if (key.find("smenuvideo") != std::string::npos) s_menuVideo = value;
			if (key.find("bmenuentry") != std::string::npos) menuEntry = value == "1";
			if (key.find("bskipvideos") != std::string::npos) skipVideos = value == "1";
			if (key.find("smenuaction") != std::string::npos) s_menuAction = Settings::Lower(value);
			if (key.find("bpickfirst") != std::string::npos) s_pickFirst = value == "1";
			if (key.find("sconsoleaftera") != std::string::npos) s_consoleAfterA = value;
			if (key.find("splace") != std::string::npos) s_place = value;
			if (key.find("iplacepitch") != std::string::npos) s_placePitch = std::stoi(value);
			if (key.find("bnamecheck") != std::string::npos) s_nameCheck = value == "1";
			if (key.find("sloadcards") != std::string::npos) {
				// "card|card|..." : the cards the loading screens show, in order.
				std::vector<std::string> cards;
				for (std::size_t start = 0; start < value.size();) {
					const auto bar = value.find('|', start);
					cards.push_back(value.substr(start, bar == std::string::npos ? std::string::npos : bar - start));
					start = bar == std::string::npos ? value.size() : bar + 1;
				}
				LoadScreens::SetTestCards(std::move(cards));
			}
			if (key.find("binventorycheck") != std::string::npos) s_inventoryCheck = value == "1";
			if (key.find("bskipreload") != std::string::npos) s_skipReload = value == "1";
			if (key.find("bnocontainer") != std::string::npos) s_noContainer = value == "1";
			if (key.find("banykey") != std::string::npos) s_anyKey = value == "1";
			if (key.find("iidleseconds") != std::string::npos) s_idleSeconds = std::stoi(value);
			if (key.find("iloads") != std::string::npos) s_loadCount = std::stoi(value);
		}
		if (s_enabled) {
			MainMenu::SetSkipStartupVideos(skipVideos);
			MainMenu::SetEntryEnabled(menuEntry);
			logger::info("autotest: menu entry {}, startup videos {}", menuEntry ? "on" : "off", skipVideos ? "skipped" : "played");
		}
		if (s_enabled && s_mode == "mainmenu") {
			logger::info("autotest: main menu mode, video '{}'", s_menuVideo);
			if (!s_menuVideo.empty()) MainMenu::SetOptions({ { "Vanilla", "", "" }, { "Test video", s_menuVideo, "" } }, 0);
			return true;
		}
		if (s_enabled && !s_save.empty()) {
			logger::info("autotest: enabled, will load '{}'", s_save);
			Engine::SetTracing(true);
			return true;
		}
		s_enabled = false;
		return false;
	}

	// The name a reference shows: its per-object name when it has one, else its base object's name.
	[[nodiscard]] std::string ShownName(RE::TESObjectREFR* a_ref, bool& a_custom)
	{
		auto* base = a_ref->GetObjectReference();
		auto* extra = a_ref->extraList ? a_ref->extraList->GetByType<RE::ExtraTextDisplayData>() : nullptr;
		a_custom = extra != nullptr;
		if (extra && base) {
			const char* text = extra->GetDisplayName(base).c_str();
			return text ? text : "";
		}
		return base ? std::string(RE::TESFullName::GetFullName(*base)) : std::string();
	}

	// Logs the name of every placed item of the listed base objects in the player's cell, then picks up the first one that
	// carries a per-object name; returns that item's base object id (0 when none).
	std::uint32_t NameCheckPlaced()
	{
		std::set<std::uint32_t> bases;
		for (std::size_t start = 0; start < s_place.size();) {
			const auto comma = s_place.find(',', start);
			bases.insert(static_cast<std::uint32_t>(std::strtoul(s_place.substr(start, comma == std::string::npos ? std::string::npos : comma - start).c_str(), nullptr, 16)));
			start = comma == std::string::npos ? s_place.size() : comma + 1;
		}
		auto* player = RE::PlayerCharacter::GetSingleton();
		auto* cell = player ? player->GetParentCell() : nullptr;
		if (!cell) return 0;
		RE::TESObjectREFR* pick = nullptr;
		for (auto& entry : cell->references) {
			auto* ref = entry.get();
			auto* base = ref ? ref->GetObjectReference() : nullptr;
			if (!base || !bases.contains(base->GetFormID()) || (ref->GetFormID() >> 24) != 0xFF) continue;
			bool custom = false;
			const auto name = ShownName(ref, custom);
			logger::info("autotest: name check: placed {:08X} (base {:08X}) shows '{}'{}", ref->GetFormID(), base->GetFormID(), name, custom ? " (per-object name)" : "");
			if (custom && !pick) pick = ref;
		}
		if (!pick) return 0;
		const auto baseID = pick->GetObjectReference()->GetFormID();
		logger::info("autotest: name check: picking up {:08X}", pick->GetFormID());
		pick->ActivateRef(player, nullptr, 1, false, false, false);
		return baseID;
	}

	// Activates every placed reference of the placed base objects (sPlace), which picks them up.
	void PickUpPlaced()
	{
		std::set<std::uint32_t> bases;
		for (std::size_t start = 0; start < s_place.size();) {
			const auto comma = s_place.find(',', start);
			bases.insert(static_cast<std::uint32_t>(std::strtoul(s_place.substr(start, comma == std::string::npos ? std::string::npos : comma - start).c_str(), nullptr, 16)));
			start = comma == std::string::npos ? s_place.size() : comma + 1;
		}
		auto* player = RE::PlayerCharacter::GetSingleton();
		auto* cell = player ? player->GetParentCell() : nullptr;
		if (!cell) return;
		std::vector<RE::TESObjectREFR*> picks;
		for (auto& entry : cell->references) {
			auto* ref = entry.get();
			auto* base = ref ? ref->GetObjectReference() : nullptr;
			if (base && bases.contains(base->GetFormID()) && (ref->GetFormID() >> 24) == 0xFF) picks.push_back(ref);
		}
		for (auto* ref : picks) {
			logger::info("autotest: picking up {:08X} (base {:08X})", ref->GetFormID(), ref->GetObjectReference()->GetFormID());
			ref->ActivateRef(player, nullptr, 1, false, false, false);
		}
	}

	// Logs the materials every geometry of each placed reference shows, so a run can tell which pack's version each placed object took.
	void LogPlacedMaterials()
	{
		auto* player = RE::PlayerCharacter::GetSingleton();
		auto* cell = player ? player->GetParentCell() : nullptr;
		if (!cell) return;
		for (auto& entry : cell->references) {
			auto* ref = entry.get();
			if (!ref || (ref->GetFormID() >> 24) != 0xFF) continue;
			auto* base = ref->GetObjectReference();
			auto* root = ref->Get3D();
			if (!base || !root) continue;
			std::string shown;
			Engine::ForEachGeometry(root, [&](RE::NiAVObject* a_geometry, int) {
				auto name = Engine::ShaderPropertyName(a_geometry);
				if (name.empty()) name = Engine::InlineKey(a_geometry);
				if (!name.empty()) shown += (shown.empty() ? "" : ", ") + name;
			});
			logger::info("autotest: placed {:08X} (base {:08X}) shows {}", ref->GetFormID(), base->GetFormID(), shown.empty() ? "no named geometry" : shown);
		}
	}

	// Logs every inventory stack of one base object the player carries, with the name the inventory shows for it.
	void NameCheckInventory(std::uint32_t a_baseFormID)
	{
		auto* player = RE::PlayerCharacter::GetSingleton();
		auto* list = player ? player->inventoryList : nullptr;
		if (!list) return;
		const RE::BSAutoReadLock lock{ list->rwLock };
		for (auto& item : list->data) {
			if (!item.object || item.object->GetFormID() != a_baseFormID) continue;
			std::uint32_t id = 0;
			for (auto* stack = item.stackData.get(); stack; stack = stack->nextStack.get(), ++id) {
				const char* name = item.GetDisplayFullName(id);
				logger::info("autotest: name check: inventory stack {} of {:08X}: count {}, shows '{}'", id, a_baseFormID, stack->GetCount(), name ? name : "");
			}
		}
	}

	// Re-queues itself every second so the log shows when the game thread stops running tasks.
	void Heartbeat()
	{
		static int s_ticks = 0;
		if (++s_ticks % 5 == 0) logger::info("autotest: game thread heartbeat {} (thread {})", s_ticks, GetCurrentThreadId());
		std::thread([] {
			std::this_thread::sleep_for(std::chrono::seconds(1));
			RunOnGameThread(Heartbeat);
		}).detach();
	}

	// Waits until the startup videos are over and the real main menu is up; a S.P.E.C.I.A.L. clip at startup runs 161 s.
	void WaitForMainMenu()
	{
		for (int i = 0; i < 2000 && !MainMenu::AtMainMenu(); ++i) std::this_thread::sleep_for(std::chrono::milliseconds(100));
		logger::info("autotest: at main menu {}", MainMenu::AtMainMenu());
	}

	void OnGameDataReady()
	{
		if (!s_enabled || s_loadIssued.exchange(true)) return;
		if (s_mode == "mainmenu") {
			std::thread([] {
				WaitForMainMenu();
				std::this_thread::sleep_for(std::chrono::seconds(5));
				Console("Screenshot");
				// Enter stands in for "press any button" so the list shows; then the MENU ART entry opens the selector, the option that is not current is picked and the rebuilt menu read back.
				if (s_anyKey) {
					PressScanCode(0x1C);
					std::this_thread::sleep_for(std::chrono::seconds(3));
					RunOnGameThread([] { MainMenu::LogMenuState("after Enter"); });
				}
				Console("Screenshot");
				RunOnGameThread([] { logger::info("autotest: main menu {}", MainMenu::EntryState()); });
				if (s_pickFirst) {
					std::this_thread::sleep_for(std::chrono::seconds(1));
					RunOnGameThread([] { MainMenu::PressEntry(); });
					std::this_thread::sleep_for(std::chrono::seconds(2));
					Console("Screenshot");
					RunOnGameThread([] { logger::info("autotest: selector {}", MainMenu::EntryState()); });
					std::this_thread::sleep_for(std::chrono::seconds(1));
					RunOnGameThread([] { MainMenu::PressOption((MainMenu::Selected() + 1) % std::max(1, MainMenu::OptionCount())); });
					std::this_thread::sleep_for(std::chrono::seconds(20));
					PressScanCode(0x1C);
					std::this_thread::sleep_for(std::chrono::seconds(3));
					RunOnGameThread([] { logger::info("autotest: after the pick: option {}, main menu {}", MainMenu::Selected(), MainMenu::EntryState()); });
				}
				Console("Screenshot");
				RunOnGameThread([] { MainMenu::LogMenuState("before menu action"); });
				std::this_thread::sleep_for(std::chrono::seconds(1));
				MenuAction();
				std::this_thread::sleep_for(std::chrono::seconds(5));
				Console("qqq");
			}).detach();
			return;
		}
		RunOnGameThread(Heartbeat);
		logger::info("autotest: game data ready; loading '{}' once the main menu is up", s_save);
		std::thread([] {
			WaitForMainMenu();
			std::this_thread::sleep_for(std::chrono::seconds(3));
			Console("load " + s_save);
			for (int i = 0; i < 60 && !s_finishIssued.load(); ++i) {
				std::this_thread::sleep_for(std::chrono::seconds(1));
				RunOnGameThread([] { ConfirmMessageBox(); });
			}
		}).detach();
	}

	void OnPostLoadGame(bool a_loaded)
	{
		if (!s_enabled || s_mode == "mainmenu") return;
		// Slider test on the first share page with two or more packs: every slider is independent, so moving one never moves another,
		// on its own page or on any other.
		if (s_mode == "sliders") {
			if (!a_loaded || s_finishIssued.exchange(true)) return;
			std::thread([] {
				std::this_thread::sleep_for(std::chrono::seconds(10));
				// Leaves out the Pip-Boy and world map lists and the Other replacers switches.
				const auto tested = [](std::string_view a_section) {
					const auto category = Settings::NormalizeCategory(a_section);
					return !category.starts_with("pipboy") && category != "other";
				};
				const auto sections = Packs::Sections();
				std::string section, other;
				for (const auto& s : sections) {
					if (!tested(s)) continue;
					const auto n = Packs::SlotsFor(Settings::NormalizeCategory(s)).size();
					if (section.empty() && n >= 2) section = s;
					else if (!section.empty() && other.empty() && n >= 1 && s != section) other = s;
				}
				if (section.empty() || other.empty()) {
					logger::info("autotest: sliders: FAIL need a section with 2 packs and another with 1 ({} sections)", sections.size());
					Console("qqq");
					return;
				}
				logger::info("autotest: sliders: section {} slots 1 and 2; other section {} slot 1", section, other);
				const auto check = [&](std::string_view a_step, std::array<int, 3> a_expected) { return CheckShares(a_step, section, { 1, 2 }, other, 1, a_expected) ? 1 : 0; };
				MenuSliderChange(0, 1, section);
				MenuSliderChange(0, 2, section);
				MenuSliderChange(0, 1, other);
				int passed = 0;
				passed += check("start", { 0, 0, 0 });
				MenuSliderChange(60, 1, section);
				passed += check("first to 60", { 60, 0, 0 });
				MenuSliderChange(70, 2, section);
				passed += check("second to 70 (over 100 together; first stays)", { 60, 70, 0 });
				MenuSliderChange(100, 1, section);
				passed += check("first to 100 (second stays)", { 100, 70, 0 });
				MenuSliderChange(80, 1, other);
				passed += check("other section to 80 (this page stays)", { 100, 70, 80 });
				MenuSliderChange(0, 2, section);
				passed += check("second to 0 (first and the other page stay)", { 100, 0, 80 });
				logger::info("autotest: sliders: {} of 6 checks passed", passed);
				Console("qqq");
			}).detach();
			return;
		}
		// Apply all test: a slider change stays pending, leaving the menu answered Cancel puts it back, a close with nothing
		// pending asks nothing, and switching to another mod then leaving at once asks once, answered Apply all; the reload is photographed.
		if (s_mode == "mcm") {
			static std::atomic<int> s_loads{ 0 };
			if (!a_loaded) return;
			const auto load = ++s_loads;
			s_finishIssued.store(true);
			logger::info("autotest: apply all test, load {}", load);
			std::thread([load] {
				std::this_thread::sleep_for(std::chrono::seconds(10));
				if (load > 1) {
					LogApplyState("after the reload");
					LogStats("after apply all");
					Console("Screenshot");
					std::this_thread::sleep_for(std::chrono::seconds(3));
					Console("qqq");
					return;
				}
				LogApplyState("start");
				Console("Screenshot");
				MenuSliderChange(100);
				LogApplyState("slider moved to 100");
				RE::UIMessageQueue::GetSingleton()->AddMessage("PauseMenu", RE::UI_MESSAGE_TYPE::kShow);
				std::this_thread::sleep_for(std::chrono::seconds(3));
				SendMcmEvent({ "OnMCMMenuOpen|ArtDistributionTool" });
				std::this_thread::sleep_for(std::chrono::seconds(2));
				SendMcmEvent({ "OnMCMClose" });
				if (WaitForApplyPrompt(20)) {
					Console("Screenshot");
					std::this_thread::sleep_for(std::chrono::seconds(2));
					AnswerApplyPrompt(1);
				} else {
					logger::warn("autotest: leaving the menu with a change showed no prompt");
				}
				std::this_thread::sleep_for(std::chrono::seconds(6));
				LogApplyState("after Cancel");
				SendMcmEvent({ "OnMCMMenuClose|ArtDistributionTool" });
				const bool stray = WaitForApplyPrompt(6);
				logger::info("autotest: a close with nothing to apply showed a prompt: {}", stray);
				if (stray) AnswerApplyPrompt(1);
				MenuSliderChange(100);
				SendMcmEvent({ "OnMCMMenuClose|ArtDistributionTool" });
				SendMcmEvent({ "OnMCMClose" });
				if (WaitForApplyPrompt(20)) {
					Console("Screenshot");
					std::this_thread::sleep_for(std::chrono::seconds(2));
					AnswerApplyPrompt(0);
				} else {
					logger::warn("autotest: switching to another mod with a change showed no prompt");
				}
				std::this_thread::sleep_for(std::chrono::seconds(3));
				LogApplyState("after Apply all");
				for (int i = 0; i < 120; ++i) {
					std::this_thread::sleep_for(std::chrono::seconds(1));
					RunOnGameThread([] { ConfirmMessageBox(true); });
				}
			}).detach();
			return;
		}
		// Apply-now test: first load places the items, changes a setting through MCM (sConsoleAfterA) and reloads through
		// ADT.ReloadNow; the second load (the reloaded save) is photographed and the game quits.
		if (s_mode == "applynow") {
			static std::atomic<int> s_loads{ 0 };
			if (!a_loaded) return;
			const auto load = ++s_loads;
			s_finishIssued.store(true);
			logger::info("autotest: apply-now test, load {}", load);
			std::thread([load] {
				std::this_thread::sleep_for(std::chrono::seconds(10));
				if (load == 1) {
					for (std::size_t start = 0; start < s_place.size();) {
						const auto comma = s_place.find(',', start);
						Console(fmt::format("player.placeatme {} 1", s_place.substr(start, comma == std::string::npos ? std::string::npos : comma - start)));
						start = comma == std::string::npos ? s_place.size() : comma + 1;
					}
					Console("player.setangle x 70");
					std::this_thread::sleep_for(std::chrono::seconds(4));
					if (s_inventoryCheck) {
						// Picks the items up and drops the first one again, so the reloaded save shows what the co-save kept.
						RunOnGameThread(PickUpPlaced);
						std::this_thread::sleep_for(std::chrono::seconds(2));
						Console(fmt::format("player.drop {} 1", s_place.substr(0, s_place.find(','))));
						std::this_thread::sleep_for(std::chrono::seconds(2));
					}
					LogStats("before apply");
					Console("Screenshot");
					// Commands separated by '|', as a slider change does it: MCM's stored value, then the plugin's.
					for (std::size_t start = 0; start < s_consoleAfterA.size();) {
						const auto bar = s_consoleAfterA.find('|', start);
						Console(s_consoleAfterA.substr(start, bar == std::string::npos ? std::string::npos : bar - start));
						start = bar == std::string::npos ? s_consoleAfterA.size() : bar + 1;
					}
					std::this_thread::sleep_for(std::chrono::seconds(2));
					Console("cgf \"ADT.ReloadNow\"");
					for (int i = 0; i < 60; ++i) {
						std::this_thread::sleep_for(std::chrono::seconds(1));
						RunOnGameThread([] { ConfirmMessageBox(true); });
					}
				} else {
					LogStats("after apply");
					Console("Screenshot");
					std::this_thread::sleep_for(std::chrono::seconds(3));
					Console("qqq");
				}
			}).detach();
			return;
		}
		// Loading screen test: reloads the save iLoads times, so each loading screen can be photographed with its own framing.
		if (s_mode == "loadscreens") {
			static std::atomic<int> s_loads{ 0 };
			if (!a_loaded) return;
			const auto load = ++s_loads;
			s_finishIssued.store(true);
			logger::info("autotest: loading screen test, load {} of {}", load, s_loadCount);
			std::thread([load] {
				std::this_thread::sleep_for(std::chrono::seconds(5));
				if (load >= s_loadCount) {
					Console("qqq");
					return;
				}
				Console("load " + s_save);
				for (int i = 0; i < 30; ++i) {
					std::this_thread::sleep_for(std::chrono::seconds(1));
					RunOnGameThread([] { ConfirmMessageBox(true); });
				}
			}).detach();
			return;
		}
		if (!a_loaded) {
			logger::info("autotest: the load did not complete; still answering prompts");
			return;
		}
		if (s_finishIssued.exchange(true)) return;
		logger::info("autotest: post load game; screenshot A in 10 s, third-person screenshot P, refresh, screenshot B, slot 1 to 0, screenshot C, then quit");
		std::thread([] {
			std::this_thread::sleep_for(std::chrono::seconds(10));
			LogStats("A (initial)");
			Console("Screenshot");
			if (!s_consoleAfterA.empty()) {
				Console(s_consoleAfterA);
				std::this_thread::sleep_for(std::chrono::seconds(6));
				RunOnGameThread([] {
					auto* ui = RE::UI::GetSingleton();
					logger::info("autotest: after the console command: MainMenu open {}, at main menu {}", ui && ui->GetMenuOpen("MainMenu"), MainMenu::AtMainMenu());
				});
				Console("Screenshot");
			}
			// Third person with the camera detached and the player turned to face it shows the Pip-Boy on the left forearm.
			auto heading = std::make_shared<float>(0.0f);
			RunOnGameThread([heading] { if (auto* player = RE::PlayerCharacter::GetSingleton(); player) *heading = player->data.angle.z * 57.29578f; });
			std::this_thread::sleep_for(std::chrono::seconds(1));
			Console("cgf \"Game.ForceThirdPerson\"");
			std::this_thread::sleep_for(std::chrono::seconds(3));
			Console("tfc");
			std::this_thread::sleep_for(std::chrono::seconds(1));
			Console(fmt::format("player.setangle z {:.1f}", std::fmod(*heading + 180.0f, 360.0f)));
			Console("player.setangle x 0");
			std::this_thread::sleep_for(std::chrono::seconds(3));
			LogStats("P (third person, facing the camera)");
			Console("Screenshot");
			std::this_thread::sleep_for(std::chrono::seconds(2));
			Console("tfc");
			Console(fmt::format("player.setangle z {:.1f}", *heading));
			Console("cgf \"Game.ForceFirstPerson\"");
			std::this_thread::sleep_for(std::chrono::seconds(3));
			if (s_place.empty()) {
				Console("player.placeatme 249ED7 1");
				Console("player.placeatme 249ED7 1");
			} else {
				// The items drop at the player's feet, or "@distance" units in front; the camera tilts to iPlacePitch (70: steeply down).
				for (std::size_t start = 0; start < s_place.size();) {
					const auto comma = s_place.find(',', start);
					const auto entry = s_place.substr(start, comma == std::string::npos ? std::string::npos : comma - start);
					const auto at = entry.find('@');
					const auto id = ResolvePlace(entry.substr(0, at));
					Console(at == std::string::npos ? fmt::format("player.placeatme {} 1", id) : fmt::format("player.placeatme {} 1 {} 0", id, entry.substr(at + 1)));
					start = comma == std::string::npos ? s_place.size() : comma + 1;
				}
				Console(fmt::format("player.setangle x {}", s_placePitch));
			}
			std::this_thread::sleep_for(std::chrono::seconds(4));
			LogStats("A2 (placed items)");
			RunOnGameThread(LogPlacedMaterials);
			Console("Screenshot");
			if (s_nameCheck) {
				auto picked = std::make_shared<std::atomic<std::uint32_t>>(0);
				RunOnGameThread([picked] { picked->store(NameCheckPlaced()); });
				std::this_thread::sleep_for(std::chrono::seconds(3));
				RunOnGameThread([picked] { if (picked->load()) NameCheckInventory(picked->load()); });
				std::this_thread::sleep_for(std::chrono::seconds(1));
			}
			if (s_inventoryCheck) {
				// Picks the placed items up, opens the player's inventory with model logging on and photographs its 3D view.
				RunOnGameThread(PickUpPlaced);
				std::this_thread::sleep_for(std::chrono::seconds(2));
				if (!s_noContainer) {
					Engine::SetModelTracing(true);
					Console("player.openactorcontainer 1");
					std::this_thread::sleep_for(std::chrono::seconds(5));
					LogStats("I (inventory open)");
					Console("Screenshot");
					std::this_thread::sleep_for(std::chrono::seconds(2));
					Engine::SetModelTracing(false);
					RunOnGameThread([] { RE::UIMessageQueue::GetSingleton()->AddMessage("ContainerMenu", RE::UI_MESSAGE_TYPE::kHide); });
					std::this_thread::sleep_for(std::chrono::seconds(2));
				}
				Engine::SetModelTracing(true);
				for (std::size_t start = 0; start < s_place.size();) {
					const auto comma = s_place.find(',', start);
					Console(fmt::format("player.drop {} 1", s_place.substr(start, comma == std::string::npos ? std::string::npos : comma - start)));
					start = comma == std::string::npos ? s_place.size() : comma + 1;
				}
				std::this_thread::sleep_for(std::chrono::seconds(4));
				Engine::SetModelTracing(false);
				LogStats("D (items dropped again)");
				Console("Screenshot");
			}
			std::this_thread::sleep_for(std::chrono::seconds(3));
			if (s_skipReload) {
				if (s_inventoryCheck) {
					// Saves and loads again, so the log shows the carried and dropped items read back from the co-save.
					Console("save adtinv");
					std::this_thread::sleep_for(std::chrono::seconds(20));
					Console("load adtinv");
					for (int i = 0; i < 30; ++i) {
						std::this_thread::sleep_for(std::chrono::seconds(1));
						RunOnGameThread([] { ConfirmMessageBox(true); });
					}
				}
				Console("qqq");
				return;
			}
			RunOnGameThread([] {
				Settings::SetSeed(Settings::Seed() + 1);
				Distributor::Reapply();
			});
			std::this_thread::sleep_for(std::chrono::seconds(2));
			ReloadArea(true);
			LogStats("B (new seed)");
			Console("Screenshot");
			std::this_thread::sleep_for(std::chrono::seconds(3));
			RunOnGameThread([] {
				Settings::SetPercent("paintings", 1, 0);
				Distributor::Reapply();
			});
			std::this_thread::sleep_for(std::chrono::seconds(2));
			ReloadArea(true);
			LogStats("C (paintings slot 1 at 0)");
			Console("Screenshot");
			std::this_thread::sleep_for(std::chrono::seconds(3));
			Console("qqq");
		}).detach();
	}
}
