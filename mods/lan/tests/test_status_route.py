"""SDK status replies must work before multicast discovery learns a new peer."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


class StatusRoute(unittest.TestCase):
    def test_real_sender_uses_sdk_endpoint_when_discovery_is_late(self):
        source=(Path(__file__).parents[1]/'source/module/network_thread.c').read_text()
        sender=source[source.index('bool network_thread_send_status_record('):source.index('void network_thread_set_mii(')]
        harness=r'''
#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdlib.h>
#include <stdio.h>
#define LOG_INFO(...) ((void)0)
#define LOG_ERROR(...) ((void)0)
#define STATUS_MAGIC 0xbb49cc4d
#define MKW_ADVERTISED_IP 0x08080808u
#define BROADCAST_PORT 27900
#define AF_INET 2
typedef int so_ret_t;
typedef struct {unsigned sa_len,sa_family,sa_port;uint32_t sa_addr;} so_addr_t;
static struct dwc_gamedata {unsigned last_enter_type,last_enter_port;uint32_t last_enter_ip,last_enter_pid,last_enter_length;uint64_t last_enter_timestamp;unsigned player_count;struct {uint32_t player_id,local_ip;} peers[MKW_LAN_MATCH_NODES];} game;
static struct dwc_gamedata *get_dwc_gamedata(void){return &game;}
static uint64_t OSGetTime(void){return 0x123456789abcdef0ULL;}
static struct {struct {uint32_t ip;unsigned communication_port;} info;} tracked_peers[2];
static uint32_t my_fake_pid=0x4a000001;
static unsigned communication_port=5555;
static int broadcast_socket=7;
static int find_peer_by_pid(uint32_t pid){return pid==2?0:pid==3?1:-1;}
static unsigned sent;static uint32_t seen_ip;static int send_result=1;
static int SOSendTo(int fd,const void *data,unsigned length,int flags,const so_addr_t *address){
    if(fd!=7 || length!=24 || flags || address->sa_family!=2 || address->sa_port!=27900)abort();
    const uint32_t *words=data;
    if(words[0]!=STATUS_MAGIC || words[1]!=__builtin_bswap32(90) || words[3]!=MKW_ADVERTISED_IP || words[4]!=__builtin_bswap32(my_fake_pid) || words[5]!=0x12345678)abort();
    ++sent;seen_ip=address->sa_addr;return send_result;
}
'''+sender+r'''
#define CHECK(x) do{if(!(x)){fprintf(stderr,"status route line %u: %s\n",__LINE__,#x);abort();}}while(0)
int main(void){
    const uint32_t packet=0x12345678;tracked_peers[0].info.ip=0x0a000002;tracked_peers[0].info.communication_port=6000;
    CHECK(network_thread_send_status_record(1,2,0x01020304,7000,&packet,1) && sent==1 && seen_ip==0x0a000002);
    CHECK(network_thread_send_status_record(1,99,0x0a000063,7000,&packet,1) && sent==2 && seen_ip==0x0a000063);
    CHECK(network_thread_send_status_record(1,3,0x0a000003,7000,&packet,1) && sent==3 && seen_ip==0x0a000003);
    game.player_count=1;game.peers[0].player_id=4;game.peers[0].local_ip=0x0a000004;
    CHECK(network_thread_send_status_record(1,4,MKW_ADVERTISED_IP,7000,&packet,1) && sent==4 && seen_ip==0x0a000004);
    CHECK(!network_thread_send_status_record(1,99,MKW_ADVERTISED_IP,7000,&packet,1) && sent==4);
    for(unsigned i=0;i<3;i++){
        uint32_t ip=i==0?0:i==1?0xefff4d4b:0xffffffff;
        CHECK(!network_thread_send_status_record(1,99,ip,7000,&packet,1) && sent==4);
    }
    CHECK(network_thread_send_status_record(2,99,0x0a000063,7000,&packet,1) && sent==5);
    CHECK(game.last_enter_type==2 && game.last_enter_port==7000 && game.last_enter_ip==0x0a000063 && game.last_enter_pid==99 && game.last_enter_length==1 && game.last_enter_timestamp==0x123456789abcdef0ULL);
    send_result=-1;CHECK(!network_thread_send_status_record(1,99,0x0a000063,7000,&packet,1) && sent==6);
    send_result=1;
    game.player_count=MKW_LAN_MATCH_NODES;
    game.peers[MKW_LAN_MATCH_NODES-1].player_id=1234;
    game.peers[MKW_LAN_MATCH_NODES-1].local_ip=0x0a000070;
    CHECK(network_thread_send_status_record(1,1234,MKW_ADVERTISED_IP,7000,&packet,1) && sent==7 && seen_ip==0x0a000070);
    game.player_count--;
    CHECK(!network_thread_send_status_record(1,1234,MKW_ADVERTISED_IP,7000,&packet,1) && sent==7);
    game.player_count=MKW_LAN_MATCH_NODES+1;
    CHECK(!network_thread_send_status_record(1,1235,MKW_ADVERTISED_IP,7000,&packet,1) && sent==7);
}
'''
        with tempfile.TemporaryDirectory(prefix='lan-status-route-') as temporary:
            path=Path(temporary);(path/'test.c').write_text(harness)
            for nodes in (32, 64, 128):
                with self.subTest(nodes=nodes):
                    command = [os.environ.get('CC', 'cc'), '-std=c11', '-Wall', '-Werror',
                               f'-DMKW_LAN_MATCH_NODES={nodes}', str(path/'test.c'), '-o', str(path/'test')]
                    compile = subprocess.run(command, capture_output=True, text=True)
                    self.assertEqual(compile.returncode, 0, compile.stderr)
                    run = subprocess.run([str(path/'test')], capture_output=True, text=True)
                    self.assertEqual(run.returncode, 0, run.stderr)


if __name__=='__main__':
    unittest.main()
