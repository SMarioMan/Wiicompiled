"""Exercise cross-socket SYN order using the production retention/replay code."""
import os
from pathlib import Path
import re
import subprocess
import tempfile
import unittest


class SynControlOrder(unittest.TestCase):
    def test_original_sdk_receives_owned_syn_after_actual_mesh_cancellation(self):
        module = Path(__file__).parents[1] / 'source/module'
        source = (module / 'network_thread.c').read_text()
        helper = source[source.index('static struct {\n    bool present;'):]
        states = re.search(r'enum dwc_match_state \{.*?\};', (module/'mkw.h').read_text(), re.S)[0]
        harness = r'''
#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#define BROADCAST_INTERVAL 60
#define LOG_INFO(...) ((void)0)
'''+states+r'''
struct dwc_gamedata {unsigned dwc_host_state,player_count;enum dwc_match_state dwc_match_state;
    struct {unsigned peer_index,player_id;} peers[32];};
static struct dwc_gamedata game,other;
static struct dwc_gamedata *current=&game;
static bool network_thread_running=true;
static bool sdk_polling;
static int broadcast_socket=4;
static uint64_t clock_units;
static uint64_t OSGetTime(void){return clock_units;}
static uint64_t lan_discovery_time(uint64_t ticks){return ticks;}
static unsigned polls,acks;static bool cancel_pending,recurse;
static uint32_t received[8],reentrant_packet[8]={0,100,101,102,103,104,105,106};
static struct dwc_gamedata *get_dwc_gamedata(void){return current;}
static void replay_deferred_syn(struct dwc_gamedata*);
bool network_thread_prepare_syn(uint32_t,uint32_t,const uint32_t*);
static void DWCi_ProcessMatchSynPacket(uint32_t aid,uint32_t type,const uint32_t *packet){
    if(aid!=0||type!=1)abort();
    /* This is the real SDK's state gate; only its accepted path creates ACK. */
    if(current->dwc_match_state!=DWC_MATCH_STATE_CL_WAITING)return;
    unsigned words=packet[0]==__builtin_bswap32(1)?2:8;
    memset(received,0,sizeof(received));memcpy(received,packet,words*4);
    acks++;current->dwc_match_state=DWC_MATCH_STATE_CL_SYN;
}
static void receive_packets(int socket,bool status){
    if(socket!=4||!status)abort();
    polls++;
    if(recurse)network_thread_prepare_syn(0,1,reentrant_packet);
    /* Only the original cancellation handler makes this state transition. */
    if(cancel_pending){game.dwc_match_state=DWC_MATCH_STATE_CL_WAITING;cancel_pending=false;
        replay_deferred_syn(&game);}
}
'''+helper+r'''
#define CHECK(x) do{if(!(x))abort();}while(0)
static void reset(unsigned state){
    clear_deferred_syn();current=&game;network_thread_running=true;broadcast_socket=4;
    game=(struct dwc_gamedata){.dwc_host_state=1,.dwc_match_state=state,.player_count=7};
    game.peers[0].player_id=0x7f000001;
    clock_units=1000;polls=acks=0;cancel_pending=recurse=false;
}
static bool syn(uint32_t *packet){
    bool retained=network_thread_prepare_syn(0,1,packet);
    if(!retained)DWCi_ProcessMatchSynPacket(0,1,packet);
    return retained;
}
int main(void){
    uint32_t packet[8]={0,1,2,3,4,5,6,7};
    reset(3);cancel_pending=true;syn(packet);
    if(MKW_LAN_CAPACITY<=12){CHECK(!polls&&!acks&&cancel_pending);return 0;}
    CHECK(polls==1&&acks==1&&!cancel_pending&&!deferred_syn.present);
    const unsigned busy_states[]={3,4,5,6,13,14,15,16};
    for(unsigned i=0;i<sizeof(busy_states)/sizeof(busy_states[0]);i++){
        unsigned state=busy_states[i];reset(state);
        uint32_t owned[8]={0,11,12,13,14,15,16,17};
        CHECK(syn(owned)&&polls==1&&!acks&&game.dwc_match_state==state);
        memset(owned,0xee,sizeof(owned)); /* transport storage may be reused */
        replay_deferred_syn(&game);CHECK(!acks&&game.dwc_match_state==state);
        game.dwc_match_state=DWC_MATCH_STATE_CL_WAITING;
        replay_deferred_syn(&game);CHECK(acks==1&&received[1]==11&&received[7]==17&&!deferred_syn.present);
        replay_deferred_syn(&game);CHECK(acks==1);
    }
    // The queued-control path stays bounded under a reentrant SDK callback.
    reset(5);cancel_pending=recurse=true;syn(packet);CHECK(polls==1&&acks==1&&!deferred_syn.present);
    // A current replacement supersedes the previous transport-owned payload.
    reset(14);CHECK(syn(packet));uint32_t newer[8]={0,41,42,43,44,45,46,47};
    CHECK(syn(newer));game.dwc_match_state=1;replay_deferred_syn(&game);CHECK(acks==1&&received[7]==47);
    // Flag one is the SDK's short, two-word SYN variant.
    reset(3);uint32_t short_packet[2]={__builtin_bswap32(1),0x12345678};
    CHECK(syn(short_packet));game.dwc_match_state=1;replay_deferred_syn(&game);
    CHECK(acks==1&&received[1]==0x12345678&&received[2]==0);
    // Expiry and room/host changes must never replay a prior barrier.
    for(unsigned reason=0;reason<8;reason++){
        reset(3);CHECK(syn(packet));game.dwc_match_state=1;
        if(reason==0)clock_units+=60*5*7+1;
        if(reason==1)game.peers[0].player_id++;
        if(reason==2)game.dwc_host_state=2;
        if(reason==3)network_thread_running=false;
        if(reason==4)broadcast_socket=-1;
        if(reason==5){other=game;current=&other;}
        if(reason==6)game.dwc_match_state=DWC_MATCH_STATE_WAIT_CLOSE;
        if(reason==7)current=NULL;
        replay_deferred_syn(reason==7?&game:current);CHECK(!acks&&!deferred_syn.present);
    }
    reset(3);CHECK(syn(packet));CHECK(!network_thread_prepare_syn(0,3,packet));CHECK(!deferred_syn.present);
    reset(3);CHECK(!network_thread_prepare_syn(1,1,packet)&&!polls&&!deferred_syn.present);
    reset(3);CHECK(!network_thread_prepare_syn(0,1,NULL)&&!polls&&!deferred_syn.present);
    for(unsigned count=0;count<=33;count++)if(count<2||count>32){
        reset(3);game.player_count=count;CHECK(!network_thread_prepare_syn(0,1,packet)&&!polls);
    }
    reset(3);game.peers[0].peer_index=32;CHECK(!network_thread_prepare_syn(32,1,packet)&&!polls);
    reset(3);game.peers[0].player_id=0;CHECK(!network_thread_prepare_syn(0,1,packet)&&!polls);
    for(unsigned state=0;state<=20;state++){
        bool busy=false;
        for(unsigned i=0;i<sizeof(busy_states)/sizeof(busy_states[0]);i++)if(state==busy_states[i])busy=true;
        if(busy)continue;
        reset(state);CHECK(!network_thread_prepare_syn(0,1,packet)&&!polls&&!deferred_syn.present);
    }
    reset(1);CHECK(!syn(packet)&&acks==1&&!polls);
    reset(3);current=NULL;CHECK(!network_thread_prepare_syn(0,1,packet)&&!polls);
}
'''
        with tempfile.TemporaryDirectory(prefix='lan-syn-order-') as directory:
            path=Path(directory);(path/'test.c').write_text(harness)
            for capacity in (12,13,24,32):
                result=subprocess.run([os.environ.get('CC','cc'),'-std=c11','-Wall','-Wextra','-Werror',
                    f'-DMKW_LAN_CAPACITY={capacity}',str(path/'test.c'),'-o',str(path/'test')],capture_output=True,text=True)
                self.assertEqual(result.returncode,0,result.stderr)
                subprocess.run([str(path/'test')],check=True,timeout=3)


if __name__=='__main__':
    unittest.main()
