using System.Diagnostics;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class BattleSoundHeapTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(128)]
    [Trait("Requires", "USA game assets and Clang")]
    public void OriginalChildHeapCreationAndDestructionPreserveTheSelectedParent(int capacity)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName??throw new DirectoryNotFoundException("Repository root missing");
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        uint Word(uint address)=>System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(address,4),4));
        // Pin the caller ABI and sound-size table independently of the native wrapper.
        Assert.Equal(0x80840c98u,Word(0x8054e2b0));Assert.Equal(0x4bcd848du,Word(0x8054e2b8));
        Assert.Equal(0x907f0c70u,Word(0x8054e2bc));Assert.Equal(0x700000u,Word(0x8089ccb0));
        var entries=new uint[]{0x80226744,0x80226810,0x80229750};
        var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));
        var translator=new FunctionTranslator(image);
        var temp=Path.Combine(Path.GetTempPath(),"mkw-battle-sound-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try {
            var sources=new List<string>{Path.Combine(root,"runtime/src/hle/battle_sound_heap.cpp"),Path.Combine(root,"runtime/src/guest_region.cpp")};
            foreach(var pc in entries){var file=Path.Combine(temp,$"{pc:X8}.cpp");
                File.WriteAllText(file,translator.Translate(pc,new TranslationOptions(EnableLeafInlining:false,KnownFunctionEntryPoints:map.Addresses.ToHashSet())).CxxCode);sources.Add(file);}
            var declarations=string.Join("\n",entries.Select(pc=>$"extern \"C\" void func_{pc:X8}(CpuContext*);"));
            var registrations=string.Join("\n",entries.Select(pc=>$"entry(0x{pc:X8},func_{pc:X8});"));
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include "mkw_guest_layout.h"
                #include <cstdio>
                #include <cstring>
                #include <vector>
                {{declarations}}
                extern "C" void ExpHeap_CreateBattleSoundHeap(CpuContext*);
                namespace MkwGuestLayout {const Layout kLayout{.playerCapacity={{capacity}}};}
                constexpr uint32_t scene=0x81001000,primary=0x81003000,secondary=0x81004000,
                    parentVt=0x81005000,childVt=0x81006000,first=0x81200000,second=0x81400000,sp=0x81f00000,
                    bytes=0x700100,caller=0x8054e2bc;
                bool rejectPrimary=false,rejectSecondary=false,rejectConstruction=false;
                uint32_t live=0,constructed=0,destroyed=0,freed=0;
                std::vector<uint32_t> requests;
                void entry(uint32_t address,void(*fn)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="sound heap owner fixture";
                    info.rawCpuInvoker=fn;info.entryPoint=reinterpret_cast<void*>(fn);TranslatedFunctionRegistry::Register(info);}
                void allocate(CpuContext* c){
                    auto parent=c->gpr[3];if((parent!=primary&&parent!=secondary)||c->gpr[4]<bytes||c->gpr[5]!=4)std::abort();
                    requests.push_back(parent);
                    const bool reject=parent==primary?rejectPrimary:rejectSecondary;
                    c->gpr[3]=reject?0:parent==primary?first:second;
                    if(!reject)++live;
                    c->gpr[4]=0xdead;c->gpr[5]=0xbeef;
                }
                void construct(CpuContext* c){
                    if(c->gpr[4]<bytes||c->gpr[5]!=0)std::abort();
                    if(rejectConstruction){c->gpr[3]=0;return;}
                    Memory::Write32(c->gpr[3],childVt);++constructed;
                }
                void release(CpuContext* c){
                    if(!live||c->gpr[4]!=(c->gpr[3]==primary?first:second))std::abort();--live;++freed;
                }
                void destruct(CpuContext* c){if(int32_t(c->gpr[4])!=-1)std::abort();++destroyed;}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    entry(0x80001000,allocate);entry(0x80001004,release);entry(0x80001008,construct);entry(0x8000100c,destruct);
                    // Use the real child factory and destroy routines; parent allocator,
                    // internal heap construction and virtual destructor are callbacks.
                    entry(0x80226698,construct);
                    Memory::Write32(primary,parentVt);Memory::Write32(secondary,parentVt);
                    Memory::Write32(parentVt+0x14,0x80001000);Memory::Write32(parentVt+0x18,0x80001004);
                    Memory::Write32(childVt+8,0x8000100c);
                    CpuContext c{};CpuContextScope scope(&c);c.gpr[1]=sp;
                    c.gpr[2]=0x8038ac20;c.gpr[13]=0x80388880;
                    for(uint32_t r=14;r<32;r++)c.gpr[r]=0x12340000+r;c.gpr[31]=scene;
                    for(uint32_t scenario=0;scenario<10;scenario++)for(uint32_t lifetime=0;lifetime<2;lifetime++){
                        requests.clear();rejectPrimary=scenario==1;rejectSecondary=scenario==8;rejectConstruction=scenario==9;
                        Memory::Write32(scene+0xc88,scenario==5?0:scenario==6?secondary:primary);
                        Memory::Write32(scene+0xc8c,secondary);Memory::Write32(scene+0xc98,scenario==7?primary:secondary);
                        c.gpr[3]=scenario==3?bytes+0x300000:bytes;c.gpr[4]=secondary;c.gpr[5]=0;
                        c.lr=scenario==2?caller+4:caller;
                        // Scenario 4 uses another preserved scene register, with no owner.
                        c.gpr[31]=scenario==4?scene+0x1000:scene;
                        ExpHeap_CreateBattleSoundHeap(&c);
                        const bool routed={{capacity}}>12&&scenario!=2&&scenario!=3&&scenario!=4&&scenario!=5&&scenario!=6&&scenario!=7;
                        const bool success=scenario!=9&&(scenario!=8||routed);
                        uint32_t owner=routed&&scenario!=1&&scenario!=9?primary:secondary;
                        if(requests.size()!=(routed&&(scenario==1||scenario==9)?2u:1u))return 4;
                        if(requests.front()!=(routed?primary:secondary)||requests.back()!=owner)return 5;
                        if(bool(c.gpr[3])!=success)return 6;
                        if(success){auto child=c.gpr[3];if(child!=(owner==primary?first:second)||Memory::Read32(child+0x18)!=owner)return 7;
                            func_80226810(&c);}
                        if(live||Memory::Read32(scene+0xc8c)!=secondary||Memory::Read32(scene+0xc88)!=(scenario==5?0:scenario==6?secondary:primary))return 8;
                        if(c.gpr[1]!=sp||c.gpr[31]!=(scenario==4?scene+0x1000:scene))return 9;
                        for(uint32_t r=14;r<31;r++)if(c.gpr[r]!=0x12340000+r)return 10;
                    }
                    return constructed==destroyed&&freed>=destroyed?0:11;
                }
                """);
            var args=TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false);
            var support=Path.Combine(temp,"translator_test_support.cpp");
            File.WriteAllText(support,File.ReadAllText(support).Replace("const uint8_t kMkwGuestRegion = 0;","const uint8_t kMkwGuestRegion = 1;",StringComparison.Ordinal));
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",args){RedirectStandardError=true})!){
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);if(!done)compile.Kill(entireProcessTree:true);
                Assert.True(done,"Battle sound heap compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());}
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);if(!finished)run.Kill(entireProcessTree:true);
            Assert.True(finished,"Battle sound heap execution timed out");Assert.True(run.ExitCode==0,$"Battle sound heap returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }finally{Directory.Delete(temp,recursive:true);}
    }
}
