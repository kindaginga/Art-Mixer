#include "AutoTest.h"

#include "Distributor.h"
#include "Engine.h"
#include "MainMenu.h"
#include "Settings.h"

#include <cmath>

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>

namespace GShuf::AutoTest
{
	namespace
	{
		bool s_enabled{ false };
		std::string s_save;
		std::string s_mode;
		std::string s_menuVideo;
		std::atomic<bool> s_loadIssued{ false };
		std::atomic<bool> s_finishIssued{ false };
		std::atomic<bool> s_confirmed{ false };

		void RunOnGameThread(std::function<void()> a_task)
		{
			F4SE::GetTaskInterface()->AddTask(std::move(a_task));
		}

		// Runs a console command on the game thread and waits until it has actually executed (loading screens stall the queue).
		void Console(std::string a_command)
		{
			auto done = std::make_shared<std::atomic<bool>>(false);
			RunOnGameThread([command = std::move(a_command), done] {
				RE::Console::ExecuteCommand(command.c_str());
				logger::info("autotest: issued '{}'", command);
				done->store(true);
			});
			for (int i = 0; i < 1200 && !done->load(); ++i) std::this_thread::sleep_for(std::chrono::milliseconds(100));
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
		void ConfirmMessageBox()
		{
			if (s_confirmed.load() || s_finishIssued.load()) return;
			auto ui = RE::UI::GetSingleton();
			if (!ui) return;
			auto menu = ui->GetMenu<RE::MessageBoxMenu>();
			if (!menu) return;
			const auto dataVtable = REL::Relocation<std::uintptr_t>{ RE::VTABLE::MessageBoxData[0] }.address();
			const auto raw = reinterpret_cast<const std::uintptr_t*>(menu.get());
			RE::MessageBoxData* data = nullptr;
			for (int i = 0x18; i < 0x140 && !data; i += 8) {
				if (!IsReadable(raw + i / 8, 8)) break;
				const auto candidate = reinterpret_cast<const std::uintptr_t*>(raw[i / 8]);
				if (IsReadable(candidate, 0x68) && candidate[0] == dataVtable) data = reinterpret_cast<RE::MessageBoxData*>(raw[i / 8]);
			}
			if (!data) return;
			auto callback = data->callback.get();
			if (!IsReadable(callback, 0x10)) return;
			logger::info("autotest: confirming message box '{}'", data->bodyText.c_str() ? data->bodyText.c_str() : "");
			s_confirmed.store(true);
			(*callback)(0);
			RE::UIMessageQueue::GetSingleton()->AddMessage(RE::MessageBoxMenu::MENU_NAME, RE::UI_MESSAGE_TYPE::kHide);
		}

		// Reloads the loaded area through the console and puts the player back where they stood.
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
				std::this_thread::sleep_for(std::chrono::seconds(20));
			}
			Console(fmt::format("cow Commonwealth {} {}", cellX, cellY));
			std::this_thread::sleep_for(std::chrono::seconds(20));
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
			logger::info("autotest: {}: models seen {}, tracked geometries {}, swaps applied {}, Load3D calls {}, references with art {}, reference swaps {}", a_stage, stats.modelsSeen, stats.tracked, stats.swapped, stats.set3DCalls, stats.referencesSeen, stats.referenceSwaps);
		}
	}

	bool Configure()
	{
		const auto path = std::filesystem::path("Data") / "F4SE" / "Plugins" / "GalleryShuffle.ini";
		std::ifstream file(path);
		if (!file) return false;
		std::string line;
		bool inSection = false;
		bool picker = true;
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
			if (key.find("bpicker") != std::string::npos) picker = value == "1";
			if (key.find("bskipvideos") != std::string::npos) skipVideos = value == "1";
		}
		if (s_enabled) {
			MainMenu::SetSkipStartupVideos(skipVideos);
			MainMenu::SetPickerEnabled(picker);
			logger::info("autotest: picker {}, startup videos {}", picker ? "on" : "off", skipVideos ? "skipped" : "played");
		}
		if (s_enabled && s_mode == "mainmenu") {
			logger::info("autotest: main menu mode, video '{}'", s_menuVideo);
			if (!s_menuVideo.empty()) MainMenu::SetOptions({ { "Vanilla", "" }, { "Test video", s_menuVideo } }, 0);
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
				std::this_thread::sleep_for(std::chrono::seconds(2));
				RunOnGameThread([] { MainMenu::Select(1); });
				std::this_thread::sleep_for(std::chrono::seconds(10));
				Console("Screenshot");
				std::this_thread::sleep_for(std::chrono::seconds(3));
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
			for (int i = 0; i < 40 && !s_confirmed.load() && !s_finishIssued.load(); ++i) {
				std::this_thread::sleep_for(std::chrono::seconds(1));
				RunOnGameThread(ConfirmMessageBox);
			}
		}).detach();
	}

	void OnPostLoadGame()
	{
		if (!s_enabled || s_finishIssued.exchange(true)) return;
		logger::info("autotest: post load game; screenshot A in 10 s, third-person screenshot P, refresh, screenshot B, slot 1 to 0, screenshot C, then quit");
		std::thread([] {
			std::this_thread::sleep_for(std::chrono::seconds(10));
			LogStats("A (initial)");
			Console("Screenshot");
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
			Console("player.placeatme 249ED7 1");
			Console("player.placeatme 249ED7 1");
			std::this_thread::sleep_for(std::chrono::seconds(4));
			LogStats("A2 (two placed frames)");
			Console("Screenshot");
			std::this_thread::sleep_for(std::chrono::seconds(3));
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
