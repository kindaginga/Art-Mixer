#include "Engine.h"

#include "Settings.h"

#include <unordered_set>

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>

#pragma warning(push, 0)
#include <Zydis/Zydis.h>
#pragma warning(pop)

namespace ADT::Engine
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
			std::size_t lightingPropertyCtor;
			std::size_t effectPropertyCtor;
			std::size_t lightingLoadTextureSet;
		};

		[[nodiscard]] std::optional<MaterialFunctions> FunctionsForRuntime(const REL::Version& a_version)
		{
			const auto v = a_version.string();
			if (v == "1-10-163-0") return MaterialFunctions{ 0x1C9D170, 0x27C4550, 0x27BD630, 0x3901F0, 0x27CC880, 0x27CAAC0, 0x27CD200 };
			if (v == "1-10-984-0") return MaterialFunctions{ 0x168EBF0, 0x2014440, 0x200D340, 0x543AC0, 0x201BF90, 0x201A330, 0x201C900 };
			if (v == "1-11-169-0") return MaterialFunctions{ 0x17A5110, 0x2164FE0, 0x215DEE0, 0x595010, 0x216CB30, 0x216AED0, 0x216D4A0 };
			if (v == "1-11-240-0") return MaterialFunctions{ 0x17A9D80, 0x21715E0, 0x216A4E0, 0x597D70, 0x2179130, 0x21774D0, 0x2179AA0 };
			return std::nullopt;
		}

		std::optional<MaterialFunctions> s_functions;
		std::atomic<bool> s_tracing{ false };
		std::atomic<bool> s_modelTracing{ false };
		std::atomic<int> s_traceCount{ 0 };

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

		Reference3DCallback s_referenceCallback{ nullptr };

		using Load3DFn = RE::NiAVObject* (*)(RE::TESObjectREFR*, bool);
		Load3DFn s_originalLoad3D{ nullptr };

		// The model loader asks each reference for its model (TESObjectREFR::GetTESModel) when it queues the reference's 3D;
		// answering with a copy that names a pack model makes the loader load that model for this one reference.
		using GetTESModelFn = RE::TESModel* (*)(const RE::TESObjectREFR*);
		GetTESModelFn s_originalGetTESModel{ nullptr };
		ModelChooser s_modelChooser{ nullptr };
		std::mutex s_modelCopiesLock;
		std::map<std::tuple<const RE::TESModel*, std::string, bool, const RE::BGSMaterialSwap*>, RE::TESModel*> s_modelCopies;
		std::atomic<int> s_modelLogs{ 0 };

		// One copy per vanilla model component, replacement path and swap choice, kept for the session: the component's own
		// vtable stays, the path changes, the material swap stays unless the choice names one (or none), and the precomputed
		// texture and material lists of the vanilla file are dropped.
		RE::TESModel* ModelCopy(RE::TESModel* a_model, const std::string& a_path, const std::optional<RE::BGSMaterialSwap*>& a_swap)
		{
			std::scoped_lock lock(s_modelCopiesLock);
			const std::tuple<const RE::TESModel*, std::string, bool, const RE::BGSMaterialSwap*> key{ a_model, a_path, a_swap.has_value(), a_swap.value_or(nullptr) };
			if (const auto it = s_modelCopies.find(key); it != s_modelCopies.end()) return it->second;
			const bool swapComponent = a_model->GetAsModelMaterialSwap() != nullptr;
			const std::size_t size = swapComponent ? sizeof(RE::BGSModelMaterialSwap) : sizeof(RE::TESModel);
			auto* memory = static_cast<std::byte*>(::operator new(size));
			std::memcpy(memory, a_model, size);
			auto* copy = reinterpret_cast<RE::TESModel*>(memory);
			new (&copy->model) RE::BSFixedString(a_path.c_str());
			copy->textures = nullptr;
			copy->materials = nullptr;
			copy->numTextures = 0;
			copy->numTexturesSRGB = 0;
			copy->numMaterials = 0;
			if (a_swap && swapComponent) static_cast<RE::BGSModelMaterialSwap*>(copy)->swapForm = *a_swap;
			s_modelCopies.emplace(key, copy);
			return copy;
		}

		RE::TESModel* ChosenModel(const RE::TESObjectREFR* a_ref, bool a_log)
		{
			auto* model = s_originalGetTESModel(a_ref);
			if (!s_modelChooser || !a_ref || !model) return model;
			const char* path = model->GetModel();
			if (!path || !*path) return model;
			const auto* base = a_ref->GetObjectReference();
			const auto baseFormID = base ? base->GetFormID() : 0u;
			const bool inWorld = a_ref->GetParentCell() != nullptr;
			if (a_log && s_modelTracing.load()) logger::info("engine: model request {:08X} (base {:08X}, {}): {}", a_ref->GetFormID(), baseFormID, inWorld ? "in a cell" : "in no cell", path);
			const auto choice = s_modelChooser(a_ref->GetFormID(), baseFormID, path, inWorld);
			if (choice.path.empty()) return model;
			if (a_log && s_modelLogs.fetch_add(1) < 20) {
				const auto swap = !choice.swap ? std::string() : *choice.swap ? fmt::format(" with material swap {:08X}", (*choice.swap)->GetFormID()) : std::string(" without its material swap");
				logger::info("reference {:08X} (base {:08X}): model {} -> {}{}", a_ref->GetFormID(), baseFormID, path, choice.path, swap);
			}
			return ModelCopy(model, choice.path, choice.swap);
		}

		RE::TESModel* HookedGetTESModel(const RE::TESObjectREFR* a_ref)
		{
			return ChosenModel(a_ref, true);
		}

		// BSScaleformFileOpener slot 2 (GetFileModifyTime) and slot 3 (OpenFileEx) each pass the url through the movie redirector.
		using ModifyTimeFn = std::int64_t (*)(void*, const char*);
		using OpenFileExFn = void* (*)(void*, const char*, void*, std::int32_t, std::int32_t);
		ModifyTimeFn s_originalModifyTime{ nullptr };
		OpenFileExFn s_originalOpenFileEx{ nullptr };
		MovieRedirector s_movieRedirector{ nullptr };
		std::atomic<int> s_movieLogs{ 0 };

		std::int64_t HookedModifyTime(void* a_self, const char* a_url)
		{
			const auto replacement = s_movieRedirector && a_url ? s_movieRedirector(a_url) : std::string();
			return s_originalModifyTime(a_self, replacement.empty() ? a_url : replacement.c_str());
		}

		void* HookedOpenFileEx(void* a_self, const char* a_url, void* a_log, std::int32_t a_flags, std::int32_t a_mode)
		{
			const auto replacement = s_movieRedirector && a_url ? s_movieRedirector(a_url) : std::string();
			if (!replacement.empty() && s_movieLogs.fetch_add(1) < 40) logger::info("engine: interface file {} -> {}", a_url, replacement);
			return s_originalOpenFileEx(a_self, replacement.empty() ? a_url : replacement.c_str(), a_log, a_flags, a_mode);
		}

		// BSShaderManager::GetTexture(path, demand, out, normal, srgb, flag), the by-name texture loader.
		using GetTextureFn = std::uintptr_t (*)(const char*, bool, void*, bool, bool, bool);
		GetTextureFn s_originalGetTexture{ nullptr };
		TextureRedirector s_textureRedirector{ nullptr };
		std::atomic<int> s_textureLogs{ 0 };
		std::mutex s_texturePathsLock;
		std::unordered_set<std::string> s_texturePaths;

		// Returns a copy of the path that lives for the session.
		const char* Intern(std::string a_path)
		{
			std::scoped_lock lock(s_texturePathsLock);
			return s_texturePaths.insert(std::move(a_path)).first->c_str();
		}

		std::uintptr_t HookedGetTexture(const char* a_path, bool a_demand, void* a_out, bool a_normal, bool a_srgb, bool a_flag)
		{
			if (s_textureRedirector && a_path && *a_path && *a_path != '\x08') {
				// The part after any leading slashes, "data\" and "textures\" is the path relative to Textures.
				std::string lower = Settings::Lower(a_path);
				std::replace(lower.begin(), lower.end(), '/', '\\');
				std::size_t start = 0;
				while (start < lower.size() && lower[start] == '\\') ++start;
				if (lower.compare(start, 5, "data\\") == 0) start += 5;
				if (lower.compare(start, 9, "textures\\") == 0) start += 9;
				const auto replacement = s_textureRedirector(std::string_view(lower).substr(start));
				if (!replacement.empty()) {
					const char* redirected = Intern(std::string(a_path, start) + replacement);
					if (s_textureLogs.fetch_add(1) < 40) logger::info("engine: texture {} -> {}", a_path, redirected);
					return s_originalGetTexture(redirected, a_demand, a_out, a_normal, a_srgb, a_flag);
				}
			}
			return s_originalGetTexture(a_path, a_demand, a_out, a_normal, a_srgb, a_flag);
		}

		// The target of the first direct call in a function, decoded up to its first return.
		std::optional<std::uintptr_t> FirstCallTarget(std::uintptr_t a_function)
		{
			ZydisDecoder decoder{};
			if (ZYAN_FAILED(ZydisDecoderInit(&decoder, ZYDIS_MACHINE_MODE_LONG_64, ZYDIS_STACK_WIDTH_64))) return std::nullopt;
			ZydisDecodedInstruction instruction{};
			std::array<ZydisDecodedOperand, ZYDIS_MAX_OPERAND_COUNT> operands{};
			auto ip = a_function;
			for (int i = 0; i < 64; ++i) {
				if (ZYAN_FAILED(ZydisDecoderDecodeFull(&decoder, reinterpret_cast<const void*>(ip), ZYDIS_MAX_INSTRUCTION_LENGTH, &instruction, operands.data()))) return std::nullopt;
				if (instruction.mnemonic == ZYDIS_MNEMONIC_RET || instruction.mnemonic == ZYDIS_MNEMONIC_JMP) return std::nullopt;
				if (instruction.mnemonic == ZYDIS_MNEMONIC_CALL && operands[0].type == ZYDIS_OPERAND_TYPE_IMMEDIATE && operands[0].imm.is_relative) {
					return ip + instruction.length + static_cast<std::intptr_t>(operands[0].imm.value.s);
				}
				ip += instruction.length;
			}
			return std::nullopt;
		}

		// Points the function's start at the hook and returns a stub that runs the moved instructions and the rest, or null.
		void* Detour(std::uintptr_t a_target, void* a_hook)
		{
			ZydisDecoder decoder{};
			if (ZYAN_FAILED(ZydisDecoderInit(&decoder, ZYDIS_MACHINE_MODE_LONG_64, ZYDIS_STACK_WIDTH_64))) return nullptr;
			ZydisDecodedInstruction instruction{};
			std::array<ZydisDecodedOperand, ZYDIS_MAX_OPERAND_COUNT> operands{};
			std::size_t length = 0;
			while (length < 5) {
				if (ZYAN_FAILED(ZydisDecoderDecodeFull(&decoder, reinterpret_cast<const void*>(a_target + length), ZYDIS_MAX_INSTRUCTION_LENGTH, &instruction, operands.data()))) return nullptr;
				if ((instruction.attributes & ZYDIS_ATTRIB_IS_RELATIVE) != 0 || instruction.meta.category == ZYDIS_CATEGORY_RET ||
					instruction.meta.category == ZYDIS_CATEGORY_UNCOND_BR || instruction.meta.category == ZYDIS_CATEGORY_COND_BR || instruction.meta.category == ZYDIS_CATEGORY_CALL) {
					return nullptr;
				}
				length += instruction.length;
			}
			auto& trampoline = F4SE::GetTrampoline();
			auto* stub = static_cast<std::uint8_t*>(trampoline.allocate(length + 14));
			std::memcpy(stub, reinterpret_cast<const void*>(a_target), length);
			// jmp qword ptr [rip+0] followed by the address of the rest of the function.
			const std::array<std::uint8_t, 6> jump{ 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 };
			std::memcpy(stub + length, jump.data(), jump.size());
			const std::uintptr_t rest = a_target + length;
			std::memcpy(stub + length + jump.size(), &rest, sizeof(rest));
			trampoline.write_branch<5>(a_target, reinterpret_cast<std::uintptr_t>(a_hook));
			if (length > 5) {
				std::vector<std::uint8_t> fill(length - 5, 0xCC);
				REL::safe_write(a_target + 5, fill.data(), fill.size());
			}
			FlushInstructionCache(GetCurrentProcess(), reinterpret_cast<const void*>(a_target), length);
			return stub;
		}

		RE::NiAVObject* HookedLoad3D(RE::TESObjectREFR* a_ref, bool a_backgroundLoading)
		{
			auto* result = s_originalLoad3D(a_ref, a_backgroundLoading);
			if (s_modelTracing.load() && a_ref) {
				const auto* base = a_ref->GetObjectReference();
				logger::info("engine: 3D loaded {:08X} (base {:08X}, {})", a_ref->GetFormID(), base ? base->GetFormID() : 0, a_ref->GetParentCell() ? "in a cell" : "in no cell");
			}
			if (s_referenceCallback && a_ref && result) {
				s_referenceCallback(a_ref, result);
			}
			return result;
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
		s_originalLoad3D = reinterpret_cast<Load3DFn>(vtable.write_vfunc(0x86, &HookedLoad3D));
		const auto base = REL::Module::get().base();
		logger::info("engine: hooked TESObjectREFR::Load3D (vtable {:X}, original {:X})", vtable.address() - base, reinterpret_cast<std::uintptr_t>(s_originalLoad3D) - base);
		return s_originalLoad3D != nullptr;
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

	// The diffuse texture of a lighting shader: its material keeps the model's texture set, and slot 0 is the diffuse.
	static std::string LightingDiffuse(RE::BSShaderProperty* a_shader)
	{
		auto* material = reinterpret_cast<std::uint8_t*>(a_shader->material);
		auto* textureSet = material ? *reinterpret_cast<RE::BSTextureSet**>(material + 0x78) : nullptr;
		const char* diffuse = textureSet ? textureSet->GetTextureFilename(static_cast<RE::BSShaderProperty::TextureTypeEnum>(0)) : nullptr;
		return diffuse ? std::string(diffuse) : std::string();
	}

	std::string DiffuseName(RE::NiAVObject* a_geometry)
	{
		auto* shader = AnyShader(a_geometry);
		static const REL::Relocation<const RE::NiRTTI*> lightingRtti{ RE::Ni_RTTI::BSLightingShaderProperty };
		const auto* rtti = shader ? shader->GetRTTI() : nullptr;
		return rtti && rtti->IsKindOf(lightingRtti.get()) ? LightingDiffuse(shader) : std::string();
	}

	std::string ShaderPropertyName(RE::NiAVObject* a_geometry)
	{
		auto* prop = ShaderProperty(a_geometry);
		return prop && prop->name.c_str() ? std::string(prop->name.c_str()) : std::string();
	}

	RE::BSShaderProperty* AnyShader(RE::NiAVObject* a_geometry)
	{
		if (!a_geometry) return nullptr;
		static const REL::Relocation<const RE::NiRTTI*> shaderRtti{ RE::Ni_RTTI::BSShaderProperty };
		auto** props = reinterpret_cast<RE::NiObject**>(reinterpret_cast<std::uintptr_t>(a_geometry) + 0x130);
		for (int i = 1; i >= 0; --i) {
			auto* prop = props[i];
			const auto* rtti = prop ? prop->GetRTTI() : nullptr;
			if (rtti && rtti->IsKindOf(shaderRtti.get())) return static_cast<RE::BSShaderProperty*>(prop);
		}
		return nullptr;
	}

	std::string InlineKey(RE::NiAVObject* a_geometry)
	{
		if (ShaderProperty(a_geometry)) return {};
		auto* shader = AnyShader(a_geometry);
		if (!shader) return {};
		static const REL::Relocation<const RE::NiRTTI*> lightingRtti{ RE::Ni_RTTI::BSLightingShaderProperty };
		static const REL::Relocation<const RE::NiRTTI*> effectRtti{ RE::Ni_RTTI::BSEffectShaderProperty };
		const auto* rtti = shader->GetRTTI();
		std::string path;
		if (rtti && rtti->IsKindOf(lightingRtti.get())) {
			path = LightingDiffuse(shader);
		} else if (rtti && rtti->IsKindOf(effectRtti.get())) {
			// An effect shader's base texture object carries the file it was loaded from.
			auto* texture = shader->GetBaseTexture();
			if (texture && texture->name.c_str()) path = texture->name.c_str();
		}
		return path.empty() ? std::string() : "@" + TexturePath(path);
	}

	namespace
	{
		// Materials replaced on a geometry, kept alive until every draw queued before the change has run.
		struct Retired
		{
			std::uint8_t* material;
			bool lighting;
			std::chrono::steady_clock::time_point since;
		};
		std::mutex s_retiredLock;
		std::vector<Retired> s_retired;
		bool s_releaseQueued{ false };
		std::atomic<int> s_releaseLogs{ 0 };
		constexpr auto kRetireDelay = std::chrono::seconds(1);

		std::atomic<int> s_holderLogs{ 0 };

		// Hands the reference taken before a material change to a new shader property of the material's own kind and destroys that property, so the game releases the material through its own code.
		void Release(std::uint8_t* a_material, bool a_lighting)
		{
			const auto size = a_lighting ? std::size_t{ 0xE8 } : std::size_t{ 0x88 };
			auto* holder = static_cast<std::uint8_t*>(RE::malloc(size));
			if (!holder) return;
			using PropertyCtor = void* (*)(void*);
			const auto base = REL::Module::get().base();
			reinterpret_cast<PropertyCtor>(base + (a_lighting ? s_functions->lightingPropertyCtor : s_functions->effectPropertyCtor))(holder);
			auto*& slot = *reinterpret_cast<std::uint8_t**>(holder + 0x58);
			if (s_holderLogs.fetch_add(1) < 5) logger::info("engine: release holder {} ({}) replaces its default material {} with material {}", fmt::ptr(holder), a_lighting ? "lighting" : "effect", fmt::ptr(slot), fmt::ptr(a_material));
			// Puts the retired material in the holder's slot in place of the shared default material.
			slot = a_material;
			RE::NiPointer<RE::NiRefObject> owner{ reinterpret_cast<RE::NiRefObject*>(holder) };
		}

		void QueueRelease();

		// On the game thread: releases the materials retired at least kRetireDelay ago and queues the rest again.
		void ReleaseRetired()
		{
			std::vector<Retired> due;
			bool more = false;
			{
				std::scoped_lock lock(s_retiredLock);
				const auto now = std::chrono::steady_clock::now();
				std::erase_if(s_retired, [&](const Retired& a_entry) {
					if (now - a_entry.since < kRetireDelay) return false;
					due.push_back(a_entry);
					return true;
				});
				more = !s_retired.empty();
				s_releaseQueued = more;
			}
			for (const auto& entry : due) Release(entry.material, entry.lighting);
			if (!due.empty() && s_releaseLogs.fetch_add(1) < 20) logger::info("engine: released {} replaced materials", due.size());
			if (more) QueueRelease();
		}

		// Runs ReleaseRetired on the game thread once kRetireDelay has passed.
		void QueueRelease()
		{
			std::thread([] {
				std::this_thread::sleep_for(kRetireDelay);
				F4SE::GetTaskInterface()->AddTask([] { ReleaseRetired(); });
			}).detach();
		}

		void RetireMaterial(std::uint8_t* a_material, bool a_lighting)
		{
			bool queue = false;
			{
				std::scoped_lock lock(s_retiredLock);
				s_retired.push_back({ a_material, a_lighting, std::chrono::steady_clock::now() });
				queue = !s_releaseQueued;
				s_releaseQueued = true;
			}
			if (queue) QueueRelease();
		}

		std::atomic<int> s_textureSetLogs{ 0 };
		std::atomic<int> s_copyLogs{ 0 };

		// True when the bytes at the address are committed, readable memory.
		[[nodiscard]] bool Readable(const void* a_ptr, std::size_t a_size)
		{
			MEMORY_BASIC_INFORMATION info{};
			if (!a_ptr || VirtualQuery(a_ptr, &info, sizeof(info)) != sizeof(info)) return false;
			if (info.State != MEM_COMMIT || (info.Protect & (PAGE_NOACCESS | PAGE_GUARD)) != 0) return false;
			const auto end = reinterpret_cast<std::uintptr_t>(info.BaseAddress) + info.RegionSize;
			return reinterpret_cast<std::uintptr_t>(a_ptr) + a_size <= end;
		}

		// The texture set a material file loaded into the shader data: the one pointer in it to a BSShaderTextureSet.
		[[nodiscard]] RE::BSTextureSet* LoadedTextureSet(std::uint8_t* a_data, std::size_t a_size)
		{
			static const REL::Relocation<std::uintptr_t> vtable{ RE::VTABLE::BSShaderTextureSet[0] };
			for (std::size_t at = 0; at + sizeof(void*) <= a_size; at += sizeof(void*)) {
				auto* candidate = *reinterpret_cast<std::uintptr_t**>(a_data + at);
				if (!Readable(candidate, sizeof(std::uintptr_t))) continue;
				if (*candidate == vtable.address()) return reinterpret_cast<RE::BSTextureSet*>(candidate);
			}
			return nullptr;
		}

		// Gives a lighting shader's material the material file's texture set where it still holds another one, as it does for a
		// shader the model carries inline: the material data is applied, but the textures stay the model's own.
		void TakeTextureSet(RE::NiAVObject* a_geometry, std::uint8_t* a_shaderData, const std::string& a_file)
		{
			auto* shader = AnyShader(a_geometry);
			static const REL::Relocation<const RE::NiRTTI*> lightingRtti{ RE::Ni_RTTI::BSLightingShaderProperty };
			const auto* rtti = shader ? shader->GetRTTI() : nullptr;
			if (!rtti || !rtti->IsKindOf(lightingRtti.get())) return;
			auto* material = reinterpret_cast<std::uint8_t*>(shader->material);
			if (!material) return;
			auto* loaded = LoadedTextureSet(a_shaderData, 0x1E0);
			if (!loaded) {
				if (s_textureSetLogs.fetch_add(1) < 20) logger::warn("engine: {} loaded no texture set, so the shape keeps its own textures", a_file);
				return;
			}
			auto*& current = *reinterpret_cast<RE::BSTextureSet**>(material + 0x78);
			const auto type = static_cast<RE::BSShaderProperty::TextureTypeEnum>(0);
			const char* wanted = loaded->GetTextureFilename(type);
			const char* held = current ? current->GetTextureFilename(type) : nullptr;
			if (current == loaded || (wanted && held && Settings::Lower(wanted) == Settings::Lower(held))) return;
			// A material other shapes also hold is copied first (the material's Create and CopyMembers, virtuals 1 and 2), so only this
			// shape changes; the shape's reference to the shared one is released like any replaced material.
			const auto references = *reinterpret_cast<volatile LONG*>(material + 0x08);
			if (references > 1) {
				const auto* functions = *reinterpret_cast<std::uintptr_t**>(material);
				using CreateFn = std::uint8_t* (*)(void*);
				using CopyMembersFn = void (*)(void*, const void*);
				auto* copy = reinterpret_cast<CreateFn>(functions[1])(material);
				if (!copy) {
					logger::warn("engine: {} could not copy the shared material {}; the shape keeps the textures {}", a_file, fmt::ptr(material), held ? held : "");
					return;
				}
				reinterpret_cast<CopyMembersFn>(functions[2])(copy, material);
				InterlockedIncrement(reinterpret_cast<volatile LONG*>(copy + 0x08));
				shader->material = reinterpret_cast<RE::BSShaderMaterial*>(copy);
				RetireMaterial(material, true);
				if (s_copyLogs.fetch_add(1) < 20) logger::info("engine: {} gave the shape its own copy {} of the material {} that {} references share", a_file, fmt::ptr(copy), fmt::ptr(material), references);
				material = copy;
			}
			auto*& own = *reinterpret_cast<RE::BSTextureSet**>(material + 0x78);
			loaded->IncRefCount();
			auto* previous = own;
			own = loaded;
			if (previous) previous->DecRefCount();
			// The material's ClearTextures (virtual 0x0A) drops the loaded textures; the property's LoadTextureSet loads the new set.
			using ClearTexturesFn = void (*)(void*);
			reinterpret_cast<ClearTexturesFn>((*reinterpret_cast<std::uintptr_t**>(material))[0x0A])(material);
			using LoadTextureSetFn = void (*)(void*, std::uint32_t);
			reinterpret_cast<LoadTextureSetFn>(REL::Module::get().base() + s_functions->lightingLoadTextureSet)(shader, 0);
			if (s_textureSetLogs.fetch_add(1) < 60) logger::info("engine: {} replaced the shape's own textures {} with {}", a_file, held ? held : "", wanted ? wanted : "");
		}
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

		const bool trace = s_tracing.load() && s_traceCount.fetch_add(1) < 2000;
		if (trace) logger::info("engine: [thread {}] loading {}", GetCurrentThreadId(), file);
		// The geometry's current material gets a reference of its own, so the change cannot free it while a queued draw still reads it.
		auto* previousShader = AnyShader(a_geometry);
		static const REL::Relocation<const RE::NiRTTI*> lightingShaderRtti{ RE::Ni_RTTI::BSLightingShaderProperty };
		static const REL::Relocation<const RE::NiRTTI*> effectShaderRtti{ RE::Ni_RTTI::BSEffectShaderProperty };
		const auto* previousRtti = previousShader ? previousShader->GetRTTI() : nullptr;
		const bool previousLighting = previousRtti && previousRtti->IsKindOf(lightingShaderRtti.get());
		const bool previousEffect = previousRtti && previousRtti->IsKindOf(effectShaderRtti.get());
		auto* previous = previousShader && (previousLighting || previousEffect) ? reinterpret_cast<std::uint8_t*>(previousShader->material) : nullptr;
		if (previous) InterlockedIncrement(reinterpret_cast<volatile LONG*>(previous + 0x08));
		alignas(16) std::uint8_t shaderData[0x1E0]{};
		ctor(shaderData);
		const auto status = loadMaterialFile(file.c_str(), shaderData, 0);
		if (trace) logger::info("engine: [thread {}] loaded {} status {}", GetCurrentThreadId(), file, status);
		bool ok = false;
		if (status == 0) {
			applyMaterialData(shaderData, a_geometry, false);
			TakeTextureSet(a_geometry, shaderData, file);
			ok = true;
		} else {
			logger::warn("engine: LoadMaterialFile('{}') returned {}", a_materialFile, status);
		}
		dtor(shaderData);
		if (previous) RetireMaterial(previous, previousLighting);
		if (ok) {
			// The engine keys the renderer's material lookup on the property name, so it has to match the new file.
			if (!Settings::Lower(file).ends_with(".bgem") && DiffuseName(a_geometry).empty()) {
				logger::warn("engine: material '{}' applied but the geometry shows no diffuse texture", file);
			}
			RE::NiObjectNET* prop = ShaderProperty(a_geometry);
			if (!prop) prop = AnyShader(a_geometry);
			if (prop) {
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

	void ForEachMaterialSubstitution(const RE::BGSMaterialSwap* a_swap, const std::function<void(std::string_view, std::string_view)>& a_visitor)
	{
		if (!a_swap) return;
		for (const auto& entry : a_swap->swapMap) {
			const char* source = entry.first.c_str();
			const char* target = entry.second.swapMaterial.c_str();
			if (source && target && *source && *target) a_visitor(source, target);
		}
	}

	RE::TESModel* EffectiveModel(const RE::TESObjectREFR* a_ref)
	{
		if (!a_ref) return nullptr;
		return s_originalGetTESModel ? ChosenModel(a_ref, false) : a_ref->GetTESModel();
	}

	std::string TexturePath(std::string_view a_path)
	{
		return Settings::RelativePath(a_path, "textures");
	}

	bool SetTextureSetTexture(RE::BGSTextureSet* a_set, std::size_t a_index, std::string_view a_texture)
	{
		if (!a_set || a_index >= std::size(a_set->textures)) return false;
		auto& name = a_set->textures[a_index].textureName;
		const auto current = TexturePath(name.c_str() ? name.c_str() : "");
		const auto wanted = TexturePath(a_texture);
		if (current == wanted) return false;
		auto& id = a_set->textureFileIDs[a_index];
		// Keeps the form the engine hashed the name in: with the Textures folder in front or without it.
		const bool withFolder = id == FileID("textures\\" + current);
		if (!withFolder && id != FileID(current)) {
			logger::warn("engine: texture set {:08X} texture {} names {} but its file id is not that name's; left as it is", a_set->GetFormID(), a_index, current);
			return false;
		}
		name = wanted;
		id = FileID(withFolder ? "textures\\" + wanted : wanted);
		return true;
	}

	RE::BSResource::ID FileID(std::string_view a_path)
	{
		auto path = Settings::Lower(a_path);
		std::replace(path.begin(), path.end(), '/', '\\');
		const std::string_view whole(path);
		const auto slash = whole.rfind('\\');
		const auto folder = slash == std::string_view::npos ? std::string_view() : whole.substr(0, slash);
		const auto name = slash == std::string_view::npos ? whole : whole.substr(slash + 1);
		const auto dot = name.rfind('.');
		const auto stem = dot == std::string_view::npos ? name : name.substr(0, dot);
		const auto extension = dot == std::string_view::npos ? std::string_view() : name.substr(dot + 1);
		const auto crc = [](std::string_view a_text) { return RE::detail::GenerateCRC32({ reinterpret_cast<const std::uint8_t*>(a_text.data()), a_text.size() }); };
		RE::BSResource::ID id;
		id.file = crc(stem);
		std::memcpy(&id.ext, extension.data(), std::min(extension.size(), sizeof(id.ext)));
		id.dir = crc(folder);
		return id;
	}

	void ForEachGeometry(RE::NiAVObject* a_root, const std::function<void(RE::NiAVObject*, int)>& a_visitor)
	{
		int index = 0;
		Visit(a_root, index, a_visitor);
	}

	bool SetReferenceName(RE::TESObjectREFR* a_ref, const std::string& a_name, const std::function<bool(std::string_view)>& a_owned)
	{
		auto* list = a_ref ? a_ref->extraList.get() : nullptr;
		if (!list) return false;
		if (auto* existing = list->GetByType<RE::ExtraTextDisplayData>(); existing) {
			const std::string_view current = existing->displayName.c_str() ? existing->displayName.c_str() : "";
			if (existing->ownerInstance.get() != RE::ExtraTextDisplayData::DisplayDataType::kCustomName || !a_owned(current)) return false;
			if (a_name.empty()) {
				list->RemoveExtra<RE::ExtraTextDisplayData>();
				return true;
			}
			if (current == a_name) return false;
			existing->displayName = RE::BSFixedStringCS(a_name.c_str());
			existing->customNameLength = static_cast<std::uint16_t>(a_name.size());
			return true;
		}
		if (a_name.empty()) return false;
		auto* data = static_cast<RE::ExtraTextDisplayData*>(RE::malloc(sizeof(RE::ExtraTextDisplayData)));
		if (!data) return false;
		std::memset(static_cast<void*>(data), 0, sizeof(RE::ExtraTextDisplayData));
		RE::stl::emplace_vtable(data);
		data->type = RE::EXTRA_DATA_TYPE::kTextDisplayData;
		new (&data->displayName) RE::BSFixedStringCS(a_name.c_str());
		data->ownerInstance = RE::ExtraTextDisplayData::DisplayDataType::kCustomName;
		data->customNameLength = static_cast<std::uint16_t>(a_name.size());
		list->AddExtra(data);
		return true;
	}

	bool InstallMovieHook(MovieRedirector a_redirector)
	{
		s_movieRedirector = a_redirector;
		REL::Relocation<std::uintptr_t> vtable{ RE::VTABLE::BSScaleformFileOpener[0] };
		s_originalModifyTime = reinterpret_cast<ModifyTimeFn>(vtable.write_vfunc(0x2, &HookedModifyTime));
		s_originalOpenFileEx = reinterpret_cast<OpenFileExFn>(vtable.write_vfunc(0x3, &HookedOpenFileEx));
		const auto base = REL::Module::get().base();
		logger::info("engine: hooked BSScaleformFileOpener (vtable {:X}, originals {:X} {:X})", vtable.address() - base,
			reinterpret_cast<std::uintptr_t>(s_originalModifyTime) - base, reinterpret_cast<std::uintptr_t>(s_originalOpenFileEx) - base);
		return s_originalModifyTime && s_originalOpenFileEx;
	}

	bool InstallModelHook(ModelChooser a_chooser)
	{
		s_modelChooser = a_chooser;
		REL::Relocation<std::uintptr_t> vtable{ RE::VTABLE::TESObjectREFR[0] };
		s_originalGetTESModel = reinterpret_cast<GetTESModelFn>(vtable.write_vfunc(0x90, &HookedGetTESModel));
		const auto base = REL::Module::get().base();
		logger::info("engine: hooked TESObjectREFR::GetTESModel (vtable {:X}, original {:X})", vtable.address() - base, reinterpret_cast<std::uintptr_t>(s_originalGetTESModel) - base);
		return s_originalGetTESModel != nullptr;
	}

	bool InstallTextureHook(TextureRedirector a_redirector)
	{
		const auto base = REL::Module::get().base();
		REL::Relocation<std::uintptr_t> vtable{ RE::VTABLE::BSShaderTextureSet[0] };
		const auto getTextureSlot = *reinterpret_cast<const std::uintptr_t*>(vtable.address() + 0x2B * sizeof(std::uintptr_t));
		// Another plugin may already have replaced the vtable entry; only the game's own code is decoded and patched.
		const auto text = REL::Module::get().segment(REL::Segment::text);
		const auto inGame = [&](std::uintptr_t a_address) { return a_address >= text.address() && a_address < text.address() + text.size(); };
		if (!inGame(getTextureSlot)) {
			logger::error("engine: BSShaderTextureSet::GetTexture points outside the game ({:X}), another plugin hooks it; textures loaded by name stay the game's", getTextureSlot);
			return false;
		}
		const auto loader = FirstCallTarget(getTextureSlot);
		if (loader && !inGame(*loader)) {
			logger::error("engine: the texture loader call leads outside the game ({:X}); textures loaded by name stay the game's", *loader);
			return false;
		}
		if (!loader) {
			logger::error("engine: no texture loader call in BSShaderTextureSet::GetTexture ({:X}); textures loaded by name stay the game's", getTextureSlot - base);
			return false;
		}
		s_textureRedirector = a_redirector;
		F4SE::AllocTrampoline(64);
		s_originalGetTexture = reinterpret_cast<GetTextureFn>(Detour(*loader, reinterpret_cast<void*>(&HookedGetTexture)));
		if (!s_originalGetTexture) {
			logger::error("engine: the texture loader at {:X} starts with code that cannot be moved; textures loaded by name stay the game's", *loader - base);
			return false;
		}
		logger::info("engine: hooked the texture loader {:X} (called from BSShaderTextureSet::GetTexture {:X})", *loader - base, getTextureSlot - base);
		return true;
	}

	void SetTracing(bool a_enabled)
	{
		s_tracing.store(a_enabled);
	}

	void SetModelTracing(bool a_enabled)
	{
		s_modelTracing.store(a_enabled);
	}

	bool ModelTracing()
	{
		return s_modelTracing.load();
	}
}
