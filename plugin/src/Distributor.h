#pragma once

namespace GShuf::Distributor
{
	struct Stats
	{
		std::size_t tracked{ 0 };
		std::size_t swapped{ 0 };
		std::size_t modelsSeen{ 0 };
		std::size_t referencesSeen{ 0 };
		std::size_t referenceSwaps{ 0 };
		std::size_t set3DCalls{ 0 };
	};

	// Registers every catalogued art geometry in the model and applies the current assignment (loader thread).
	void OnModelLoaded(const char* a_modelPath, RE::NiAVObject* a_root);
	// Applies a per-reference pick to catalogued geometries in a reference's freshly attached 3D (game thread).
	void OnReference3D(RE::TESObjectREFR* a_ref, RE::NiAVObject* a_root);
	// Applies the per-reference pick to an attached 3D root; runs on the game thread as a task.
	void ApplyToReference(std::uint32_t a_formID, RE::NiAVObject* a_root);
	// Re-evaluates every tracked geometry against the current settings; runs on the game thread.
	void Reapply();
	// Queues Reapply on the game thread.
	void QueueReapply();
	[[nodiscard]] Stats GetStats();
}
