#include "local_config.h"

#include "network_thread.h"

#include <fcntl.h>
#include <rvl/dwc.h>
#include <rvl/OSTime.h>
#include <rvl/so.h>
#include <rvl/vi.h>
#include <stdbool.h>

#include "log.h"
#include "mkw.h"
#include "port_addresses.h"
#include "processor.h"
#include "discovery_clock.h"

// port to do udp advertisement on
#define BROADCAST_PORT 27900
#define DISCOVERY_PORT 27909
#define DISCOVERY_GROUP 0xefff4d4b /* 239.255.77.75 */
#define BROADCAST_INTERVAL 60
_Static_assert(LAN_DISCOVERY_TICKS_PER_UNIT * BROADCAST_INTERVAL == secs_to_ticks(1),
               "Discovery clock must match the Wii timebase");
#define BROADCAST_TIMEOUT (BROADCAST_INTERVAL * 5)
/* A queued reservation remains valid for the expanded SDK wait allowance,
 * independently of the shorter discovery advertisement lifetime. */
#define RESERVATION_TIMEOUT (BROADCAST_INTERVAL * (MKW_LAN_CAPACITY > 12 ? 60 : 5))
// Version 3 uses capacity-sized SELECT sections and packed team words.
#define DISCOVERY_PROTOCOL 3
#define BROADCAST_MAGIC (('R' << 24) | ('M' << 16) | ('C' << 8) | 'X')
#define BROADCAST_TRACK_MAX (MKW_LAN_CAPACITY > 32 ? MKW_LAN_CAPACITY : 32)
/* Leave time for SDK matching and the other receive socket during discovery
 * bursts. Draining until EAGAIN can monopolize this thread on a busy link. */
#define RECEIVE_BATCH_MAX 8

#define STATUS_MAGIC 0xbb49cc4d
#define STATUS_SIZE_MAX 512

uint8_t network_thread_stack[NETWORK_THREAD_SIZE];
OSThread_t network_thread;
uint32_t my_fake_pid;
uint32_t local_ip;
bool network_thread_running;

static uint64_t timestamp;
static so_fd_t broadcast_socket = -1;
static so_fd_t multicast_socket = -1;
static so_fd_t data_socket = -1;
#define LAN_DATA_PORT 27910
#define LAN_DATA_MAGIC 0x4D4B5744u
#define LAN_DATA_MAX_PAYLOAD 1472
static uint64_t broadcast_timer;
static int8_t game_state; // 1 is idling
static uint16_t communication_port;
static const uint8_t *my_mii;
static uint16_t my_longitude;
static uint16_t my_latitude;
static uint8_t my_country;
static uint8_t my_state;
static uint64_t time_in_state_1;
static bool sdk_service_ready;
static bool sdk_polling;
static void clear_deferred_syn(void);
static void clear_deferred_node(void);
static void replay_deferred_syn(struct dwc_gamedata *game);

static so_ret_t set_nonblock(so_fd_t fd) {
	int fcntl = SOFcntl(fd, F_GETFL, 0);
	so_ret_t r = fcntl < 0 ? fcntl : SOFcntl(fd, F_SETFL, fcntl | O_NONBLOCK);
	return r;
}

static struct tracked_peer {
	uint64_t last_update;
	uint32_t real_pid;
	struct peer_info info;
	uint16_t longitude;
	uint16_t latitude;
	uint8_t country;
	uint8_t state;
	uint8_t mii[MII_SIZE];
} tracked_peers[BROADCAST_TRACK_MAX];

static void close_discovery(void) {
    sdk_service_ready = false;
    if (data_socket >= 0) SOClose(data_socket);
    if (multicast_socket >= 0) SOClose(multicast_socket);
    if (broadcast_socket >= 0) SOClose(broadcast_socket);
    data_socket = multicast_socket = broadcast_socket = -1;
}

static bool initialise_multicast(void) {
    /* A separate shared multicast receiver keeps unicast SDK replies routed
       to their own per-instance socket, rather than a wildcard reuse socket. */
    multicast_socket = SOSocket(PF_INET, SOCK_DGRAM, PROTO_UDP);
    if (multicast_socket < 0) return false;
    const uint32_t enabled = 1;
    const uint32_t interface = (local_ip >> 24) == 127 ? 0x7f000001 : local_ip;
    const uint32_t membership[2] = { DISCOVERY_GROUP, interface };
    const so_addr_t address = {
        .sa_len = sizeof(so_addr_t), .sa_family = AF_INET,
        .sa_port = DISCOVERY_PORT, .sa_addr = 0
    };
    /* Module imports use SOBind's original-function trampoline. */
    if (SOSetSockOpt(multicast_socket, SOL_SOCKET, SO_REUSEADDR, &enabled, sizeof(enabled)) < 0 ||
        SOBind(multicast_socket, &address) < 0 ||
        SOSetSockOpt(multicast_socket, SOL_IP, IP_ADD_MEMBERSHIP, membership, sizeof(membership)) < 0 ||
        set_nonblock(multicast_socket) < 0 ||
        SOSetSockOpt(broadcast_socket, SOL_IP, IP_MULTICAST_IF, &interface, sizeof(interface)) < 0 ||
        SOSetSockOpt(broadcast_socket, SOL_IP, IP_MULTICAST_TTL, &enabled, sizeof(enabled)) < 0 ||
        SOSetSockOpt(broadcast_socket, SOL_IP, IP_MULTICAST_LOOP, &enabled, sizeof(enabled)) < 0) {
        LOG_ERROR("could not initialise multicast discovery");
        return false;
    }
    return true;
}

static bool initialise(void) {
	sdk_service_ready = false;
	if (game_state != 0)
		LOG_INFO("game_state %d -> 0", (int)game_state);
	game_state = 0;
	clear_deferred_syn();
    clear_deferred_node();
	/* Discovery deadlines use the SDK clock, not the rate this thread runs. */
	timestamp = lan_discovery_time(OSGetTime());
	broadcast_timer = timestamp;
	local_ip = MKW_LOCAL_IP;
	if (local_ip == 0) {
		LOG_ERROR("SOGetHostID returned 0");
		return false;
	}

	broadcast_socket = SOSocket(PF_INET, SOCK_DGRAM, PROTO_UDP);
	if (broadcast_socket < 0) {
		LOG_ERROR("SOSocket returned %d", broadcast_socket);
		return false;
	}

	const so_addr_t baddr = {
		.sa_len = sizeof(so_addr_t),
		.sa_family = AF_INET,
		.sa_port = BROADCAST_PORT,
		.sa_addr = MKW_LOCAL_IP
	};

	so_ret_t r = SOBind(broadcast_socket, &baddr);
	if (r < 0) {
		LOG_ERROR("SOBind returned %d", r);
		return false;
	}

	r = set_nonblock(broadcast_socket);
	if (r < 0) {
		LOG_ERROR("set_nonblock returned %d", r);
		return false;
	}

    if (!initialise_multicast()) return false;

#if MKW_LAN_CAPACITY > 12
    /* Direct UDP data socket for client-to-client unreliable packets under
     * Star topology so the host GT2 link is not saturated by O(N^2) relays. */
    data_socket = SOSocket(PF_INET, SOCK_DGRAM, PROTO_UDP);
    if (data_socket >= 0) {
        const so_addr_t daddr = {
            .sa_len = sizeof(so_addr_t),
            .sa_family = AF_INET,
            .sa_port = LAN_DATA_PORT,
            .sa_addr = MKW_LOCAL_IP
        };
        if (SOBind(data_socket, &daddr) < 0 || set_nonblock(data_socket) < 0) {
            SOClose(data_socket);
            data_socket = -1;
        }
    }
#endif

	r = dwc_init_gt2_socket();
	if (r != 0) {
		LOG_ERROR("dwc_init_gt2_socket returned %d", r);
		return false;
	}

	struct dwc_gamedata *gamedata = get_dwc_gamedata();
	communication_port = gamedata->gamedata2->socket->local_port;
    LOG_INFO("advertised game port %u", (unsigned)communication_port);
	gamedata->gamedata2->online_status = 3;

	return true;
}

