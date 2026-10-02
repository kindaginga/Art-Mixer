#pragma once

namespace GShuf::Sounds
{
	// Hooks the engine's sound creation and music track start so every sound, music track and voice file a pack replaces plays
	// one pack's file, chosen by share under the current layout seed (after Packs::Load).
	bool Install();
}
