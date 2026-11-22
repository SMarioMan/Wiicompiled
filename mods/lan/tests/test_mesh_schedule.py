"""Execute pair scheduling and the actual LAN deadline helper with SDK boundaries mocked."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


class MeshSchedule(unittest.TestCase):
    def test_acyclic_pair_ownership_stagger_backoff_and_stock_fallback(self):
        module = Path(__file__).parents[1]/'source/module'
        source = (module/'mesh_schedule.c').read_text()
        helper = source[source.index('uint64_t LANMeshNextTick('):]
        harness = r'''
#include <stdint.h>
#include <stdbool.h>
#include <stdlib.h>
#include <stdio.h>
#include "mesh_schedule.h"
#define DWC_MATCH_STATE_CL_WAITING 1
#define millisecs_to_ticks(n) ((n)*60750u)
static struct dwc_gamedata {unsigned player_count,dwc_match_state;uint32_t my_pid;} game;
static unsigned originals,clocks;
static int have_game=1;
static struct dwc_gamedata *get_dwc_gamedata(void){return have_game?&game:NULL;}
static uint64_t DWCi_GetNextMeshMakeTryTick(void){++originals;return 0x123456789abcdef0ULL;}
static uint64_t OSGetTime(void){++clocks;return 10000000000ULL;}
'''+helper+r'''
#define CHECK(x) do{if(!(x)){fprintf(stderr,"mesh schedule line %u: %s\n",__LINE__,#x);abort();}}while(0)
int main(void){
    for(uint32_t a=1;a<=100;a++)for(uint32_t b=a+1;b<=100;b++){
        const uint32_t low=0x7f000000u+a,high=0x7f000000u+b;
        uint32_t before_low=0,before_high=0;
        for(uint32_t attempt=0;attempt<8;attempt++){
            const uint32_t owner=lan_mesh_delay_ms(low,high,attempt),fallback=lan_mesh_delay_ms(high,low,attempt);
            CHECK(owner>=125 && owner<=2124 && fallback==LAN_MESH_PASSIVE_DELAY);
            CHECK(owner>=before_low && fallback>=before_high);before_low=owner;before_high=fallback;
            if(!attempt)CHECK(owner<=249);
        }
        CHECK(lan_mesh_delay_ms(low,high,0xffffffffu)<=2124);
    }
    unsigned jitter_mask=0;
    for(uint32_t peer=2;peer<=32;peer++)jitter_mask|=1u<<((lan_mesh_delay_ms(0x7f000001,0x7f000000+peer,0)-125)/4);
    CHECK(__builtin_popcount(jitter_mask)>=8);
    uint32_t peer[12]={0};peer[0]=0x7f000002;game.my_pid=0x7f000001;game.dwc_match_state=1;
    for(unsigned count=1;count<=MKW_LAN_MATCH_NODES+1;count++){
        game.player_count=count;originals=clocks=0;uint64_t next=LANMeshNextTick(peer);
        if(MKW_LAN_CAPACITY<=12 || count<=2 || count>MKW_LAN_MATCH_NODES)CHECK(next==0x123456789abcdef0ULL && originals==1 && !clocks);
        else CHECK(next==10000000000ULL+(uint64_t)millisecs_to_ticks(lan_mesh_delay_ms(game.my_pid,peer[0],0)) && !originals && clocks==1);
    }
    game.player_count=24;
    for(unsigned attempt=0;attempt<8;attempt++){
        peer[6]=attempt;originals=clocks=0;uint64_t next=LANMeshNextTick(peer);
        if(MKW_LAN_CAPACITY<=12)CHECK(next==0x123456789abcdef0ULL && originals==1 && !clocks);
        else CHECK(next==10000000000ULL+(uint64_t)millisecs_to_ticks(lan_mesh_delay_ms(game.my_pid,peer[0],attempt)) && !originals && clocks==1);
    }
    // Passive endpoints never acquire a finite outgoing retry, even after
    // the old 30/60-second fallback windows or exhausted retry counters.
    game.my_pid=0x7f000018;peer[0]=0x7f000002;
    for(unsigned attempt=0;attempt<8;attempt++){
        peer[6]=attempt;originals=clocks=0;uint64_t next=LANMeshNextTick(peer);
        if(MKW_LAN_CAPACITY<=12)CHECK(next==0x123456789abcdef0ULL && originals==1);
        else CHECK(next==INT64_MAX && !originals && !clocks && (int64_t)next>0);
    }
    // Any reservation chain strictly increases its PID; there is always a
    // passive sink instead of the observed 7<->12 and 16<->24 wait cycles.
    for(uint32_t a=1;a<=24;a++)for(uint32_t b=1;b<=24;b++)if(a!=b){
        bool forward=lan_mesh_delay_ms(a,b,0)!=LAN_MESH_PASSIVE_DELAY;
        bool backward=lan_mesh_delay_ms(b,a,0xffffffffu)!=LAN_MESH_PASSIVE_DELAY;
        CHECK(forward!=backward);
        if(forward)CHECK(a<b);
    }
    for(unsigned state=0;state<=18;state++)if(state!=1){game.dwc_match_state=state;originals=0;CHECK(LANMeshNextTick(peer)==0x123456789abcdef0ULL && originals==1);}
    game.dwc_match_state=1;peer[0]=game.my_pid;CHECK(LANMeshNextTick(peer)==0x123456789abcdef0ULL);
    peer[0]=0;CHECK(LANMeshNextTick(peer)==0x123456789abcdef0ULL);
    CHECK(LANMeshNextTick(NULL)==0x123456789abcdef0ULL);have_game=0;CHECK(LANMeshNextTick(peer)==0x123456789abcdef0ULL);
}
'''
        with tempfile.TemporaryDirectory(prefix='lan-mesh-schedule-') as temporary:
            path=Path(temporary)
            for capacity, nodes in ((12,32), (24,32), (33,64), (128,128)):
                (path/'test.c').write_text(harness)
                compile = subprocess.run([os.environ.get('CC','cc'),'-std=c11','-Wall','-Werror',f'-DMKW_LAN_CAPACITY={capacity}',f'-DMKW_LAN_MATCH_NODES={nodes}','-I',str(module),
                    str(path/'test.c'),'-o',str(path/'test')],capture_output=True,text=True)
                self.assertEqual(compile.returncode,0,compile.stderr)
                run = subprocess.run([str(path/'test')],capture_output=True,text=True)
                self.assertEqual(run.returncode,0,run.stderr)


if __name__=='__main__':
    unittest.main()
