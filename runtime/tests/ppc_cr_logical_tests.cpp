#include <cstdint>
#include "isa/ppc_isa_cr.h"

#include <cstdio>
#include <initializer_list>

int main()
{
    // Columns are (BA,BB) = 00,01,10,11. In particular, crorc is
    // BA | ~BB; reversing the complemented operand breaks compound guards.
    constexpr unsigned truth[8][4] = {
        {1,0,0,0}, {0,0,1,0}, {0,1,1,0}, {1,1,1,0},
        {0,0,0,1}, {1,0,0,1}, {1,0,1,1}, {0,1,1,1}
    };
    for (unsigned op=0; op<8; ++op)
        for (unsigned ba=0; ba<32; ++ba)
            for (unsigned bb=0; bb<32; ++bb)
                for (unsigned bt=0; bt<32; ++bt)
                    for (uint32_t initial : {0u, 0xffffffffu, 0xa5c39678u, 0x5a3c6987u}) {
                        const unsigned a=(initial>>(31-ba))&1u, b=(initial>>(31-bb))&1u;
                        const uint32_t mask=1u<<(31-bt);
                        const uint32_t expected=(initial&~mask)|(truth[op][a*2+b] ? mask : 0u);
                        const uint32_t actual=PpcCrLogicalResident(initial,op,bt,ba,bb);
                        if (actual!=expected) {
                            std::fprintf(stderr,"CR logical op %u BT=%u BA=%u BB=%u: %08x != %08x\n",
                                         op,bt,ba,bb,actual,expected);
                            return 1;
                        }
                    }
    return 0;
}
