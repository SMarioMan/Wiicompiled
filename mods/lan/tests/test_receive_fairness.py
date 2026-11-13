"""Execute the real receive loop against continuously readable sockets."""
import os
from pathlib import Path
import re
import subprocess
import tempfile
import unittest


class ReceiveFairness(unittest.TestCase):
    def test_busy_socket_yields_and_counts_malformed_packets(self):
        source = (Path(__file__).parents[1] / 'source/module/network_thread.c').read_text()
        begin = source.index('static void receive_packets(')
        end = source.index('static void check_for_status_or_broadcast(', begin)
        limit = re.search(r'^#define RECEIVE_BATCH_MAX (\d+)$', source, re.M)
        self.assertIsNotNone(limit)
        harness = r'''
#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>
typedef int so_fd_t;
typedef int so_ret_t;
typedef struct { unsigned sa_len; } so_addr_t;
struct broadcast_packet { uint32_t magic; uint32_t payload; };
#define STATUS_SIZE_MAX 512
#define STATUS_MAGIC 0xbb49cc4d
#define BROADCAST_MAGIC 0x524d4358
#define LOG_ERROR(...) ((void)0)
static unsigned calls, statuses, broadcasts;
static int mode;
static so_ret_t SORecvFrom(so_fd_t fd,void *buffer,unsigned size,int flags,so_addr_t *address) {
    (void)fd;(void)size;(void)flags;(void)address;
    ++calls;
    if(mode==3)return -1;
    if(mode==4 && calls==3)return -1;
    ((uint32_t *)buffer)[0]=mode==0?STATUS_MAGIC:BROADCAST_MAGIC;
    return mode==2?1:sizeof(struct broadcast_packet);
}
static void process_status(uint32_t *buffer,int32_t length,so_addr_t address) {
    (void)buffer;(void)length;(void)address;++statuses;
}
static void process_broadcast(struct broadcast_packet packet,so_addr_t address) {
    (void)packet;(void)address;++broadcasts;
}
'''
        harness += f'\n#define RECEIVE_BATCH_MAX {limit[1]}\n' + source[begin:end]
        harness += r'''
int main(void) {
    for(mode=0;mode<5;mode++) {
        calls=statuses=broadcasts=0;
        receive_packets(1,true);
        unsigned expected=mode==3?1:mode==4?3:RECEIVE_BATCH_MAX;
        if(calls!=expected)return 1;
        if(statuses!=(mode==0?expected:0))return 2;
        if(broadcasts!=(mode==1?expected:mode==4?2:0))return 3;
    }
    mode=0;calls=statuses=broadcasts=0;
    receive_packets(2,false);
    if(calls!=RECEIVE_BATCH_MAX || statuses || broadcasts)return 4;
    return 0;
}
'''
        with tempfile.TemporaryDirectory(prefix='lan-receive-') as directory:
            path=Path(directory);(path/'test.c').write_text(harness)
            subprocess.run([os.environ.get('CC','cc'),'-std=c11','-Wall','-Wextra','-Werror',str(path/'test.c'),'-o',str(path/'test')],check=True,capture_output=True)
            subprocess.run([str(path/'test')],check=True,timeout=3)


if __name__ == '__main__':
    unittest.main()
