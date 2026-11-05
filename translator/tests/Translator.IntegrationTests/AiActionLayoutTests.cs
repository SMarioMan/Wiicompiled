using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class AiActionLayoutTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void OriginalAiAllocationSdkCookieAndLoaderCoverAllSlotsAndStayWithinAuthoredRows(int players)
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
        if(players==12)
        {
            var baseline=new FunctionTranslator(image);
            foreach(var pc in new uint[] {0x8072d540,0x8072d9f8})
                Assert.Equal(baseline.Translate(pc,options).CxxCode,translator.Translate(pc,options).CxxCode);
        }
        var temp=Path.Combine(Path.GetTempPath(),"mkw-ai-actions-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var sources=new List<string>();
            foreach(var pc in new uint[] {0x8072d540,0x8072d9f8,0x800202d4})
            {
                var file=Path.Combine(temp,$"{pc:X8}.cpp");File.WriteAllText(file,translator.Translate(pc,options).CxxCode);sources.Add(file);
            }
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <array>
                #include <bit>
                #include <cmath>
                #include <cstring>
                extern "C" void func_8072D540(CpuContext*);
                extern "C" void func_8072D9F8(CpuContext*);
                extern "C" void func_800202D4(CpuContext*);
                constexpr uint32_t allocation=0x80060000;
                uint32_t speedSize=0,arraySize=0,speedCalls=0,arrayCalls=0;
                void speed_alloc(CpuContext* ctx){speedSize=ctx->gpr[3];speedCalls++;ctx->gpr[3]=0x80050000;}
                void array_alloc(CpuContext* ctx){arraySize=ctx->gpr[3];arrayCalls++;ctx->gpr[3]=allocation;}
                void no_file_io(CpuContext*){}
                void register_fixture(uint32_t address,void(*entry)(CpuContext*)) {
                    TranslatedFunctionInfo info{};info.address=address;info.name="external fixture";
                    info.rawCpuInvoker=entry;info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);
                }
                int main() {
                    Memory::Init(2*1024*1024);
                    constexpr uint32_t owner=0x80010000,raw=0x80020000,sp=0x800f0000,size={{layout.Symbol("AIActionAllocation.size")}};
                    register_fixture(0x80229a48,speed_alloc);register_fixture(0x80229a6c,array_alloc);
                    register_fixture(0x8072d680,no_file_io);register_fixture(0x800202d4,func_800202D4);
                    std::memset(Memory::GetPointer(allocation,size),0x7e,size);
                    for(uint32_t i=0;i<16;i++){Memory::Write8(allocation-16+i,0x55);Memory::Write8(allocation+size+i,0x55);
                        Memory::Write8(sp-0x60+i,0x55);Memory::Write8(sp+8+i,0x55);}
                    CpuContext ctx{};ctx.gpr[1]=sp;ctx.gpr[3]=owner;ctx.gpr[4]=2;
                    for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                    CpuContextScope scope(&ctx);func_8072D540(&ctx);
                    // The independent three-float speed allocation must stay 12 bytes.
                    if(speedCalls!=1 || speedSize!=12 || arrayCalls!=1 || arraySize!=size)return 1;
                    if(ctx.gpr[3]!=owner || ctx.gpr[1]!=sp || Memory::Read32(owner+8)!=allocation+16)return 2;
                    if(Memory::Read32(allocation)!=8 || Memory::Read32(allocation+4)!={{players}})return 3;
                    std::array<uint32_t,3*12*8> original{};
                    for(uint32_t d=0;d<3;d++)for(uint32_t i=0;i<12;i++)for(uint32_t f=0;f<8;f++) {
                        const uint32_t index=d*12*8+i*8+f;
                        original[index]=std::bit_cast<uint32_t>(float(d*64+i*8+f)+0.75f);
                        Memory::Write32(raw+index*4,original[index]);
                    }
                    for(uint32_t d=0;d<3;d++) {
                        std::memset(Memory::GetPointer(allocation+16,{{players}}*8),0x7e,{{players}}*8);
                        ctx.gpr[3]=owner;ctx.gpr[4]=raw;ctx.gpr[5]=d;func_8072D9F8(&ctx);
                        for(uint32_t i=0;i<{{players}};i++) {
                            const uint32_t rank=uint32_t(std::lround(double(i)*11/({{players}}-1)));
                            for(uint32_t f=0;f<8;f++)if(Memory::Read8(allocation+16+i*8+f)!=d*64+rank*8+f)return 4;
                        }
                        if(ctx.gpr[1]!=sp)return 5;
                    }
                    for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r)return 6;
                    for(uint32_t i=0;i<original.size();i++)if(Memory::Read32(raw+i*4)!=original[i])return 7;
                    for(uint32_t i=0;i<16;i++)if(Memory::Read8(allocation-16+i)!=0x55 || Memory::Read8(allocation+size+i)!=0x55 ||
                        Memory::Read8(sp-0x60+i)!=0x55 || Memory::Read8(sp+8+i)!=0x55)return 8;
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(45000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"AI action compilation timed out");
                Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(5000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"AI action execution timed out");
            Assert.True(run.ExitCode==0,$"AI action returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
