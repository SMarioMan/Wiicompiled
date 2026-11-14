"""Discovery intervals and peer ages must not depend on thread polling speed."""
from pathlib import Path
import os
import subprocess
import tempfile
import unittest


class DiscoveryClockTests(unittest.TestCase):
    def test_sdk_clock_drives_discovery_without_catchup_bursts(self):
        source = (Path(__file__).parents[1] / 'source/module/network_thread.c').read_text()
        begin = source.index('static bool broadcast_due(')
        end = source.index('static bool compute_and_broadcast_status(', begin)
        harness = r'''
#include <stdbool.h>
#include <stdint.h>
#define BROADCAST_INTERVAL 60
#define secs_to_ticks(sec) ((sec)*60750000u)
static uint64_t clock_ticks, timestamp, broadcast_timer;
static uint64_t OSGetTime(void) { return clock_ticks; }
'''+source[begin:end]+r'''
int main(void) {
    // Match initialise(): the first advertisement is immediately due.
    clock_ticks=secs_to_ticks(100ULL);
    timestamp=clock_ticks/(secs_to_ticks(1)/BROADCAST_INTERVAL);
    broadcast_timer=timestamp;
    if(!broadcast_due() || timestamp!=6000) return 1;
    // Busy polling is not elapsed time, including automation's startup loop.
    for(unsigned i=0;i<10000;i++) if(broadcast_due()) return 2;
    clock_ticks+=secs_to_ticks(1)-1;
    if(broadcast_due() || timestamp!=6059) return 3;
    clock_ticks++;
    if(!broadcast_due() || timestamp!=6060) return 4;
    // A slow frame still advertises on its next poll, without a burst of
    // stale advertisements. Peer expiry advances by the same clock.
    clock_ticks+=secs_to_ticks(10ULL);
    if(!broadcast_due() || timestamp!=6660) return 5;
    if(broadcast_due()) return 6;
    // SDK time can exceed the 32-bit tick rollover after about 71 seconds.
    clock_ticks=secs_to_ticks(1000000ULL);
    if(!broadcast_due() || timestamp!=60000000) return 7;
    uint64_t received=timestamp;
    clock_ticks+=secs_to_ticks(5ULL);
    broadcast_due();
    if(timestamp-received!=300) return 8;
}
'''
        with tempfile.TemporaryDirectory(prefix='lan-discovery-clock-') as directory:
            path = Path(directory)
            (path / 'test.c').write_text(harness)
            subprocess.run([os.environ.get('CC', 'cc'), '-std=c11', '-Wall', '-Wextra',
                '-Werror', str(path / 'test.c'), '-o', str(path / 'test')],
                check=True, capture_output=True)
            subprocess.run([str(path / 'test')], check=True, timeout=3)


if __name__ == '__main__':
    unittest.main()
