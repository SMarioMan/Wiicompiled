using System.Diagnostics;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class StartingGridTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealGridGeometryPreservesAuthoredFieldsAndExtendsDistinctStaggeredRows(int players)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var manifest=Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json");
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));
        var options=new TranslationOptions(EnableLeafInlining:false,KnownFunctionEntryPoints:map.Addresses.ToHashSet());
        var relocations=GuestLayoutRelocations.Read(manifest,players);
        var translator=new FunctionTranslator(image,layoutRelocations:relocations);
        var temp=Path.Combine(Path.GetTempPath(),"mkw-starting-grid-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var entries=new uint[] {0x8050fef4,0x80510360,0x80510388,0x8051039c,0x805103c4,0x805103f8,
                0x80239bd4,0x80239f4c,0x8023a1bc,0x8022f4dc,0x8022f4e8,0x80085070,0x800850e0,0x80020a30,0x80020a7c};
            var sources=new List<string>();
            foreach(var pc in entries)
            {
                var file=Path.Combine(temp,$"{pc:X8}.cpp");File.WriteAllText(file,translator.Translate(pc,options).CxxCode);sources.Add(file);
            }
            var baseline=Path.Combine(temp,"original.cpp");
            File.WriteAllText(baseline,new FunctionTranslator(image).Translate(0x8050fef4,options with {PreferredName="grid_original"}).CxxCode.Replace("func_8050FEF4", "grid_original_entry"));
            sources.Add(baseline);
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
                {{declarations}}
                extern "C" void grid_original(CpuContext*);
                #include <bit>
                #include <cmath>
                #include <set>
                constexpr uint32_t manager=0x81010000,section=manager+0x100,holders=manager+0x200,stgi=manager+0x300,
                    rawStgi=manager+0x400,ktpt=manager+0x500,rawKtpt=manager+0x600,pos=manager+0x700,rot=manager+0x800,
                    sp=0x81f00000,n={{players}};
                void write_float(uint32_t p,float v){Memory::Write32(p,std::bit_cast<uint32_t>(v));}
                float read_float(uint32_t p){return std::bit_cast<float>(Memory::Read32(p));}
                using Vec=std::array<float,3>;
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="starting grid geometry";
                    info.rawCpuInvoker=entry;info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);
                    FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    Memory::Write32(0x809b8f28,manager);Memory::Write32(manager+0x3c,section);
                    Memory::Write32(section,holders);Memory::Write16(section+4,1);Memory::Write32(holders,stgi);Memory::Write32(stgi,rawStgi);
                    Memory::Write32(ktpt,rawKtpt);write_float(rawKtpt,1000);write_float(rawKtpt+4,120);write_float(rawKtpt+8,2000);
                    write_float(0x809b8f2c,100);
                    // The standalone harness does not run the game bootstrap's
                    // static constructors for EGG's unit-axis vectors.
                    for(uint32_t axis=0;axis<3;axis++){
                        write_float(0x8029fd98+axis*4,axis==0?1.f:0.f);
                        write_float(0x8029fdc8+axis*4,axis==2?1.f:0.f);
                    }
                    CpuContext ctx{};ctx.gpr[1]=sp;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                    for(uint32_t r=14;r<32;r++){ctx.gpr[r]=0x12340000+r;ctx.fpr[r].d=r+0.25;}
                    for(uint32_t i=0;i<16;i++){
                        Memory::Write8(pos-16+i,0x55);Memory::Write8(pos+12+i,0x55);
                        Memory::Write8(rot-16+i,0x55);Memory::Write8(rot+12+i,0x55);
                        Memory::Write8(sp-0x800-16+i,0x55);Memory::Write8(sp+8+i,0x55);
                    }
                    auto calculate=[&](auto entry,uint32_t count,uint32_t index){
                        ctx.gpr[3]=ktpt;ctx.gpr[4]=pos;ctx.gpr[5]=rot;ctx.gpr[6]=index;ctx.gpr[7]=count;entry(&ctx);
                        Vec value{};for(uint32_t axis=0;axis<3;axis++){
                            value[axis]=read_float(pos+axis*4);if(!std::isfinite(value[axis]))std::abort();
                            if(Memory::Read32(rot+axis*4)!=Memory::Read32(rawKtpt+12+axis*4))std::abort();
                        }
                        if(ctx.gpr[1]!=sp)std::abort();
                        for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r || ctx.fpr[r].d!=r+0.25)std::abort();
                        return value;
                    };
                    for(auto narrow:{0u,1u})for(auto yaw:{0.f,90.f,180.f,270.f}){
                        Memory::Write8(rawStgi+1,narrow);write_float(rawKtpt+12,0);write_float(rawKtpt+16,yaw);write_float(rawKtpt+20,0);
                        std::array<Vec,12> authored{};for(uint32_t i=0;i<12;i++)authored[i]=calculate(grid_original,12,i);
                        Vec rowStep{};for(uint32_t axis=0;axis<3;axis++)rowStep[axis]=(authored[6][axis]-authored[0][axis])/2;
                        for(uint32_t count=1;count<=n;count++){
                            if(count>13 && count!=n)continue;
                            std::set<Vec> occupied;
                            for(uint32_t i=0;i<count;i++){
                                auto actual=calculate(func_8050FEF4,count,i);
                                if(count<=12){if(actual!=calculate(grid_original,count,i))return 4;}
                                else for(uint32_t axis=0;axis<3;axis++){
                                    const float expected=authored[i%12][axis]+rowStep[axis]*4*(i/12);
                                    if(std::abs(actual[axis]-expected)>0.02f+std::abs(expected)*0.00001f){
                                        std::fprintf(stderr,"grid count=%u index=%u axis=%u got=%f expected=%f\n",count,i,axis,actual[axis],expected);return 5;}
                                }
                                if(!occupied.insert(actual).second){std::fprintf(stderr,"duplicate grid narrow=%u yaw=%f count=%u index=%u point=%f,%f,%f\n",narrow,yaw,count,i,actual[0],actual[1],actual[2]);return 6;}
                            }
                        }
                    }
                    for(uint32_t i=0;i<16;i++)if(Memory::Read8(pos-16+i)!=0x55 || Memory::Read8(pos+12+i)!=0x55 ||
                        Memory::Read8(rot-16+i)!=0x55 || Memory::Read8(rot+12+i)!=0x55 ||
                        Memory::Read8(sp-0x800-16+i)!=0x55 || Memory::Read8(sp+8+i)!=0x55)return 7;
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(45000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Starting grid compilation timed out");
                Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(15000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Starting grid execution timed out");
            Assert.True(run.ExitCode==0,$"Starting grid lifecycle returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
