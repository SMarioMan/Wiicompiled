"""Learn a status sender's real endpoint without fabricating discovery/Mii presence."""
import os
from pathlib import Path
import re
import subprocess
import tempfile
import unittest


class StatusLearning(unittest.TestCase):
    def test_actual_learning_requires_consistent_sender_and_preserves_presence(self):
        module = Path(__file__).parents[1] / 'source/module'
        source = (module / 'network_thread.c').read_text()
        header = (module / 'network_thread.h').read_text()
        capacity = re.search(r'^#define BROADCAST_TRACK_MAX .*$', source, re.M)[0]
        peer_info = re.search(r'struct peer_info \{.*?\};', header, re.S)[0]
        table = re.search(r'static struct tracked_peer \{.*?\} tracked_peers\[BROADCAST_TRACK_MAX\];', source, re.S)[0]
        lookup = source[source.index('static int find_or_alloc_peer('):source.index('static void process_broadcast(')]
        learn = source[source.index('static void learn_status_peer('):source.index('/* The SDK accepts one server reservation')]
        harness=r'''
#include <stdint.h>
#include <stdlib.h>
#include <stdio.h>
#define MII_SIZE 0x4c
typedef struct {uint32_t sa_addr;uint16_t sa_port;} so_addr_t;
static uint64_t timestamp=777;
static uint32_t local_ip=0x7f000001;
''' + '\n'.join((capacity, peer_info, table, lookup, learn)) + r'''
#define CHECK(x) do{if(!(x)){fprintf(stderr,"status learning line %u: %s\n",__LINE__,#x);abort();}}while(0)
int main(void){
    _Static_assert(BROADCAST_TRACK_MAX == EXPECTED_SLOTS, "discovery slot count");
    uint32_t words[5]={0, __builtin_bswap32(90), (1u<<24)|6789,0,__builtin_bswap32(0x7f000002)};
    so_addr_t addr={0x7f000002,27900};
    for(unsigned bad=0;bad<6;bad++){
        uint32_t version=words[1],command=words[2],pid=words[4];so_addr_t a=addr;
        if(bad==0)words[1]=0;if(bad==1)words[2]&=0xffff0000;
        if(bad==2)words[4]=__builtin_bswap32(0x7f000003);
        if(bad==3)a.sa_addr=0;if(bad==4)a.sa_addr=0xefff4d4b;if(bad==5)a.sa_addr=0x7f000001;
        learn_status_peer(words,a);CHECK(!tracked_peers[0].info.ip);
        words[1]=version;words[2]=command;words[4]=pid;
    }
    learn_status_peer(words,addr);
    CHECK(tracked_peers[0].info.ip==addr.sa_addr && tracked_peers[0].real_pid==0x7f000002);
    CHECK(tracked_peers[0].info.communication_port==6789 && tracked_peers[0].last_update==777 && !tracked_peers[0].info.status);
    words[2]=(1u<<24)|9999;timestamp=999;learn_status_peer(words,addr);
    CHECK(tracked_peers[0].info.communication_port==6789 && tracked_peers[0].last_update==777);
    for(int i=1;i<EXPECTED_SLOTS;i++){
        addr.sa_addr=0x7f000010+i;
        words[4]=__builtin_bswap32(addr.sa_addr);
        learn_status_peer(words,addr);
        CHECK(find_peer_by_pid(addr.sa_addr)==i);
        CHECK(find_peer_by_fake_pid(i+1)==i);
        CHECK(tracked_peers[i].info.ip==addr.sa_addr && tracked_peers[i].info.communication_port==9999);
    }
    addr.sa_addr++;
    words[4]=__builtin_bswap32(addr.sa_addr);
    learn_status_peer(words,addr);
    CHECK(find_peer_by_pid(addr.sa_addr)==-1);
    CHECK(find_peer_by_fake_pid(EXPECTED_SLOTS+1)==-1);
    CHECK(tracked_peers[EXPECTED_SLOTS-1].info.ip==addr.sa_addr-1);
}
'''
        with tempfile.TemporaryDirectory(prefix='lan-status-learning-') as temporary:
            path = Path(temporary)
            (path / 'test.c').write_text(harness)
            for configured, slots in [(12, 32), (32, 32), (33, 33), (128, 128)]:
                with self.subTest(configured=configured):
                    command = [os.environ.get('CC', 'cc'), '-std=c11', '-Wall', '-Werror',
                               '-Wno-misleading-indentation', f'-DMKW_LAN_CAPACITY={configured}',
                               f'-DEXPECTED_SLOTS={slots}', str(path / 'test.c'), '-o', str(path / 'test')]
                    subprocess.run(command, check=True)
                    subprocess.run([str(path / 'test')], check=True)


if __name__=='__main__':
    unittest.main()
