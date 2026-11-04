#include "local_config.h"
#include <rvl/so.h>
/* main.c
 *   by Alex Chadwick
 *
 * Copyright (C) 2014, Alex Chadwick
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */

#include <bslug.h>

#include <gamespy/sb.h>
#include <stdint.h>
#include <rvl/dwc.h>
#include <rvl/OSLink.h>
#include <rvl/OSTime.h>
#include <rvl/OSThread.h>

#include "mkw.h"
#include "network_thread.h"
#include "port_addresses.h"
#include "ui.h"

/* Which game to support. ? is a wild card (e.g. RMC? is any version of Mario
 * Kart Wii) */
BSLUG_MODULE_GAME("RMC?");
BSLUG_MODULE_NAME("MKW LAN multiplayer");
BSLUG_MODULE_VERSION("v0.9");
BSLUG_MODULE_AUTHOR("Chadderz and MrBean35000vr");
BSLUG_MODULE_LICENSE("BSD");

extern void _start(void);

extern uint32_t DWC_ConnectToGameServerAsync2[];
void on_populate_friend(struct menu_friend_list_friend *friend, void *save_file_license, int index);
void sleep_a_frame(void);
uint32_t BMG_Print[2];

struct /* size 0x24 */ {
  uint8_t _unk00[0x1c - 0x00];
  uint32_t pid; /* +0x1c */
  uint8_t _unk20[0x24 - 0x20];
  uint32_t game_id; /* +0x24 RMCJ */
} pid_struct;

/* Supply original MKW NDEV arguments before main stores argc/argv. */
static char host_boot[] = "-s132 -l0 -p292 -r0";
char *mkw_argv[] = { 0, host_boot, 0 };
extern void MKWSetBootArgs(void);
__asm__(".section .text.MKWSetBootArgs,\"ax\"\n"
        ".global MKWSetBootArgs\n"
        "MKWSetBootArgs:\n"
        "lis 4,mkw_argv@ha\n"
        "addi 4,4,mkw_argv@l\n"
        "stw 4,-27668(13)\n"
        "li 4,2\n"
        "stw 4,-27672(13)\n"
        "blr\n"
        ".previous\n");



/* Explicitly attach the boot-selected GC port to its menu input holder.
   InitControllerHolders otherwise starts with the dummy controller. Registers
   r27 (channel) and r30 (Input::Manager) belong to the original call site.
   Layouts and PAL call site are documented by Pulsar's Input/Section headers;
   USA code addresses use the -0x4474 mapping from versions.txt. */
extern void MKWSetController(void);
__asm__(".section .text.MKWSetController,\"ax\"\n"
        ".global MKWSetController\n"
        "MKWSetController:\n"
        "cmpwi 27,0\n"
        "bne 1f\n"
        "addi 4,30,0x3ba0\n"
        "lis 5,0x809c\n"
        "lwz 5,-0x2af8(5)\n"
        "stw 3,0x44(5)\n"
        "lwz 6,0x38(5)\n"
        "stw 6,0x3c(5)\n"
        "1: li 5,0\n"
        "lis 12,MKWOriginalControllerSet@ha\n"
        "addi 12,12,MKWOriginalControllerSet@l\n"
        "mtctr 12\n"
        "bctr\n"
        ".previous\n");
int (*MKWOriginalExtract)(void *self);
extern void *MKWSaveManager;
extern bool MKWHasLicense(void *manager, unsigned index);
int MKWBootSection(void *self) {
    /* The debug boot path assumes an existing license. Let first-time users
       create a license in the normal menu before selecting online play. */
    if (!MKWSaveManager || !MKWHasLicense(MKWSaveManager, 0)) return 0x3f;
    MKWOriginalExtract(self);
    return MKW_INSTANCE_ID == 1 ? 0x55 : 0x84;
}


#define SLEEP_TARGET microsecs_to_ticks(16666)

void sleep_a_frame(void) {
	static uint32_t last_time;
	uint32_t this_time;
	asm volatile ("mftbl %0" : "=r"(this_time));

	uint32_t difference = this_time - last_time;
	if (difference < SLEEP_TARGET) {
		OSSleepTicks(SLEEP_TARGET - difference);
		this_time += SLEEP_TARGET - difference;
	}
	last_time = this_time;
}

static void setup_pid(struct dwc_gamedata *gamedata) {
	my_fake_pid = 0x4d4b5700 | MKW_INSTANCE_ID;
	gamedata->my_pid = my_fake_pid;
	gamedata->gamedata2->my_pid = my_fake_pid;
	pid_struct.pid = my_fake_pid;
	pid_struct.game_id = 0x524D434A; // RMCJ
}

void on_populate_friend(struct menu_friend_list_friend *friend, void *save_file_license, int index) {
	network_thread_set_friend(index, friend);
}

static uint8_t mii[MII_SIZE];