struct broadcast_packet {
	uint32_t magic;
	uint16_t protocol;
	uint16_t capacity;
	uint32_t pid;
	uint8_t mii[MII_SIZE];
	uint16_t communication_port;
	uint16_t longitude;
	uint16_t latitude;
	uint8_t country;
	uint8_t state;
	int8_t status;
} __attribute__((packed));

static void check_for_stuck_friend_join(struct dwc_gamedata *gamedata) {
	int isr;
	_CPU_ISR_Disable(isr);
	/* This code is looking to DC friend rooms if they get stuck on joining. */
	void *menu_data = *(void **)get_port_address(9);
	menu_data = menu_data ? *(void **)menu_data : 0;
	void *panel9b = menu_data ? ((void **)menu_data)[0x9b + 2] : 0;
	uint32_t *state = panel9b ? (uint32_t *)panel9b + (0x35c / 4) : 0;
	uint32_t start_state = state ? *state : 0;
	if (
		/* if the network has put us back on the menu OR */
		gamedata->gamedata2->online_status == 3 ||
		/* we're still joining AND */
		((game_state == 4 || game_state == 13) && (state && start_state == 1) && (
			/* we've somehow ended up as host OR */
			gamedata->dwc_match_state == DWC_MATCH_STATE_SV_WAITING ||
			/* we've taken too long. */
			ticks_to_secs(OSGetTime() - time_in_state_1) >= 65
		))
	) {
		LOG_INFO("game_state %d -> 0", (int)game_state);
		game_state = 0;
		/* prevent any state transition if a dialogue (panel4d) is open - it's probably a dc message :) */
		void *panel4d = menu_data ? ((void **)menu_data)[0x4d + 2] : 0;
		if (state && panel4d && ((int *)panel4d)[2] == 1) {
			switch (start_state) {
			case 1: *state = 2; break;
			case 3: *state = 2; break;
			default: break;
			}
			LOG_INFO("panel9b.state %u -> %u", (unsigned)start_state, (unsigned)*state);
		}
	}
	_CPU_ISR_Restore(isr);
}

static void update_game_state(struct dwc_gamedata *gamedata) {
	if (game_state != 0) {
		check_for_stuck_friend_join(gamedata);
		// Check if suspend has changed.
		int8_t old_state = game_state;
		switch (game_state) {
		case  3: if ( gamedata->dwc_suspend) game_state = 11; break;
		case  4: if ( gamedata->dwc_suspend) game_state = 13; break;
		case 11: if (!gamedata->dwc_suspend) game_state =  3; break;
		case 13: if (!gamedata->dwc_suspend) game_state =  4; break;
		}
		if (old_state != game_state)
			LOG_INFO("game_state %d -> %d", (int)old_state, (int)game_state);
	}
}

/* USA globals verified against the pinned game's CreateInstance routines.
 * Telemetry is independent of the versioned lobby discovery record. */
extern const uint8_t *MKWRaceInfo;
extern const uint8_t *MKWRaceData;
extern const uint8_t *MKWNetController;

static bool broadcast_due(void) {
    timestamp = lan_discovery_time(OSGetTime());
    if (timestamp < broadcast_timer) return false;
    broadcast_timer = timestamp + BROADCAST_INTERVAL;
    return true;
}

