using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class PositionTrackingTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealPositionTrackingSortsAllParticipantsAndPreservesItsExpandedFrame(int players)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var manifest=Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json");
        using var doc=JsonDocument.Parse(File.ReadAllText(manifest));
        var layout=new GuestLayouts(doc.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!,players);
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));
        var options=new TranslationOptions(EnableLeafInlining:false,KnownFunctionEntryPoints:map.Addresses.ToHashSet());
        var translator=new FunctionTranslator(image,layoutRelocations:GuestLayoutRelocations.Read(manifest,players));
        var temp=Path.Combine(Path.GetTempPath(),"mkw-ai-objects-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var entries=new uint[] {0x8052eb90,0x80020a20,0x80020a6c};
            var sources=new List<string>();
            foreach(var pc in entries)
            {
                var file=Path.Combine(temp,$"{pc:X8}.cpp");File.WriteAllText(file,translator.Translate(pc,options).CxxCode);sources.Add(file);
            }
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var declarations=string.Join("\n",entries.Select(pc=>$"extern \"C\" void func_{pc:X8}(CpuContext*);"));
            var registrations=string.Join("\n",entries.Select(pc=>$"register_entry(0x{pc:X8},func_{pc:X8});"));
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <array>
                #include <cstdlib>
                #include <cstring>
                #include <cstdio>
                #include <algorithm>
                {{declarations}}
                constexpr uint32_t root=0x81000000,info=0x81100000,gm=0x81110000,array=0x81120000,
                    ids=0x81130000,nodes=0x81200000,timers=0x81300000,sp=0x81f00000,n={{players}},frame={{layout.Symbol("PositionTrackingFrame.size")}};
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="position tracking";
                    info.rawCpuInvoker=entry;info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);
                    FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    const auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    Memory::Write32(0x809b8f68,root);Memory::Write8(root+0x24,n);
                    Memory::Write32(0x809b8f70,info);Memory::Write32(gm+4,info);
                    Memory::Write32(info+0xc,array);Memory::Write32(info+0x18,ids);
                    for(uint32_t i=0;i<16;i++){Memory::Write8(sp-frame-16+i,0x55);Memory::Write8(sp+8+i,0x55);
                        Memory::Write8(ids-16+i,0x55);Memory::Write8(ids+n+i,0x55);}
                    CpuContext ctx{};ctx.gpr[1]=sp;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                    for(uint32_t mode=0;mode<4;mode++){
                        std::array<uint32_t,n> expected{};std::array<double,n> keys{};
                        for(uint32_t i=0;i<n;i++){
                            const uint32_t p=nodes+i*0x100,t=timers+i*16;
                            const bool finished=mode==1 || (mode==2 && i%2==0);
                            const float completion=mode==3 ? float(i%3) : float(i);
                            expected[i]=i;keys[i]=finished ? 1000000.0-i : completion;
                            Memory::Write32(array+i*4,p);Memory::Write8(p+0x20,i+1);
                            uint32_t bits;std::memcpy(&bits,&completion,4);Memory::Write32(p+0xc,bits);
                            Memory::Write32(p+0x38,finished?2:0);Memory::Write32(p+0x40,t);
                            Memory::Write16(t+4,0);Memory::Write8(t+6,0);Memory::Write16(t+8,i);
                        }
                        std::stable_sort(expected.begin(),expected.end(),[&](auto a,auto b){return keys[a]>keys[b];});
                        for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                        for(uint32_t r=14;r<32;r++)ctx.fpr[r].raw=0x1234000000000000ull+r;
                        ctx.gpr[3]=gm;func_8052EB90(&ctx);
                        for(uint32_t i=0;i<n;i++)if(Memory::Read8(ids+i)!=expected[i] ||
                            Memory::Read8(nodes+expected[i]*0x100+0x20)!=i+1)return 4+mode;
                        if(ctx.gpr[1]!=sp)return 8;
                        for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r || ctx.fpr[r].raw!=0x1234000000000000ull+r)return 9;
                        for(uint32_t i=0;i<16;i++)if(Memory::Read8(sp-frame-16+i)!=0x55 || Memory::Read8(sp+8+i)!=0x55 ||
                            Memory::Read8(ids-16+i)!=0x55 || Memory::Read8(ids+n+i)!=0x55)return 10;
                    }
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(45000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Position tracking compilation timed out");
                Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(5000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Position tracking execution timed out");
            Assert.True(run.ExitCode==0,$"Position tracking lifecycle returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
