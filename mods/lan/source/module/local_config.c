#include "local_config.h"
volatile struct mkw_lan_config mkw_lan_config = {
    .magic = 0x4d4b5743, .version = 1, .instance = 1,
    .instances = MKW_LAN_CAPACITY, .automation = 0
};
