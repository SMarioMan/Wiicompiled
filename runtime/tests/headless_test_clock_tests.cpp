#include "headless_test_clock.h"
#include <cassert>
#include <array>
#include <limits>

int main() {
    using namespace HeadlessTestClock;
    assert(Configure(nullptr,false)==1);
    assert(Nanoseconds(123456789)==123456789 && DeltaMicros(3)==3);
    for(const char* bad:std::array{"", "0", "65", "-1", "+2", "2x", "999999999999999999"}) {
        bool rejected=false;
        try{Configure(bad,true);}catch(const std::invalid_argument&){rejected=true;}
        assert(rejected);
    }
    bool rejected=false;
    try{Configure("16",false);}catch(const std::invalid_argument&){rejected=true;}
    assert(rejected);
    assert(Configure("16",true)==16);
    assert(Nanoseconds(16000000000ull)==1000000000ull);
    assert(Nanoseconds(std::numeric_limits<uint64_t>::max())==std::numeric_limits<uint64_t>::max()/16);
    uint64_t total=0;
    for(unsigned i=0;i<16003;i++)total+=DeltaMicros(1);
    assert(total==1000); // Sub-microsecond fractions are retained across polls.
    total+=DeltaMicros(13);
    assert(total==1001);
    assert(Configure(nullptr,true)==1);
    assert(DeltaMicros(99)==99 && Nanoseconds(999)==999);
    assert(Configure("64",true)==64);
    assert(DeltaMicros(63)==0 && DeltaMicros(1)==1);
}
