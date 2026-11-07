#include "local_config.h"
volatile struct mkw_lan_config mkw_lan_config = {
    .magic = 0x4d4b5743, .version = 2, .instance = 1,
    .instances = MKW_LAN_CAPACITY, .automation = 0, .capacity = MKW_LAN_CAPACITY,
    .input_gcn = ((uint32_t)MKW_LAN_INPUT_GCN_HI << 16) | MKW_LAN_INPUT_GCN_LO,
    .race_course = MKW_LAN_RACE_COURSE_OFFSET, .race_mode = MKW_LAN_RACE_MODE_OFFSET,
    .race_player_count = MKW_LAN_RACE_PLAYER_COUNT_OFFSET
};
