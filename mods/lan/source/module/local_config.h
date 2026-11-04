#ifndef MKW_LOCAL_CONFIG_H
#define MKW_LOCAL_CONFIG_H
#include <stdint.h>
#define MKW_LAN_CAPACITY 12
/* Versioned data ABI; native code locates this record through ELF symbols. */
struct mkw_lan_config {
    uint32_t magic;
    uint32_t version;
    uint32_t instance;
    uint32_t instances;
    uint32_t automation; /* 0 manual menus, 1 auto-host, 2 auto-join */
    uint32_t bind_ip; /* zero selects 127.0.0.instance */
    uint32_t peer_count;
    uint32_t peers[MKW_LAN_CAPACITY];
};
extern volatile struct mkw_lan_config mkw_lan_config;
#define MKW_INSTANCE_ID (mkw_lan_config.instance)
#define MKW_INSTANCE_COUNT (mkw_lan_config.instances)
#define MKW_AUTOMATION (mkw_lan_config.automation)
#define MKW_LOCAL_IP (mkw_lan_config.bind_ip ? mkw_lan_config.bind_ip : (0x7f000000 | MKW_INSTANCE_ID))
#endif
