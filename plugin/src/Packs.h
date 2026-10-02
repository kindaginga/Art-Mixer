#pragma once

namespace GShuf::Packs
{
	struct Image
	{
		std::string material;  // path relative to Materials, as written by the converter
		std::string slot;      // vanilla slot this image replaces, empty for standalone art
		int width{ 0 };
		int height{ 0 };
		std::optional<RE::BGSMaterialSwap*> swap;  // the material swap a model version loads with: unset keeps the object's, null is none
		std::string swapForm;                      // the swap form ("file:local id") that swap resolves from at data load
		std::string name;        // the name the object shows while this image is its version, empty for none
		std::string card;        // loading screen card model (relative to Meshes) a picture shows on, empty for none
		float cardWidth{ 0.0F };  // width of a flat picture card in units, 0 for a magazine card
		std::optional<std::array<std::int16_t, 6>> bounds;  // object bounds this version needs: min x, y, z, max x, y, z
	};

	// An interface file a pack replaces: the game's path (lowercase, relative to Data), the pack's copy and the pick it follows.
	struct Movie
	{
		std::string path;
		std::string file;
		std::string category;
	};

	// A loading screen's vanilla card: the card model (relative to Meshes), the form the game's screen shows and its color remap.
	struct ScreenCard
	{
		std::string card;
		std::string model;      // the form the game's screen shows ("file:local id")
		std::string gameModel;  // that form's game model, lowercase, relative to Meshes
		std::string swap;       // that form's game material swap ("file:local id"), empty for none
		std::optional<float> color;
	};

	// A game record value a converted mod's plugin changes: "swap" (from, mod, game materials) or "textureSet" (index, mod, game textures).
	struct Restore
	{
		std::string kind;
		std::string form;
		std::string from;
		std::string mod;
		std::string game;
		int index{ 0 };
		std::optional<float> gameColor;  // the game entry's color remapping index, unset when it has none
		std::optional<float> modColor;   // the mod entry's color remapping index, unset when it has none
	};

	struct Pack
	{
		std::string id;
		std::string name;
		bool vanilla{ false };
		std::string video;  // main menu video relative to Video, empty when the pack has none
		std::string music;  // main menu theme relative to Music, empty when the pack has none
		std::map<std::string, std::vector<Image>> images;  // by category
		std::vector<Movie> movies;
		std::unordered_map<std::string, ScreenCard> screens;  // loading screen ("file:local id", lowercase) -> its card
	};

	// Loads Data\GShuf\packs.json written by the converter.
	bool Load();
	// Resolves the swap forms pack versions name and puts back game records a converted mod's plugin changed (game data loaded).
	void OnGameDataReady();
	// The card the vanilla pack shows a loading screen ("file:local id") with, or null.
	[[nodiscard]] const ScreenCard* VanillaScreenCard(std::string_view a_screen);
	// Every game record change of a converted mod's plugin that packs.json lists.
	[[nodiscard]] const std::vector<Restore>& Restores();
	// Whether a swap entry ("file:local id" and the material it replaces) shows the game's or a converted mod's material.
	[[nodiscard]] bool OwnsSwapEntry(std::string_view a_form, std::string_view a_material, std::string_view a_shown);
	[[nodiscard]] const std::vector<Pack>& All();
	[[nodiscard]] const Pack* Vanilla();
	// Identifies the installed pack set; changes whenever the converter rebuilds it.
	[[nodiscard]] std::string Signature();
	// Packs that replace the interface file (path relative to Data, any case and slash), with their copies.
	[[nodiscard]] std::vector<std::pair<const Pack*, const Movie*>> MoviesFor(std::string_view a_path);
	// The page a pack's one slider sits on ("paintings", "posters", "signage", "magazines", "quest" or "other"): the page with the most
	// of its art, the first in menu order on a tie; "" for a pack with art on none.
	[[nodiscard]] std::string MainPage(const Pack& a_pack);
	// Packs with something for a category (a page or a kind), in pack order, whatever page their slider sits on.
	[[nodiscard]] std::vector<const Pack*> PacksWith(std::string_view a_category);
	// The packs a page or list shows, in slot order (1-based slots): a page lists the packs whose one slider sits on it; a kind lists
	// the Other replacers packs with something for it; loading screens every pack with pictures; the Pip-Boy lists every pack with
	// something for them.
	[[nodiscard]] std::vector<const Pack*> SlotsFor(std::string_view a_category);
	// The first slot of the group of packs that replace some of the same objects as a category's slot (1-based), whose shares add up to 100;
	// 1 when packs.json lists no groups for the category, 0 outside the category's slots.
	[[nodiscard]] int GroupOf(std::string_view a_category, int a_slot);
	// The MCM section names of every share category in menu order, empty for packs.json from an older converter.
	[[nodiscard]] const std::vector<std::string>& Sections();
}
