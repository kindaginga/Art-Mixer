#pragma once

namespace GShuf::MainMenu
{
	struct Option
	{
		std::string name;
		std::string video;  // relative to the game folder, e.g. Data\Video\GShuf\Packs\x\MainMenuLoop.bk2; empty means vanilla
	};

	// Hooks the game's Bink video open so the main menu video can come from a pack.
	bool Install();
	// Registers the corner picker menu and the main menu open/close sink; needs the UI singleton.
	void OnGameDataReady();
	// Replaces the list the picker offers and the current choice.
	void SetOptions(std::vector<Option> a_options, int a_selected);
	// Builds the option list from the installed packs and restores the saved choice.
	void LoadFromPacks();
	[[nodiscard]] int Selected();
	[[nodiscard]] int OptionCount();
	// Applies a choice by index and re-shows the main menu so the new video starts.
	void Select(int a_index);
	// Sets the video file directly; empty restores vanilla.
	void SetMenuVideo(const std::string& a_path);
	[[nodiscard]] std::string MenuVideo();
}
