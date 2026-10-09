#pragma once

namespace ArtMixer::Papyrus
{
	// Registers the ArtMixer native script functions with the Papyrus VM.
	bool Register(RE::BSScript::IVirtualMachine* a_vm);
}
