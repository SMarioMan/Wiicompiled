#include "abi_bridge.h"
#include "guest_function_imports.h"
#include "memory.h"
#include "mkw_guest_layout.h"

#include <algorithm>
#include <array>
#include <cstdint>
#include <stdexcept>
#include <vector>

namespace {
constexpr uint32_t kAuthoredRanks = 12;
constexpr uint32_t kItems = 19;
constexpr uint32_t kProbabilityStride = kItems * 2;
constexpr uint32_t kRouletteStride = (kItems + 1) * 4;

uint32_t Allocate(CpuContext* caller, uint32_t bytes) {
    CpuContext allocation = *caller;
    allocation.gpr[3] = bytes;
    CpuContextScope scope(&allocation);
    MKW_GUEST_FUNC(80229DF0)(&allocation);
    if (!allocation.gpr[3]) throw std::runtime_error("Item probability allocation failed");
    return allocation.gpr[3];
}

uint16_t Interpolate(const std::vector<uint16_t>& source, uint32_t item,
                     uint64_t numerator, uint32_t denominator) {
    const uint32_t lower = static_cast<uint32_t>(numerator / denominator);
    const uint32_t remainder = static_cast<uint32_t>(numerator % denominator);
    const uint32_t upper = std::min(lower + 1, static_cast<uint32_t>(source.size() / kItems - 1));
    return static_cast<uint16_t>((uint64_t{source[lower * kItems + item]} * (denominator - remainder) +
        uint64_t{source[upper * kItems + item]} * remainder + denominator / 2) / denominator);
}

std::vector<uint16_t> ReadProbabilities(uint32_t buffer, uint32_t ranks) {
    std::vector<uint16_t> result(ranks * kItems);
    for (uint32_t i = 0; i < result.size(); ++i) result[i] = Memory::Read16(buffer + i * 2);
    return result;
}

// The data address differs independently of the function address across game
// regions. Decode the original function's checked address materialization.
uint32_t AuthoredRank(uint32_t count, uint32_t position) {
    const uint32_t function = MKW_GADDR(807BAD20);
    const uint32_t high = Memory::Read32(function + 4), low = Memory::Read32(function + 8);
    if ((high & 0xffff0000u) != 0x3ca00000u || (low & 0xffff0000u) != 0x38a50000u)
        throw std::runtime_error("Unrecognized original item-rank table address");
    const uint32_t table = ((high & 0xffffu) << 16) + static_cast<int16_t>(low & 0xffffu);
    const uint32_t rank = Memory::Read16(table + ((count - 1) * kAuthoredRanks + position) * 2);
    if (!rank || rank > kAuthoredRanks) throw std::runtime_error("Invalid original item-rank table entry");
    return rank - 1;
}
}

// Keep the game's item-mode modifiers and roulette union logic. Only the
// rank axis changes: every participant uses the same capacity-sized table.
extern "C" void ItemSlotData_ProcessTable_807ba9d8(CpuContext* ctx) {
    const uint32_t capacity = MkwGuestLayout::kLayout.playerCapacity;
    const uint32_t raw = ctx->gpr[4], holder = ctx->gpr[5], self = ctx->gpr[3];
    const bool updateRoulette = ctx->gpr[6] != 0;
    if (capacity <= kAuthoredRanks || ctx->gpr[7] || Memory::Read8(raw) != kAuthoredRanks) {
        MKW_GUEST_NEXT(807BA9D8)(ctx);
        return;
    }
    if (Memory::Read8(raw + 1) != kItems)
        throw std::runtime_error("Normal item tables must contain 19 item rows");
    uint32_t buffer = Memory::Read32(holder + 4);
    if (!buffer) {
        buffer = Allocate(ctx, capacity * kProbabilityStride);
        Memory::Write32(holder, capacity);
        Memory::Write32(holder + 4, buffer);
    } else if (Memory::Read32(holder) != capacity) {
        throw std::runtime_error("Item probability storage does not match the translated capacity");
    }
    MKW_GUEST_NEXT(807BA9D8)(ctx);
    const auto authored = ReadProbabilities(buffer, kAuthoredRanks);
    for (uint32_t rank = 0; rank < capacity; ++rank)
        for (uint32_t item = 0; item < kItems; ++item)
            Memory::Write16(buffer + rank * kProbabilityStride + item * 2,
                Interpolate(authored, item, uint64_t{rank} * (kAuthoredRanks - 1), capacity - 1));
    if (updateRoulette) {
        const uint32_t roulette = Memory::Read32(self + 0x2c);
        std::array<uint32_t, kAuthoredRanks * (kItems + 1)> visual{};
        for (uint32_t i = 0; i < visual.size(); ++i) visual[i] = Memory::Read32(roulette + i * 4);
        for (uint32_t rank = 0; rank < capacity; ++rank) {
            const uint32_t source = (uint64_t{rank} * (kAuthoredRanks - 1) + (capacity - 1) / 2) / (capacity - 1);
            for (uint32_t word = 0; word < kItems + 1; ++word)
                Memory::Write32(roulette + rank * kRouletteStride + word * 4, visual[source * (kItems + 1) + word]);
        }
    }
}
REGISTER_NATIVE_WRAPPER_AS(0x807BA9D8, ItemSlotData_ProcessTable_807ba9d8,
                           "ItemSlotData_ProcessTable_807ba9d8");

extern "C" void ItemSlotData_PostProcessVSTable_807bad20(CpuContext* ctx) {
    const uint32_t capacity = MkwGuestLayout::kLayout.playerCapacity;
    if (capacity <= kAuthoredRanks) {
        MKW_GUEST_NEXT(807BAD20)(ctx);
        return;
    }
    const uint32_t count = Memory::Read32(ctx->gpr[3] + 0x44), holder = ctx->gpr[4];
    if (count > capacity || Memory::Read32(holder) != capacity)
        throw std::runtime_error("VS item field exceeds its probability table");
    const uint32_t buffer = Memory::Read32(holder + 4);
    const auto source = ReadProbabilities(buffer, capacity);
    for (uint32_t rank = 0; rank < capacity; ++rank) {
        const uint64_t numerator = count <= kAuthoredRanks
            ? uint64_t{rank < count ? AuthoredRank(count, rank) : 0} * (capacity - 1)
            : uint64_t{rank} * (capacity - 1);
        const uint32_t denominator = count <= kAuthoredRanks ? kAuthoredRanks - 1 : count - 1;
        for (uint32_t item = 0; item < kItems; ++item)
            Memory::Write16(buffer + rank * kProbabilityStride + item * 2,
                rank < count ? Interpolate(source, item, numerator, denominator) : 0);
    }
}
REGISTER_NATIVE_WRAPPER_AS(0x807BAD20, ItemSlotData_PostProcessVSTable_807bad20,
                           "ItemSlotData_PostProcessVSTable_807bad20");
