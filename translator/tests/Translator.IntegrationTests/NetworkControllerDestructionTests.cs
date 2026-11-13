using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class NetworkControllerDestructionTests
{
    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(32)] [InlineData(48)] [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void OriginalDestructorReleasesEveryFullPacketAndPreservesAdjacentStorage(int players)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName ?? throw new DirectoryNotFoundException();
        var manifest=Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json");
        using var doc=JsonDocument.Parse(File.ReadAllText(manifest));
        var layout=new GuestLayouts(doc.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!,players);
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));
        var translator=new FunctionTranslator(image,layoutRelocations:GuestLayoutRelocations.Read(manifest,players));
        var temp=Path.Combine(Path.GetTempPath(),"mkw-controller-destroy-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try {
            uint[] fixtures={0x80229b5c,0x80229a90,0x80241cec,0x8065a964,0x80660714};
            var source=Path.Combine(temp,"destructor.cpp");File.WriteAllText(source,translator.Translate(0x80652f94,new TranslationOptions(EnableLeafInlining:false,KnownFunctionEntryPoints:map.Addresses.ToHashSet(),LeafInliningBlockedTargets:fixtures.ToHashSet())).CxxCode);
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <cstdio>
                #include <cstring>
                #include <cstdlib>
                #include <set>
                extern "C" void func_80652F94(CpuContext*);
                constexpr uint32_t n={{players}},root=0x81010000,stack=0x81f00000,
                    size={{layout.Symbol("RKNetController.size")}},full={{layout.Symbol("RKNetController.fullSend.offset")}},
                    send={{layout.Symbol("RKNetController.splitSend.offset")}},receive={{layout.Symbol("RKNetController.splitReceived.offset")}},
                    stride={{layout.Symbol("RKNetPointerRow.size")}};
                #define CHECK(x) do{if(!(x)){std::fprintf(stderr,"controller destruction n=%u line=%d: %s\n",n,__LINE__,#x);std::abort();} }while(false)
                std::set<uint32_t> buffers,holders;unsigned rows,tasks,heaps,friends,users,roots;
                void delete_array(CpuContext* c){auto p=c->gpr[3];if(buffers.erase(p))return;
                    CHECK(p==root+send || p==root+send+stride || p==root+receive || p==root+receive+stride);++rows;}
                void delete_object(CpuContext* c){auto p=c->gpr[3];if(p==root){++roots;return;}
                    CHECK(holders.erase(p)==1 && Memory::Read32(p)==0);}
                void task(CpuContext* c){CHECK(c->gpr[3]==0x81040000);++tasks;}
                void heap(CpuContext* c){CHECK(c->gpr[3]==0x81050000);++heaps;}
                void friend_destroy(CpuContext*){++friends;}
                void user_destroy(CpuContext*){++users;}
                void entry(uint32_t pc,void(*f)(CpuContext*)){TranslatedFunctionInfo i{};i.address=pc;i.name="controller destructor fixture";i.rawCpuInvoker=f;i.entryPoint=reinterpret_cast<void*>(f);TranslatedFunctionRegistry::Register(i);}
                int main(int argc,char** argv){
                    CHECK(argc==2);Memory::Init(32*1024*1024);auto f=std::fopen(argv[1],"rb");CHECK(f);
                    CHECK(std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},f)=={{image.Memory.Length}});std::fclose(f);
                    entry(0x80229b5c,delete_array);entry(0x80229a90,delete_object);entry(0x80241cec,task);
                    entry(0x8065a964,friend_destroy);entry(0x80660714,user_destroy);entry(0x80006000,heap);
                    CpuContext c{};c.gpr[1]=stack;c.gpr[13]=0x80388880;c.gpr[2]=0x8038ac20;CpuContextScope scope(&c);
                    for(uint32_t r=14;r<32;r++)c.gpr[r]=0x12340000+r;
                    for(bool sparse:{false,true})for(unsigned deallocate=0;deallocate<2;deallocate++){
                        std::memset(Memory::GetPointer(root-16,size+32),0x55,size+32);buffers.clear();holders.clear();
                        rows=tasks=heaps=friends=users=roots=0;
                        for(uint32_t p=0;p<n;p++){
                            auto holder=0x81100000+p*32,data=0x81200000+p*256;
                            if(sparse && p%4==0){Memory::Write32(root+full+p*4,0);continue;}
                            Memory::Write32(root+full+p*4,holder);Memory::Write32(holder,data);buffers.insert(data);holders.insert(holder);
                        }
                        Memory::Write32(root+0x24,0x81040000);Memory::Write32(root+0x20,0x81050000);
                        Memory::Write32(0x81050000,0x81060000);Memory::Write32(0x8106001c,0x80006000);
                        c.gpr[3]=root;c.gpr[4]=deallocate?1:0;func_80652F94(&c);
                        CHECK(buffers.empty() && holders.empty() && rows==4 && tasks==1 && heaps==1 && friends==1 && users==1 && roots==deallocate && c.gpr[3]==root);
                        for(unsigned i=0;i<16;i++)CHECK(Memory::Read8(root-16+i)==0x55 && Memory::Read8(root+size+i)==0x55);
                        CHECK(c.gpr[1]==stack);for(uint32_t r=14;r<32;r++)CHECK(c.gpr[r]==0x12340000+r);
                    }
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",TranslatorCppTestHarness.BuildCompileArguments(root,temp,new[]{source},harness,exe,includeDataSections:false)){RedirectStandardError=true})!) {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(45000);if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Destructor compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){RedirectStandardError=true})!;var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Destructor execution timed out");Assert.True(run.ExitCode==0,$"Controller destructor exited {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        } finally {Directory.Delete(temp,recursive:true);}
    }
}
