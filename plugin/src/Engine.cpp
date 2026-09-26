#include "Engine.h"

#include "Settings.h"

namespace GShuf::Engine
{
	namespace
	{
		// Game functions F4SE publishes per runtime, as offsets from the module base.
		struct MaterialFunctions
		{
			std::size_t loadMaterialFile;
			std::size_t applyMaterialData;
			std::size_t shaderDataCtor;
			std::size_t shaderDataDtor;
		};

		[[nodiscard]] std::optional<MaterialFunctions> FunctionsForRuntime(const REL::Version& a_version)
		{
			const auto v = a_version.string();
			if (v == "1-10-163-0") return MaterialFunctions{ 0x1C9D170, 0x27C4550, 0x27BD630, 0x3901F0 };
			if (v == "1-10-984-0") return MaterialFunctions{ 0x168EBF0, 0x2014440, 0x200D340, 0x543AC0 };
			if (v == "1-11-169-0") return MaterialFunctions{ 0x17A5110, 0x2164FE0, 0x215DEE0, 0x595010 };
			if (v == "1-11-240-0") return MaterialFunctions{ 0x17A9D80, 0x21715E0, 0x216A4E0, 0x597D70 };
			return std::nullopt;
		}

		std::optional<MaterialFunctions> s_functions;

		using ProcessFn = void (*)(void*, void*, const char*, RE::NiAVObject**, std::uint32_t*);
		ProcessFn s_originalProcess{ nullptr };
		ModelLoadedCallback s_callback{ nullptr };

		void HookedProcess(void* a_self, void* a_data, const char* a_name, RE::NiAVObject** a_root, std::uint32_t* a_type)
		{
			s_originalProcess(a_self, a_data, a_name, a_root, a_type);
			if (s_callback && a_root && *a_root) {
				s_callback(a_name ? a_name : "", *a_root);
			}
		}

		using Set3DFn = void (*)(RE::TESObjectREFR*, RE::NiAVObject*, bool);
		Set3DFn s_originalSet3D{ nullptr };
		Reference3DCallback s_referenceCallback{ nullptr };

		void HookedSet3D(RE::TESObjectREFR* a_ref, RE::NiAVObject* a_object, bool a_queue3DTasks)
		{
			s_originalSet3D(a_ref, a_object, a_queue3DTasks);
			if (s_referenceCallback && a_ref && a_object) {
				s_referenceCallback(a_ref, a_object);
			}
		}

		void Visit(RE::NiAVObject* a_object, int& a_index, const std::function<void(RE::NiAVObject*, int)>& a_visitor)
		{
			if (!a_object) return;
			if (IsGeometry(a_object)) {
				a_visitor(a_object, a_index++);
				return;
			}
			if (auto* node = a_object->IsNode(); node) {
				for (std::uint16_t i = 0; i < node->children.size(); ++i) {
					Visit(node->children[i].get(), a_index, a_visitor);
				}
			}
		}
	}

	bool Initialize()
	{
		s_functions = FunctionsForRuntime(REL::Module::get().version());
		if (!s_functions) {
			logger::error("engine: runtime {} has no material function table; art distribution disabled", REL::Module::get().version().string());
			return false;
		}
		return true;
	}

	bool Available()
	{
		return s_functions.has_value();
	}

	bool InstallHooks(ModelLoadedCallback a_callback)
	{
		s_callback = a_callback;
		REL::Relocation<std::uintptr_t> processorVtable{ RE::VTABLE::TESModelDB____TESProcessor[0] };
		s_originalProcess = reinterpret_cast<ProcessFn>(processorVtable.write_vfunc(1, &HookedProcess));
		const auto base = REL::Module::get().base();
		logger::info("engine: hooked TESProcessor::Process (vtable {:X}, original {:X})", processorVtable.address() - base, reinterpret_cast<std::uintptr_t>(s_originalProcess) - base);
		return s_originalProcess != nullptr;
	}

	bool InstallReferenceHook(Reference3DCallback a_callback)
	{
		s_referenceCallback = a_callback;
		REL::Relocation<std::uintptr_t> vtable{ RE::VTABLE::TESObjectREFR[0] };
		s_originalSet3D = reinterpret_cast<Set3DFn>(vtable.write_vfunc(0x88, &HookedSet3D));
		const auto base = REL::Module::get().base();
		logger::info("engine: hooked TESObjectREFR::Set3D (vtable {:X}, original {:X})", vtable.address() - base, reinterpret_cast<std::uintptr_t>(s_originalSet3D) - base);
		return s_originalSet3D != nullptr;
	}

