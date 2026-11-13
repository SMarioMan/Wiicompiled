using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class RespawnGridTests
{
    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(48)] [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealRescueGeometryBoundsEveryPlayerAndPreservesBattleCenters(int players)
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
        var temp=Path.Combine(Path.GetTempPath(),"mkw-rescue-grid-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var entries=new uint[] {0x805144f8,0x80514644,0x805103c4,0x80020a3c,0x80020a88};
            var sources=new List<string>{Path.Combine(root,"runtime/src/hle/respawn_grid.cpp"),Path.Combine(root,"runtime/src/guest_region.cpp")};
            foreach(var pc in entries) {
                var file=Path.Combine(temp,$"{pc:X8}.cpp");File.WriteAllText(file,translator.Translate(pc,options).CxxCode);sources.Add(file);
            }
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var declarations=string.Join("\n",entries.Select(pc=>$"extern \"C\" void func_{pc:X8}(CpuContext*);"));
            var registrations=string.Join("\n",entries.Select(pc=>$"register_entry(0x{pc:X8},func_{pc:X8});"));
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include "mkw_guest_layout.h"
                #include <array>
                #include <bit>
                #include <cmath>
                #include <cstdio>
                #include <set>
                {{declarations}}
                extern "C" void JGPT_ExpandedRespawnGrid(CpuContext*);
                namespace MkwGuestLayout { const Layout kLayout={{{players}},0,0,0,0,0,0,0}; }
                constexpr uint32_t data=0x81010000,holder=data+0x8000,raw=holder+0x100,pos=holder+0x200,sp=0x81f00000,n={{players}};
                void put(uint32_t p,float v){Memory::Write32(p,std::bit_cast<uint32_t>(v));}
                float get(uint32_t p){return std::bit_cast<float>(Memory::Read32(p));}
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="rescue geometry";
                    info.rawCpuInvoker=entry;info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);
                    FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    register_entry(0x8051896c,func_805144F8);
                    // The standalone runtime uses PAL identities; the actual
                    // translated helper still reads the USA instance address.
                    Memory::Write32(0x809bd728,data);Memory::Write32(0x809b8f68,data);
                    Memory::Write8(data+4,n);Memory::Write32(holder,raw);
                    put(raw,1000);put(raw+4,200);put(raw+8,3000);
                    CpuContext ctx{};ctx.gpr[1]=sp;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                    for(uint32_t r=14;r<32;r++){ctx.gpr[r]=0x12340000+r;ctx.fpr[r].d=r+.25;}
                    for(uint32_t i=0;i<16;i++){Memory::Write8(pos-16+i,0x55);Memory::Write8(pos+12+i,0x55);}
                    for(unsigned mode: {0u,1u,2u,5u})for(float yaw: {0.f,1.5707963268f,3.1415926536f,4.7123889804f}){
                        Memory::Write32(data+{{layout.Symbol("Racedata.race.offset")+layout.Symbol("Scenario.settings.offset")}}+8,mode);
                        for(unsigned axis=0;axis<3;axis++){
                            put(holder+0x1c+axis*4,axis==0?std::cos(yaw):axis==2?-std::sin(yaw):0.f);
                            put(holder+0x10+axis*4,axis==0?std::sin(yaw):axis==2?std::cos(yaw):0.f);
                        }
                        std::set<std::array<float,3>> occupied;
                        for(unsigned i=0;i<n;i++){
                            ctx.gpr[3]=holder;ctx.gpr[4]=pos;ctx.gpr[5]=i;
                            std::array<float,3> stock{};
                            if(i<12){func_805144F8(&ctx);for(unsigned a=0;a<3;a++)stock[a]=get(pos+a*4);ctx.gpr[3]=holder;ctx.gpr[4]=pos;ctx.gpr[5]=i;}
                            JGPT_ExpandedRespawnGrid(&ctx);
                            std::array<float,3> actual{};for(unsigned a=0;a<3;a++)actual[a]=get(pos+a*4);
                            if(!std::isfinite(actual[0]) || !std::isfinite(actual[1]) || !std::isfinite(actual[2]))return 4;
                            if(n<=12 && stock!=actual)return 5;
                            const float x=actual[0]-1000,z=actual[2]-3000;
                            const float across=x*std::cos(yaw)-z*std::sin(yaw),along=x*std::sin(yaw)+z*std::cos(yaw);
                            if(mode==2 || mode==5){if(actual!=std::array<float,3>{1000,200,3000})return 6;}
                            else {
                                if(std::abs(across)>450.02f || along<-.02f || along>600.02f || std::abs(actual[1]-200)>.01f)return 7;
                                if(!occupied.insert(actual).second)return 8;
                            }
                            if(ctx.gpr[1]!=sp)return 9;
                            for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r || ctx.fpr[r].d!=r+.25)return 10;
                        }
                        // Sentinel 255 requests the exact authored center in every mode.
                        ctx.gpr[3]=holder;ctx.gpr[4]=pos;ctx.gpr[5]=255;JGPT_ExpandedRespawnGrid(&ctx);
                        if(get(pos)!=1000 || get(pos+4)!=200 || get(pos+8)!=3000)return 11;
                    }
                    for(uint32_t i=0;i<16;i++)if(Memory::Read8(pos-16+i)!=0x55 || Memory::Read8(pos+12+i)!=0x55)return 12;
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!) {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(45000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Rescue grid compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(15000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Rescue grid execution timed out");
            Assert.True(run.ExitCode==0,$"Rescue geometry returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
