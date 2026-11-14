"""Exercise the observed SYN/control-socket inversion without forcing SDK state."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


class SynControlOrder(unittest.TestCase):
    def test_host_syn_drains_queued_cancellation_before_the_sdk_state_gate(self):
        source = (Path(__file__).parents[1]/'source/module/network_thread.c').read_text()
        helper = source[source.index('void network_thread_prepare_syn('):]
        harness = r'''
#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>
#define DWC_MATCH_STATE_CL_WAIT_RESV 3
struct dwc_gamedata {unsigned dwc_host_state,dwc_match_state,player_count;struct {unsigned peer_index;} peers[32];};
static struct dwc_gamedata game;
static struct dwc_gamedata *current=&game;
static bool network_thread_running=true;
static int broadcast_socket=4;
static unsigned polls,acks;static bool cancel_pending,recurse;
static struct dwc_gamedata *get_dwc_gamedata(void){return current;}
void network_thread_prepare_syn(uint32_t,uint32_t);
static void receive_packets(int socket,bool status){
    if(socket!=4||!status)abort();
    polls++;
    if(recurse)network_thread_prepare_syn(0,1);
    /* This represents the already-queued authenticated host stop-mesh command.
       Its original SDK handler, not the ordering helper, changes the state. */
    if(cancel_pending){game.dwc_match_state=1;cancel_pending=false;}
}
'''+helper+r'''
#define CHECK(x) do{if(!(x))abort();}while(0)
static void syn(void){network_thread_prepare_syn(0,1);if(game.dwc_match_state==1)acks++;}
int main(void){
    game=(struct dwc_gamedata){.dwc_host_state=1,.dwc_match_state=3,.player_count=7};
    cancel_pending=true;recurse=true;syn();
    if(MKW_LAN_CAPACITY<=12){CHECK(!polls&&!acks&&cancel_pending);return 0;}
    CHECK(polls==1&&acks==1&&!cancel_pending);
    game.dwc_match_state=3;cancel_pending=false;syn();
    CHECK(polls==2&&acks==1&&game.dwc_match_state==3); /* no invented cancellation */
    for(unsigned state=0;state<=20;state++)if(state!=3){game.dwc_match_state=state;network_thread_prepare_syn(0,1);CHECK(polls==2);}
    game.dwc_match_state=3;
    for(unsigned type=0;type<5;type++)if(type!=1){network_thread_prepare_syn(0,type);CHECK(polls==2);}
    network_thread_prepare_syn(1,1);CHECK(polls==2); /* not the host */
    for(unsigned role=0;role<4;role++)if(role!=1){game.dwc_host_state=role;network_thread_prepare_syn(0,1);CHECK(polls==2);}
    game.dwc_host_state=1;
    for(unsigned n=0;n<34;n++)if(n<2||n>32){game.player_count=n;network_thread_prepare_syn(0,1);CHECK(polls==2);}
    game.player_count=7;network_thread_running=false;network_thread_prepare_syn(0,1);CHECK(polls==2);
    network_thread_running=true;broadcast_socket=-1;network_thread_prepare_syn(0,1);CHECK(polls==2);
    broadcast_socket=4;current=NULL;network_thread_prepare_syn(0,1);CHECK(polls==2);
    current=&game;cancel_pending=true;syn();CHECK(polls==3&&acks==2);
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
