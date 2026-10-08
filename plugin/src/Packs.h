#pragma once

namespace ADT::Packs
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
		std::string card;        // the game's loading screen magazine with this cover (relative to Meshes), empty for none
		std::optional<std::array<std::int16_t, 6>> bounds;  // object bounds this version needs: min x, y, z, max x, y, z
		std::optional<std::array<std::int32_t, 6>> mapBounds;  // the Commonwealth world map data this world map needs: usable width, height, NW cell x, y, SE cell x, y
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
		std::string page;   // the page its one control sits on ("paintings", "posters", "signage", "magazines", "quest", "mixed" or "other"), empty for none
		std::string video;  // main menu video relative to Video, empty when the pack has none
		std::string music;  // main menu theme relative to Music, empty when the pack has none
		std::map<std::string, std::vector<Image>> images;  // by category
		std::vector<Movie> movies;
		std::unordered_map<std::string, ScreenCard> screens;  // loading screen ("file:local id", lowercase) -> its card
	};

	// A game model the plugin hands the game with an object's picks as a material swap: the copy that names its inline shaders as
	// material files (empty when it has none), the replaced materials it draws with, and each named inline shader's key with the
	// material file that stands for it in the copy (all relative to Materials or Meshes, lowercase).
	struct ModelUse
	{
		std::string copy;
		std::vector<std::string> materials;
		std::vector<std::pair<std::string, std::string>> inlineShaders;
	};

	// Loads Data\ADT\packs.json written by the converter.
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
	// Packs with something for a category (a page or a kind), in pack order, whatever page their slider sits on.
	[[nodiscard]] std::vector<const Pack*> PacksWith(std::string_view a_category);
	// The packs a page or list shows, in slot order (1-based slots), as packs.json lists them.
	[[nodiscard]] const std::vector<const Pack*>& SlotsFor(std::string_view a_category);
	// The MCM section names of every share category in menu order.
	[[nodiscard]] const std::vector<std::string>& Sections();
	// The model a reference loads (relative to Meshes, any case and slash) as packs.json lists it, or null (any thread).
	[[nodiscard]] const ModelUse* ModelFor(std::string_view a_model);
}
