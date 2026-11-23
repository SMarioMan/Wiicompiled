"""Replay a next-admission node update after the preceding reliable SYN-2."""
from pathlib import Path
import os
import re
import subprocess
import tempfile
import unittest


class NodeControlOrder(unittest.TestCase):
    def test_udp_node_update_cannot_be_undone_by_previous_syn_completion(self):
        module = Path(__file__).parents[1] / 'source/module'
        source = (module/'network_thread.c').read_text()
        begin = source.index('static struct deferred_node_control {')
        helpers = source[begin:source.index('bool network_thread_get_fake_pid_status(')]
        main = (module/'main.c').read_text()
        begin = main.index('static void MyDWCi_ProcessMatchSynPacket(')
        wrapper = main[begin:main.index('\n}', begin)+2]
        states = re.search(r'enum dwc_match_state \{.*?\};', (module/'mkw.h').read_text(), re.S)[0]
        harness = r'''
#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>
#include <stdio.h>
#include <string.h>
#define STATUS_MAGIC 0xbb49cc4d
#define STATUS_SIZE_MAX 512
#define RESERVATION_TIMEOUT 3600
#define BROADCAST_INTERVAL 60
#define MKW_ADVERTISED_IP 0x08080808
#define LOG_INFO(...) ((void)0)
typedef struct {uint32_t sa_addr;uint16_t sa_port;} so_addr_t;
'''+states+r'''
struct dwc_gamedata {enum dwc_match_state dwc_match_state;uint32_t public_ip,dwc_host_state,player_count;
    struct {uint32_t player_id,peer_index;} peers[MKW_LAN_MATCH_NODES];};
static struct dwc_gamedata game,other,*current=&game;
static bool network_thread_running=true;
static int broadcast_socket=4;
static uint64_t timestamp=1000;
static uint64_t OSGetTime(void){return timestamp;}
static uint64_t lan_discovery_time(uint64_t value){return value;}
static struct dwc_gamedata *get_dwc_gamedata(void){return current;}
static void replay_deferred_syn(struct dwc_gamedata *g){(void)g;}
static unsigned controls,completed,mesh=1,last_payload;
static struct pending_reservation {uint64_t enqueued_at,received_at;unsigned length;so_addr_t address;uint32_t packet[128];} pending_reservations[32];
static int find_peer_by_pid(uint32_t pid){(void)pid;return -1;}
static void learn_status_peer(uint32_t *p,so_addr_t a){(void)p;(void)a;}
static void dwc_process_status_record(unsigned type,uint32_t pid,uint32_t ip,uint16_t port,const uint32_t *packet,unsigned words){
    if(type!=7||ip!=pid||port!=27900||words!=2)abort();
    controls++;last_payload=packet[0];mesh=0;
}
static bool network_thread_prepare_syn(uint32_t aid,uint32_t type,const uint32_t *packet){(void)aid;(void)type;(void)packet;return false;}
static void DWCi_ProcessMatchSynPacket(uint32_t aid,uint32_t type,const uint32_t *packet){
    (void)packet;if(aid||type!=3)abort();completed++;mesh=1;
    current->dwc_match_state=DWC_MATCH_STATE_CL_WAITING;
}
'''+helpers+wrapper+r'''
#define CHECK(x) do{if(!(x)){fprintf(stderr,"node order line %d controls=%u completed=%u mesh=%u\n",__LINE__,controls,completed,mesh);abort();}}while(0)
static void reset(void){
    game=(struct dwc_gamedata){.dwc_match_state=DWC_MATCH_STATE_CL_SYN,.dwc_host_state=1,.player_count=13};
    game.peers[0].player_id=0x7f000001;current=&game;
    network_thread_running=true;broadcast_socket=4;controls=completed=0;mesh=1;timestamp=1000;
}
int main(void){
    uint32_t packet[7]={STATUS_MAGIC,0,(7u<<24)|(8u<<16),0,__builtin_bswap32(0x7f000001),0x0e00007f,0x0d000000};
    so_addr_t address={0x7f000001,27900};reset();
    process_status(packet,sizeof(packet),address);
    if(MKW_LAN_CAPACITY<=12){CHECK(controls==1);MyDWCi_ProcessMatchSynPacket(0,3,packet);CHECK(mesh==1);return 0;}
    CHECK(!controls);
    network_thread_after_syn(1,3);network_thread_after_syn(0,2);CHECK(!controls);
    packet[5]=0xeeeeeeee; // the UDP receive buffer is reused
    MyDWCi_ProcessMatchSynPacket(0,3,packet);
    CHECK(completed==1&&controls==1&&mesh==0&&last_payload==0x0e00007f);
    // A later poll must not dispatch the same new-node update twice.
    network_thread_after_syn(0,3);CHECK(controls==1);
    for(unsigned reason=0;reason<7;reason++){
        reset();packet[5]=0x0e00007f;process_status(packet,sizeof(packet),address);CHECK(!controls);
        if(reason==0)timestamp+=RESERVATION_TIMEOUT+1;
        if(reason==1)game.peers[0].player_id++;
        if(reason==2)game.dwc_host_state=2;
        if(reason==3)network_thread_running=false;
        if(reason==4)broadcast_socket=-1;
        if(reason==5){other=game;current=&other;}
        if(reason==6)current=NULL;
        network_thread_after_syn(0,3);CHECK(!controls&&!deferred_node.length);
    }
    reset();game.player_count=MKW_LAN_MATCH_NODES;
    packet[5]=0x0e00007f;process_status(packet,sizeof(packet),address);CHECK(!controls);
    MyDWCi_ProcessMatchSynPacket(0,3,packet);CHECK(completed==1&&controls==1&&mesh==0);
    for(unsigned guard=0;guard<5;guard++){
        reset();
        if(guard==0)game.dwc_host_state=2;
        if(guard==1)game.player_count=1;
        if(guard==2)game.player_count=MKW_LAN_MATCH_NODES+1;
        if(guard==3)game.peers[0].player_id++;
        if(guard==4)game.dwc_match_state=DWC_MATCH_STATE_CL_WAIT_RESV;
        process_status(packet,sizeof(packet),address);CHECK(controls==1);
    }
    reset();process_status(packet,20,address);CHECK(!controls&&!deferred_node.length);
    // Idle handling retains the original dispatch path.
    reset();game.dwc_match_state=DWC_MATCH_STATE_CL_WAITING;process_status(packet,sizeof(packet),address);CHECK(controls==1);
}
'''
        with tempfile.TemporaryDirectory(prefix='lan-node-order-') as directory:
            path=Path(directory);(path/'test.c').write_text(harness)
            for capacity, nodes in ((12,32),(13,32),(24,32),(32,32),(33,64),(128,128)):
                result=subprocess.run([os.environ.get('CC','cc'),'-std=c11','-Wall','-Wextra','-Werror',
                    '-Wno-unused-function',f'-DMKW_LAN_CAPACITY={capacity}',f'-DMKW_LAN_MATCH_NODES={nodes}',str(path/'test.c'),'-o',str(path/'test')],capture_output=True,text=True)
                self.assertEqual(result.returncode,0,result.stderr)
                result=subprocess.run([str(path/'test')],capture_output=True,text=True,timeout=3)
                self.assertEqual(result.returncode,0,result.stderr)


if __name__=='__main__':
    unittest.main()
