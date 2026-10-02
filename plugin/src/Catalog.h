#pragma once

namespace GShuf::Catalog
{
	struct Slot
	{
		std::string material;  // normalized path relative to Materials
		std::string category;
		int width{ 0 };
		int height{ 0 };
		bool blank{ false };
		std::string restore;  // for an inline shader slot: "Materials\..." file that puts the vanilla look back
		std::string model;    // for an object slot ("&"): the object's game model, lowercase, relative to Meshes
	};

	// Loads Data\GShuf\vanilla-slots.json.
	bool Load();
	// Resolves the object slots ("&" plugin file ":" hex id) to the running load order's form ids; call once the game data is loaded.
	void ResolveForms();
	[[nodiscard]] const Slot* Find(std::string_view a_shaderPropertyName);
	// The slot of one game object whose model a pack replaces, or null (any thread, after ResolveForms).
	[[nodiscard]] const Slot* FindForm(std::uint32_t a_formID);
	// Visits every resolved object slot with its form id (after ResolveForms).
	void ForEachObjectSlot(const std::function<void(std::uint32_t, const Slot&)>& a_visitor);
	[[nodiscard]] std::size_t Count();
	// A form's name as packs.json writes it: its plugin file, lowercase, ":" and its local id in hex.
	[[nodiscard]] std::string FormText(const RE::TESForm* a_form);
	// A reference's key that survives load order changes: "plugin file:hex local id", or the form id for Fallout4.esm and game-made references (any thread, after ResolveForms).
	[[nodiscard]] std::string ReferenceKey(std::uint32_t a_formID);
	// The running form id of a form named "plugin file:hex local id", or 0 when that plugin is not loaded.
	[[nodiscard]] std::uint32_t FormIDOf(std::string_view a_form);
	// Lowercases, converts slashes and strips leading data\ and materials\ segments.
	[[nodiscard]] std::string NormalizeMaterialPath(std::string_view a_path);
}