static bool compute_and_broadcast_status(so_fd_t fd) {
	struct dwc_gamedata *gamedata = get_dwc_gamedata();
	if (gamedata == NULL)
		// DCed from wifi
		return false;
	update_game_state(gamedata);
    const uint8_t *controller=MKWNetController;
    const uint32_t current=controller ? *(const uint32_t *)(controller+MKW_LAN_NET_CURRENT_SUB) : 2;
    const uint8_t *sub=current<2 ? controller+MKW_LAN_NET_SUBS+current*MKW_LAN_NET_SUB_STRIDE : 0;
#ifdef MKW_LAN_DIAGNOSTICS
    static uint32_t previous_count, previous_votes, previous_suspend, previous_first, previous_active;
    const uint32_t active=sub ? *(const uint32_t *)(sub+16) : 0;
    if (previous_count != gamedata->player_count || previous_votes != gamedata->suspend_votes ||
        previous_suspend != gamedata->dwc_suspend || previous_first != gamedata->peers[0].player_id || previous_active!=active) {
        MKWDebugReport("LAN SDK role=%u count=%u self=%x first=%x first_aid=%u votes=%x suspend=%u match=%u\n",
            (unsigned)gamedata->dwc_host_state, (unsigned)gamedata->player_count,
            (unsigned)gamedata->my_pid, (unsigned)gamedata->peers[0].player_id,
            (unsigned)gamedata->peers[0].peer_index, (unsigned)gamedata->suspend_votes,
            (unsigned)gamedata->dwc_suspend, (unsigned)gamedata->dwc_match_state);
        MKWDebugReport("LAN controller sub=%u active=%x local_aid=%u\n",(unsigned)current,
            (unsigned)active,sub ? (unsigned)sub[33] : 255);
        if(previous_count!=gamedata->player_count && gamedata->player_count<=MKW_LAN_MATCH_NODES)
            for(unsigned i=0;i<gamedata->player_count;i++)
                MKWDebugReport("LAN SDK node %u pid=%x aid=%u\n",i,
                    (unsigned)gamedata->peers[i].player_id,(unsigned)gamedata->peers[i].peer_index);
        previous_count=gamedata->player_count;previous_votes=gamedata->suspend_votes;
        previous_suspend=gamedata->dwc_suspend;previous_first=gamedata->peers[0].player_id;
        previous_active=active;
    }
#endif

	struct broadcast_packet packet = {
		.magic = BROADCAST_MAGIC,
        .protocol = DISCOVERY_PROTOCOL, .capacity = MKW_LAN_CAPACITY,
		.pid = my_fake_pid,
		.communication_port = communication_port,
		.status = game_state,
		.country = 0xff,
		.state = 0xff
	};
	if (my_mii) {
		for (int i = 0; i < MII_SIZE; i++)
			packet.mii[i] = my_mii[i];
		packet.longitude = my_longitude;
		packet.latitude = my_latitude;
		packet.country = my_country;
		packet.state = my_state;
	}
	const so_addr_t baddr = {
		.sa_len = sizeof(so_addr_t),
		.sa_family = AF_INET,
		.sa_port = BROADCAST_PORT,
		.sa_addr = 0xffffffff
	};
	so_addr_t discovery = baddr;
    discovery.sa_addr = DISCOVERY_GROUP;
    discovery.sa_port = DISCOVERY_PORT;
    SOSendTo(fd, &packet, sizeof(packet), 0, &discovery);
    /* Explicit peers remain a fallback across multicast-filtering links. */
    for (unsigned i = 0; i < mkw_lan_config.peer_count; i++) {
        so_addr_t destination = baddr;
        destination.sa_addr = mkw_lan_config.peers[i];
        if (destination.sa_addr != local_ip)
            SOSendTo(fd, &packet, sizeof(packet), 0, &destination);
    }
    /* Optional launcher status channel; gameplay does not depend on a listener. */
    uint32_t status[7] = { 0x4d4b574c, MKW_INSTANCE_ID, (uint32_t)game_state,
                          gamedata->player_count, 0, 0xffffffff, 0 };
    uint32_t *manager = *(uint32_t **)get_port_address(9);
    uint32_t *section = manager ? (uint32_t *)manager[0] : 0;
    if (section) {
        status[4] = section[0];
        uint32_t count = section[0x37c / 4];
        if (count > 0 && count <= 11) {
            uint32_t *page = (uint32_t *)section[0x354 / 4 + count - 1];
            if (page) { status[5] = page[1]; status[6] = page[2]; }
        }
    }
    so_addr_t monitor = baddr;
    monitor.sa_addr = 0x7f0000fe;
    SOSendTo(fd, status, sizeof(status), 0, &monitor);
    /* Visible room membership can precede the SDK's remaining mesh joins.
     * Export readiness separately so test clients do not suspend those joins. */
#if MKW_LAN_CAPACITY > 32
    uint32_t conn_words[4] = { sub ? *(const uint32_t *)(sub+20) : 0, 0, 0, 0 };
    if (sub && sub[33] < 128)
        conn_words[sub[33] / 32] |= 1u << (sub[33] % 32);
    const uint32_t *conn_ptrs = (const uint32_t *)0x90000800u;
    const uint8_t *conn_meta = (const uint8_t *)(0x90000800u + MKW_LAN_MATCH_NODES * 4u);
    for (unsigned i = 0; i < MKW_LAN_MATCH_NODES; i++) {
        if (!conn_ptrs[i]) continue;
        const uint8_t aid = conn_meta[i * 8u + 1u];
        if (aid < 128)
            conn_words[aid / 32] |= 1u << (aid % 32);
    }
    /* Under Star / Host-Relay topology, clients maintain a direct GT2 link to
     * the host (peers[0]) and reach all room peers through DWC's native relay.
     * The host still reports only its true direct GT2 client connections. */
    if (gamedata->dwc_host_state == 1 && gamedata->player_count >= 2 &&
        gamedata->player_count <= MKW_LAN_MATCH_NODES) {
        const uint8_t host_aid = gamedata->peers[0].peer_index;
        if (host_aid < 128 && (conn_words[host_aid / 32] & (1u << (host_aid % 32)))) {
            for (unsigned i = 0; i < gamedata->player_count; i++) {
                const uint8_t aid = gamedata->peers[i].peer_index;
                if (aid < 128)
                    conn_words[aid / 32] |= 1u << (aid % 32);
            }
        }
    }
    const uint32_t network_status[10] = {
        0x4d4b574e, MKW_INSTANCE_ID, gamedata->dwc_host_state,
        gamedata->dwc_match_state, gamedata->player_count,
        conn_words[0], gamedata->dwc_suspend,
        conn_words[1], conn_words[2], conn_words[3]
    };
#else
    const uint32_t network_status[7] = {
        0x4d4b574e, MKW_INSTANCE_ID, gamedata->dwc_host_state,
        gamedata->dwc_match_state, gamedata->player_count,
        sub ? *(const uint32_t *)(sub+20) : 0, gamedata->dwc_suspend
    };
#endif
    SOSendTo(fd, network_status, sizeof(network_status), 0, &monitor);
    if (status[4] >= 0x70 && status[4] <= 0x77 && MKWRaceInfo && MKWRaceData) {
        const uint32_t race_status[7] = {
            0x4d4b5752, MKW_INSTANCE_ID,
            *(const uint32_t *)(MKWRaceInfo + 0x28), /* RaceStage */
            *(const uint32_t *)(MKWRaceInfo + 0x20), /* simulation frame */
            *(const uint32_t *)(MKWRaceData + MKW_LAN_RACE_COURSE_OFFSET), /* race course */
            MKWRaceData[MKW_LAN_RACE_PLAYER_COUNT_OFFSET], /* in-race scenario player count */
            *(const uint32_t *)(MKWRaceData + MKW_LAN_RACE_MODE_OFFSET) /* game mode */
        };
        SOSendTo(fd, race_status, sizeof(race_status), 0, &monitor);
    }
	return true;
}

static int find_or_alloc_peer(const so_addr_t *ip) {
	if (ip->sa_addr == local_ip) return -1;
	for (int i = 0; i < BROADCAST_TRACK_MAX; i++) {
		if (tracked_peers[i].info.ip == ip->sa_addr) {
			return i;
		} else if (tracked_peers[i].info.ip == 0) {
			tracked_peers[i].info.ip = ip->sa_addr;
			return i;
		}
	}
	return -1;
}
static int find_peer_by_fake_pid(uint32_t fake_pid) {
	if (fake_pid == 0) return -1;
	if (fake_pid < BROADCAST_TRACK_MAX + 1) return fake_pid - 1;
	return -1;
}
static int find_peer_by_pid(uint32_t pid) {
	if (pid == 0) return -1;
	if (pid < BROADCAST_TRACK_MAX + 1) return pid - 1;
	for (int i = 0; i < BROADCAST_TRACK_MAX; i++) {
		if (tracked_peers[i].real_pid == pid) {
			return i;
		}
	}
	return -1;
}

