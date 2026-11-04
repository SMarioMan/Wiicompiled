#include "local_config.h"
/* Distinct markers allow deterministic per-profile configuration after link. */
volatile uint32_t mkw_instance_id = (0x4d4b0000 | MKW_BUILD_INSTANCE_ID);
volatile uint32_t mkw_instance_count = (0x4d4b0100 | MKW_BUILD_INSTANCE_COUNT);
