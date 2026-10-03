#include "ApplyNow.h"

#include "Settings.h"

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>

namespace ADT::ApplyNow
{
	namespace
	{
		constexpr std::string_view kSaveName{ "ADTApply" };
		// How long a requested save may take before the reload is given up.
		constexpr auto kSaveTimeout = std::chrono::seconds(60);
		// How long the save file may take to show its new write time once the game reports the save.
		constexpr auto kWriteWait = std::chrono::seconds(10);

		enum class Stage
		{
			kIdle,
			kSaving,          // confirmed; waiting for the save to be written
			kReturningToMenu  // saved; waiting for the main menu before loading
		};
		std::atomic<Stage> s_stage{ Stage::kIdle };
		std::atomic<std::uint32_t> s_request{ 0 };                            // numbers each Reload, so a late check acts only on its own request
		std::atomic<std::filesystem::file_time_type::rep> s_requestedAt{ 0 };  // file time of the current request
		std::atomic<bool> s_checking{ false };                                // a check of the save file is running
		std::atomic<bool> s_reloading{ false };
		std::atomic<int> s_answer{ -1 };

		// Stores the pressed button (0 Apply all, 1 Cancel) for the menu script that waits on it.
		class Prompt : public RE::IMessageBoxCallback
		{
		public:
			void operator()(std::uint8_t a_button) override
			{
				logger::info("apply all: answered {}", a_button == 0 ? "Apply all" : "Cancel");
				s_answer.store(a_button == 0 ? 0 : 1);
			}
		};

		// Closes the notice box; the button needs no action.
		class Notice : public RE::IMessageBoxCallback
		{
		public:
			void operator()(std::uint8_t) override {}
		};

		// The folder the game writes saves to: My Games\Fallout4 plus the sLocalSavePath its INI files set (Saves\ when none does).
		[[nodiscard]] std::optional<std::filesystem::path> SaveFolder()
		{
			const auto logs = logger::log_directory();
			if (!logs) return std::nullopt;
			const auto root = logs->parent_path();
			std::wstring local{ L"Saves\\" };
			for (const auto* ini : { L"Fallout4.ini", L"Fallout4Prefs.ini", L"Fallout4Custom.ini" }) {
				std::array<wchar_t, MAX_PATH> value{};
				const auto file = (root / ini).wstring();
				if (::GetPrivateProfileStringW(L"General", L"sLocalSavePath", L"", value.data(), static_cast<DWORD>(value.size()), file.c_str()) > 0) local = value.data();
			}
			return root / local;
		}

		// True when ADTApply.fos was written at or after the current request.
		[[nodiscard]] bool SaveWritten()
		{
			const auto folder = SaveFolder();
			if (!folder) return false;
			std::error_code error;
			const auto written = std::filesystem::last_write_time(*folder / (std::string(kSaveName) + ".fos"), error);
			return !error && written.time_since_epoch().count() >= s_requestedAt.load();
		}

		// The save's name without folder or extension, from the post-save message data; empty when the message carries none.
		[[nodiscard]] std::string SavedName(const void* a_data, std::uint32_t a_length)
		{
			if (!a_data || a_length == 0) return {};
			std::string_view text{ static_cast<const char*>(a_data), a_length };
			text = text.substr(0, text.find('\0'));
			if (const auto slash = text.find_last_of("\\/"); slash != std::string_view::npos) text.remove_prefix(slash + 1);
			if (text.size() > 4 && Settings::Lower(text.substr(text.size() - 4)) == ".fos") text.remove_suffix(4);
			return std::string(text);
		}

		// Ends a request that is still saving: nothing reloads, and a box tells the player why.
		void Fail(std::uint32_t a_request, std::string_view a_why)
		{
			auto expected = Stage::kSaving;
			if (s_request.load() != a_request || !s_stage.compare_exchange_strong(expected, Stage::kIdle)) return;
			logger::warn("apply all: {}; nothing is reloaded", a_why);
			F4SE::GetTaskInterface()->AddTask([] {
				if (auto* manager = RE::MessageMenuManager::GetSingleton(); manager) {
					manager->Create("Art Distribution Tool",
						"The game could not save as \"ADTApply\", so it did not return to the main menu or reload.\n\nYour changes are applied and show in full the next time you load a save.",
						new Notice(), RE::WARNING_TYPES::kDefault, "OK");
				}
			});
		}
	}