	bool IsGeometry(RE::NiAVObject* a_object)
	{
		return a_object && a_object->IsGeometry() != nullptr;
	}

	RE::NiObjectNET* ShaderProperty(RE::NiAVObject* a_geometry)
	{
		if (!a_geometry) return nullptr;
		auto** props = reinterpret_cast<RE::NiObjectNET**>(reinterpret_cast<std::uintptr_t>(a_geometry) + 0x130);
		for (int i = 1; i >= 0; --i) {
			auto* prop = props[i];
			if (!prop) continue;
			const auto name = Settings::Lower(prop->name.c_str() ? prop->name.c_str() : "");
			if (name.ends_with(".bgsm") || name.ends_with(".bgem")) {
				return prop;
			}
		}
		return nullptr;
	}

	std::string DiffuseName(RE::NiAVObject* a_geometry)
	{
		auto* prop = ShaderProperty(a_geometry);
		auto* material = prop ? *reinterpret_cast<std::uint8_t**>(reinterpret_cast<std::uintptr_t>(prop) + 0x58) : nullptr;
		auto* textureSet = material ? *reinterpret_cast<RE::BSTextureSet**>(material + 0x78) : nullptr;
		const char* diffuse = textureSet ? textureSet->GetTextureFilename(static_cast<RE::BSShaderProperty::TextureTypeEnum>(0)) : nullptr;
		return diffuse ? std::string(diffuse) : std::string();
	}

	std::string ShaderPropertyName(RE::NiAVObject* a_geometry)
	{
		auto* prop = ShaderProperty(a_geometry);
		return prop && prop->name.c_str() ? std::string(prop->name.c_str()) : std::string();
	}

	bool ApplyMaterial(RE::NiAVObject* a_geometry, const std::string& a_materialFile, int a_invalidate)
	{
		if (!s_functions || !a_geometry) return false;
		const auto base = REL::Module::get().base();
		using LoadMaterialFileFn = std::uint32_t (*)(const char*, void*, std::uint32_t);
		using ShaderDataFn = void (*)(void*);
		using ApplyMaterialDataFn = void (*)(void*, RE::NiAVObject*, bool);
		const auto loadMaterialFile = reinterpret_cast<LoadMaterialFileFn>(base + s_functions->loadMaterialFile);
		const auto applyMaterialData = reinterpret_cast<ApplyMaterialDataFn>(base + s_functions->applyMaterialData);
		const auto ctor = reinterpret_cast<ShaderDataFn>(base + s_functions->shaderDataCtor);
		const auto dtor = reinterpret_cast<ShaderDataFn>(base + s_functions->shaderDataDtor);

		// The loader resolves paths relative to Data, so a NIF's "data\materials\..." name is trimmed to "materials\...".
		std::string file = a_materialFile;
		if (Settings::Lower(file).starts_with("data\\")) file.erase(0, 5);
		const auto diffuseBefore = DiffuseName(a_geometry);

		alignas(16) std::uint8_t shaderData[0x1E0]{};
		ctor(shaderData);
		const auto status = loadMaterialFile(file.c_str(), shaderData, 0);
		bool ok = false;
		if (status == 0) {
			applyMaterialData(shaderData, a_geometry, false);
			ok = true;
		} else {
			logger::warn("engine: LoadMaterialFile('{}') returned {}", a_materialFile, status);
		}
		dtor(shaderData);
		if (ok) {
			// The engine keys the renderer's material lookup on the property name, so it has to match the new file.
			const auto diffuseAfter = DiffuseName(a_geometry);
			if (diffuseAfter.empty() || diffuseAfter == diffuseBefore) {
				logger::warn("engine: material '{}' applied but diffuse stayed '{}'", file, diffuseAfter);
			}
			if (auto* prop = ShaderProperty(a_geometry); prop) {
				prop->name = RE::BSFixedString(file);
				auto* shaderProp = static_cast<RE::BSShaderProperty*>(prop);
				if (a_invalidate >= 1) {
					shaderProp->DoClearRenderPasses();
					shaderProp->lastRenderPassState = 0;
				}
				if (a_invalidate >= 2) {
					if (auto* geometry = a_geometry->IsGeometry(); geometry) shaderProp->SetupGeometry(geometry);
					a_geometry->SetMaterialNeedsUpdate(true);
				}
			}
		}
		return ok;
	}

	void ForEachGeometry(RE::NiAVObject* a_root, const std::function<void(RE::NiAVObject*, int)>& a_visitor)
	{
		int index = 0;
		Visit(a_root, index, a_visitor);
	}
}
