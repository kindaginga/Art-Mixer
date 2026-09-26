#include "Settings.h"

namespace GShuf::Settings
{
	namespace
	{
		std::mutex s_mutex;
		Values s_values;

		[[nodiscard]] std::string Trim(std::string_view a_text)
		{
			const auto begin = a_text.find_first_not_of(" \t\r\n");
			if (begin == std::string_view::npos) return {};
			const auto end = a_text.find_last_not_of(" \t\r\n");
			return std::string(a_text.substr(begin, end - begin + 1));
		}

		[[nodiscard]] std::optional<int> ParseInt(std::string_view a_text)
		{
			const auto text = Trim(a_text);
			if (text.empty()) return std::nullopt;
			try {
				return static_cast<int>(std::stod(text));
			} catch (...) {
				return std::nullopt;
			}
		}

		// Applies one MCM ini file onto the in-memory values without locking.
		void ApplyIni(const std::filesystem::path& a_path)
		{
			std::ifstream file(a_path);
			if (!file) return;
			std::string line;
			std::string section;
			int applied = 0;
			while (std::getline(file, line)) {
				const auto text = Trim(line);
				if (text.empty() || text[0] == ';' || text[0] == '#') continue;
				if (text.front() == '[' && text.back() == ']') {
					section = Lower(text.substr(1, text.size() - 2));
					continue;
				}
				const auto eq = text.find('=');
				if (eq == std::string::npos) continue;
				const auto key = Lower(Trim(text.substr(0, eq)));
				const auto value = ParseInt(text.substr(eq + 1));
				if (!value) continue;
				if (section == "main") {
					if (key == "bblankcanvases") s_values.blankCanvases = *value != 0;
					else if (key == "iseed") s_values.seed = *value;
					++applied;
				} else if (key.starts_with("islot")) {
					const auto slot = ParseInt(key.substr(5));
					if (!slot || *slot < 1 || *slot > kSlotsPerCategory) continue;
					s_values.categories[section].percent[*slot - 1] = std::clamp(*value, 0, 100);
					++applied;
				}
			}
			logger::info("settings: applied {} values from {}", applied, a_path.string());
		}
	}

	std::string Lower(std::string_view a_text)
	{
		std::string out(a_text);
		for (auto& c : out) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
		return out;
	}

	std::string NormalizeCategory(std::string_view a_category)
	{
		return Lower(Trim(a_category));
	}

	void Load()
	{
		std::scoped_lock lock(s_mutex);
		s_values = Values{};
		ApplyIni(std::filesystem::path("Data") / "MCM" / "Config" / std::string(kModName) / "settings.ini");
		ApplyIni(std::filesystem::path("Data") / "MCM" / "Settings" / (std::string(kModName) + ".ini"));
		logger::info("settings: blank canvases {}, seed {}, {} categories with values", s_values.blankCanvases, s_values.seed, s_values.categories.size());
	}

	Values Get()
	{
		std::scoped_lock lock(s_mutex);
		return s_values;
	}

	void SetPercent(std::string_view a_category, int a_slot, int a_value)
	{
		if (a_slot < 1 || a_slot > kSlotsPerCategory) return;
		std::scoped_lock lock(s_mutex);
		s_values.categories[NormalizeCategory(a_category)].percent[a_slot - 1] = std::clamp(a_value, 0, 100);
	}

	void SetBlankCanvases(bool a_enabled)
	{
		std::scoped_lock lock(s_mutex);
		s_values.blankCanvases = a_enabled;
	}

	void SetSeed(int a_seed)
	{
		std::scoped_lock lock(s_mutex);
		s_values.seed = a_seed;
	}

	int Seed()
	{
		std::scoped_lock lock(s_mutex);
		return s_values.seed;
	}
}
