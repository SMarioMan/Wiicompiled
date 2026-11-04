#ifndef MKW_LOCAL_CONFIG_H
#define MKW_LOCAL_CONFIG_H
#include <stdint.h>
extern volatile uint32_t mkw_instance_id;
extern volatile uint32_t mkw_instance_count;
#define MKW_INSTANCE_ID (mkw_instance_id & 255)
#define MKW_INSTANCE_COUNT (mkw_instance_count & 255)
#define MKW_LOCAL_IP (0x7f000000 | MKW_INSTANCE_ID)
#endif
