"""Run the production control-thread policy against SDK caller priorities."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


class ControlPriorityTests(unittest.TestCase):
    def test_expanded_service_shares_sdk_priority_without_preempting_owner(self):
        source = (Path(__file__).parents[1] / 'source/module/main.c').read_text()
        begin = source.index('static int lan_control_priority(')
        end = source.index('static int MyDWC_LoginAsync(', begin)
        harness = r'''
#include <stdbool.h>
#include <stddef.h>
typedef struct { int priority; } OSThread_t;
static OSThread_t owner;
static bool present = true;
static OSThread_t *OSGetCurrentThread(void) { return present ? &owner : NULL; }
static int OSGetThreadPriority(OSThread_t *thread) { return thread->priority; }
'''+source[begin:end]+r'''
int main(void) {
    for(int priority=-1;priority<=32;priority++) {
        owner.priority=priority;
        int selected=lan_control_priority();
        if(selected<0 || selected>31) return 1;
        if(MKW_LAN_CAPACITY<=12 && selected!=24) return 2;
        if(MKW_LAN_CAPACITY>12 && priority>=0 && priority<24 && selected!=priority) return 3;
        if(MKW_LAN_CAPACITY>12 && priority>=0 && selected<priority && priority<24) return 4;
        if((priority<0 || priority>=24) && selected!=24) return 5;
    }
    present=false;
    if(lan_control_priority()!=24) return 6;
}
'''
        with tempfile.TemporaryDirectory(prefix='lan-control-priority-') as directory:
            path=Path(directory)
            (path/'test.c').write_text(harness)
            for capacity in (12,13,24,32):
                with self.subTest(capacity=capacity):
                    subprocess.run([os.environ.get('CC','cc'),'-std=c11','-Wall',
                        '-Wextra','-Werror',f'-DMKW_LAN_CAPACITY={capacity}',
                        str(path/'test.c'),'-o',str(path/'test')],check=True,capture_output=True)
                    subprocess.run([str(path/'test')],check=True,timeout=3)


if __name__ == '__main__':
    unittest.main()
