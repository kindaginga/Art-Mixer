#pragma once

namespace ArtMixer::ApplyNow
{
	// Shows the Apply all prompt (the leaving wording when a_leaving) and clears the stored answer; false when no box could be shown.
	bool Ask(bool a_leaving);
	// The prompt's answer: -1 while the box is open, 0 for Apply all, 1 for Cancel.
	[[nodiscard]] int Answer();
	// Saves the game as ArtMixerApply, returns to the main menu and loads that save, so every setting shows at once.
	void Reload();
	// Returns to the main menu once the post-save message names ArtMixerApply (or no save) and ArtMixerApply.fos is newer than the request.
	void OnPostSaveGame(const void* a_data, std::uint32_t a_length);
	// Called before every load: drops a save still pending and returns true once for the load the reload itself starts.
	bool OnPreLoadGame();
	// Loads the save once the main menu is up again (its loop video opened).
	void OnMainMenuReady();
	// True from a confirmed reload's save until its load starts or the reload is given up.
	[[nodiscard]] bool Reloading();
}
