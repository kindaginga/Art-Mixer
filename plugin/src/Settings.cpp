#include "Settings.h"

namespace GShuf::Settings
{
	namespace
	{
		std::mutex s_mutex;
		Values s_values;
		// Category names of the Other page's kinds start with this; their MCM keys live in section GShuf_Other.
		constexpr std::string_view kOtherKindPrefix{ "other_" };

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
					// Only the GShuf_ sections the MCM pages write; anything else is a leftover of an older layout.
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
					if (key == "bblankcanvases") s_values.blankCanvases = *value != 0;
					else if (key == "bnewlayouteachload") s_values.newLayoutEachLoad = *value != 0;
					else if (key == "iseed") s_values.seed = *value;
					++applied;
				} else if (key.starts_with("bkinds")) {
					// An Other replacers pack's "Set by kind of object" switch.
					const auto slot = ParseInt(key.substr(6));
					if (!slot || *slot < 1 || *slot > kSlotsPerCategory) continue;
					s_values.otherByKind[static_cast<std::size_t>(*slot - 1)] = *value != 0;
					++applied;
				} else if (key.starts_with("islot")) {
					// "iSlot<n>_<kind>" in the Other section belongs to category other_<kind>; other sections take only "iSlot<n>".
					const auto underscore = key.find('_', 5);
					const auto slot = ParseInt(key.substr(5, underscore == std::string::npos ? std::string::npos : underscore - 5));
					if (!slot || *slot < 1 || *slot > kSlotsPerCategory) continue;
					std::string category = section;
					if (underscore != std::string::npos) {
						if (section != "other" || underscore + 1 >= key.size()) continue;
						category = std::string(kOtherKindPrefix) + key.substr(underscore + 1);
					}
					s_values.categories[category].percent[*slot - 1] = std::clamp(*value, 0, 100);
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

	std::string NormalizeCategory(std::string_view a_category)
	{
		auto category = Lower(Trim(a_category));
		if (category.starts_with(kSectionPrefix)) category.erase(0, kSectionPrefix.size());
		return category;
	}

	std::string MenuKey(std::string_view a_prefix, int a_slot, std::string_view a_category)
	{
		const auto category = NormalizeCategory(a_category);
		if (category.starts_with(kOtherKindPrefix)) return fmt::format("{}{}_{}:GShuf_Other", a_prefix, a_slot, category.substr(kOtherKindPrefix.size()));
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
		logger::info("settings: menu values taken over: blank canvases {}, seed {}, {} categories with values", s_values.blankCanvases, s_values.seed, s_values.categories.size());
	}

	void Load()
	{
		bool firstStart = false;
		{
			std::scoped_lock lock(s_mutex);
			s_values = Values{};
			ApplyIni(std::filesystem::path("Data") / "MCM" / "Config" / std::string(kModName) / "settings.ini");
			// Reads the applied settings, or on the first start without them the menu's own file.
			const auto applied = AppliedPath();
			firstStart = !std::filesystem::exists(applied);
			ApplyIni(firstStart ? std::filesystem::path("Data") / "MCM" / "Settings" / (std::string(kModName) + ".ini") : applied);
			logger::info("settings: blank canvases {}, seed {}, {} categories with values", s_values.blankCanvases, s_values.seed, s_values.categories.size());
		}
		// Writes the values just read as the applied settings, before the menu can change its own file.
		if (firstStart) Save();
	}

	bool Save()
	{
		std::string text = "; Gallery Shuffle: the settings in effect, written whenever changes are applied in the menu.\n";
		{
			std::scoped_lock lock(s_mutex);
			text += fmt::format("[GShuf_Main]\nbBlankCanvases={}\nbNewLayoutEachLoad={}\niSeed={}\n", s_values.blankCanvases ? 1 : 0, s_values.newLayoutEachLoad ? 1 : 0, s_values.seed);
			for (const auto& [category, settings] : s_values.categories) {
				text += fmt::format("\n[{}{}]\n", kSectionPrefix, category);
				for (int i = 0; i < kSlotsPerCategory; ++i) text += fmt::format("iSlot{}={}\n", i + 1, settings.percent[i]);
			}
			text += "\n[GShuf_OtherKinds]\n";
			for (int i = 0; i < kSlotsPerCategory; ++i) text += fmt::format("bKinds{}={}\n", i + 1, s_values.otherByKind[static_cast<std::size_t>(i)] ? 1 : 0);
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

	void SetBlankCanvases(bool a_enabled)
	{
		std::scoped_lock lock(s_mutex);
		s_values.blankCanvases = a_enabled;
	}

	void SetNewLayoutEachLoad(bool a_enabled)
	{
		std::scoped_lock lock(s_mutex);
		s_values.newLayoutEachLoad = a_enabled;
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
