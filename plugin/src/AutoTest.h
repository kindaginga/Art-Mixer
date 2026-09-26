#pragma once

namespace GShuf::AutoTest
{
	// Reads [AutoTest] from Data\F4SE\Plugins\GalleryShuffle.ini; returns true when a scripted run is configured.
	bool Configure();
	void OnGameDataReady();
	void OnPostLoadGame();
}
