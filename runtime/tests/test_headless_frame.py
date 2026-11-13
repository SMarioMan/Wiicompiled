"""Check ownership and GX completion in the actual frame-ending helper."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


class HeadlessFrameTests(unittest.TestCase):
    def test_both_worker_paths_finish_gx_and_release_hidden_overlay(self):
        source=(Path(__file__).parents[1]/'include/aurora_events.h').read_text()
        begin=source.index('inline void EndAuroraFrame()')
        end=source.index('\n}\n',begin)+3
        harness=r'''
#include <atomic>
#include <cstdlib>
#include <initializer_list>
struct AuroraImguiFrame {};
static AuroraImguiFrame token;
static bool owned=false,defer=false;
static unsigned frozen=0,released=0,gxFinished=0,directFinished=0,workerCalls=0;
inline std::atomic_bool g_auroraRenderHostUi{true};
static void require(bool value){if(!value)std::abort();}
static AuroraImguiFrame* aurora_end_frame_window(){require(!owned);owned=true;++frozen;return &token;}
static void aurora_release_imgui_frame(AuroraImguiFrame* frame){if(frame){require(frame==&token && owned);owned=false;++released;}}
static void aurora_end_frame_gx(AuroraImguiFrame* frame){++gxFinished;aurora_release_imgui_frame(frame);}
static void aurora_end_frame(){++directFinished;++gxFinished;}
static void SetAuroraFrameGxBegun(bool value){require(!value);}
static void GxWorkerEndFrame(AuroraImguiFrame* frame){++workerCalls;aurora_end_frame_gx(frame);}
namespace GxWorker {
static bool ShouldDefer(){return defer;}
template<class F> void Submit(F f){f();}
static void Sync(){}
}
'''+source[begin:end]+r'''
int main(){
 for(bool visible:{false,true})for(bool worker:{false,true}){
  defer=worker;g_auroraRenderHostUi.store(visible);
  frozen=released=gxFinished=directFinished=workerCalls=0;
  EndAuroraFrame();
  require(!owned && gxFinished==1 && workerCalls==unsigned(worker));
  require(directFinished==unsigned(visible && !worker));
  require(frozen==unsigned(!visible || worker) && released==frozen);
 }
}
'''
        with tempfile.TemporaryDirectory(prefix='headless-frame-') as directory:
            path=Path(directory);(path/'test.cpp').write_text(harness)
            compiled=subprocess.run([os.environ.get('CXX','c++'),'-std=c++20','-Wall','-Wextra','-Werror',str(path/'test.cpp'),'-o',str(path/'test')],capture_output=True,text=True)
            self.assertEqual(compiled.returncode,0,compiled.stderr)
            subprocess.run([str(path/'test')],check=True,timeout=3)


if __name__=='__main__':
    unittest.main()
