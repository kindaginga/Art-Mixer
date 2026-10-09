#pragma once

namespace ArtMixer::CoreCheck
{
	// Checks the mod's plugin file and scripts against the list the package wrote (Data\ArtMixer\core-files.json), logs the
	// result, and shows a message box naming the files when some are missing or changed.
	void OnGameDataReady();
}