static void process_broadcast(const struct broadcast_packet buffer, const so_addr_t raddr) {
	if (buffer.magic != BROADCAST_MAGIC || buffer.protocol != DISCOVERY_PROTOCOL || buffer.capacity != MKW_LAN_CAPACITY) return;
	int peer = find_or_alloc_peer(&raddr);
	if (peer == -1) return;
	tracked_peers[peer].last_update = timestamp;
	tracked_peers[peer].real_pid = buffer.pid;
	tracked_peers[peer].info.communication_port = buffer.communication_port;
	tracked_peers[peer].info.status = buffer.status + 1;
	tracked_peers[peer].longitude = buffer.longitude;
	tracked_peers[peer].latitude = buffer.latitude;
	tracked_peers[peer].country = buffer.country;
	tracked_peers[peer].state = buffer.state;
	for (int i = 0; i < MII_SIZE; i++)
		tracked_peers[peer].mii[i] = buffer.mii[i];

	if (peer >= MKW_FRIEND_LIST_SLOTS) return;

	int isr;
	_CPU_ISR_Disable(isr);
	void *menu_data = *(void **)get_port_address(9);
	menu_data = menu_data ? *(void **)menu_data : 0;
	struct menu_friend_list_friend_full *ff = menu_data ? ((void **)menu_data)[0x404 / 4] : 0;
	struct menu_friend_list_friend *f = ff ? &ff[peer].f : 0;
	if (f && !f->mii_known) {
		f->longitude = tracked_peers[peer].longitude;
		f->latitude = tracked_peers[peer].latitude;
		f->country = tracked_peers[peer].country;
		f->state = tracked_peers[peer].state;
		f->mii_known = 1;
		for (int i = 0; i < MII_SIZE; i++)
			f->mii[i] = tracked_peers[peer].mii[i];
		f->flags |= 1;
	}
	_CPU_ISR_Restore(isr);
}

/* Status handshakes can arrive before multicast discovery. Learn the actual
 * control-channel sender without inventing lobby/Mii presence. */
static void learn_status_peer(const uint32_t *buffer, const so_addr_t raddr) {
    const uint32_t pid = __builtin_bswap32(buffer[4]);
    if (find_peer_by_pid(pid) >= 0 || buffer[1] != __builtin_bswap32(90) ||
        !(buffer[2] & 0xffff) || !raddr.sa_addr || (raddr.sa_addr >> 28) >= 14 ||
        pid != (0x40000000u | (raddr.sa_addr & 0x3fffffffu))) return;
    const int peer = find_or_alloc_peer(&raddr);
    if (peer < 0) return;
    tracked_peers[peer].real_pid = pid;
    tracked_peers[peer].info.communication_port = buffer[2] & 0xffff;
    tracked_peers[peer].last_update = timestamp;
}

/* The SDK accepts one server reservation at a time. Keep one request per
 * discovered peer and preserve its first arrival across retries. Queue idle
 * arrivals too, so a new datagram cannot jump ahead of waiting peers. */
static struct pending_reservation {
    uint64_t enqueued_at;
    uint64_t received_at;
    uint32_t length;
    so_addr_t address;
    uint32_t packet[STATUS_SIZE_MAX / 4];
} pending_reservations[BROADCAST_TRACK_MAX];
static unsigned reservation_cursor;

/* Every contender can wait behind a serialized reservation, including while
 * an expanded room is still growing through its first twelve members. Keep
 * stock builds unchanged; never reset SDK timer origins or retry counts. */
static uint64_t mesh_resv_wait_since;

static void ensure_transport_send_split_max(struct dwc_gamedata *gamedata) {
#if MKW_LAN_CAPACITY > 32
    if (!gamedata) return;
    /* DWCi_SendMatchSynPacket (type 3) sends (player_count - 1) * 24 + 8 bytes
     * via DWCi_SendReliableSystemData and immediately frees the buffer, while
     * DWCi_RecvCallback dispatches system payloads on the first chunk without
     * reassembly. Stock sendSplitMax (1465) truncates SYN at 63+ peers. */
    struct dwc_transport *transport = (struct dwc_transport *)(gamedata + 1);
    const uint16_t required = (uint16_t)(MKW_LAN_MATCH_NODES * 24 + 64);
    if (transport->send_split_max && transport->send_split_max < required) {
        transport->send_split_max = required;
    }
    /* Loading 65-128 karts concurrently can exceed the stock 20s DWC receive
     * timeout on slower peers; allow 60s when capacity > 32. */
    if (transport->receive_timeout && transport->receive_timeout < 60000) {
        transport->receive_timeout = 60000;
    }
#else
    (void)gamedata;
#endif
}

static void update_reservation_budget(struct dwc_gamedata *gamedata) {
    ensure_transport_send_split_max(gamedata);
    if (!gamedata || gamedata->dwc_match_state != DWC_MATCH_STATE_CL_WAIT_RESV) {
        mesh_resv_wait_since = 0;
        return;
    }
    /* During P2P mesh building (already in room, player_count >= 2), the LAN
     * mod's dwc_think+0x110 patch suppresses the SDK's 6s CL_WAIT_RESV timeout
     * handler, which would otherwise leave a client permanently wedged if a
     * single peer reservation UDP datagram was dropped or ignored. Return to
     * CL_WAITING after 4s so the mesh retries cleanly without an error dialog. */
    if (MKW_LAN_CAPACITY > 12 && gamedata->player_count >= 2) {
        if (!mesh_resv_wait_since) {
            mesh_resv_wait_since = timestamp;
        } else if (timestamp - mesh_resv_wait_since >= BROADCAST_INTERVAL * 4) {
            mesh_resv_wait_since = 0;
            gamedata->resv_target_pid = 0;
            gamedata->resv_outgoing_pid = 0;
            gamedata->reservation_timeout_ms = 0;
            gamedata->dwc_match_state = DWC_MATCH_STATE_CL_WAITING;
            LOG_INFO("recovered stalled P2P CL_WAIT_RESV back to CL_WAITING");
        }
        return;
    }
    mesh_resv_wait_since = 0;
    if (gamedata->reservation_timeout_ms < 6000) return;
    unsigned participants = 1;
    for (unsigned i = 0; i < BROADCAST_TRACK_MAX; i++)
        if (tracked_peers[i].info.status &&
            timestamp - tracked_peers[i].last_update <= BROADCAST_TIMEOUT)
            participants++;
    if (participants < gamedata->player_count) participants = gamedata->player_count;
    if (participants > MKW_LAN_CAPACITY) participants = MKW_LAN_CAPACITY;
    unsigned budget = 6000;
    if (MKW_LAN_CAPACITY > 12) budget += (participants - 1) * 2000;
    /* Stay below the existing 65-second friend-room join watchdog. */
    if (budget > 60000) budget = 60000;
    if (gamedata->reservation_timeout_ms < budget)
        gamedata->reservation_timeout_ms = budget;
}

/* UDP new-node notification can overtake the preceding reliable SYN-2.
 * Applying it in CL_SYN lets that older completion resume mesh creation after
 * the next admission has stopped it. Keep its owned control until SYN-2 has
 * actually completed, before the SDK can start another outgoing reservation.
 * Only one next admission can exist: its host barrier needs this client's ACK
 * before announcing a further node. */
