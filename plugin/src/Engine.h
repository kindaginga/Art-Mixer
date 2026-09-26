#pragma once

namespace GShuf::Engine
{
	using ModelLoadedCallback = void (*)(const char* a_modelPath, RE::NiAVObject* a_root);
	using Reference3DCallback = void (*)(RE::TESObjectREFR* a_ref, RE::NiAVObject* a_root);

	// Resolves the material functions for the running executable; false when the runtime is unknown.
	bool Initialize();
	[[nodiscard]] bool Available();
	// Installs the TESModelDB::TESProcessor::Process hook that reports every loaded model.
	bool InstallHooks(ModelLoadedCallback a_callback);
	// Installs the TESObjectREFR::Load3D hook that reports every reference's freshly created 3D.
	bool InstallReferenceHook(Reference3DCallback a_callback);
	// Loads a material file through the engine and applies it to one geometry, renaming its shader property.
	// a_invalidate: 0 none, 1 clears the cached render passes, 2 also re-runs SetupGeometry and flags a material update.
	bool ApplyMaterial(RE::NiAVObject* a_geometry, const std::string& a_materialFile, int a_invalidate = 1);
	[[nodiscard]] RE::NiObjectNET* ShaderProperty(RE::NiAVObject* a_geometry);
	[[nodiscard]] std::string ShaderPropertyName(RE::NiAVObject* a_geometry);
	// Diffuse texture path of the geometry's current material, empty when unavailable.
	[[nodiscard]] std::string DiffuseName(RE::NiAVObject* a_geometry);
	// Visits every geometry under the root in traversal order with a stable index.
	void ForEachGeometry(RE::NiAVObject* a_root, const std::function<void(RE::NiAVObject*, int)>& a_visitor);
	[[nodiscard]] bool IsGeometry(RE::NiAVObject* a_object);
	// Logs every material application with its thread id (diagnostic, enabled by the autotest).
	void SetTracing(bool a_enabled);
}
