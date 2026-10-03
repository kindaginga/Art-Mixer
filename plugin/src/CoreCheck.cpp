#include "CoreCheck.h"

#include "Settings.h"

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include <bcrypt.h>

namespace ADT::CoreCheck
{
	namespace
	{
		constexpr auto kList = "ADT/core-files.json";

		// Closes the message box; the button needs no action.
		class Notice : public RE::IMessageBoxCallback
		{
		public:
			void operator()(std::uint8_t) override {}
		};

		// The file's bytes, or nothing when it cannot be opened.
		[[nodiscard]] std::optional<std::vector<std::uint8_t>> ReadAll(const std::filesystem::path& a_path)
		{
			std::ifstream file(a_path, std::ios::binary);
			if (!file) return std::nullopt;
			return std::vector<std::uint8_t>(std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>());
		}

		// The SHA-256 of the bytes as lowercase hex; empty when Windows cannot compute it.
		[[nodiscard]] std::string Sha256(const std::vector<std::uint8_t>& a_bytes)
		{
			std::array<UCHAR, 32> digest{};
			BCRYPT_ALG_HANDLE algorithm = nullptr;
			if (::BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) return {};
			auto bytes = a_bytes;
			BCRYPT_HASH_HANDLE hash = nullptr;
			auto status = ::BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0);
			if (status >= 0) status = ::BCryptHashData(hash, bytes.data(), static_cast<ULONG>(bytes.size()), 0);
			if (status >= 0) status = ::BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0);
			if (hash) ::BCryptDestroyHash(hash);
			::BCryptCloseAlgorithmProvider(algorithm, 0);
			if (status < 0) return {};
			std::string hex;
			for (const auto byte : digest) hex += fmt::format("{:02x}", byte);
			return hex;
		}

		// The listed files the game loads that are missing or changed: the mod's plugin file and its scripts. a_checked receives how many were checked.
		[[nodiscard]] std::vector<std::string> Damaged(int& a_checked)
		{
			const auto data = std::filesystem::path("Data");
			std::vector<std::string> damaged;
			try {
				std::ifstream file(data / kList);
				if (!file) return { kList };
				const auto list = nlohmann::json::parse(file);
				for (const auto& row : list.at("files")) {
					const auto path = row.at("path").get<std::string>();
					const auto lower = Settings::Lower(path);
					if (!lower.ends_with(".esl") && !lower.ends_with(".pex")) continue;
					++a_checked;
					const auto bytes = ReadAll(data / std::filesystem::path(path));
					if (!bytes || bytes->size() != row.at("size").get<std::size_t>()) {
						damaged.push_back(path);
						continue;
					}
					const auto hash = Sha256(*bytes);
					if (hash.empty()) {
						logger::error("own files: SHA-256 unavailable, {} not compared", path);
						continue;
					}
					if (hash != Settings::Lower(row.at("sha256").get<std::string>())) damaged.push_back(path);
				}
			} catch (const std::exception&) {
				return { kList };
			}
			return damaged;
		}
	}

	void OnGameDataReady()
	{
		int checked = 0;
		const auto damaged = Damaged(checked);
		if (damaged.empty()) {
			logger::info("own files: {} checked, all intact", checked);
			return;
		}
		std::string names;
		for (const auto& path : damaged) names += (names.empty() ? "" : ", ") + path;
		logger::warn("own files: {} missing or changed: {}", damaged.size(), names);
		std::string body = "These files of Art Distribution Tool are missing or changed:\n";
		for (const auto& path : damaged) body += "\n" + path;
		body += "\n\nOpen the converter (Tools\\ArtDistributionTool\\ArtDistributionToolConverter.exe in the mod), which restores them where it can, or reinstall Art Distribution Tool with your mod manager.";
		F4SE::GetTaskInterface()->AddTask([body] {
			if (auto* manager = RE::MessageMenuManager::GetSingleton(); manager) {
				manager->Create("Art Distribution Tool", body.c_str(), new Notice(), RE::WARNING_TYPES::kDefault, "OK");
				logger::info("own files: message shown");
			}
		});
	}
}
