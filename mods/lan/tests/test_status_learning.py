"""Learn a status sender's real endpoint without fabricating discovery/Mii presence."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


class StatusLearning(unittest.TestCase):
    def test_actual_learning_requires_consistent_sender_and_preserves_presence(self):
        source=(Path(__file__).parents[1]/'source/module/network_thread.c').read_text()
        helper=source[source.index('static void learn_status_peer('):source.index('/* The SDK accepts one server reservation')]
        harness=r'''
#include <stdint.h>
#include <stdlib.h>
#include <stdio.h>
#include <string.h>
typedef struct {uint32_t sa_addr;uint16_t sa_port;} so_addr_t;
static uint64_t timestamp=777;
static struct {uint64_t last_update;uint32_t real_pid;struct {uint32_t ip;unsigned communication_port,status;} info;} tracked_peers[4];
static int find_peer_by_pid(uint32_t pid){for(unsigned i=0;i<4;i++)if(tracked_peers[i].real_pid==pid)return i;return -1;}
static int find_or_alloc_peer(const so_addr_t *a){if(a->sa_addr==0x7f000001)return -1;for(unsigned i=0;i<4;i++)if(!tracked_peers[i].info.ip){tracked_peers[i].info.ip=a->sa_addr;return i;}return -1;}
'''+helper+r'''
#define CHECK(x) do{if(!(x)){fprintf(stderr,"status learning line %u: %s\n",__LINE__,#x);abort();}}while(0)
int main(void){
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
    for(unsigned i=1;i<4;i++)tracked_peers[i].info.ip=0x7f000010+i;
    words[4]=__builtin_bswap32(0x7f000020);addr.sa_addr=0x7f000020;learn_status_peer(words,addr);
    CHECK(find_peer_by_pid(0x7f000020)==-1);
}
'''
        with tempfile.TemporaryDirectory(prefix='lan-status-learning-') as temporary:
            path=Path(temporary);(path/'test.c').write_text(harness)
            compile=subprocess.run([os.environ.get('CC','cc'),'-std=c11','-Wall','-Werror','-Wno-misleading-indentation',str(path/'test.c'),'-o',str(path/'test')],capture_output=True,text=True)
            self.assertEqual(compile.returncode,0,compile.stderr)
            run=subprocess.run([str(path/'test')],capture_output=True,text=True)
            self.assertEqual(run.returncode,0,run.stderr)


if __name__=='__main__':
    unittest.main()
