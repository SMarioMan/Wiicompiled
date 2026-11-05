using System.Diagnostics;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class CheckpointTraversalTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealCheckpointTraversalFindsTheContainingCheckpointInACycleForEveryPlayerAndBothNetworkModes(int players)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var manifest=Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json");
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));
        var options=new TranslationOptions(EnableLeafInlining:false,KnownFunctionEntryPoints:map.Addresses.ToHashSet());
        var translator=new FunctionTranslator(image,layoutRelocations:GuestLayoutRelocations.Read(manifest,players));
        var temp=Path.Combine(Path.GetTempPath(),"mkw-ai-objects-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var entries=new uint[] {0x8050cae4,0x8050cbc0,0x8050cc9c,0x8050d08c,0x80020a1c,0x80020a2c,0x80020a68,0x80020a78};
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
                constexpr uint32_t manager=0x80010000,section=0x80020000,holders=0x80030000,raw=0x80040000,sp=0x800f0000;
                uint32_t calls=0;
                // Geometry and checkpoint lookup are independent fixtures. The real
                // entry, reset loop, both linked-list helpers and recursive search run.
                void completion(CpuContext* ctx){if(++calls>64)std::abort();ctx->gpr[3]=ctx->gpr[3]==holders+0x100 ? 0 : 1;}
                void holder(CpuContext* ctx){if(ctx->gpr[3]!=manager || ctx->gpr[4]>=2)std::abort();ctx->gpr[3]=holders+ctx->gpr[4]*0x100;}
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="checkpoint traversal";
                    info.rawCpuInvoker=entry;info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                int main(){
                    Memory::Init(16*1024*1024);
                    {{registrations}}
                    register_entry(0x8050c908,completion);register_entry(0x805117b0,holder);
                    Memory::Write32(manager+0x20,section);Memory::Write16(section+4,2);
                    for(uint32_t i=0;i<2;i++){
                        const uint32_t p=holders+i*0x100,other=holders+(1-i)*0x100;
                        Memory::Write32(p,raw+i*20);Memory::Write8(raw+i*20+0x11,0xff);
                        Memory::Write16(p+4,1);Memory::Write16(p+6,1);Memory::Write16(p+0x1a,i);
                        Memory::Write32(p+0x20,other);Memory::Write32(p+0x38,other);
                        Memory::Write32(p-4,0xabcdef01);Memory::Write32(p+0xc8,0xabcdef01);
                    }
                    for(uint32_t i=0;i<16;i++){Memory::Write8(sp-0x400-16+i,0x55);Memory::Write8(sp+8+i,0x55);}
                    CpuContext ctx{};ctx.gpr[1]=sp;CpuContextScope scope(&ctx);
                    for(uint32_t online=0;online<2;online++){
                        uint32_t expectedResult=0,expectedCalls=0;
                        for(uint32_t player=0;player<{{players}};player++){
                            Memory::Write16(holders+0x18,0xa5a5);Memory::Write16(holders+0x118,0xa5a5);
                            for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                            ctx.gpr[3]=manager;ctx.gpr[4]=player;ctx.gpr[5]=0x80050000;ctx.gpr[6]=0;
                            ctx.gpr[7]=0x80060000;ctx.gpr[8]=online;calls=0;
                            func_8050D08C(&ctx);
                            if(player==0){expectedResult=ctx.gpr[3];expectedCalls=calls;}
                            if(ctx.gpr[3]!=expectedResult || calls!=expectedCalls || !calls)return 1;
                            if(ctx.gpr[1]!=sp)return 2;
                            for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r)return 3;
                            const uint32_t bit={{players}}==12 ? (1u<<player) : 1u;
                            if(Memory::Read16(holders+0x18)!=bit || Memory::Read16(holders+0x118)!=bit)return 4;
                            for(uint32_t i=0;i<2;i++)if(Memory::Read32(holders+i*0x100-4)!=0xabcdef01 ||
                                Memory::Read32(holders+i*0x100+0xc8)!=0xabcdef01 || Memory::Read16(holders+i*0x100+0x1a)!=i)return 5;
                            for(uint32_t i=0;i<16;i++)if(Memory::Read8(sp-0x400-16+i)!=0x55 || Memory::Read8(sp+8+i)!=0x55)return 6;
                        }
                    }
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(45000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Checkpoint traversal compilation timed out");
                Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(5000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Checkpoint traversal execution timed out");
            Assert.True(run.ExitCode==0,$"Checkpoint traversal lifecycle returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