	bool Ask(bool a_leaving)
	{
		auto* manager = RE::MessageMenuManager::GetSingleton();
		if (!manager) {
			logger::error("apply all: message box manager unavailable");
			return false;
		}
		s_answer.store(-1);
		logger::info("apply all: asking ({})", a_leaving ? "leaving the menu with changes" : "button");
		F4SE::GetTaskInterface()->AddTask([a_leaving] {
			const char* body = a_leaving ?
				"Apply your changes?\n\nApply all: saves as \"ADTApply\" and reloads.\nCancel: discards them." :
				"Apply your changes?\n\nSaves as \"ADTApply\" and reloads.";
			RE::MessageMenuManager::GetSingleton()->Create("Art Distribution Tool", body, new Prompt(), RE::WARNING_TYPES::kDefault, "Apply all", "Cancel");
		});
		return true;
	}

	int Answer()
	{
		return s_answer.load();
	}

	void Reload()
	{
		if (auto* ui = RE::UI::GetSingleton(); ui && ui->GetMenuOpen("MainMenu")) {
			logger::info("apply all: no game is loaded, so nothing is reloaded");
			return;
		}
		const auto request = s_request.fetch_add(1) + 1;
		s_requestedAt.store(std::filesystem::file_time_type::clock::now().time_since_epoch().count());
		s_stage.store(Stage::kSaving);
		logger::info("apply all: saving as {}", kSaveName);
		F4SE::GetTaskInterface()->AddTask([] { RE::Console::ExecuteCommand(fmt::format("save {}", kSaveName).c_str()); });
		// Gives the request up when no written save has ended it in time.
		std::thread([request] {
			std::this_thread::sleep_for(kSaveTimeout);
			Fail(request, fmt::format("{} was not saved within {} seconds", kSaveName, kSaveTimeout.count()));
		}).detach();
	}

	void OnPostSaveGame(const void* a_data, std::uint32_t a_length)
	{
		if (s_stage.load() != Stage::kSaving) return;
		const auto name = SavedName(a_data, a_length);
		// Another save finishing (an autosave, a quicksave) leaves the request waiting for its own.
		if (!name.empty() && Settings::Lower(name) != Settings::Lower(kSaveName)) {
			logger::info("apply all: save '{}' is not {}; still waiting", name, kSaveName);
			return;
		}
		if (s_checking.exchange(true)) return;
		const auto request = s_request.load();
		// Returns to the main menu only once ADTApply.fos carries a write time from this request.
		std::thread([request] {
			const auto deadline = std::chrono::steady_clock::now() + kWriteWait;
			bool written = SaveWritten();
			while (!written && std::chrono::steady_clock::now() < deadline) {
				std::this_thread::sleep_for(std::chrono::milliseconds(250));
				written = SaveWritten();
			}
			s_checking.store(false);
			if (!written) {
				Fail(request, fmt::format("the game reported a save, but {}.fos was not written", kSaveName));
				return;
			}
			auto expected = Stage::kSaving;
			if (s_request.load() != request || !s_stage.compare_exchange_strong(expected, Stage::kReturningToMenu)) return;
			// A save of the current area loads without rebuilding it, so the load goes through the main menu, which rebuilds everything.
			logger::info("apply all: {}.fos written; returning to the main menu", kSaveName);
			F4SE::GetTaskInterface()->AddTask([] {
				if (auto* game = RE::Main::GetSingleton(); game) game->resetGame = true;
			});
		}).detach();
	}

	bool OnPreLoadGame()
	{
		auto expected = Stage::kSaving;
		if (s_stage.compare_exchange_strong(expected, Stage::kIdle)) logger::info("apply all: another save is loading before {} was written; nothing is reloaded", kSaveName);
		return s_reloading.exchange(false);
	}

	void OnMainMenuReady()
	{
		auto expected = Stage::kReturningToMenu;
		if (!s_stage.compare_exchange_strong(expected, Stage::kIdle)) return;
		logger::info("apply all: main menu up; loading {}", kSaveName);
		s_reloading.store(true);
		std::thread([] {
			std::this_thread::sleep_for(std::chrono::seconds(1));
			F4SE::GetTaskInterface()->AddTask([] { RE::Console::ExecuteCommand(fmt::format("load {}", kSaveName).c_str()); });
		}).detach();
	}
}