static struct deferred_node_control {
    unsigned length;
    struct dwc_gamedata *game;
    uint32_t host_pid;
    uint64_t received_at;
    so_addr_t address;
    uint32_t packet[7];
} deferred_node;

static void clear_deferred_node(void) {
    deferred_node.length = 0;
}

static bool retain_next_node(struct dwc_gamedata *game, const uint32_t *packet,
                             unsigned payload, so_addr_t address) {
    if (MKW_LAN_CAPACITY <= 12 || !network_thread_running || broadcast_socket < 0 ||
        !game || game->dwc_host_state != 1 || game->player_count < 2 ||
        game->player_count > MKW_LAN_MATCH_NODES ||
        game->dwc_match_state != DWC_MATCH_STATE_CL_SYN || (packet[2] >> 24) != 7 ||
        payload != 8 || !game->peers[0].player_id ||
        __builtin_bswap32(packet[4]) != game->peers[0].player_id) return false;
    deferred_node.game = game;
    deferred_node.host_pid = game->peers[0].player_id;
    deferred_node.received_at = lan_discovery_time(OSGetTime());
    deferred_node.address = address;
    for (unsigned i = 0; i < 7; i++) deferred_node.packet[i] = packet[i];
    deferred_node.length = 28;
    LOG_INFO("retaining next node until preceding SYN completion");
    return true;
}

static void dispatch_status(struct dwc_gamedata *, uint32_t *, so_addr_t);

static void replay_deferred_node(struct dwc_gamedata *game) {
    if (!deferred_node.length) return;
    if (game != get_dwc_gamedata() || game != deferred_node.game ||
        !network_thread_running || broadcast_socket < 0 || !game ||
        game->dwc_host_state != 1 || game->peers[0].player_id != deferred_node.host_pid ||
        lan_discovery_time(OSGetTime()) - deferred_node.received_at > RESERVATION_TIMEOUT ||
        game->dwc_match_state == DWC_MATCH_STATE_WAIT_CLOSE) {
        clear_deferred_node();
        return;
    }
    if (game->dwc_match_state != DWC_MATCH_STATE_CL_WAITING) return;
    uint32_t packet[7];
    for (unsigned i = 0; i < 7; i++) packet[i] = deferred_node.packet[i];
    const so_addr_t address = deferred_node.address;
    clear_deferred_node();
    LOG_INFO("replaying next node after preceding SYN completion");
    dispatch_status(game, packet, address);
}

void network_thread_after_syn(uint32_t aid, uint32_t type) {
    struct dwc_gamedata *game = get_dwc_gamedata();
    if (type != 3 || (game && aid != game->peers[0].peer_index)) return;
    replay_deferred_node(game);
}

static void dispatch_status(struct dwc_gamedata *gamedata, uint32_t *buffer,
                            const so_addr_t raddr) {
    gamedata->public_ip = MKW_ADVERTISED_IP;
    dwc_process_status_record(buffer[2] >> 24, __builtin_bswap32(buffer[4]),
        raddr.sa_addr, raddr.sa_port, buffer + 5, (buffer[2] >> 18) & 0x3f);
    replay_deferred_syn(gamedata);
}

static void process_status(uint32_t *buffer, int32_t length, const so_addr_t raddr) {
    LOG_INFO("receive size %u from %x:%u", (unsigned)length, (unsigned)raddr.sa_addr, (unsigned)raddr.sa_port);
    if (length < 0x14 || length > STATUS_SIZE_MAX) return;
    const unsigned payload = (buffer[2] >> 16) & 0xff;
    if (payload % 4 || (unsigned)length < 0x14 + payload) return;
    if (buffer[0] != STATUS_MAGIC) return;
    LOG_INFO("receive status message %u from %x:%u", (unsigned)(buffer[2] >> 24), (unsigned)raddr.sa_addr, (unsigned)raddr.sa_port);
    struct dwc_gamedata *gamedata = get_dwc_gamedata();
    if (!gamedata) return;
    learn_status_peer(buffer, raddr);
    if (retain_next_node(gamedata, buffer, payload, raddr)) return;
    const enum dwc_match_state state = gamedata->dwc_match_state;
    if ((buffer[2] >> 24) == 1 &&
        (state == DWC_MATCH_STATE_CL_WAITING || state == DWC_MATCH_STATE_SV_WAITING ||
         (state >= DWC_MATCH_STATE_SV_OWN_NN && state <= DWC_MATCH_STATE_SV_SYN_WAIT))) {
        const int peer = find_peer_by_pid(__builtin_bswap32(buffer[4]));
        if (peer >= 0) {
            struct pending_reservation *pending = &pending_reservations[peer];
            if (!pending->length || timestamp - pending->received_at > RESERVATION_TIMEOUT)
                pending->enqueued_at = timestamp;
            pending->received_at = timestamp;
            pending->address = raddr;
            pending->length = 0x14 + payload;
            for (unsigned i = 0; i < pending->length / 4; i++) pending->packet[i] = buffer[i];
            LOG_INFO("queued reservation from %x", (unsigned)raddr.sa_addr);
            return;
        }
    }
    dispatch_status(gamedata, buffer, raddr);
}

bool network_thread_get_fake_pid_status(uint32_t pid, uint8_t *status) {
	struct ls {
		uint32_t gid;
		uint8_t region;
		uint8_t state;
		uint8_t player_count;
		uint8_t race_number;
	};
	int peer = find_peer_by_fake_pid(pid);
	if (peer == -1) return false;
	if (tracked_peers[peer].info.status == 0) return false;
	*(struct ls *)status = (struct ls){
		.gid = pid,
		.region = 0xff,
		.state = (tracked_peers[peer].info.status - 1) & 0x3f,
		.player_count = MKW_LAN_CAPACITY,
		.race_number = tracked_peers[peer].info.status >> 6
	};
	return true;
}

static void receive_packets(so_fd_t socket, bool allow_status) {
	so_ret_t r;
	unsigned received = 0;
	do {
		uint32_t buffer[STATUS_SIZE_MAX / 4];
		so_addr_t raddr = { .sa_len = sizeof(so_addr_t) };
		r = SORecvFrom(socket, &buffer, sizeof(buffer), 0, &raddr);
		if (r <= 0) break;
		++received;
		if (r < 0x4) {
			LOG_ERROR("received overly short message. %u length", (unsigned)r);
			continue;
		}
		if (allow_status && buffer[0] == STATUS_MAGIC) {
			process_status(buffer, r, raddr);
		} else if (buffer[0] == BROADCAST_MAGIC) {
			if (r != sizeof(struct broadcast_packet)) continue;
			process_broadcast(*(const struct broadcast_packet *)buffer, raddr);
		} else {
			LOG_ERROR("received unknown message. %u length %x header", (unsigned)r, (unsigned)buffer[0]);
		}
	} while (received < RECEIVE_BATCH_MAX);
 }

