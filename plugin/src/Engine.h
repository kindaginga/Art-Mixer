#pragma once

namespace GShuf::Engine
{
	using ModelLoadedCallback = void (*)(const char* a_modelPath, RE::NiAVObject* a_root);
	using Reference3DCallback = void (*)(RE::TESObjectREFR* a_ref, RE::NiAVObject* a_root);
	// The model (relative to Meshes) a reference loads instead of its own (empty keeps its own) and its material swap (unset keeps the object's, null is none).
	struct ModelChoice
	{
		std::string path;
		std::optional<RE::BGSMaterialSwap*> swap;
	};
	using ModelChooser = ModelChoice (*)(std::uint32_t a_refFormID, std::uint32_t a_baseFormID, const char* a_modelPath, bool a_inWorld);

	// Resolves the material functions for the running executable; false when the runtime is unknown.
	bool Initialize();
	[[nodiscard]] bool Available();
	// Installs the TESModelDB::TESProcessor::Process hook that reports every loaded model.
	bool InstallHooks(ModelLoadedCallback a_callback);
	// Installs the TESObjectREFR::Load3D hook that reports every reference's freshly created 3D.
	bool InstallReferenceHook(Reference3DCallback a_callback);
	// Installs the TESObjectREFR::GetTESModel hook through which a reference loads a pack's replacement model.
	bool InstallModelHook(ModelChooser a_chooser);
	// The file (relative to the game folder, like the request) an interface movie request opens instead, or empty.
	using MovieRedirector = std::string (*)(std::string_view a_url);
	// Hooks the game's Scaleform file opener (BSScaleformFileOpener), through which every interface movie and its imports
	// open, so a request can open a pack's file while the movie keeps its own address for relative imports.
	bool InstallMovieHook(MovieRedirector a_redirector);
	// The texture (relative to Textures) a by-name texture request (lowercase, relative to Textures) loads instead, or empty.
	using TextureRedirector = std::string (*)(std::string_view a_relative);
	// Hooks the engine's by-name texture loader, found as the call in BSShaderTextureSet::GetTexture.
	bool InstallTextureHook(TextureRedirector a_redirector);
	// Gives a reference the name it shows (the engine's per-object name, as a workbench rename writes it), or removes it when
	// a_name is empty; a name already on the reference is changed or removed only when a_owned says it is one of ours.
	bool SetReferenceName(RE::TESObjectREFR* a_ref, const std::string& a_name, const std::function<bool(std::string_view)>& a_owned);
	// Loads a material file through the engine and applies it to one geometry, renaming its shader property.
	// a_invalidate: 0 none, 1 clears the cached render passes, 2 also re-runs SetupGeometry and flags a material update.
	bool ApplyMaterial(RE::NiAVObject* a_geometry, const std::string& a_materialFile, int a_invalidate = 1);
	[[nodiscard]] RE::NiObjectNET* ShaderProperty(RE::NiAVObject* a_geometry);
	[[nodiscard]] std::string ShaderPropertyName(RE::NiAVObject* a_geometry);
	// The geometry's lighting or effect shader property, whether or not it names a material file.
	[[nodiscard]] RE::BSShaderProperty* AnyShader(RE::NiAVObject* a_geometry);
	// For a shader the model carries inline (no material file): "@" and its diffuse or effect texture, lowercase,
	// relative to Textures; empty for shaders that name a material file.
	[[nodiscard]] std::string InlineKey(RE::NiAVObject* a_geometry);
	// Diffuse texture path of the geometry's lighting shader, named material or inline; empty for other shaders.
	[[nodiscard]] std::string DiffuseName(RE::NiAVObject* a_geometry);
	// Visits every geometry under the root in traversal order with a stable index.
	void ForEachGeometry(RE::NiAVObject* a_root, const std::function<void(RE::NiAVObject*, int)>& a_visitor);
	// Visits every substitution of a material swap form: the game material and the material shown in its place.
	void ForEachMaterialSubstitution(const RE::BGSMaterialSwap* a_swap, const std::function<void(std::string_view, std::string_view)>& a_visitor);
	// The engine's id of a file by its path relative to Data: CRC-32 of the lowercase stem and folder, first four extension characters.
	[[nodiscard]] RE::BSResource::ID FileID(std::string_view a_path);
	// A texture path relative to Textures: lowercase, backslashes, no leading data\ or textures\.
	[[nodiscard]] std::string TexturePath(std::string_view a_path);
	// Writes a texture (relative to Textures) into a texture set with its file id; false when unchanged or the id is not the name's hash.
	bool SetTextureSetTexture(RE::BGSTextureSet* a_set, std::size_t a_index, std::string_view a_texture);
	// The model a reference loads, as the model hook answers it, without logging (the object's own model before the hook is installed).
	[[nodiscard]] RE::TESModel* EffectiveModel(const RE::TESObjectREFR* a_ref);
	[[nodiscard]] bool IsGeometry(RE::NiAVObject* a_object);
	// Logs every material application with its thread id (diagnostic, enabled by the autotest).
	void SetTracing(bool a_enabled);
	// Logs every reference model request and 3D load with its reference, base and cell (diagnostic, enabled by the autotest).
	void SetModelTracing(bool a_enabled);
	[[nodiscard]] bool ModelTracing();
}
