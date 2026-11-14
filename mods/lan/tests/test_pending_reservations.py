"""Execute the actual reservation queue and polling code for host and client peers."""
import os
from pathlib import Path
import re
import subprocess
import tempfile
import unittest


class PendingReservations(unittest.TestCase):
    def test_busy_peers_resume_queued_requests_in_both_idle_states(self):
        source = (Path(__file__).parents[1] / 'source/module/network_thread.c').read_text()
        declaration = source[source.index('static struct pending_reservation {'):source.index('bool network_thread_get_fake_pid_status(')]
        poll = source[source.index('static void check_for_status_or_broadcast('):source.index('void network_thread_poll_sdk(')]
        header = (Path(__file__).parents[1] / 'source/module/mkw.h').read_text()
        states = re.search(r'enum dwc_match_state \{.*?\};', header, re.S)[0]
        harness = r'''
#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>
#include <stdio.h>
#define BROADCAST_TRACK_MAX 4
#define MKW_LAN_CAPACITY 32
#define STATUS_SIZE_MAX 512
#define STATUS_MAGIC 0xbb49cc4d
#define BROADCAST_TIMEOUT 120
#define RESERVATION_TIMEOUT 1440
#define MKW_ADVERTISED_IP 0x08080808
#define LOG_INFO(...) ((void)0)
typedef struct { uint32_t sa_addr; uint16_t sa_port; } so_addr_t;
'''
        harness += states + r'''
static struct dwc_gamedata {enum dwc_match_state dwc_match_state;uint32_t public_ip;uint32_t reservation_timeout_ms;uint32_t player_count;} game;
static void replay_deferred_syn(struct dwc_gamedata *g){(void)g;}
static bool have_game=true;
static struct dwc_gamedata *get_dwc_gamedata(void){return have_game?&game:NULL;}
static uint64_t timestamp=100;
static struct {uint64_t last_update;struct {unsigned status;} info;} tracked_peers[BROADCAST_TRACK_MAX];
static unsigned delivered,seen,polls,last,last_type;
static int broadcast_socket=1,multicast_socket=2;
static int find_peer_by_pid(uint32_t pid){return pid>=1 && pid<=BROADCAST_TRACK_MAX?(int)pid-1:-1;}
static void dwc_process_status_record(unsigned type,uint32_t pid,uint32_t ip,uint16_t port,const uint32_t *data,unsigned size){
    if((type!=1 && type!=2) || pid<1 || pid>4 || ip!=0x7f000001+pid || port!=27900 || size>1 || (size && data[0]!=0x12345678))abort();
    ++delivered;last=pid;last_type=type;seen|=1u<<pid;game.dwc_match_state=DWC_MATCH_STATE_SV_OWN_NN;
}
static void learn_status_peer(const uint32_t *buffer,const so_addr_t addr){(void)buffer;(void)addr;}
static void receive_packets(int fd,bool status){if(fd!=(status?1:2))abort();++polls;}
'''
        harness += declaration + poll + r'''
#define CHECK(x) do{if(!(x)){fprintf(stderr,"reservation fixture line %d state=%u delivered=%u\n",__LINE__,game.dwc_match_state,delivered);abort();}}while(0)
static void request(unsigned pid){
    uint32_t packet[5]={STATUS_MAGIC,0,1u<<24,0,__builtin_bswap32(pid)};
    so_addr_t address={0x7f000001+pid,27900};process_status(packet,sizeof(packet),address);
}
int main(void){
    for(unsigned role=0;role<2;role++){
        for(unsigned i=0;i<4;i++)pending_reservations[i].length=0;
        delivered=seen=polls=0;timestamp=100;game.dwc_match_state=DWC_MATCH_STATE_SV_OWN_GT2;
        request(1);request(2);CHECK(!delivered && pending_reservations[0].length==20 && pending_reservations[1].length==20);
        for(unsigned busy=DWC_MATCH_STATE_SV_OWN_NN;busy<=DWC_MATCH_STATE_SV_SYN_WAIT;busy++){
            game.dwc_match_state=(enum dwc_match_state)busy;check_for_status_or_broadcast();CHECK(!delivered);
        }
        game.dwc_match_state=DWC_MATCH_STATE_CL_WAIT_RESV;check_for_status_or_broadcast();CHECK(!delivered);
        game.dwc_match_state=role?DWC_MATCH_STATE_SV_WAITING:DWC_MATCH_STATE_CL_WAITING;
        check_for_status_or_broadcast();CHECK(delivered==1 && seen==2 && !pending_reservations[0].length && pending_reservations[1].length==20);
        check_for_status_or_broadcast();CHECK(delivered==1); /* SDK is busy again. */
        game.dwc_match_state=role?DWC_MATCH_STATE_SV_WAITING:DWC_MATCH_STATE_CL_WAITING;
        check_for_status_or_broadcast();CHECK(delivered==2 && seen==6 && !pending_reservations[1].length);
        game.dwc_match_state=DWC_MATCH_STATE_SV_OWN_GT2;request(1);request(3);
        pending_reservations[0].received_at=0;timestamp=RESERVATION_TIMEOUT+1;
        pending_reservations[2].received_at=timestamp;
        game.dwc_match_state=role?DWC_MATCH_STATE_SV_WAITING:DWC_MATCH_STATE_CL_WAITING;
        check_for_status_or_broadcast();CHECK(delivered==3 && seen==14 && !pending_reservations[0].length && !pending_reservations[2].length);
        CHECK(polls>0 && polls%2==0);
    }
    /* Older high-index requests must beat newer low-index requests, even
       when duplicates refresh their arrival timestamp. New arrivals at idle
       must not bypass an already waiting peer. */
    for(unsigned role=0;role<2;role++){
        for(unsigned i=0;i<4;i++)pending_reservations[i].length=0;
        delivered=seen=polls=last=0;timestamp=100;game.dwc_match_state=DWC_MATCH_STATE_SV_OWN_GT2;
        request(3);timestamp=101;request(1);timestamp=102;request(3);
        game.dwc_match_state=role?DWC_MATCH_STATE_SV_WAITING:DWC_MATCH_STATE_CL_WAITING;
        check_for_status_or_broadcast();CHECK(delivered==1 && last==3);
        request(2);game.dwc_match_state=role?DWC_MATCH_STATE_SV_WAITING:DWC_MATCH_STATE_CL_WAITING;
        request(4);CHECK(delivered==1);
        check_for_status_or_broadcast();CHECK(delivered==2 && last==1);
        game.dwc_match_state=role?DWC_MATCH_STATE_SV_WAITING:DWC_MATCH_STATE_CL_WAITING;
        check_for_status_or_broadcast();CHECK(delivered==3 && last==2);
        game.dwc_match_state=role?DWC_MATCH_STATE_SV_WAITING:DWC_MATCH_STATE_CL_WAITING;
        check_for_status_or_broadcast();CHECK(delivered==4 && last==4);
    }
    /* A request outlives discovery while waiting behind SDK reservations. */
    for(unsigned i=0;i<4;i++)pending_reservations[i].length=0;
    timestamp=100;game.dwc_match_state=DWC_MATCH_STATE_SV_OWN_GT2;
    request(1);timestamp+=BROADCAST_TIMEOUT+1;
    unsigned queued_before=delivered;
    game.dwc_match_state=DWC_MATCH_STATE_SV_WAITING;
    check_for_status_or_broadcast();CHECK(delivered==queued_before+1 && last==1);
    for(unsigned i=0;i<4;i++)pending_reservations[i].length=0;
    delivered=0;
    /* Preserve the SDK's handling of crossed client negotiations; deferring
       both directions of a simultaneous outgoing link can deadlock. */
    for(unsigned state=DWC_MATCH_STATE_CL_WAIT_RESV;state<=DWC_MATCH_STATE_CL_SYN;state++){
        game.dwc_match_state=(enum dwc_match_state)state;request(1);
        CHECK(delivered==state-DWC_MATCH_STATE_CL_WAIT_RESV+1 && !pending_reservations[0].length);
    }
    uint32_t packet[6]={STATUS_MAGIC,0,2u<<24,0,__builtin_bswap32(1),0x12345678};
    so_addr_t address={0x7f000002,27900};
    game.dwc_match_state=DWC_MATCH_STATE_SV_OWN_GT2;process_status(packet,20,address);
    CHECK(last_type==2 && !pending_reservations[0].length);
    unsigned before=delivered;packet[2]=1u<<24;
    process_status(packet,-1,address);process_status(packet,19,address);process_status(packet,STATUS_SIZE_MAX+4,address);
    packet[2]=(1u<<24)|(4u<<16);process_status(packet,20,address);
    packet[2]=(1u<<24)|(3u<<16);process_status(packet,24,address);
    CHECK(delivered==before && !pending_reservations[0].length);
    packet[2]=(1u<<24)|(4u<<16);process_status(packet,28,address);
    CHECK(pending_reservations[0].length==24);
    game.dwc_match_state=DWC_MATCH_STATE_SV_WAITING;check_for_status_or_broadcast();
    CHECK(delivered==before+1 && last_type==1 && !pending_reservations[0].length);
    have_game=false;request(1);check_for_status_or_broadcast();CHECK(delivered==before+1);
    return 0;
}
'''
        with tempfile.TemporaryDirectory(prefix='lan-reservations-') as directory:
            path = Path(directory)
            (path / 'test.c').write_text(harness)
            compile = subprocess.run([os.environ.get('CC', 'cc'), '-std=c11', '-Wall', '-Wextra', '-Werror', '-Wno-error=sign-compare', str(path / 'test.c'), '-o', str(path / 'test')], capture_output=True, text=True)
            self.assertEqual(compile.returncode, 0, compile.stderr)
            subprocess.run([str(path / 'test')], check=True, timeout=3)


if __name__ == '__main__':
    unittest.main()
