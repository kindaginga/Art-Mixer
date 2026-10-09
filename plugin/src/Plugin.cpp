#include "Plugin.h"

#include "BuildStamp.h"

#include "ApplyNow.h"
#include "AutoTest.h"
#include "Catalog.h"
#include "CoreCheck.h"
#include "Distributor.h"
#include "Engine.h"
#include "GameArchives.h"
#include "Inventory.h"
#include "LoadScreens.h"
#include "MainMenu.h"
#include "MenuRotation.h"
#include "Packs.h"
#include "Papyrus.h"
#include "Settings.h"

namespace
{
	[[nodiscard]] constexpr std::uint32_t PackVersion(std::uint32_t a_major, std::uint32_t a_minor, std::uint32_t a_build) noexcept
	{
		return ((a_major & 0xFF) << 24) | ((a_minor & 0xFF) << 16) | ((a_build & 0xFFF) << 4);
	}

	[[nodiscard]] constexpr F4SE::PluginVersionData MakePluginVersionData() noexcept
	{
		F4SE::PluginVersionData data{};
		data.pluginVersion = PackVersion(
			static_cast<std::uint32_t>(Version::MAJOR),
			static_cast<std::uint32_t>(Version::MINOR),
			static_cast<std::uint32_t>(Version::PATCH));
		for (std::size_t i = 0; i < Version::PROJECT.size() && i < std::size(data.name) - 1; ++i) {
			data.name[i] = Version::PROJECT[i];
		}
		data.addressIndependence = F4SE::PluginVersionData::kAddressIndependence_Signatures;
		data.structureIndependence =
			F4SE::PluginVersionData::kStructureIndependence_1_10_980Layout |
			F4SE::PluginVersionData::kStructureIndependence_1_11_137Layout;
		return data;
	}

	[[nodiscard]] bool InitializeLogger()
	{
		auto path = logger::log_directory();
		if (!path) return false;
		*path /= fmt::format(FMT_STRING("{}.log"), Version::PROJECT);
		auto sink = std::make_shared<spdlog::sinks::basic_file_sink_mt>(path->string(), true);
		auto log = std::make_shared<spdlog::logger>("global log"s, std::move(sink));
		log->set_level(spdlog::level::info);
		log->flush_on(spdlog::level::info);
		spdlog::set_default_logger(std::move(log));
		spdlog::set_pattern("[%H:%M:%S.%e] [%^%l%$] %v"s);
		return true;
	}

	void OnMessage(F4SE::MessagingInterface::Message* a_message)
	{
		if (!a_message) return;
		switch (a_message->type) {
		case F4SE::MessagingInterface::kPostLoad:
			ArtMixer::MenuRotation::OnPluginsLoaded();
			break;
		case F4SE::MessagingInterface::kGameDataReady:
			ArtMixer::CoreCheck::OnGameDataReady();
			ArtMixer::GameArchives::Install();
			ArtMixer::Catalog::ResolveForms();
			ArtMixer::Packs::OnGameDataReady();
			ArtMixer::Distributor::ApplyTextureSets();
			ArtMixer::Distributor::ApplyBounds();
			ArtMixer::Distributor::ApplyWorldMap();
			if (!ArtMixer::AutoTest::Skips("pickmodels")) ArtMixer::Distributor::ApplyPickModels();
			if (!ArtMixer::AutoTest::Skips("inventory")) ArtMixer::Inventory::OnGameDataReady();
			if (!ArtMixer::AutoTest::Skips("loadscreens")) ArtMixer::LoadScreens::OnGameDataReady();
			ArtMixer::MainMenu::OnGameDataReady();
			ArtMixer::AutoTest::OnGameDataReady();
			break;
		case F4SE::MessagingInterface::kPostSaveGame:
			ArtMixer::ApplyNow::OnPostSaveGame(a_message->data, a_message->dataLen);
			break;
		case F4SE::MessagingInterface::kPreLoadGame:
			// "New layout on every load": the save's areas load with the next seed; Apply now's own reload keeps the current one.
			if (!ArtMixer::ApplyNow::OnPreLoadGame() && ArtMixer::Settings::Get().newLayoutEachLoad) {
				ArtMixer::Settings::SetSeed(ArtMixer::Settings::Seed() + 1);
				ArtMixer::Settings::Save();
				ArtMixer::Distributor::Reapply();
				logger::info("new layout on every load: seed {}", ArtMixer::Settings::Seed());
			}
			break;
		case F4SE::MessagingInterface::kPostLoadGame:
			// The message's data pointer carries the load result itself.
			ArtMixer::AutoTest::OnPostLoadGame(reinterpret_cast<std::uintptr_t>(a_message->data) != 0);
			break;
		default:
			break;
		}
	}
}

extern "C" DLLEXPORT constinit F4SE::PluginVersionData F4SEPlugin_Version = MakePluginVersionData();

bool Plugin::Initialize(const F4SE::LoadInterface* a_f4se)
{
	if (!InitializeLogger()) return false;
	if (a_f4se->IsEditor()) {
		logger::critical("loaded in editor");
		return false;
	}
	F4SE::Init(a_f4se);
	const auto& module = REL::Module::get();
	logger::info("{} v{} (build {}) loaded on runtime {}", Version::PROJECT, Version::NAME, BuildStamp::COMMIT, module.version().string());

	const bool engine = ArtMixer::Engine::Initialize();
	ArtMixer::Catalog::Load();
	ArtMixer::Packs::Load();
	ArtMixer::MenuRotation::Detect();
	if (engine) {
		ArtMixer::Engine::InstallHooks(&ArtMixer::Distributor::OnModelLoaded);
		ArtMixer::Engine::InstallReferenceHook(&ArtMixer::Distributor::OnReference3D);
		ArtMixer::Engine::InstallModelHook(&ArtMixer::Distributor::ChooseModel);
		ArtMixer::Engine::InstallSwapApplyHook();
		if (!ArtMixer::AutoTest::Skips("textures")) ArtMixer::Engine::InstallTextureHook(&ArtMixer::Distributor::ChooseTexture);
	}
	if (!ArtMixer::AutoTest::Skips("inventory")) ArtMixer::Inventory::Install();
	ArtMixer::MainMenu::Install();
	if (engine) ArtMixer::Engine::InstallMovieHook(&ArtMixer::MainMenu::RedirectMovie);
	ArtMixer::MainMenu::LoadFromPacks();
	// The menu lists are built first: the applied settings name their picks by entry.
	ArtMixer::Settings::Load();
	ArtMixer::MainMenu::LoadPicks();
	// Random Main Menu reads its settings file when it loads, after this plugin, so the file is written here and again once its module is known.
	ArtMixer::MenuRotation::Apply();
	ArtMixer::AutoTest::Configure();

	F4SE::GetMessagingInterface()->RegisterListener(OnMessage);
	F4SE::GetPapyrusInterface()->Register(ArtMixer::Papyrus::Register);
	return true;
}
