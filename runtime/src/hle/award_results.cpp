#include "abi_bridge.h"
#include "guest_function_imports.h"
#include "memory.h"
#include "mkw_guest_layout.h"

#include <algorithm>
#include <vector>

// RMCE01 AwardResults has twelve authored cards. The original function
// indexes them directly by cup rank, which can be 128 in an expanded field.
extern "C" void AwardResults_PrepareExpandedGrandPrixItems(CpuContext* ctx) {
    const auto& layout = MkwGuestLayout::kLayout;
    if (layout.playerCapacity <= 12 || GuestRegion::Current() != GuestRegion::Id::NtscU) {
        MKW_GUEST_NEXT(805BCDC4)(ctx);
        return;
    }
    const auto manager = Memory::Read32(MKW_GADDR(809C1E38));
    const auto section = manager ? Memory::Read32(manager) : 0;
    const auto data = Memory::Read32(MKW_GADDR(809BD728));
    if (!data || !section || Memory::Read32(section) != 0x35) {
        MKW_GUEST_NEXT(805BCDC4)(ctx);
        return;
    }

    struct Racer { uint32_t index, rank; bool local; };
    std::vector<Racer> racers;
    racers.reserve(layout.playerCapacity);
    const auto players = data + layout.racedataAwards + layout.scenarioPlayers;
    for (uint32_t i = 0; i < layout.playerCapacity; ++i) {
        const auto player = players + i * layout.playerStride;
        const auto type = Memory::Read32(player + 0x10);
        const auto rank = Memory::Read8(player + 0xe0);
        if (type != 5 && rank && rank <= layout.playerCapacity)
            racers.push_back({i, rank, type == 0});
    }
    constexpr uint32_t cardCount = 12, cardsOffset = 1532, cardStride = 372;
    constexpr uint32_t cardHiddenOffset = 128, localCountOffset = 5998, rowCountOffset = 5999;
    const auto byRank = [](const Racer& a, const Racer& b) {
        if (a.rank != b.rank) return a.rank < b.rank;
        return a.index < b.index;
    };
    // Keep the local racers in the ceremony. Fill the other cards with the
    // highest-ranked CPU racers, then order the cards by their real cup ranks.
    std::sort(racers.begin(), racers.end(), [&](const Racer& a, const Racer& b) {
        return a.local == b.local ? byRank(a, b) : a.local;
    });
    if (racers.size() > cardCount) racers.resize(cardCount);
    std::sort(racers.begin(), racers.end(), byRank);

    const auto page = ctx->gpr[3];
    Memory::Write8(page + rowCountOffset, static_cast<uint8_t>(racers.size()));
    const bool multipleLocalRacers = Memory::Read8(page + localCountOffset) >= 2;
    for (uint32_t i = 0; i < cardCount; ++i) {
        const auto card = page + cardsOffset + i * cardStride;
        if (i >= racers.size()) {
            Memory::Write8(card + cardHiddenOffset, 1);
            continue;
        }
        CpuContext call = *ctx;
        call.gpr[3] = card;
        call.gpr[4] = racers[i].index;
        call.gpr[5] = 0;
        call.gpr[6] = multipleLocalRacers;
        MKW_GUEST_FUNC(805BBBD0)(&call);
    }
}
REGISTER_NATIVE_WRAPPER_AS(0x805BCDC4, AwardResults_PrepareExpandedGrandPrixItems,
                           "AwardResults_PrepareExpandedGrandPrixItems");
