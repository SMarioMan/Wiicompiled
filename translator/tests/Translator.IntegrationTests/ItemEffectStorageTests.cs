using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class ItemEffectStorageTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealPowAndBlooperUseUniformPlayerArraysAndIndependentScreenSlots(int players)
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
        var temp=Path.Combine(Path.GetTempPath(),"mkw-item-effects-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var entries=new uint[]{0x807d9614,0x807d976c,0x807d96fc,0x807d9808,0x807d9940,0x807d9ec8,0x807da250,
                0x807d4f74,0x807d5ee8,0x807d5cc4,0x807d5e60,0x807d5d98,0x807d53f4,0x807d64ec,0x807d5154};
            var fixtures=new HashSet<uint>{0x80229a48,0x807d9f94,0x807d5220,0x80558978,0x80558960,
                0x807d5510,0x80552eb4,0x807acf74,0x807adf1c,0x80010004,0x8055304c,0x80552fc0,0x8058a5e0,0x805898dc,0x8058a5bc,0x8058aaf0,0x80809f90,0x80010000,0x80010008};
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
                constexpr uint32_t n={{players}},powRoot=0x81010000,gesso=0x81020000,screenBase=0x81030000,
                    raceinfo=0x81040000,playerPointers=0x81050000,racePlayers=0x81060000,positions=0x81070000,
                    itemManager=0x81080000,itemPlayers=0x81100000,kartStates=0x81200000,racedata=0x81300000,stack=0x81f00000,
                    powSize={{layout.Symbol("PowManager.size")}},powScreens={{layout.Symbol("PowManager.screens.offset")}},
                    shockwave={{layout.Symbol("PowManager.shockwaveStarted.offset")}},
                    gessoSize={{layout.Symbol("GessoManager.size")}},gessoScreens={{layout.Symbol("GessoManager.screens.offset")}};
                uint32_t screenCount=0,screenAllocations=0,modelCalls=0,manager=0,managerSize=0,screenSize=0,screenOffset=0,resetCalls=0;
                int32_t immune=-1;
                void no_op(CpuContext*){}
                void reset_model(CpuContext* ctx){if(ctx->gpr[3]!=0x81090000 || ctx->gpr[4]!=0)std::abort();resetCalls++;}
                void model_animation(CpuContext* ctx){ctx->gpr[3]=0x81092000;}
                void linked_animation(CpuContext* ctx){ctx->gpr[3]=0x81093000;}
                void valid_animation(CpuContext* ctx){ctx->gpr[3]=1;}
                void allocate(CpuContext* ctx){
                    if(ctx->gpr[3]==managerSize && screenAllocations==0){ctx->gpr[3]=manager;return;}
                    if(ctx->gpr[3]!=screenSize || screenAllocations>=screenCount)std::abort();
                    ctx->gpr[3]=screenBase+screenAllocations++*256;}
                void screen_ctor(CpuContext* ctx){
                    auto slot=ctx->gpr[4];if(slot>=screenCount || ctx->gpr[3]!=screenBase+slot*256)std::abort();
                    auto p=ctx->gpr[3];std::memset(Memory::GetPointer(p,256),0,256);
                    if(manager==powRoot){Memory::Write32(p,0x81090000);Memory::Write32(p+4,slot);Memory::Write32(p+8,n);}
                    else{Memory::Write32(p+4,0x81090000);Memory::Write32(p+8,slot);Memory::Write16(p+14,n);
                        Memory::Write32(p+32,0x81091000);} }
                void model(CpuContext* ctx){if(ctx->gpr[3]!=0x81090000 || ctx->gpr[4]>=screenCount)std::abort();modelCalls++;}
                void get_kart(CpuContext* ctx){if(ctx->gpr[4]>=n)std::abort();ctx->gpr[3]=kartStates+ctx->gpr[4]*32;}
                void local(CpuContext* ctx){auto id=(ctx->gpr[3]-kartStates)/32;ctx->gpr[3]=id>=n-screenCount;}
                void hud(CpuContext* ctx){auto p=ctx->gpr[3];uint32_t id;
                    if(p>=itemPlayers && p<itemPlayers+n*584)id=(p-itemPlayers)/584;
                    else if(p>=kartStates && p<kartStates+n*32)id=(p-kartStates)/32;else std::abort();
                    ctx->gpr[3]=id>=n-screenCount?id-(n-screenCount):uint32_t(-1);}
                void immunity(CpuContext* ctx){ctx->gpr[3]=int32_t((ctx->gpr[3]-itemPlayers)/584)==immune;}
                void character_size(CpuContext* ctx){ctx->gpr[3]=0;}
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="item effects fixture";
                    info.rawCpuInvoker=entry;info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                void guards(){for(uint32_t i=0;i<16;i++)if(Memory::Read8(manager-16+i)!=0x55 || Memory::Read8(manager+managerSize+i)!=0x55)std::abort();
                    for(uint32_t slot=0;slot<screenCount;slot++)if(Memory::Read32(manager+screenOffset+slot*4)!=screenBase+slot*256)std::abort();}
                void prepare(uint32_t root,uint32_t size,uint32_t offset,uint32_t element){
                    manager=root;managerSize=size;screenOffset=offset;screenSize=element;screenAllocations=modelCalls=0;
                    std::memset(Memory::GetPointer(root-16,size+32),0x55,size+32);
                    Memory::Write8(racedata+0x25,screenCount);Memory::Write32(0x809b8f68,racedata);}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    register_entry(0x807d5510,no_op);register_entry(0x80552eb4,linked_animation);
                    register_entry(0x807acf74,no_op);register_entry(0x807adf1c,no_op);register_entry(0x80010004,valid_animation);
                    register_entry(0x80229a48,allocate);register_entry(0x807d9f94,screen_ctor);register_entry(0x807d5220,screen_ctor);
                    register_entry(0x80558978,model);register_entry(0x80558960,model);register_entry(0x8055304c,no_op);register_entry(0x80552fc0,model_animation);
                    register_entry(0x805898dc,get_kart);register_entry(0x8058a5bc,local);register_entry(0x8058a5e0,hud);
                    register_entry(0x8058aaf0,immunity);register_entry(0x80809f90,character_size);register_entry(0x80010000,no_op);register_entry(0x80010008,reset_model);
                    Memory::Write32(0x8109300c,0x81093100);Memory::Write32(0x81093100,0x81091000);
                    Memory::Write32(0x8109312c,0x81093200);Memory::Write16(0x81093220,60);
                    Memory::Write32(0x81091008,0x80010004);Memory::Write32(0x810910e0,0x80010000);
                    Memory::Write32(0x81092000,0x81091000);Memory::Write32(0x8109101c,0x80010000);Memory::Write32(0x81090000,0x81091000);Memory::Write32(0x8109100c,0x80010000);Memory::Write32(0x81091118,0x80010000);
                    Memory::Write32(0x809b8f70,raceinfo);Memory::Write32(raceinfo+12,playerPointers);Memory::Write32(raceinfo+24,positions);
                    Memory::Write32(0x809bee20,itemManager);Memory::Write32(itemManager+20,itemPlayers);Memory::Write8(0x809bf0b0,n);
                    for(uint32_t i=0;i<n;i++){
                        Memory::Write32(playerPointers+i*4,racePlayers+i*128);Memory::Write8(racePlayers+i*128+32,i+1);Memory::Write8(positions+i,i);
                        Memory::Write32(itemPlayers+i*584,kartStates+i*32);Memory::Write32(kartStates+i*32+4,kartStates+i*32+16);
                        Memory::Write32(kartStates+i*32+24,0);Memory::Write32(kartStates+i*32+28,0);Memory::Write32(itemPlayers+i*584+0x240,0);
                    }
                    CpuContext ctx{};ctx.gpr[1]=stack;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                    for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                    for(screenCount=1;screenCount<=4;screenCount++){
                        for(uint32_t ctor=0;ctor<2;ctor++){
                            prepare(powRoot,powSize,powScreens,16);ctx.gpr[3]=powRoot;
                            if(ctor)func_807D976C(&ctx);else func_807D9614(&ctx);
                            if(screenAllocations!=screenCount || Memory::Read32(powRoot+4)!=screenCount || Memory::Read8(powRoot+8)!=n)return 4;
                            Memory::Write32(0x809bf798,powRoot);func_807D96FC(&ctx);guards();
                            for(uint32_t team=0;team<2;team++)for(uint32_t user=0;user<n;user++){
                                Memory::Write8(0x809bf0b3,team);Memory::Write8(0x809bf0b5,team);
                                for(uint32_t i=0;i<n;i++)Memory::Write32(racedata+0xf4+i*240,i%2);
                                Memory::Write32(powRoot,0);Memory::Write8(powRoot+shockwave,0x55);
                                ctx.gpr[3]=powRoot;ctx.gpr[4]=user;func_807D9808(&ctx);
                                if(Memory::Read32(powRoot)!=159 || Memory::Read8(powRoot+8)!=user || Memory::Read8(powRoot+shockwave)!=0)return 5;
                                for(uint32_t i=0;i<n;i++)if(Memory::Read8(powRoot+9+i)!=(i!=user && (team?i%2!=user%2:i<user)))return 6;
                                for(uint32_t effect=1;effect<=3;effect++){ctx.gpr[3]=powRoot;ctx.gpr[4]=effect;func_807D9940(&ctx);guards();}
                            }
                            ctx.gpr[3]=powRoot;func_807D9EC8(&ctx);guards();ctx.gpr[3]=powRoot;func_807DA250(&ctx);guards();
                            if(Memory::Read8(powRoot+8)!=n)return 7;
                            for(uint32_t slot=0;slot<screenCount;slot++)if(Memory::Read32(screenBase+slot*256+8)!=n)return 8;
                            prepare(gesso,gessoSize,gessoScreens,208);ctx.gpr[3]=gesso;
                            if(ctor)func_807D5E60(&ctx);else func_807D5CC4(&ctx);
                            if(screenAllocations!=screenCount || Memory::Read32(gesso)!=screenCount)return 9;
                            for(uint32_t repeat=0;repeat<2;repeat++){
                                for(uint32_t i=0;i<n;i++)Memory::Write32(gesso+16+i*4,0x12340000+i);
                                for(uint32_t slot=0;slot<screenCount;slot++){Memory::Write32(screenBase+slot*256,0x12345678);Memory::Write16(screenBase+slot*256+14,0xffff);}
                                Memory::Write32(gesso+8,0x12345678);Memory::Write32(0x8109100c,0x80010008);resetCalls=0;
                                ctx.gpr[3]=gesso;func_807D5154(&ctx);guards();
                                if(resetCalls!=screenCount || Memory::Read32(gesso+8)!=0)return 19;
                                for(uint32_t i=0;i<n;i++)if(Memory::Read32(gesso+16+i*4)!=0x12340000+i)return 20;
                                for(uint32_t slot=0;slot<screenCount;slot++)if(Memory::Read32(screenBase+slot*256)!=3 || Memory::Read16(screenBase+slot*256+14)!=n)return 21;
                                Memory::Write32(0x8109100c,0x80010000);
                            }
                            Memory::Write32(0x809bf790,gesso);func_807D5D98(&ctx);guards();
                            ctx.gpr[3]=gesso;func_807D53F4(&ctx);guards();
                            for(uint32_t i=0;i<n;i++)if(Memory::Read32(gesso+16+i*4)!=0)return 10;
                            for(uint32_t team=0;team<2;team++)for(uint32_t user=0;user<n;user++)for(uint32_t shield=0;shield<2;shield++){
                                Memory::Write8(0x809bf0b3,team);Memory::Write8(0x809bf0b5,0);immune=shield?int32_t(user/2):-1;
                                Memory::Write32(gesso+8,0);Memory::Write8(itemPlayers+user*584+24,user);
                                if(shield){ctx.gpr[3]=gesso;ctx.gpr[4]=user;func_807D5EE8(&ctx);}
                                else{ctx.gpr[3]=itemPlayers+user*584;func_807D4F74(&ctx);}
                                if(Memory::Read16(gesso+4)!=user || Memory::Read32(gesso+8)!=1 || Memory::Read32(gesso+12)!=90)return 14;
                                for(uint32_t slot=0;slot<screenCount;slot++)if(Memory::Read16(screenBase+slot*256+14)!=n)return 15;
                                guards();
                                for(uint32_t slot=0;slot<screenCount;slot++)Memory::Write16(screenBase+slot*256+14,n);
                                ctx.gpr[3]=gesso;func_807D64EC(&ctx);guards();
                                for(uint32_t i=0;i<n;i++){
                                    auto expected=team?(i%2!=user%2?2:0):i<(user?user:1) && int32_t(i)!=immune?3-(i*12/n)/4:0;
                                    if(Memory::Read32(gesso+16+i*4)!=expected){std::fprintf(stderr,"blooper n=%u team=%u user=%u id=%u got=%u expected=%u\n",n,team,user,i,Memory::Read32(gesso+16+i*4),expected);return 11;}
                                }
                            }
                        }
                    }
                    for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r)return 12;
                    if(ctx.gpr[1]!=stack)return 13;
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Item effects compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Item effects execution timed out");
            Assert.True(run.ExitCode==0,$"Item effects returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{if(Environment.GetEnvironmentVariable("MKW_KEEP_EFFECT_TEST_TEMP")!="1")Directory.Delete(temp,recursive:true);
            else Console.Error.WriteLine("Effect fixture: "+temp);}
    }
}