static void check_for_status_or_broadcast(void) {
    receive_packets(broadcast_socket, true);
    receive_packets(multicast_socket, false);
    struct dwc_gamedata *gamedata = get_dwc_gamedata();
    replay_deferred_syn(gamedata);
    update_reservation_budget(gamedata);
    /* Clients also accept peer links while the SDK builds the mesh. They
     * return to CL_WAITING after accepting a link, rather than SV_WAITING. */
    if (gamedata && (gamedata->dwc_match_state == DWC_MATCH_STATE_SV_WAITING ||
                     gamedata->dwc_match_state == DWC_MATCH_STATE_CL_WAITING)) {
        struct pending_reservation *oldest = NULL;
        uint64_t oldest_age = 0;
        unsigned selected = 0;
        /* Round-robin ties avoid slot bias when requests share one VI tick. */
        for (unsigned step = 0; step < BROADCAST_TRACK_MAX; step++) {
            const unsigned i = (reservation_cursor + step) % BROADCAST_TRACK_MAX;
            struct pending_reservation *pending = &pending_reservations[i];
            if (!pending->length) continue;
            if (timestamp - pending->received_at > RESERVATION_TIMEOUT) {
                pending->length = 0;
                continue;
            }
            const uint64_t age = timestamp - pending->enqueued_at;
            if (!oldest || age > oldest_age) {
                oldest = pending;
                oldest_age = age;
                selected = i;
            }
        }
        if (oldest) {
            oldest->length = 0;
            reservation_cursor = (selected + 1) % BROADCAST_TRACK_MAX;
            LOG_INFO("resuming oldest reservation from %x", (unsigned)oldest->address.sa_addr);
            dispatch_status(gamedata, oldest->packet, oldest->address);
        }
    }
	for (int i = 0; i < BROADCAST_TRACK_MAX; i++) {
		if (tracked_peers[i].last_update + BROADCAST_TIMEOUT < timestamp) {
			tracked_peers[i].info.status = 0;
		}
	}
}

/* The SDK can yield to another guest thread while finishing a barrier's
 * socket writes. Dispatching the next reservation from that other thread
 * reverses stop/resume control order. Expanded matching mutations run only
 * at the owning SDK thread's complete-update boundaries. */
void network_thread_poll_sdk(void) {
    if (MKW_LAN_CAPACITY <= 12 || !network_thread_running || !sdk_service_ready || sdk_polling) return;
    sdk_polling = true;
    timestamp = lan_discovery_time(OSGetTime());
    check_for_status_or_broadcast();
    sdk_polling = false;
}

static void service_network_thread(void) {
    if (MKW_LAN_CAPACITY <= 12) {
        check_for_status_or_broadcast();
        return;
    }
    /* Discovery updates no SDK matching state. The control socket, pending
     * reservations, SYN replay and SDK timers belong to poll_sdk above. */
    receive_packets(multicast_socket, false);
    for (unsigned i = 0; i < BROADCAST_TRACK_MAX; i++)
        if (timestamp - tracked_peers[i].last_update > BROADCAST_TIMEOUT)
            tracked_peers[i].info.status = 0;
}

void* network_thread_main(void *arg) {
	network_thread_running = true;
	LOG_INFO("network_thread startup");
	if (!initialise()) {
        close_discovery();
        network_thread_running = false;
		mkw_net_state->connection_state = 5; // idk lol
		return NULL;
	}
	LOG_INFO("network_thread initialised");
    sdk_service_ready = true;
    /* Automation joins the first advertising host, independent of instance ID.
       Manual login does not wait for a host; users can create their own room. */
    if (MKW_AUTOMATION == 2) {
        for (unsigned frame = 0; frame < 600; frame++) {
            bool found = false;
            if (broadcast_due()) compute_and_broadcast_status(broadcast_socket);
            service_network_thread();
            for (unsigned peer = 0; peer < BROADCAST_TRACK_MAX; peer++) {
                if (tracked_peers[peer].info.status == 4) {
                    if (peer) {
                        struct tracked_peer temporary = tracked_peers[0];
                        tracked_peers[0] = tracked_peers[peer];
                        tracked_peers[peer] = temporary;
                    }
                    found = true;
                    break;
                }
            }
            if (found) break;
            VIWaitForRetrace();
        }
    }
	mkw_net_state->connection_state = 5;
	while (1) {
		if (broadcast_due()) {
			if (!compute_and_broadcast_status(broadcast_socket))
				break;
		}
		service_network_thread();
		VIWaitForRetrace();
	}
	LOG_INFO("network_thread exit");
    clear_deferred_syn();
    clear_deferred_node();
    close_discovery();
    reservation_cursor = 0;
	my_mii = 0;
	for (int i = 0; i < BROADCAST_TRACK_MAX; i++)
		{
            tracked_peers[i] = (struct tracked_peer){ };
            pending_reservations[i].length = 0;
        }
	network_thread_running = false;
	return NULL;
}

static void fill_in_peer_0(struct dwc_gamedata *gamedata) {
	gamedata->public_port = communication_port;
	gamedata->public_ip = MKW_ADVERTISED_IP;
	gamedata->peers[0].player_id = gamedata->my_pid;
	gamedata->peers[0].public_ip = MKW_ADVERTISED_IP;
	gamedata->peers[0].local_ip = local_ip;
	gamedata->peers[0].public_port = communication_port;
	gamedata->peers[0].private_port = communication_port;
	gamedata->peers[0].communication_ip = 0;
	gamedata->peers[0].communication_port = 0;
	gamedata->peers[0]._unk17[0] = 1;
	gamedata->peers[0].players_at_console_little_endian = gamedata->players_at_console_little_endian;
}

void network_thread_after_server_setup(struct dwc_gamedata *gamedata) {
    clear_deferred_syn();
    clear_deferred_node();
    ensure_transport_send_split_max(gamedata);
	LOG_INFO("network_thread_after_server_setup()");
	fill_in_peer_0(gamedata);
	gamedata->peers[0].peer_index = 0;
	gamedata->player_count = 1;
	gamedata->dwc_match_state = DWC_MATCH_STATE_SV_WAITING;
	LOG_INFO("game_state %d -> 3", (int)game_state);
	game_state = 3;
}

void network_thread_on_server_join(struct dwc_gamedata *gamedata) {
	clear_deferred_syn();
    clear_deferred_node();
    ensure_transport_send_split_max(gamedata);
	LOG_INFO("network_thread_on_server_join()");
	fill_in_peer_0(gamedata);
	gamedata->peers[0].peer_index = 0xff;
	gamedata->player_count = 1;
	gamedata->sb_self_lookup_complete = 1;
	LOG_INFO("game_state %d -> 4", (int)game_state);
	game_state = 4;
	time_in_state_1 = OSGetTime();
}

