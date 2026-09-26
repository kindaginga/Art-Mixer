#include "Plugin.h"

#include "AutoTest.h"
#include "Catalog.h"
#include "Distributor.h"
#include "Engine.h"
#include "MainMenu.h"
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
		case F4SE::MessagingInterface::kGameDataReady:
			GShuf::MainMenu::OnGameDataReady();
			GShuf::AutoTest::OnGameDataReady();
			break;
		case F4SE::MessagingInterface::kPostLoadGame:
			GShuf::AutoTest::OnPostLoadGame();
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
	logger::info("{} v{} loaded on runtime {}", Version::PROJECT, Version::NAME, module.version().string());

	const bool engine = GShuf::Engine::Initialize();
	GShuf::Catalog::Load();
	GShuf::Packs::Load();
	GShuf::Settings::Load();
	if (engine) {
		GShuf::Engine::InstallHooks(&GShuf::Distributor::OnModelLoaded);
		GShuf::Engine::InstallReferenceHook(&GShuf::Distributor::OnReference3D);
	}
	GShuf::MainMenu::Install();
	GShuf::MainMenu::LoadFromPacks();
	GShuf::AutoTest::Configure();

	F4SE::GetMessagingInterface()->RegisterListener(OnMessage);
	F4SE::GetPapyrusInterface()->Register(GShuf::Papyrus::Register);
	return true;
}
