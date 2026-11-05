using System.Diagnostics;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class AiObjectStorageTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealAiObjectOwnerConstructsInitializesLooksUpAndDestroysEverySlot(int players)
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
            var entries=new uint[] {0x8072c710,0x8072c754,0x8072c8b8,0x8072c924,0x8072c9cc,0x8072c9fc,0x8072bf20};
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
                constexpr uint32_t data=0x80010000,objects=0x80040000,sp=0x800f0000;
                uint32_t allocated=0,lookups=0,released=0;
                std::array<bool,{{players}}> deleted{};
                void allocate(CpuContext* ctx){if(ctx->gpr[3]!=16 || allocated>={{players}})std::abort();ctx->gpr[3]=objects+16*allocated++;}
                void release(CpuContext* ctx){const uint32_t p=ctx->gpr[3];if(p<objects || p>=objects+{{players}}*16 || (p-objects)%16)std::abort();
                    const uint32_t id=(p-objects)/16;if(deleted[id])std::abort();deleted[id]=true;released++;}
                void kart_lookup(CpuContext* ctx){if(ctx->gpr[4]>={{players}})std::abort();lookups++;ctx->gpr[3]=0x80100000+ctx->gpr[4]*0x400;}
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="real AI object lifecycle";
                    info.rawCpuInvoker=entry;info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                int main(){
                    Memory::Init(16*1024*1024);
                    {{registrations}}
                    register_entry(0x80229a48,allocate);register_entry(0x80229a90,release);register_entry(0x805898dc,kart_lookup);
                    Memory::Write32(0x808c5cf8,0x8072c9fc); // Original leaf object's virtual destructor.
                    Memory::Write32(0x809be398,0x80020000);Memory::Write32(0x8002001c,2);
                    constexpr uint32_t size=4+{{players}}*4;
                    std::memset(Memory::GetPointer(data,size),0x7e,size);
                    std::memset(Memory::GetPointer(objects,{{players}}*16),0x7e,{{players}}*16);
                    for(uint32_t i=0;i<16;i++){Memory::Write8(data-16+i,0x55);Memory::Write8(data+size+i,0x55);
                        Memory::Write8(objects-16+i,0x55);Memory::Write8(objects+{{players}}*16+i,0x55);
                        Memory::Write8(sp-0x60+i,0x55);Memory::Write8(sp+8+i,0x55);}
                    CpuContext ctx{};ctx.gpr[1]=sp;for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                    CpuContextScope scope(&ctx);ctx.gpr[3]=data;func_8072C710(&ctx);
                    if(Memory::Read32(data)!=0x808c5cd8)return 1;
                    for(uint32_t i=0;i<{{players}};i++)if(Memory::Read32(data+4+i*4)!=0)return 2;
                    for(uint32_t i=0;i<{{players}};i++){
                        ctx.gpr[3]=data;ctx.gpr[4]=i;func_8072C8B8(&ctx);
                        ctx.gpr[3]=data;ctx.gpr[4]=i;func_8072C9CC(&ctx);
                        if(ctx.gpr[3]!=objects+i*16 || Memory::Read32(ctx.gpr[3]+8)!=i)return 3;
                    }
                    ctx.gpr[3]=data;func_8072C924(&ctx);
                    if(allocated!={{players}} || lookups!={{players}})return 4;
                    for(uint32_t i=0;i<{{players}};i++)if(Memory::Read32(objects+i*16+4)!=0x80100000+i*0x400 ||
                        Memory::Read32(objects+i*16+12)!=75)return 5;
                    ctx.gpr[3]=data;ctx.gpr[4]=0;func_8072C754(&ctx);
                    if(released!={{players}})return 6;
                    for(uint32_t i=0;i<{{players}};i++)if(!deleted[i] || Memory::Read32(data+4+i*4)!=0)return 7;
                    if(ctx.gpr[1]!=sp)return 8;
                    for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r)return 9;
                    for(uint32_t i=0;i<16;i++)if(Memory::Read8(data-16+i)!=0x55 || Memory::Read8(data+size+i)!=0x55 ||
                        Memory::Read8(objects-16+i)!=0x55 || Memory::Read8(objects+{{players}}*16+i)!=0x55 ||
                        Memory::Read8(sp-0x60+i)!=0x55 || Memory::Read8(sp+8+i)!=0x55)return 10;
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(45000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"AI object compilation timed out");
                Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(5000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"AI object execution timed out");
            Assert.True(run.ExitCode==0,$"AI object lifecycle returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
