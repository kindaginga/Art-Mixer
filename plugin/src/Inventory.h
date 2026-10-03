#pragma once

namespace ADT::Inventory
{
	// Registers the co-save record that keeps which world object each carried item came from (plugin load).
	void Install();
	// Starts following items the player picks up and drops (game data ready).
	void OnGameDataReady();
	// The reference whose pack pick an object shows: a dropped item keeps the pick of the world object it was picked up as,
	// and an item shown outside the world (the inventory's 3D view) shows the pick of the last one of its kind picked up.
	[[nodiscard]] std::uint32_t IdentityOf(std::uint32_t a_refFormID, std::uint32_t a_baseFormID, bool a_inWorld);
}
