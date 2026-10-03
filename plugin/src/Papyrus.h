#pragma once

namespace ADT::Papyrus
{
	// Registers the ADT native script functions with the Papyrus VM.
	bool Register(RE::BSScript::IVirtualMachine* a_vm);
}
