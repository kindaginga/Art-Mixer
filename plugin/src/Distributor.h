#pragma once

namespace GShuf::Distributor
{
	struct Stats
	{
		std::size_t tracked{ 0 };
		std::size_t swapped{ 0 };
		std::size_t modelsSeen{ 0 };
	};

	// Registers every catalogued art geometry in the model and applies the current assignment (loader thread).
	void OnModelLoaded(const char* a_modelPath, RE::NiAVObject* a_root);
	// Re-evaluates every tracked geometry against the current settings; runs on the game thread.
	void Reapply();
	// Queues Reapply on the game thread.
	void QueueReapply();
	[[nodiscard]] Stats GetStats();
}
