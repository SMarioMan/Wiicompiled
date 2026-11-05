using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class AiOrderStorageTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealAiRankSortersUseUniformScratchAndPreserveLocalParticipantLimits(int players)
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
            var entries=new uint[] {0x807326b8,0x80732818};
            var sources=new List<string>();
            foreach(var pc in entries)
            {
                var file=Path.Combine(temp,$"{pc:X8}.cpp");File.WriteAllText(file,translator.Translate(pc,options).CxxCode);sources.Add(file);
            }
            var declarations=string.Join("\n",entries.Select(pc=>$"extern \"C\" void func_{pc:X8}(CpuContext*);"));
            var registrations=string.Join("\n",entries.Select(pc=>$"register_entry(0x{pc:X8},func_{pc:X8});"));
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <array>
                #include <cstdlib>
                #include <cstring>
                {{declarations}}
                constexpr uint32_t data=0x80010000,actors=0x80040000,sp=0x800f0000;
                constexpr uint32_t n={{players}},size={{layout.Symbol("AIVsSub.size")}},frame={{layout.Symbol("AIOrderFrame.size")}};
                constexpr uint32_t cpu={{layout.Symbol("AIVsSub.cpuPairs.offset")}},local={{layout.Symbol("AIVsSub.localPairs.offset")}};
                bool guards(){
                    for(uint32_t i=0;i<16;i++)if(Memory::Read8(data-16+i)!=0x55 || Memory::Read8(data+size+i)!=0x55 ||
                        Memory::Read8(sp-frame-16+i)!=0x55 || Memory::Read8(sp+i)!=0x55)return false;
                    return true;
                }
                int main(){
                    Memory::Init(16*1024*1024);
                    std::memset(Memory::GetPointer(data,size),0x7e,size);
                    for(uint32_t i=0;i<16;i++){Memory::Write8(data-16+i,0x55);Memory::Write8(data+size+i,0x55);
                        Memory::Write8(sp-frame-16+i,0x55);Memory::Write8(sp+i,0x55);}
                    CpuContext ctx{};ctx.gpr[1]=sp;for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                    CpuContextScope scope(&ctx);
                    for(uint32_t i=0;i<n;i++){
                        Memory::Write32(data+cpu+i*8,actors+i*32);
                        Memory::Write32(actors+i*32+20,n-i);
                    }
                    Memory::Write32(data+{{layout.Symbol("AIVsSub.cpuCount.offset")}},n);
                    ctx.gpr[3]=data;func_807326B8(&ctx);
                    for(uint32_t i=0;i<n;i++)if(Memory::Read32(data+cpu+i*8+4)!=actors+(n-1-i)*32 ||
                        Memory::Read32(actors+(n-1-i)*32+20)!=i+1)return 1;
                    if(ctx.gpr[1]!=sp || !guards())return 2;
                    // Four physical participants can occupy any global rank, including n.
                    const uint32_t ranks[]={n,n-2,3,1};
                    for(uint32_t i=0;i<4;i++){
                        Memory::Write32(data+local+i*8,actors+(n+i)*32);
                        Memory::Write32(actors+(n+i)*32+20,ranks[i]);
                    }
                    Memory::Write32(data+{{layout.Symbol("AIVsSub.localCount.offset")}},4);
                    ctx.gpr[3]=data;func_80732818(&ctx);
                    for(uint32_t i=0;i<4;i++)if(Memory::Read32(data+local+i*8+4)!=actors+(n+3-i)*32 ||
                        Memory::Read32(actors+(n+i)*32+20)!=ranks[i])return 3;
                    if(ctx.gpr[1]!=sp || !guards())return 4;
                    for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r)return 5;
                    // Negative ranks are inactive; sparse CPU ranks are compacted and renumbered.
                    for(uint32_t i=0;i<n;i++)Memory::Write32(actors+i*32+20,i%2 ? 0xffffffff : n-i);
                    ctx.gpr[3]=data;func_807326B8(&ctx);
                    for(uint32_t out=0,i=(n-1)&~1u;;i-=2,out++){
                        if(Memory::Read32(data+cpu+out*8+4)!=actors+i*32 || Memory::Read32(actors+i*32+20)!=out+1)return 6;
                        if(i==0)break;
                    }
                    if(ctx.gpr[1]!=sp || !guards())return 7;
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(45000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"AI rank sorting compilation timed out");
                Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(5000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"AI rank sorting execution timed out");
            Assert.True(run.ExitCode==0,$"AI rank sorting lifecycle returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
