using System.Diagnostics;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class ItemProbabilityStorageTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealItemProcessingUsesUniformRankStorageAndPreservesBattleAndSpecialTables(int players)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var layout=GuestLayoutRelocations.Read(Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json"),players);
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));
        var translator=new FunctionTranslator(image,layoutRelocations:layout);
        var temp=Path.Combine(Path.GetTempPath(),"mkw-item-probabilities-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var sources=new List<string>();
            foreach(var pc in new uint[]{0x80020a0c,0x80020a58,0x807abf78,0x807ac2c0,0x807ace70})
            {
                var path=Path.Combine(temp,$"{pc:X8}.cpp");
                File.WriteAllText(path,translator.Translate(pc,new TranslationOptions(EnableLeafInlining:false,KnownFunctionEntryPoints:map.Addresses.ToHashSet())).CxxCode);
                sources.Add(path);
            }
            var header=Path.Combine(temp,"layout.h");File.WriteAllText(header,layout.EmitNativeConstants());
            var glue=Path.Combine(temp,"native.cpp");
            File.WriteAllText(glue,$"#define MKW_TRANSLATED_PLAYER_CAPACITY {players}\n#define MKW_TRANSLATED_GUEST_LAYOUT_HEADER \"{header}\"\n"+
                $"#include \"{root}/runtime/game/guest_layout_values.cpp\"\n#include \"{root}/runtime/src/hle/item_slots.cpp\"\n");
            sources.Add(glue);sources.Add(Path.Combine(root,"runtime/src/guest_region.cpp"));
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <algorithm>
                #include <array>
                #include <cstdio>
                #include <cstring>
                #include <stdexcept>
                extern "C" void func_80020A0C(CpuContext*);
                extern "C" void func_80020A58(CpuContext*);
                extern "C" void func_807ABF78(CpuContext*);
                extern "C" void func_807AC2C0(CpuContext*);
                extern "C" void func_807ACE70(CpuContext*);
                extern "C" void ItemSlotData_ProcessTable_807ba9d8(CpuContext*);
                extern "C" void ItemSlotData_PostProcessVSTable_807bad20(CpuContext*);
                constexpr uint32_t n={{players}},self=0x81010000,raw=0x81011000,holder=0x81012000,
                    baseline=0x81012020,normal=0x81013000,roulette=0x81014000,stack=0x81f00000;
                uint32_t heap=0x81100000,allocations=0;
                std::array<uint32_t,16> ends{};
                void allocate(CpuContext* ctx){auto bytes=ctx->gpr[3];if(allocations>=ends.size())std::abort();
                    ctx->gpr[3]=heap;heap+=bytes;ends[allocations++]=heap;
                    std::memset(Memory::GetPointer(heap,16),0x55,16);heap=(heap+31)&~15u;}
                void random_instance(CpuContext* ctx){ctx->gpr[3]=0x81030000;}
                void random_last(CpuContext* ctx){if(ctx->gpr[3]!=0x81030000 || !ctx->gpr[4] || ctx->gpr[4]>19)std::abort();ctx->gpr[3]=ctx->gpr[4]-1;}
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="item probability fixture";
                    info.rawCpuInvoker=entry;info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                void seed_raw(uint32_t columns){Memory::Write8(raw,columns);Memory::Write8(raw+1,19);
                    for(uint32_t item=0;item<19;item++)for(uint32_t rank=0;rank<columns;rank++)
                        Memory::Write8(raw+2+item*columns+rank,1+item+rank*2);}
                void setup(CpuContext& ctx,uint32_t target,bool update=false,bool special=false){
                    ctx.gpr[3]=self;ctx.gpr[4]=raw;ctx.gpr[5]=target;ctx.gpr[6]=update;ctx.gpr[7]=special;}
                bool guards(){for(uint32_t a=0;a<allocations;a++)for(uint32_t i=0;i<16;i++)if(Memory::Read8(ends[a]+i)!=0x55)return false;
                    for(uint32_t i=0;i<16;i++)if(Memory::Read8(roulette+n*80+i)!=0x55)return false;return true;}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);
                    FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    register_entry(0x80020a0c,func_80020A0C);register_entry(0x80020a58,func_80020A58);
                    register_entry(0x80229a6c,allocate);register_entry(0x80229df0,allocate);
                    register_entry(0x807aecb8,random_instance);register_entry(0x8054f5ac,random_last);
                    register_entry(0x807ba9d8,func_807ABF78);register_entry(0x807bad20,func_807AC2C0);
                    // PAL native identities use the original USA function/data as this fixture's implementation.
                    Memory::Write32(0x807bad24,Memory::Read32(0x807ac2c4));Memory::Write32(0x807bad28,Memory::Read32(0x807ac2c8));
                    Memory::Write32(0x809b8f68,0x81020000);
                    Memory::Write8(0x809bf0b0,n);Memory::Write8(0x809bf0b1,0);Memory::Write8(0x809bf0b3,0);
                    Memory::Write32(self+0x28,normal);Memory::Write32(self+0x2c,roulette);
                    std::memset(Memory::GetPointer(roulette,n*80+16),0,n*80);std::memset(Memory::GetPointer(roulette+n*80,16),0x55,16);
                    CpuContext ctx{};ctx.gpr[1]=stack;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                    for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                    seed_raw(12);setup(ctx,baseline);func_807ABF78(&ctx);
                    auto original=Memory::Read32(baseline+4);
                    setup(ctx,holder,true);ItemSlotData_ProcessTable_807ba9d8(&ctx);
                    if(ctx.gpr[3]!=raw+2+12*19 || Memory::Read32(holder)!=n || allocations!=2)return 4;
                    auto buffer=Memory::Read32(holder+4);
                    for(uint32_t item=0;item<19;item++){
                        if(Memory::Read16(buffer+item*2)!=Memory::Read16(original+item*2) ||
                           Memory::Read16(buffer+(n-1)*38+item*2)!=Memory::Read16(original+11*38+item*2))return 5;
                        for(uint32_t rank=1;rank<n;rank++)if(Memory::Read16(buffer+rank*38+item*2)<Memory::Read16(buffer+(rank-1)*38+item*2))return 6;
                    }
                    for(uint32_t rank=0;rank<n;rank++)if(Memory::Read32(roulette+rank*80)==0 || Memory::Read32(roulette+rank*80)>19)return 7;
                    for(uint32_t rank=0;rank<n;rank++){
                        auto list=roulette+rank*80;auto count=Memory::Read32(list);auto last=Memory::Read32(list+count*4);
                        ctx.gpr[3]=self;ctx.gpr[4]=0;ctx.gpr[5]=rank+1;ctx.gpr[6]=0xffffffffu;ctx.gpr[7]=1;
                        func_807ACE70(&ctx);if(ctx.gpr[3]!=last)return 18;
                        ctx.gpr[3]=self;ctx.gpr[4]=0;ctx.gpr[5]=rank+1;ctx.gpr[6]=last;ctx.gpr[7]=1;
                        func_807ACE70(&ctx);if(ctx.gpr[3]!=Memory::Read32(list+4) || !guards())return 19;
                    }
                    for(uint32_t count=0;count<=n;count++){
                        setup(ctx,holder);ItemSlotData_ProcessTable_807ba9d8(&ctx);
                        Memory::Write32(self+0x44,count);ctx.gpr[3]=self;ctx.gpr[4]=holder;
                        ItemSlotData_PostProcessVSTable_807bad20(&ctx);
                        for(uint32_t rank=0;rank<n;rank++){
                            uint32_t total=0;for(uint32_t item=0;item<19;item++)total+=Memory::Read16(buffer+rank*38+item*2);
                            if((rank<count)!=(total>0))return 8;
                        }
                        if(count && Memory::Read16(buffer)!=Memory::Read16(original))return 9;
                        if(!guards() || allocations!=2)return 10;
                    }
                    for(uint32_t columns:{3u,16u}){
                        seed_raw(columns);auto target=holder+columns*16;setup(ctx,target,false,columns==16);
                        ItemSlotData_ProcessTable_807ba9d8(&ctx);
                        if(ctx.gpr[3]!=raw+2+columns*19 || Memory::Read32(target)!=columns || !guards())return 11;
                    }
                    if(n>12){
                        seed_raw(12);Memory::Write8(raw+1,18);setup(ctx,holder);
                        bool rejected=false;try{ItemSlotData_ProcessTable_807ba9d8(&ctx);}catch(const std::runtime_error&){rejected=true;}
                        if(!rejected)return 14;Memory::Write8(raw+1,19);
                        Memory::Write32(holder,n-1);setup(ctx,holder);rejected=false;
                        try{ItemSlotData_ProcessTable_807ba9d8(&ctx);}catch(const std::runtime_error&){rejected=true;}
                        if(!rejected)return 15;Memory::Write32(holder,n);
                        for(uint32_t count:{n+1,0xffffffffu}){
                            Memory::Write32(self+0x44,count);ctx.gpr[3]=self;ctx.gpr[4]=holder;rejected=false;
                            try{ItemSlotData_PostProcessVSTable_807bad20(&ctx);}catch(const std::runtime_error&){rejected=true;}
                            if(!rejected)return 16;
                        }
                        Memory::Write32(self+0x44,1);Memory::Write32(0x807bad24,0);ctx.gpr[3]=self;ctx.gpr[4]=holder;rejected=false;
                        try{ItemSlotData_PostProcessVSTable_807bad20(&ctx);}catch(const std::runtime_error&){rejected=true;}
                        if(!rejected || !guards())return 17;
                    }
                    for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r)return 12;
                    if(ctx.gpr[1]!=stack)return 13;
                    return 0;
                }
                """);
            var arguments=TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",arguments){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Item probability compilation timed out");
                Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Item probability execution timed out");
            Assert.True(run.ExitCode==0,$"Item probability returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
