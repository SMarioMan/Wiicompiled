using System.Diagnostics;
using System.Buffers.Binary;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class TeamResultStorageTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealTeamPagesAllocateBothUniformResultsAndDestroyTheirAuthoredControls(int players)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var manifest=Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json");
        using var document=JsonDocument.Parse(File.ReadAllText(manifest));
        var layout=new GuestLayouts(document.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!,players);
        var plan=GuestLayoutRelocations.Read(manifest,players);
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        // Isolate the original unused-row clear loop from the rendering tail.
        // Only its exit becomes a return; every loop instruction is real game code.
        BinaryPrimitives.WriteUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(0x807ec65c,4),4),0x4e800020);
        var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));
        var translator=new FunctionTranslator(image,layoutRelocations:plan);
        var temp=Path.Combine(Path.GetTempPath(),"mkw-team-results-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var entries=new uint[]{0x805f24dc,0x805f446c,0x805f44d4,0x805f4538,0x805f4634,0x805f469c,0x8083b18c,0x8083b224,0x8083b2bc,0x807ec5d0};
            var fixtures=new HashSet<uint>{0x80229a48,0x80229a90,0x8083a3b4,0x8060c378,0x8060c4a0,0x805c0e2c,0x80020494,0x8002058c,0x805d4828,0x805dd124};
            var sources=new List<string>();
            foreach(var pc in entries){var path=Path.Combine(temp,$"{pc:X8}.cpp");
                File.WriteAllText(path,translator.Translate(pc,new TranslationOptions(KnownFunctionEntryPoints:map.Addresses.ToHashSet(),LeafInliningBlockedTargets:fixtures)).CxxCode);sources.Add(path);}
            var declarations=string.Join("\n",entries.Select(pc=>$"extern \"C\" void func_{pc:X8}(CpuContext*);"));
            var registrations=string.Join("\n",entries.Select(pc=>$"register_entry(0x{pc:X8},func_{pc:X8});"));
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <array>
                #include <vector>
                #include <cstdio>
                #include <cstring>
                {{declarations}}
                constexpr uint32_t capacity={{players}},pageSize={{layout.Symbol("TeamResultPage.size")}},resultSize={{layout.Symbol("TeamResult.size")}},
                    slots={{layout.Symbol("TeamResult.players.count")}},playersOffset={{layout.Symbol("TeamResult.players.offset")}},rowsSize={{layout.Symbol("TeamResult.players.size")}},
                    items={{layout.Symbol("TeamResult.items.offset")}},text={{layout.Symbol("TeamResult.text.offset")}},
                    root=0x81010000,stack=0x81f00000;
                uint32_t allocated=0,freed=0,headers=0,texts=0,deleted=0,parent=0,parentDeleted=0;
                uint32_t result(uint32_t team){return root+416+team*resultSize;}
                bool control(uint32_t p){for(uint32_t t=0;t<2;t++)if(p==result(t) || (p>=result(t)+items && p<result(t)+items+6*372 && (p-result(t)-items)%372==0))return true;return false;}
                void allocate(CpuContext* ctx){if(ctx->gpr[3]!=pageSize || allocated++)std::abort();ctx->gpr[3]=root;}
                void free_page(CpuContext* ctx){if(ctx->gpr[3]!=root || freed++)std::abort();}
                void page_ctor(CpuContext* ctx){if(ctx->gpr[3]!=root || parent++)std::abort();std::memset(Memory::GetPointer(root,416),0,416);}
                void page_dtor(CpuContext* ctx){if(ctx->gpr[3]!=root && ctx->gpr[3]!=root+92)std::abort();parentDeleted++;}
                void layout_ctor(CpuContext* ctx){auto p=ctx->gpr[3];if(!control(p))std::abort();headers++;std::memset(Memory::GetPointer(p,372),0,372);Memory::Write32(p,0x80010000);}
                void layout_dtor(CpuContext* ctx){if(!control(ctx->gpr[3]))std::abort();deleted++;}
                void text_ctor(CpuContext* ctx){auto p=ctx->gpr[3];if(p!=result(0)+text && p!=result(1)+text)std::abort();texts++;std::memset(Memory::GetPointer(p,196),0,196);}
                void construct(CpuContext* ctx){auto p=ctx->gpr[3],ctor=ctx->gpr[4],stride=ctx->gpr[6],count=ctx->gpr[7];
                    if(ctor==0x805f44d4){if(p!=root+416 || stride!=resultSize || count!=2 || ctx->gpr[5]!=0x805f4538)std::abort();}
                    else if(ctor!=0x8060c378 || stride!=372 || count!=6 || ctx->gpr[5]!=0x8060c4a0)std::abort();
                    for(uint32_t i=0;i<count;i++){ctx->gpr[3]=p+i*stride;InvokeIndirectCpu(ctor,ctx);}ctx->gpr[3]=p;}
                void destroy(CpuContext* ctx){auto p=ctx->gpr[3],dtor=ctx->gpr[4],stride=ctx->gpr[5],count=ctx->gpr[6];
                    if(dtor==0x805f4538){if(p!=root+416 || stride!=resultSize || count!=2)std::abort();}
                    else if(dtor!=0x8060c4a0 || stride!=372 || count!=6)std::abort();
                    for(uint32_t i=count;i>0;i--){ctx->gpr[3]=p+(i-1)*stride;ctx->gpr[4]=uint32_t(-1);InvokeIndirectCpu(dtor,ctx);}ctx->gpr[3]=p;}
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="team result storage fixture";
                    info.rawCpuInvoker=entry;info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                bool guards(){for(uint32_t i=0;i<16;i++)if(Memory::Read8(root-16+i)!=0x55 || Memory::Read8(root+pageSize+i)!=0x55)return false;return true;}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    register_entry(0x80229a48,allocate);register_entry(0x80229a90,free_page);register_entry(0x8083a3b4,page_ctor);
                    register_entry(0x8060c378,layout_ctor);register_entry(0x8060c4a0,layout_dtor);register_entry(0x805c0e2c,text_ctor);
                    register_entry(0x80020494,construct);register_entry(0x8002058c,destroy);register_entry(0x805d4828,page_dtor);register_entry(0x805dd124,page_dtor);
                    CpuContext ctx{};ctx.gpr[1]=stack;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                    for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                    auto dtors=std::array{func_8083B2BC,func_8083B224,func_8083B18C};
                    for(uint32_t pageId=0x32;pageId<=0x34;pageId++){
                        allocated=freed=headers=texts=deleted=parent=parentDeleted=0;
                        std::memset(Memory::GetPointer(root-16,pageSize+32),0x55,pageSize+32);
                        ctx.gpr[3]=pageId;func_805F24DC(&ctx);
                        if(ctx.gpr[3]!=root || allocated!=1 || parent!=1 || headers!=14 || texts!=2 || !guards())return 4;
                        for(uint32_t t=0;t<2;t++){
                            for(uint32_t j=0;j<rowsSize;j++)if(Memory::Read8(result(t)+playersOffset+j)!=0x55)return 5;
                            for(uint32_t j=0;j<6;j++)if(Memory::Read32(result(t)+items+j*372)!=0x80010000)return 6;
                        }
                        auto saved15=ctx.gpr[15],saved18=ctx.gpr[18];
                        std::vector<uint8_t> snapshot(pageSize);
                        std::memcpy(snapshot.data(),Memory::GetPointer(root,pageSize),pageSize);
                        for(uint32_t t=0;t<2;t++)for(uint32_t used=0;used<=slots;used++){
                            std::memcpy(Memory::GetPointer(root,pageSize),snapshot.data(),pageSize);
                            ctx.gpr[15]=result(t);ctx.gpr[18]=used;func_807EC5D0(&ctx);
                            for(uint32_t byte=0;byte<pageSize;byte++){
                                uint8_t expected=snapshot[byte];auto begin=416+t*resultSize+playersOffset;
                                if(byte>=begin+used*20 && byte<begin+rowsSize){
                                    auto within=(byte-begin)%20;
                                    if(within<4)expected=0;
                                    else if(within>=12 && within<16)expected=0xff;
                                }
                                if(Memory::Read8(root+byte)!=expected)return 10;
                            }
                            if(!guards())return 11;
                        }
                        ctx.gpr[15]=saved15;ctx.gpr[18]=saved18;
                        ctx.gpr[3]=root;ctx.gpr[4]=1;dtors[pageId-0x32](&ctx);
                        if(freed!=1 || deleted!=14 || parentDeleted!=2 || !guards())return 7;
                        for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r)return 8;
                        if(ctx.gpr[1]!=stack)return 9;
                    }
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Team result storage compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Team result storage execution timed out");
            Assert.True(run.ExitCode==0,$"Team result storage returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
