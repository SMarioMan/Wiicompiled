"""Keep the expanded SDK service runnable ahead of long UI/render work."""
from pathlib import Path
import os
import subprocess
import tempfile
import unittest


class SdkPriorityTests(unittest.TestCase):
    def test_only_observed_expanded_sdk_owner_is_raised_ahead_of_main(self):
        source=(Path(__file__).parents[1]/'source/module/main.c').read_text()
        marker='static void prioritize_lan_sdk_owner('
        begin=source.index(marker)
        helper=source[begin:source.index('\n}',begin)+2]
        harness=r'''
#include <stdbool.h>
#include <stddef.h>
#include <stdlib.h>
#include <stdio.h>
typedef struct {int priority;} OSThread_t;
static OSThread_t owner;
static bool present=true;static unsigned changes;
static OSThread_t *OSGetCurrentThread(void){return present?&owner:NULL;}
static int OSGetThreadPriority(OSThread_t *t){return t->priority;}
static bool OSSetThreadPriority(OSThread_t *t,int priority){if(t!=&owner)abort();t->priority=priority;changes++;return true;}
'''+helper+r'''
#define CHECK(x) do{if(!(x)){fprintf(stderr,"SDK priority cap=%u input=%d got=%d changes=%u line=%d\n",MKW_LAN_CAPACITY,priority,owner.priority,changes,__LINE__);abort();}}while(0)
int main(void){
    for(int priority=-1;priority<=32;priority++){
        present=true;changes=0;owner.priority=priority;
        prioritize_lan_sdk_owner();
        if(MKW_LAN_CAPACITY>12 && priority==17){
            CHECK(owner.priority==15&&changes==1);
            CHECK(owner.priority<16); // Main/render work must not starve a ready SDK owner.
            prioritize_lan_sdk_owner();CHECK(changes==1);
        }else CHECK(owner.priority==priority&&!changes);
        present=false;changes=0;prioritize_lan_sdk_owner();CHECK(!changes);
    }
}
'''
        with tempfile.TemporaryDirectory(prefix='lan-sdk-priority-') as directory:
            path=Path(directory);(path/'test.c').write_text(harness)
            for capacity in (12,13,24,32):
                result=subprocess.run([os.environ.get('CC','cc'),'-std=c11','-Wall','-Wextra','-Werror',
                    '-Wno-unused-function',f'-DMKW_LAN_CAPACITY={capacity}',str(path/'test.c'),'-o',str(path/'test')],capture_output=True,text=True)
                self.assertEqual(result.returncode,0,result.stderr)
                result=subprocess.run([str(path/'test')],capture_output=True,text=True,timeout=3)
                self.assertEqual(result.returncode,0,result.stderr)


if __name__=='__main__':
    unittest.main()
