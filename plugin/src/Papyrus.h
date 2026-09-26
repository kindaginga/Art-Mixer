#pragma once

namespace GShuf::Papyrus
{
	// Registers the GShuf native script functions with the Papyrus VM.
	bool Register(RE::BSScript::IVirtualMachine* a_vm);
}
