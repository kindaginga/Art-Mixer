#pragma once

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <functional>
#include <optional>
#include <span>
#include <utility>
#include <vector>

#ifdef _MSC_VER
#	pragma warning(push, 0)
#endif
#include <Zydis/Zydis.h>
#ifdef _MSC_VER
#	pragma warning(pop)
#endif

namespace REL::detail
{
	// Finds the RVA of an MSVC x64 switch jump table read by a recent instruction on the current path.
	[[nodiscard]] inline std::optional<std::uint32_t> find_jump_table_rva(
		const std::vector<std::pair<ZydisDecodedInstruction, std::array<ZydisDecodedOperand, ZYDIS_MAX_OPERAND_COUNT>>>& a_history,
		std::uint32_t a_codeRVA,
		std::size_t a_codeSize) noexcept
	{
		for (auto it = a_history.rbegin(); it != a_history.rend(); ++it) {
			const auto& [instruction, operands] = *it;
			if (instruction.mnemonic != ZYDIS_MNEMONIC_MOV && instruction.mnemonic != ZYDIS_MNEMONIC_MOVSXD) {
				continue;
			}
			for (std::size_t i = 0; i < instruction.operand_count_visible; ++i) {
				const auto& operand = operands[i];
				if (operand.type != ZYDIS_OPERAND_TYPE_MEMORY || operand.mem.scale != 4 ||
					operand.mem.index == ZYDIS_REGISTER_NONE || !operand.mem.disp.has_displacement) {
					continue;
				}
				const auto disp = operand.mem.disp.value;
				if (disp >= static_cast<std::int64_t>(a_codeRVA) &&
					disp < static_cast<std::int64_t>(a_codeRVA) + static_cast<std::int64_t>(a_codeSize)) {
					return static_cast<std::uint32_t>(disp);
				}
			}
		}
		return std::nullopt;
	}

	// Walks the code by recursive descent from its first byte, resolving MSVC switch jump tables, and reports every
	// direct 5-byte E8/E9 branch in ascending offset order. Returns false only when the entry instruction cannot be
	// decoded. a_codeRVA is the RVA of a_code[0]; jump-table entries are image-relative RVAs.
	template <class Callback>
	[[nodiscard]] bool for_each_direct_relative_branch(
		std::span<const std::uint8_t> a_code,
		std::uint32_t a_codeRVA,
		Callback&& a_callback) noexcept
	{
		ZydisDecoder decoder{};
		if (ZYAN_FAILED(ZydisDecoderInit(
				std::addressof(decoder),
				ZYDIS_MACHINE_MODE_LONG_64,
				ZYDIS_STACK_WIDTH_64))) {
			return false;
		}

		enum : std::uint8_t
		{
			kUnknown,
			kCode,
			kData
		};
		std::vector<std::uint8_t> state(a_code.size(), kUnknown);
		std::vector<std::size_t> worklist{ 0 };
		std::vector<std::pair<std::size_t, std::uint8_t>> branches;
		bool entryDecoded = false;

		while (!worklist.empty()) {
			auto offset = worklist.back();
			worklist.pop_back();
			std::vector<std::pair<ZydisDecodedInstruction, std::array<ZydisDecodedOperand, ZYDIS_MAX_OPERAND_COUNT>>> history;

			while (offset < a_code.size() && state[offset] == kUnknown) {
				ZydisDecodedInstruction instruction{};
				std::array<ZydisDecodedOperand, ZYDIS_MAX_OPERAND_COUNT> operands{};
				if (ZYAN_FAILED(ZydisDecoderDecodeFull(
						std::addressof(decoder),
						a_code.data() + offset,
						a_code.size() - offset,
						std::addressof(instruction),
						operands.data())) ||
					instruction.length == 0 || instruction.length > a_code.size() - offset) {
					break;
				}
				if (offset == 0) {
					entryDecoded = true;
				}
				state[offset] = kCode;

				const auto opcode = a_code[offset];
				if (instruction.length == 5 && (opcode == 0xE8 || opcode == 0xE9)) {
					branches.emplace_back(offset, opcode);
				}

				history.emplace_back(instruction, operands);
				if (history.size() > 8) {
					history.erase(history.begin());
				}

				const auto next = offset + instruction.length;
				const auto category = instruction.meta.category;
				const auto relativeTarget = [&]() -> std::optional<std::size_t> {
					ZyanU64 absolute{};
					if (instruction.operand_count_visible > 0 &&
						operands[0].type == ZYDIS_OPERAND_TYPE_IMMEDIATE && operands[0].imm.is_relative &&
						ZYAN_SUCCESS(ZydisCalcAbsoluteAddress(std::addressof(instruction), operands.data(), offset, std::addressof(absolute))) &&
						absolute < a_code.size()) {
						return static_cast<std::size_t>(absolute);
					}
					return std::nullopt;
				};

				if (category == ZYDIS_CATEGORY_COND_BR) {
					if (const auto target = relativeTarget()) {
						worklist.push_back(*target);
					}
					offset = next;
					continue;
				}
				if (category == ZYDIS_CATEGORY_UNCOND_BR) {
					if (const auto target = relativeTarget()) {
						worklist.push_back(*target);
					} else if (const auto table = find_jump_table_rva(history, a_codeRVA, a_code.size())) {
						for (auto entryOffset = static_cast<std::size_t>(*table - a_codeRVA);
							 entryOffset + 4 <= a_code.size() && state[entryOffset] != kCode;
							 entryOffset += 4) {
							std::uint32_t entry{};
							std::memcpy(std::addressof(entry), a_code.data() + entryOffset, sizeof(entry));
							if (entry < a_codeRVA || entry >= a_codeRVA + a_code.size()) {
								break;
							}
							std::fill_n(state.begin() + static_cast<std::ptrdiff_t>(entryOffset), 4, kData);
							worklist.push_back(entry - a_codeRVA);
						}
					}
					break;
				}
				if (category == ZYDIS_CATEGORY_RET || instruction.mnemonic == ZYDIS_MNEMONIC_INT3 ||
					instruction.mnemonic == ZYDIS_MNEMONIC_UD2) {
					break;
				}
				offset = next;
			}
		}

		if (!entryDecoded) {
			return false;
		}
		std::ranges::sort(branches);
		for (const auto& [offset, opcode] : branches) {
			std::invoke(a_callback, offset, opcode);
		}
		return true;
	}
}
