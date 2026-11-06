using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class ItemManagerStorageTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealItemManagerKeepsItsPlayerCacheSeparateFromAllObjectHolders(int players)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var manifest=Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json");
        using var document=JsonDocument.Parse(File.ReadAllText(manifest));
        var layout=new GuestLayouts(document.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!,players);
        var plan=GuestLayoutRelocations.Read(manifest,players);
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));
        var translator=new FunctionTranslator(image,layoutRelocations:plan);
        var temp=Path.Combine(Path.GetTempPath(),"mkw-item-manager-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var entries=new uint[]{0x800202d4,0x80020494,0x8002058c,0x8078ccbc,0x8079012c,0x807902cc,
                0x807948b0,0x8079a60c,0x8079a904,0x80790664,0x80790788,0x80790844,0x80790994,0x807909ec,0x80790a44,0x80790ac0,0x807916c4};
            var fixtures=new HashSet<uint>{0x80229a48,0x80229a6c,0x8021a050,0x8021a0a4,0x807ae080,0x80787acc,0x807abd1c,
                0x807a9bac,0x807968a8,0x80791898,0x807d5cc4,0x807d9614,0x807abdb4,0x80791b5c,0x807d5d60,0x807d96c4,
                0x807d5d98,0x807d96fc,0x807acf8c,0x807ae8e0,0x807d6070,0x807d9d4c,0x80792d88,0x80792ec4,
                0x8078ccc0,0x8078e540,0x8078e6d4,0x8078d464,0x8078db24,0x8078e4a0,0x8078cd88,0x80790e44,
                0x8078e91c,0x807904d0,0x8078da3c,0x8078f83c,0x8078cf5c};
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
                #include <cstdio>
                #include <cstring>
                {{declarations}}
                constexpr uint32_t n={{players}},root=0x81010000,records=0x81100000,stack=0x81f00000,object=0x81200000,
                    size={{layout.Symbol("ItemManager.size")}},online={{layout.Symbol("ItemManager.otherLocalOnlinePlayers.offset")}},
                    holders={{layout.Symbol("ItemManager.holders.offset")}},dummy={{layout.Symbol("ItemManager.dummy.offset")}},
                    total={{layout.Symbol("ItemManager.totalItemCountMinus30.offset")}},
                    localA=n>13?12:n-2,localB=n-1;
                uint32_t allocations=0,initialized=0,constructed=0,playerInit=0,playerUpdates=0,holderUpdates=0,modelUpdates=0,
                    hidden=0,destructed=0,cleaned=0,resetPlayers=0,finishCallbacks=0;
                bool network=false;
                void no_op(CpuContext*){}
                void allocate(CpuContext* ctx){auto expected=allocations==0?size:n*584+16;
                    if(allocations>=2 || ctx->gpr[3]!=expected)std::abort();ctx->gpr[3]=allocations++?records-16:root;}
                uint32_t holderId(uint32_t p){if(p<root+holders || p>=root+holders+15*36 || (p-root-holders)%36)std::abort();return (p-root-holders)/36;}
                uint32_t playerId(uint32_t p){if(p<records || p>=records+n*584 || (p-records)%584)std::abort();return (p-records)/584;}
                void holder_init(CpuContext* ctx){auto id=holderId(ctx->gpr[3]);if(id!=initialized++ || ctx->gpr[4]!=id)std::abort();
                    for(uint32_t i=0;i<36;i+=4)Memory::Write32(ctx->gpr[3]+i,0x11220000+id*256+i);
                    Memory::Write32(ctx->gpr[3],id);Memory::Write32(ctx->gpr[3]+8,14+id);
                    Memory::Write32(ctx->gpr[3]+0x10,1);Memory::Write32(ctx->gpr[3]+0x14,1);Memory::Write32(ctx->gpr[3]+0x18,1);}
                void holder_update(CpuContext* ctx){if(holderId(ctx->gpr[3])!=holderUpdates++%15)std::abort();}
                void model_update(CpuContext* ctx){if(holderId(ctx->gpr[3])!=modelUpdates++%15)std::abort();}
                void holder_hide(CpuContext* ctx){if(holderId(ctx->gpr[3])!=hidden++)std::abort();}
                void holder_destroy(CpuContext* ctx){if(holderId(ctx->gpr[3])!=14-destructed++ || static_cast<int32_t>(ctx->gpr[4])!=-1)std::abort();}
                void holder_count(CpuContext* ctx){holderId(ctx->gpr[3]);ctx->gpr[3]=Memory::Read32(ctx->gpr[3]+0x10);}
                void holder_cleanup(CpuContext* ctx){if(holderId(ctx->gpr[3])!=cleaned++)std::abort();}
                void player_ctor(CpuContext* ctx){if(playerId(ctx->gpr[3])!=constructed++)std::abort();std::memset(Memory::GetPointer(ctx->gpr[3],584),0x7e,584);}
                void player_init(CpuContext* ctx){auto id=playerId(ctx->gpr[3]);if(id!=playerInit++ || ctx->gpr[4]!=id)std::abort();
                    Memory::Write8(ctx->gpr[3]+0x1a,network && id!=localA && id!=localB);}
                void player_update(CpuContext* ctx){if(playerId(ctx->gpr[3])!=playerUpdates++%n)std::abort();}
                void player_reset(CpuContext* ctx){if(playerId(ctx->gpr[3])!=resetPlayers++ || ctx->gpr[4]!=1)std::abort();}
                void manager_init_direct(CpuContext* ctx){if(ctx->gpr[3]!=root)std::abort();}
                void object_finished(CpuContext* ctx){
                    if(ctx->gpr[4]!=object || holderId(ctx->gpr[3])!=Memory::Read32(object+4) || holderId(ctx->gpr[3])!=finishCallbacks++)std::abort();}
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="item manager storage fixture";
                    info.rawCpuInvoker=entry;info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                bool guards(){for(uint32_t i=0;i<16;i++)if(Memory::Read8(root-16+i)!=0x55 || Memory::Read8(root+size+i)!=0x55 ||
                    Memory::Read8(records+n*584+i)!=0x55)return false;return true;}
                bool holder_storage(){for(uint32_t id=0;id<15;id++){auto p=root+holders+id*36;
                    if(Memory::Read32(p)!=id || Memory::Read32(p+8)!=14+id || Memory::Read32(p+0x10)!=1 || Memory::Read32(p+0x14)!=1)return false;}return true;}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    register_entry(0x80229a48,allocate);register_entry(0x80229a6c,allocate);
                    register_entry(0x8021a050,no_op);register_entry(0x8021a0a4,no_op);
                    for(uint32_t pc:{0x807ae080u,0x80787accu,0x807abd1cu,0x807a9bacu,0x807968a8u,0x80791898u,
                        0x807d5cc4u,0x807d9614u,0x807abdb4u,0x80791b5cu,0x807d5d60u,0x807d96c4u,
                        0x807d5d98u,0x807d96fcu,0x807acf8cu,0x807ae8e0u,0x807d6070u,0x807d9d4cu,0x80792d88u,0x80792ec4u})register_entry(pc,no_op);
                    register_entry(0x8078ccc0,holder_init);register_entry(0x8078e540,player_ctor);register_entry(0x8078e6d4,player_init);
                    register_entry(0x8078d464,holder_update);register_entry(0x8078db24,model_update);register_entry(0x8078e4a0,holder_count);
                    register_entry(0x8078cd88,holder_hide);register_entry(0x80790e44,manager_init_direct);register_entry(0x8078e91c,player_update);
                    register_entry(0x8078cf5c,object_finished);register_entry(0x807904d0,holder_destroy);register_entry(0x8078da3c,holder_cleanup);register_entry(0x8078f83c,player_reset);
                    for(uint32_t flag=0;flag<2;flag++){
                        network=flag;allocations=initialized=constructed=playerInit=playerUpdates=holderUpdates=modelUpdates=hidden=destructed=0;
                        cleaned=resetPlayers=finishCallbacks=0;
                        std::memset(Memory::GetPointer(root,size),0x7e,size);
                        for(uint32_t i=0;i<16;i++){Memory::Write8(root-16+i,0x55);Memory::Write8(root+size+i,0x55);Memory::Write8(records+n*584+i,0x55);}
                        Memory::Write32(0x809bee20,0);Memory::Write8(0x809bf0b0,n);Memory::Write8(0x809bf0b1,flag);Memory::Write8(0x809bf0b4,0);
                        for(uint32_t i=0;i<15;i++)Memory::Write32(0x809be750+i*116+0x5c,1);
                        CpuContext ctx{};ctx.gpr[1]=stack;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                        for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                        func_8079012C(&ctx);
                        if(ctx.gpr[3]!=root || allocations!=2 || initialized!=15 || constructed!=n || playerInit!=n)return 4;
                        if(Memory::Read32(root+dummy)!=0x808cd288 || Memory::Read16(root+dummy+0xc)!=0xffff ||
                            Memory::Read32(root+dummy+0x74)!=0 || Memory::Read32(root+dummy+0x78)!=0 || Memory::Read32(root+dummy+0x7c)!=64)return 15;
                        if(Memory::Read32(root+0x14)!=records || Memory::Read32(root+total)!=0 || !holder_storage() || !guards())return 5;
                        for(uint32_t i=0;i<n;i++)if(Memory::Read32(root+online+i*4)!=(flag && i==localA?records+localB*584:flag && i==localB?records+localA*584:0))return 6;
                        // The member-function thunk branches to a body outside its MAP span.
                        for(uint32_t id=0;id<15;id++){Memory::Write32(object+4,id);ctx.gpr[3]=object;func_8079A904(&ctx);}
                        if(finishCallbacks!=15 || !holder_storage())return 16;
                        ctx.gpr[3]=root;func_80790788(&ctx);if(hidden!=15 || !holder_storage())return 7;
                        ctx.gpr[3]=root;func_80790844(&ctx);
                        if(holderUpdates!=15 || modelUpdates!=15 || playerUpdates!=n || Memory::Read32(root+total)!=static_cast<uint32_t>(-15))return 8;
                        ctx.gpr[3]=root;func_80790994(&ctx);ctx.gpr[3]=root;func_807909EC(&ctx);ctx.gpr[3]=root;func_80790AC0(&ctx);
                        if(holderUpdates!=30 || modelUpdates!=30 || playerUpdates!=2*n)return 9;
                        Memory::Write32(root+total,0);ctx.gpr[3]=root;func_80790A44(&ctx);
                        if(Memory::Read32(root+total)!=static_cast<uint32_t>(-15))return 10;
                        func_807916C4(&ctx);if(cleaned!=15 || resetPlayers!=n || !holder_storage())return 11;
                        Memory::Write32(0x809bee20,0);ctx.gpr[3]=root;ctx.gpr[4]=0;func_80790664(&ctx);
                        if(destructed!=15 || !guards())return 12;
                        for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r)return 13;
                        if(ctx.gpr[1]!=stack)return 14;
                    }
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Item manager compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Item manager execution timed out");
            Assert.True(run.ExitCode==0,$"Item manager returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
