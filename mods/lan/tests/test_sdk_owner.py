"""Keep next-admission controls outside an SDK update that can yield on I/O."""
from pathlib import Path
import os
import subprocess
import tempfile
import unittest


class SdkOwnerTests(unittest.TestCase):
    def test_background_discovery_cannot_reverse_barrier_stop_resume_order(self):
        module=Path(__file__).parents[1]/'source/module'
        source=(module/'network_thread.c').read_text()
        begin=source.index('void network_thread_poll_sdk(')
        helpers=source[begin:source.index('void* network_thread_main(',begin)]
        main=(module/'main.c').read_text()
        begin=main.index('static void Mydwc_think(')
        wrapper=main[begin:main.index('\n}',begin)+2]
        harness=r'''
#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>
#define BROADCAST_TRACK_MAX 32
#define BROADCAST_TIMEOUT 300
static bool network_thread_running=true,sdk_service_ready=true,sdk_polling;
static uint64_t timestamp;
static uint64_t OSGetTime(void){return 1000;}
static uint64_t lan_discovery_time(uint64_t ticks){return ticks;}
static int multicast_socket=2;
static struct {uint64_t last_update;struct {unsigned status;} info;} tracked_peers[32];
static unsigned sdk_state=18,sdk_checks,discovery,updates,event_count,events[4];
static bool pending=true,reenter;
void network_thread_poll_sdk(void);
static void network_thread_poll_data(void){}
static void service_network_thread(void);
static void check_for_status_or_broadcast(void){
    sdk_checks++;
    if(reenter)network_thread_poll_sdk();
    if(sdk_state==12&&pending){events[event_count++]=1;pending=false;}
}
static void receive_packets(int socket,bool status){if(socket!=2||status)abort();discovery++;}
static void dwc_think(int argument){
    if(argument!=12345)abort();
    updates++;
    sdk_state=12; // SDK publishes idle before finishing its socket writes.
    service_network_thread(); // Voluntary SDK I/O yield runs background work.
    events[event_count++]=0; // Previous barrier's final resume control.
}
'''+helpers+wrapper+r'''
#define CHECK(x) do{if(!(x))abort();}while(0)
int main(void){
    Mydwc_think(12345);CHECK(updates==1&&event_count==2);
    if(MKW_LAN_CAPACITY<=12){
        CHECK(!discovery&&sdk_checks==1&&events[0]==1&&events[1]==0);return 0;
    }
    CHECK(discovery==1&&sdk_checks==2&&events[0]==0&&events[1]==1&&!sdk_polling);
    // Starting/stopping discovery must not expose a blocking/unbound socket.
    sdk_checks=0;sdk_service_ready=false;network_thread_poll_sdk();CHECK(!sdk_checks);
    sdk_service_ready=true;network_thread_running=false;network_thread_poll_sdk();CHECK(!sdk_checks);
    network_thread_running=true;
    // Nested callbacks cannot recursively dispatch another SDK update.
    reenter=true;pending=true;sdk_checks=event_count=0;network_thread_poll_sdk();
    CHECK(sdk_checks==1&&event_count==1&&!sdk_polling);
}
'''
        with tempfile.TemporaryDirectory(prefix='lan-sdk-owner-') as directory:
            path=Path(directory);(path/'test.c').write_text(harness)
            for capacity in (12,13,24,32):
                result=subprocess.run([os.environ.get('CC','cc'),'-std=c11','-Wall','-Wextra','-Werror',
                    f'-DMKW_LAN_CAPACITY={capacity}',str(path/'test.c'),'-o',str(path/'test')],capture_output=True,text=True)
                self.assertEqual(result.returncode,0,result.stderr)
                subprocess.run([str(path/'test')],check=True,timeout=3)


if __name__=='__main__':
    unittest.main()