void network_thread_after_server_join(struct dwc_gamedata *gamedata) {
	LOG_INFO("network_thread_after_server_join()");
	int peer = find_peer_by_pid(gamedata->host_pid);
	if (peer != -1) {
		gamedata->host_pid = tracked_peers[peer].real_pid;
		LOG_INFO("host_pid set to %d", (int)tracked_peers[peer].real_pid);
	} else {
		LOG_ERROR("could not set host_pid");
	}
}

bool network_thread_send_status_record(int type, uint32_t pid, uint32_t ip, uint16_t port, const uint32_t *packet, uint32_t packet_len_in_words) {
	if (type == 2 || type == 6) {
		struct dwc_gamedata *gamedata = get_dwc_gamedata();
		gamedata->last_enter_type = type;
		gamedata->last_enter_port = port;
		gamedata->last_enter_ip = ip;
		gamedata->last_enter_pid = pid;
		gamedata->last_enter_length = packet_len_in_words;
		gamedata->last_enter_timestamp = OSGetTime();
	}

	int peer = find_peer_by_pid(pid);
    uint32_t destination = peer >= 0 ? tracked_peers[peer].info.ip : 0;
    /* The public SDK address is intentionally a NAT placeholder. Prefer the
     * private endpoint in its roster when discovery has not learned this PID. */
    struct dwc_gamedata *game = get_dwc_gamedata();
    const unsigned count = game ? game->player_count : 0;
    for (unsigned i = 0; !destination && i < count && i < MKW_LAN_MATCH_NODES; i++)
        if (game->peers[i].player_id == pid && game->peers[i].local_ip &&
            game->peers[i].local_ip != MKW_ADVERTISED_IP && (game->peers[i].local_ip >> 28) < 14)
            destination = game->peers[i].local_ip;
    if (!destination && ip && ip != MKW_ADVERTISED_IP && (ip >> 28) < 14)
        destination = ip;
    if (!destination) {
        LOG_ERROR("network_thread_send_status_record has no route pid=%x sdk=%x:%u", (unsigned)pid, (unsigned)ip, (unsigned)port);
        return false;
    }
	LOG_INFO("network_thread_send_status_record %d to %x:%u", type, (unsigned)destination, (unsigned)port);
#ifdef MKW_LAN_DIAGNOSTICS
    /* Observe which SDK callers share matching state before changing the
     * control thread's scheduling relationship with those callers. */
    OSThread_t *caller = OSGetCurrentThread();
    LOG_INFO("status caller thread=%x priority=%d type=%d", (unsigned)caller,
        caller ? OSGetThreadPriority(caller) : -1, type);
#endif

	uint32_t full_packet[packet_len_in_words + 5];
	for (int i = 0; i < packet_len_in_words; i++)
		full_packet[i + 5] = packet[i];
	full_packet[0] = STATUS_MAGIC;
	full_packet[1] = __builtin_bswap32(90);
	full_packet[2] = (type << 24) | ((packet_len_in_words * 4) << 16) | communication_port;
	full_packet[3] = MKW_ADVERTISED_IP;
	full_packet[4] = __builtin_bswap32(my_fake_pid);

	const so_addr_t daddr = {
		.sa_len = sizeof(so_addr_t),
		.sa_family = AF_INET,
		.sa_port = BROADCAST_PORT,
		.sa_addr = destination
	};
	so_ret_t r = SOSendTo(broadcast_socket, &full_packet, sizeof(full_packet), 0, &daddr);
	if (r < 0) {
		LOG_ERROR("SOSendTo returned %d", r);
	}
	return r >= 0;
}

void network_thread_set_mii(const uint8_t mii[MII_SIZE], uint16_t longitude, uint16_t latitude, uint8_t country, uint8_t state) {
	my_mii = mii;
	my_longitude = longitude;
	my_latitude = latitude;
	my_country = country;
	my_state = state;
}

void network_thread_set_friend(int index, struct menu_friend_list_friend *f) {
	if ((unsigned)index >= MKW_FRIEND_LIST_SLOTS) return;
	int isr;
	_CPU_ISR_Disable(isr);
	f->friend_code = index + 1;
	f->mii_known = tracked_peers[index].info.status != 0;
	f->longitude = tracked_peers[index].longitude;
	f->latitude = tracked_peers[index].latitude;
	f->country = tracked_peers[index].country;
	f->state = tracked_peers[index].state;
	if (f->mii_known)
		for (int i = 0; i < MII_SIZE; i++)
			f->mii[i] = tracked_peers[index].mii[i];
	f->flags = 0x1388 | f->mii_known;
	_CPU_ISR_Restore(isr);
}

struct peer_info network_get_peer_info(unsigned int index) {
	if (index >= BROADCAST_TRACK_MAX) return (struct peer_info) { };
	return tracked_peers[index].info;
}

/* The two sockets can deliver the host's reliable SYN before its stop-mesh
 * control, including while this client is accepting another peer. Retain one
 * owned SYN payload until the original SDK cancellation returns to CL_WAITING.
 * The original handler still validates the SYN and constructs its ACK. */
static struct {
    bool present;
    uint32_t aid, host_pid;
    struct dwc_gamedata *game;
    uint64_t received_at;
    uint32_t packet[8];
} deferred_syn;

static void clear_deferred_syn(void) {
    deferred_syn.present = false;
}

static bool negotiating_peer(enum dwc_match_state state) {
    return (state >= DWC_MATCH_STATE_CL_WAIT_RESV && state <= DWC_MATCH_STATE_CL_CANCEL_SYN) ||
        (state >= DWC_MATCH_STATE_SV_OWN_NN && state <= DWC_MATCH_STATE_SV_CANCEL_SYN_WAIT);
}

static bool syn_client(struct dwc_gamedata *game, uint32_t aid) {
    return MKW_LAN_CAPACITY > 12 && network_thread_running && broadcast_socket >= 0 &&
        game && game->dwc_host_state == 1 && game->player_count >= 2 &&
        game->player_count <= MKW_LAN_MATCH_NODES && aid < MKW_LAN_MATCH_NODES &&
        game->peers[0].player_id &&
        aid == game->peers[0].peer_index;
}

static void replay_deferred_syn(struct dwc_gamedata *game) {
    if (!deferred_syn.present) return;
    const uint64_t now = lan_discovery_time(OSGetTime());
    if (game != get_dwc_gamedata() || game != deferred_syn.game || !syn_client(game, deferred_syn.aid) ||
        game->peers[0].player_id != deferred_syn.host_pid ||
        now - deferred_syn.received_at > (uint64_t)BROADCAST_INTERVAL * 5 * game->player_count) {
        clear_deferred_syn();
        return;
    }
    if (game->dwc_match_state != DWC_MATCH_STATE_CL_WAITING) {
        if (!negotiating_peer(game->dwc_match_state)) clear_deferred_syn();
        return;
    }
    uint32_t packet[8];
    const unsigned words = deferred_syn.packet[0] == __builtin_bswap32(1) ? 2 : 8;
    for (unsigned i = 0; i < words; i++) packet[i] = deferred_syn.packet[i];
    const uint32_t aid = deferred_syn.aid;
    clear_deferred_syn(); /* Original callbacks may re-enter the receive path. */
    LOG_INFO("replaying deferred host SYN from aid %u", (unsigned)aid);
    DWCi_ProcessMatchSynPacket(aid, 1, packet);
}

