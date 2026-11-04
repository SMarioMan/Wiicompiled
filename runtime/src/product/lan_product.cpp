#include "runtime_product.h"

#ifndef MKW_LAN_INSTANCE
#error "The LAN product requires an instance ID matching its linked guest module"
#endif

namespace RuntimeProduct {

const Descriptor& Active() noexcept {
    static constexpr Descriptor descriptor{
        Kind::Lan,
        "WiiCompiled LAN",
        MKW_LAN_INSTANCE,
    };
    return descriptor;
}

} // namespace RuntimeProduct
