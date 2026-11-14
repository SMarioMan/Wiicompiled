"""Execute the actual bounded reservation deadline policy, preserving SDK timing origins."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


class ReservationBudget(unittest.TestCase):
    def test_live_peer_budget_is_bounded_and_preserves_original_small_fields(self):
        source = (Path(__file__).parents[1]/'source/module/network_thread.c').read_text()
        policy = source[source.index('static void update_reservation_budget('):source.index('static void dispatch_status(')]
        harness = r'''
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#define BROADCAST_TRACK_MAX 32
#define BROADCAST_TIMEOUT 300
#define DWC_MATCH_STATE_CL_WAIT_RESV 3
static uint64_t timestamp=1000;
static struct {uint64_t last_update;struct {unsigned status;} info;} tracked_peers[32];
struct dwc_gamedata {unsigned dwc_match_state,reservation_timeout_ms;uint64_t origin;unsigned retries;};
'''+policy+r'''
#define CHECK(x) do{if(!(x)){fprintf(stderr,"budget capacity=%u line=%u: %s\n",MKW_LAN_CAPACITY,__LINE__,#x);abort();}}while(0)
int main(void){
    struct dwc_gamedata g={3,6000,0x123456789abcdef0ULL,4};
    for(unsigned live=1;live<=33;live++){
        for(unsigned i=0;i<32;i++){tracked_peers[i].info.status=i<live-1;tracked_peers[i].last_update=timestamp;}
        g.reservation_timeout_ms=6000;update_reservation_budget(&g);
        unsigned count=live<MKW_LAN_CAPACITY?live:MKW_LAN_CAPACITY;if(count>32)count=32;
        unsigned expected=6000+(count>12?(count-12)*2000:0);
        CHECK(g.reservation_timeout_ms==expected && expected<=46000);
        CHECK(g.origin==0x123456789abcdef0ULL && g.retries==4);
    }
    /* An allowance can grow as discovery completes; no timer origin is reset. */
    for(unsigned i=0;i<32;i++){tracked_peers[i].info.status=1;tracked_peers[i].last_update=timestamp;}
    g.reservation_timeout_ms=6000;update_reservation_budget(&g);unsigned extended=g.reservation_timeout_ms;
    timestamp+=301;update_reservation_budget(&g);CHECK(g.reservation_timeout_ms==extended);
    g.reservation_timeout_ms=6000;update_reservation_budget(&g);CHECK(g.reservation_timeout_ms==6000);
    for(unsigned i=0;i<32;i++)tracked_peers[i].last_update=timestamp;
    for(unsigned state=0;state<=18;state++)if(state!=3){g.dwc_match_state=state;g.reservation_timeout_ms=6000;update_reservation_budget(&g);CHECK(g.reservation_timeout_ms==6000);}
    g.dwc_match_state=3;
    for(unsigned special=0;special<6000;special+=1000){g.reservation_timeout_ms=special;update_reservation_budget(&g);CHECK(g.reservation_timeout_ms==special);}
    g.reservation_timeout_ms=90000;update_reservation_budget(&g);CHECK(g.reservation_timeout_ms==90000);
    update_reservation_budget(NULL);
}
'''
        with tempfile.TemporaryDirectory(prefix='lan-reservation-budget-') as temporary:
            path=Path(temporary);(path/'test.c').write_text(harness)
            for capacity in (12,13,24,32):
                compile = subprocess.run([os.environ.get('CC','cc'),'-std=c11','-Wall','-Werror',
                    f'-DMKW_LAN_CAPACITY={capacity}',str(path/'test.c'),'-o',str(path/'test')],capture_output=True,text=True)
                self.assertEqual(compile.returncode,0,compile.stderr)
                run = subprocess.run([str(path/'test')],capture_output=True,text=True)
                self.assertEqual(run.returncode,0,run.stderr)


if __name__=='__main__':
    unittest.main()
