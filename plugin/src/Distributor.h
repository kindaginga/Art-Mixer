#pragma once

#include "Engine.h"
#include "Packs.h"

namespace ADT::Distributor
{
	struct Stats
	{
		std::size_t tracked{ 0 };
		std::size_t swapped{ 0 };
		std::size_t modelsSeen{ 0 };
		std::size_t referencesSeen{ 0 };
		std::size_t referenceSwaps{ 0 };
		std::size_t set3DCalls{ 0 };
		std::size_t packArrivals{ 0 };  // reference geometries whose 3D arrived already showing a pack look
	};

	// Registers every catalogued art geometry in the model and applies the current assignment (loader thread).
	void OnModelLoaded(const char* a_modelPath, RE::NiAVObject* a_root);
	// Applies a per-reference pick to catalogued geometries in a reference's freshly attached 3D (game thread).
	void OnReference3D(RE::TESObjectREFR* a_ref, RE::NiAVObject* a_root);
	// The pack model (relative to Meshes) a reference loads instead of its own under the current shares, or an empty path (any thread).
	Engine::ModelChoice ChooseModel(std::uint32_t a_refFormID, std::uint32_t a_baseFormID, const char* a_modelPath, bool a_inWorld);
	// Whether a pack has a version of the object a roll is for.
	using Covers = std::function<bool(const Packs::Pack&)>;
	// The pack a roll of a category's shares lands on for the given key, among the packs that have a version of the object (every
	// pack without a test); the vanilla pack in the remainder (any thread).
	[[nodiscard]] const Packs::Pack* RollCategory(std::string_view a_category, std::string_view a_key, const Covers& a_covers = {});
	// A stable hash of the key under the current layout seed (any thread).
	[[nodiscard]] std::uint64_t LayoutHash(std::string_view a_key);
	// Gives every game object whose model a pack replaces in a pick category (the Pip-Boy lists) the picked pack's model (game thread).
	void ApplyPickModels();
	// The pack texture (relative to Textures) a by-name texture request loads under the current shares, or empty (any thread).
	[[nodiscard]] std::string ChooseTexture(std::string_view a_relative);
	// Widens the bounds of every object a pack version needs other bounds for to the box enclosing all versions (game data ready).
	void ApplyBounds();
	// Gives a reference the name of the pack version it shows, or takes a pack name off again (game thread).
	void ApplyName(std::uint32_t a_formID, std::uint32_t a_baseFormID, bool a_inWorld);
	// One material swap entry on a reference: the material it shows and the swap form ("file:local id") it comes from.
	struct ShownSwap
	{
		std::string material;
		std::string form;
	};
	// Game material (normalized) -> the swap entry that shows another material in its place.
	using SwapMap = std::unordered_map<std::string, ShownSwap>;
	// Applies the per-reference pick to an attached 3D root, reading each geometry through the reference's swaps; game thread task.
	void ApplyToReference(std::uint32_t a_formID, RE::NiAVObject* a_root, const SwapMap& a_swaps = {});
	// Writes into every texture set a pack changes the texture its roll picks (game data ready and every reapply).
	void ApplyTextureSets();
	// Re-evaluates every tracked geometry against the current settings; runs on the game thread.
	void Reapply();
	// Queues Reapply on the game thread.
	void QueueReapply();
	[[nodiscard]] Stats GetStats();
}