static void get_mii(void) {
	uint8_t user_record[0xc8];
	((void (*)(uint8_t *))get_port_address(4))(user_record);
	for (int i = 0; i < MII_SIZE; i++)
		mii[i] = user_record[i + 16];
	network_thread_set_mii(mii, ((uint16_t *)user_record)[0xbc / 2], ((uint16_t *)user_record)[0xbe / 2], user_record[0xb8], user_record[0xb9]);
}

static void setup_friends(void) {
	uint32_t *friend_struct = *(uint32_t **)get_port_address(5);
	friend_struct += 0x9c / 4;
	for (int i = 0; i < 30; i++) {
		friend_struct[i * 3 + 0] = 0x3800;
		friend_struct[i * 3 + 1] = i + 1;
	}
}

static int MyDWC_LoginAsync(void *r3, int r4, void (*r5)(int, int, void *), void *r6) {
	setup_pid(get_dwc_gamedata());
	get_mii();
	setup_friends();
	mkw_net_state = r6;
	OSCreateThread(&network_thread, network_thread_main, 0, network_thread_stack + NETWORK_THREAD_SIZE, NETWORK_THREAD_SIZE, 0x18, false);
	OSResumeThread(&network_thread);
	return 0;
}


static uint8_t Mydwc_get_friend_status(const struct dwc_buddy *buddy, uint8_t *loc, int *loc_length) {
	if (network_thread_get_fake_pid_status(buddy->pid, loc)) {
		*loc_length = 8;
		return 1;
	} else {
		*loc_length = -1;
		return 0;
	}
}

static bool MyDWC_SetupGameServer(int player_count, void (*r4)(void *), void *r5, int r6, int r7, void (*r8)(void *), uint8_t *r9, void *r10) {
	struct dwc_gamedata *gamedata = get_dwc_gamedata();
	setup_pid(gamedata);
	bool result = DWC_SetupGameServer(player_count, r4, r5, r6, r7, r8, r9, r10);
	network_thread_after_server_setup(gamedata);
	return result;
}

static bool MyDWC_ConnectToGameServerAsync(int player_count, void (*r4)(void *), void *r5, int r6, int r7, void (*r8)(void *), uint8_t *r9, void *r10) {
	struct dwc_gamedata *gamedata = get_dwc_gamedata();
	setup_pid(gamedata);
	network_thread_on_server_join(gamedata);
	bool result = DWC_ConnectToGameServerAsync(player_count, r4, r5, r6, r7, r8, r9, r10);
	network_thread_after_server_join(gamedata);
	return result;
}

static int Mydwc_qr2_init(uint32_t pid) {
	// let's not and say we did
	return 0;
}

static int MyDWCi_SBUpdateAsync(uint32_t pid) {
	// let's not and say we did
	return 0;
}

static int Mydwc_send_status_record(int type, uint32_t pid, uint32_t ip, uint16_t port, const uint32_t *packet, size_t packet_len_in_words) {
	return network_thread_send_status_record(type, pid, ip, port, packet, packet_len_in_words) ? 0 : 1;
}

static uint32_t MyServerBrowserGetMyPublicIPAddr(void *sb) {
	return FAKE_PUBLIC_IP;
}



BSLUG_MUST_REPLACE(DWC_LoginAsync, MyDWC_LoginAsync);
BSLUG_MUST_REPLACE(dwc_get_friend_status, Mydwc_get_friend_status);
BSLUG_MUST_REPLACE(DWC_SetupGameServer, MyDWC_SetupGameServer);
BSLUG_MUST_REPLACE(DWC_ConnectToGameServerAsync, MyDWC_ConnectToGameServerAsync);
BSLUG_MUST_REPLACE(dwc_qr2_init, Mydwc_qr2_init);
BSLUG_MUST_REPLACE(DWCi_SBUpdateAsync, MyDWCi_SBUpdateAsync);
BSLUG_MUST_REPLACE(dwc_send_status_record, Mydwc_send_status_record);
BSLUG_MUST_REPLACE(ServerBrowserGetMyPublicIPAddr, MyServerBrowserGetMyPublicIPAddr);

/* Host-side sockets must bind the same per-instance identity we advertise. */
static so_ret_t MKWBind(so_fd_t fd, const so_addr_t *address) {
    so_addr_t local = *address;
    if (local.sa_family == AF_INET) {
        local.sa_addr = MKW_LOCAL_IP;
        /* DWC's random ephemeral port can fall in Windows/Hyper-V exclusions. */
        if (local.sa_port != 27900) local.sa_port = 27901;
    }
    return SOBind(fd, &local);
}
static uint32_t MKWHostID(void) { return MKW_LOCAL_IP; }
BSLUG_MUST_REPLACE(SOBind, MKWBind);
BSLUG_MUST_REPLACE(SOGetHostID, MKWHostID);