bool network_thread_prepare_syn(uint32_t aid, uint32_t type, const uint32_t *packet) {
    struct dwc_gamedata *game = get_dwc_gamedata();
    if (!syn_client(game, aid)) return false;
    if (type != 1) {
        if (type == 2 || type == 3) clear_deferred_syn();
        return false;
    }
    if (!packet) return false;
    if (negotiating_peer(game->dwc_match_state) && !sdk_polling) {
        sdk_polling = true;
        receive_packets(broadcast_socket, true);
        sdk_polling = false;
        game = get_dwc_gamedata();
        if (!syn_client(game, aid)) return false;
    }
    if (!negotiating_peer(game->dwc_match_state)) {
        clear_deferred_syn();
        return false;
    }
    /* The pinned SYN-0 parser reads two words for flag 1, eight otherwise
     * (last load at 800D9844). Never retain the transport-owned pointer. */
    const unsigned words = packet[0] == __builtin_bswap32(1) ? 2 : 8;
    for (unsigned i = 0; i < words; i++) deferred_syn.packet[i] = packet[i];
    deferred_syn.aid = aid;
    deferred_syn.host_pid = game->peers[0].player_id;
    deferred_syn.game = game;
    deferred_syn.received_at = lan_discovery_time(OSGetTime());
    deferred_syn.present = true;
    LOG_INFO("deferring host SYN in matching state %u", (unsigned)game->dwc_match_state);
    return true;
}

/* Phase 2 Star data plane: route client-to-client unreliable RKNet packets over
 * a dedicated non-blocking UDP socket directly to each peer's local_ip, while
 * keeping Host <-> Client traffic on GT2 (preserving SDK keepalive/timeout). */
struct lan_data_header {
    uint32_t magic;
    uint8_t origin_aid;
    uint8_t target_aid;
    uint16_t payload_len;
};

static void lan_copy_bytes(uint8_t *dst, const uint8_t *src, size_t n) {
    for (size_t i = 0; i < n; i++) dst[i] = src[i];
}

int network_thread_send_unreliable(uint8_t aid, const void *buf, int size) {
#if MKW_LAN_CAPACITY > 12
    if (data_socket < 0 || !buf || size <= 0 || size > LAN_DATA_MAX_PAYLOAD)
        return -1;
    struct dwc_gamedata *game = get_dwc_gamedata();
    if (!game || game->dwc_host_state != 1 || game->player_count < 2 ||
        game->player_count > MKW_LAN_MATCH_NODES)
        return -1;
    if (aid == game->peers[0].peer_index)
        return -1;
    uint8_t my_aid = 0xFF;
    uint32_t target_ip = 0;
    for (unsigned i = 0; i < game->player_count; i++) {
        if (game->peers[i].player_id == game->my_pid)
            my_aid = game->peers[i].peer_index;
        if (game->peers[i].peer_index == aid)
            target_ip = game->peers[i].local_ip;
    }
    if (my_aid >= MKW_LAN_MATCH_NODES || !target_ip ||
        target_ip == MKW_ADVERTISED_IP || (target_ip >> 28) >= 14)
        return -1;

    static uint8_t tx_buf[sizeof(struct lan_data_header) + LAN_DATA_MAX_PAYLOAD];
    struct lan_data_header *hdr = (struct lan_data_header *)tx_buf;
    hdr->magic = LAN_DATA_MAGIC;
    hdr->origin_aid = my_aid;
    hdr->target_aid = aid;
    hdr->payload_len = (uint16_t)size;
    lan_copy_bytes(tx_buf + sizeof(*hdr), (const uint8_t *)buf, (size_t)size);

    const so_addr_t dst = {
        .sa_len = sizeof(so_addr_t),
        .sa_family = AF_INET,
        .sa_port = LAN_DATA_PORT,
        .sa_addr = target_ip
    };
    SOSendTo(data_socket, tx_buf, sizeof(*hdr) + (size_t)size, 0, &dst);
    return 1;
#else
    (void)aid; (void)buf; (void)size;
    return -1;
#endif
}

void network_thread_poll_data(void) {
#if MKW_LAN_CAPACITY > 12
    if (data_socket < 0) return;
    struct dwc_gamedata *game = get_dwc_gamedata();
    if (!game || game->dwc_host_state != 1 || game->player_count < 2 ||
        game->player_count > MKW_LAN_MATCH_NODES)
        return;
    struct dwc_transport *transport = (struct dwc_transport *)(game + 1);
    typedef void (*user_recv_fn)(uint8_t aid, uint8_t *buf, int len);
    user_recv_fn recv_cb = (user_recv_fn)transport->user_receive_callback;
    if (!recv_cb) return;

    uint8_t my_aid = 0xFF;
    for (unsigned i = 0; i < game->player_count; i++) {
        if (game->peers[i].player_id == game->my_pid) {
            my_aid = game->peers[i].peer_index;
            break;
        }
    }
    if (my_aid >= MKW_LAN_MATCH_NODES) return;

    static uint8_t rx_buf[sizeof(struct lan_data_header) + LAN_DATA_MAX_PAYLOAD];
    for (unsigned iter = 0; iter < 512; iter++) {
        so_addr_t src = { .sa_len = sizeof(src) };
        const so_ret_t n = SORecvFrom(data_socket, rx_buf, sizeof(rx_buf), 0, &src);
        if (n < (so_ret_t)sizeof(struct lan_data_header)) break;
        const struct lan_data_header *hdr = (const struct lan_data_header *)rx_buf;
        if (hdr->magic != LAN_DATA_MAGIC || hdr->target_aid != my_aid ||
            hdr->origin_aid >= MKW_LAN_MATCH_NODES || hdr->payload_len == 0 ||
            (so_ret_t)(sizeof(*hdr) + hdr->payload_len) > n)
            continue;
        uint8_t *peer_entry = &transport->peers[(unsigned)hdr->origin_aid * 64u];
        uint8_t *dst_buf = *(uint8_t **)(peer_entry + 4);
        const uint32_t dst_cap = *(const uint32_t *)(peer_entry + 8);
        if (dst_buf && hdr->payload_len <= dst_cap) {
            lan_copy_bytes(dst_buf, rx_buf + sizeof(*hdr), hdr->payload_len);
            recv_cb(hdr->origin_aid, dst_buf, hdr->payload_len);
        } else {
            recv_cb(hdr->origin_aid, rx_buf + sizeof(*hdr), hdr->payload_len);
        }
    }
#endif
}
