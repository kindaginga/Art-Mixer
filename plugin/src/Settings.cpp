#include "Settings.h"

namespace ADT::Settings
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

		// The applied settings file the plugin writes next to its DLL.
		[[nodiscard]] std::filesystem::path AppliedPath()
		{
			return std::filesystem::path("Data") / "F4SE" / "Plugins" / (std::string(kModName) + "_Applied.ini");
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
					// Only the ADT_ sections the MCM pages write; anything else is a leftover of an older layout.
					const auto name = Lower(text.substr(1, text.size() - 2));
					section = name.starts_with(kSectionPrefix) ? name.substr(kSectionPrefix.size()) : std::string();
					continue;
				}
				if (section.empty()) continue;
				const auto eq = text.find('=');
				if (eq == std::string::npos) continue;
				const auto key = Lower(Trim(text.substr(0, eq)));
				const auto value = ParseInt(text.substr(eq + 1));
				if (!value) continue;
				if (section == "main") {
					if (key == "bnewlayouteachload") s_values.newLayoutEachLoad = *value != 0;
					else if (key == "iseed") s_values.seed = *value;
					++applied;
				} else if (key.starts_with("bon")) {
					// An Other replacers pack's switch: on is the whole share, off none.
					const auto slot = ParseInt(key.substr(3));
					if (section != "other" || !slot || *slot < 1 || *slot > kSlotsPerCategory) continue;
					s_values.categories[section].percent[*slot - 1] = *value != 0 ? 100 : 0;
					++applied;
				} else if (key.starts_with("islot")) {
					// The Other replacers page stores switches, not shares.
					const auto slot = ParseInt(key.substr(5));
					if (section == "other" || !slot || *slot < 1 || *slot > kSlotsPerCategory) continue;
					s_values.categories[section].percent[*slot - 1] = std::clamp(*value, 0, 100);
					++applied;
				} else if (key == "ipick") {
					// A list page: the chosen slot gets everything, 0 means vanilla.
					auto& percent = s_values.categories[section].percent;
					for (int i = 0; i < kSlotsPerCategory; ++i) percent[i] = (*value == i + 1) ? 100 : 0;
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

	std::string RelativePath(std::string_view a_path, std::string_view a_folder)
	{
		auto path = Lower(a_path);
		std::replace(path.begin(), path.end(), '/', '\\');
		while (!path.empty() && path.front() == '\\') path.erase(0, 1);
		if (path.starts_with("data\\")) path.erase(0, 5);
		if (path.size() > a_folder.size() && path.starts_with(a_folder) && path[a_folder.size()] == '\\') path.erase(0, a_folder.size() + 1);
		return path;
	}

	std::string NormalizeCategory(std::string_view a_category)
	{
		auto category = Lower(Trim(a_category));
		if (category.starts_with(kSectionPrefix)) category.erase(0, kSectionPrefix.size());
		return category;
	}

	std::string MenuKey(std::string_view a_prefix, int a_slot, std::string_view a_category)
	{
		return fmt::format("{}{}:{}", a_prefix, a_slot, a_category);
	}

	namespace
	{
		[[nodiscard]] std::filesystem::path MenuDefaultsPath()
		{
			return std::filesystem::path("Data") / "MCM" / "Config" / std::string(kModName) / "settings.ini";
		}

		[[nodiscard]] std::filesystem::path MenuPath()
		{
			return std::filesystem::path("Data") / "MCM" / "Settings" / (std::string(kModName) + ".ini");
		}

		// Reads an ini file's whole-number values as "key:Section" ids, spelled as the file spells them.
		void ReadMenuFile(const std::filesystem::path& a_path, std::unordered_map<std::string, int>& a_values)
		{
			std::ifstream file(a_path);
			std::string line;
			std::string section;
			while (std::getline(file, line)) {
				const auto text = Trim(line);
				if (text.empty() || text[0] == ';' || text[0] == '#') continue;
				if (text.front() == '[' && text.back() == ']') {
					section = text.substr(1, text.size() - 2);
					continue;
				}
				const auto eq = text.find('=');
				if (eq == std::string::npos) continue;
				if (const auto value = ParseInt(text.substr(eq + 1))) a_values[Trim(text.substr(0, eq)) + ":" + section] = *value;
			}
		}
	}

	std::unordered_map<std::string, int> MenuValues()
	{
		std::unordered_map<std::string, int> values;
		ReadMenuFile(MenuDefaultsPath(), values);
		ReadMenuFile(MenuPath(), values);
		return values;
	}

	void LoadMenu()
	{
		std::scoped_lock lock(s_mutex);
		s_values = Values{};
		ApplyIni(MenuDefaultsPath());
		ApplyIni(MenuPath());
		logger::info("settings: menu values taken over: seed {}, {} categories with values", s_values.seed, s_values.categories.size());
	}

	void Load()
	{
		bool firstStart = false;
		{
			std::scoped_lock lock(s_mutex);
			s_values = Values{};
			ApplyIni(MenuDefaultsPath());
			// Reads the applied settings, or on the first start without them the menu's own file.
			const auto applied = AppliedPath();
			firstStart = !std::filesystem::exists(applied);
			ApplyIni(firstStart ? MenuPath() : applied);
			logger::info("settings: seed {}, {} categories with values", s_values.seed, s_values.categories.size());
		}
		// Writes the values just read as the applied settings, before the menu can change its own file.
		if (firstStart) Save();
	}

	bool Save()
	{
		std::string text = "; Art Distribution Tool: the settings in effect, written whenever changes are applied in the menu.\n";
		{
			std::scoped_lock lock(s_mutex);
			text += fmt::format("[ADT_Main]\nbNewLayoutEachLoad={}\niSeed={}\n", s_values.newLayoutEachLoad ? 1 : 0, s_values.seed);
			for (const auto& [category, settings] : s_values.categories) {
				text += fmt::format("\n[{}{}]\n", kSectionPrefix, category);
				for (int i = 0; i < kSlotsPerCategory; ++i) text += category == "other" ? fmt::format("bOn{}={}\n", i + 1, settings.percent[i] == 100 ? 1 : 0) : fmt::format("iSlot{}={}\n", i + 1, settings.percent[i]);
			}
		}
		const auto path = AppliedPath();
		std::ofstream file(path, std::ios::binary | std::ios::trunc);
		file << text;
		file.close();
		if (!file) {
			logger::error("settings: could not write {}", path.string());
			return false;
		}
		logger::info("settings: applied settings saved to {}", path.string());
		return true;
	}

	Values Get()
	{
		std::scoped_lock lock(s_mutex);
		return s_values;
	}

	int Percent(std::string_view a_category, int a_slot)
	{
		if (a_slot < 1 || a_slot > kSlotsPerCategory) return 0;
		std::scoped_lock lock(s_mutex);
		const auto it = s_values.categories.find(NormalizeCategory(a_category));
		return it == s_values.categories.end() ? 0 : it->second.percent[a_slot - 1];
	}

	void SetPercent(std::string_view a_category, int a_slot, int a_value)
	{
		if (a_slot < 1 || a_slot > kSlotsPerCategory) return;
		std::scoped_lock lock(s_mutex);
		s_values.categories[NormalizeCategory(a_category)].percent[a_slot - 1] = std::clamp(a_value, 0, 100);
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
