using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class BattleBalloonStorageTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealBattleBalloonManagerSeparatesObjectsPlayerRecordsAndTrailingState(int players) => Run(players,false);

    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(25)] [InlineData(26)]
    [InlineData(48)] [InlineData(52)] [InlineData(64)] [InlineData(100)]
    [InlineData(116)] [InlineData(118)] [InlineData(127)] [InlineData(128)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealWideBalloonCountersAndSelectedOrdinalsSupportTheFullActiveField(int players) => Run(players,true);

    private static void Run(int players,bool fullField)
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
        var temp=Path.Combine(Path.GetTempPath(),"mkw-battle-balloons-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            // Retain the original kart cleanup loop and its frame/save/restore;
            // skip surrounding unrelated kart/scene work at checked seams.
            foreach(var (pc,target) in new[]{(0x80591bfcu,0x80591d14u),(0x80591d9cu,0x80591df4u)})
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(pc,4),4),0x48000000u|((target-pc)&0x03fffffcu));
            var entries=new uint[]{0x80591be8,0x80020a3c,0x80020a88,0x8086538c,0x80865454,0x808655a8,0x80865414,0x80865818,0x808659c4,0x80865ba0,0x80865cac};
            var fixtures=new HashSet<uint>{0x80229a48,0x8087a59c,0x80010000,0x80010004,0x80010008,0x8001000c,0x80229a90,0x8087afd4,0x8087b394,0x8087b1c8};
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
                constexpr uint32_t capacity={{players}},fullField={{(fullField?1:0)}},minActive=fullField?capacity:1,maxActive=fullField?capacity:capacity<24?capacity:24,
                    size={{layout.Symbol("BattleBalloonManager.size")}},playerOffset={{layout.Symbol("BattleBalloonManager.players.offset")}},
                    tick={{layout.Symbol("BattleBalloonManager.tick.offset")}},duration={{layout.Symbol("BattleBalloonManager.duration.offset")}},
                    changed={{layout.Symbol("BattleBalloonManager.changed.offset")}},
                    root=0x81010000,racedata=0x81200000,karts=0x81030000,vtable=0x81040000,objects=0x81100000,stack=0x81f00000;
                uint32_t readyAnimation=1;
                uint32_t active=0,allocations=0,constructed=0,started=0,initialized=0,freed=0,updated=0,models=0;
                void allocate(CpuContext* ctx){
                    auto expected=allocations?484:size;if(ctx->gpr[3]!=expected || allocations>active*10)std::abort();
                    ctx->gpr[3]=allocations?objects+(allocations-1)*512:root;allocations++;}
                uint32_t id(uint32_t p){if(p<objects || p>=objects+active*10*512 || (p-objects)%512)std::abort();return (p-objects)/512;}
                void object_ctor(CpuContext* ctx){auto i=id(ctx->gpr[3]);
                    if(i!=constructed++ || ctx->gpr[4]!=i || ctx->gpr[5]!=i/(active*5))std::abort();
                    Memory::Write32(ctx->gpr[3],vtable);
                    // Ready animation-state fixture used by the real free-balloon
                    // selection path before it falls back to any unowned entry.
                    auto object=ctx->gpr[3];Memory::Write32(object+376,object+416);Memory::Write32(object+380,0);
                    Memory::Write32(object+384,0);Memory::Write8(object+389,readyAnimation);Memory::Write32(object+416,0);
                    auto cleanup=0x81400000+i*16;Memory::Write32(object+472,cleanup);Memory::Write32(cleanup,0);
                    Memory::Write8(cleanup+12,1);Memory::Write8(object+480,0);}
                void start(CpuContext* ctx){if(id(ctx->gpr[3])!=started++)std::abort();}
                void init(CpuContext* ctx){if(id(ctx->gpr[3])!=initialized++%(active*10))std::abort();}
                void update(CpuContext* ctx){if(id(ctx->gpr[3])!=updated++%(active*10))std::abort();}
                void model(CpuContext* ctx){if(id(ctx->gpr[3])!=models++%(active*10))std::abort();}
                void balloon_action(CpuContext* ctx){id(ctx->gpr[3]);}
                void free(CpuContext* ctx){if(ctx->gpr[3]!=root)std::abort();freed++;}
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="battle balloon storage fixture";
                    info.rawCpuInvoker=entry;info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                bool guards(){for(uint32_t i=0;i<16;i++)if(Memory::Read8(root-16+i)!=0x55 || Memory::Read8(root+size+i)!=0x55)return false;return true;}
                bool records(){
                    std::array<bool,capacity*10> held{};uint32_t sum=0;
                    for(uint32_t p=0;p<active;p++){
                        uint32_t base=root+playerOffset+p*24,count=Memory::Read8(base);if(count>5)return false;sum+=count;
                        for(uint32_t slot=0;slot<5;slot++){
                            auto object=Memory::Read32(base+4+slot*4);
                            if(slot>=count){if(object)return false;continue;}
                            if(!object)return false;auto index=id(object);if(held[index])return false;held[index]=true;
                            if(Memory::Read8(root+4+index*8+5)!=p || Memory::Read8(root+4+index*8+6)!=slot)return false;
                        }
                    }
                    uint32_t assigned=0;
                    for(uint32_t i=0;i<active*10;i++){
                        if(Memory::Read32(root+4+i*8)!=objects+i*512)return false;
                        bool live=Memory::Read8(root+4+i*8+5)!=active;
                        if(live!=held[i])return false;assigned+=live;
                    }
                    return sum==assigned && guards();
                }
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    register_entry(0x80229a48,allocate);register_entry(0x8087a59c,object_ctor);register_entry(0x80229a90,free);
                    register_entry(0x80010000,start);register_entry(0x80010004,init);
                    register_entry(0x80010008,update);register_entry(0x8001000c,model);
                    register_entry(0x8087afd4,balloon_action);register_entry(0x8087b394,balloon_action);register_entry(0x8087b1c8,balloon_action);
                    Memory::Write32(vtable+32,0x80010000);Memory::Write32(vtable+12,0x80010004);
                    Memory::Write32(vtable+24,0x80010008);Memory::Write32(vtable+28,0x8001000c);
                    Memory::Write32(0x809b8f68,racedata);Memory::Write32(0x809bd110,karts);
                    Memory::Write32(racedata+{{layout.Symbol("Racedata.race.offset+Scenario.settings.offset")}}+8,3);
                    Memory::Write32(racedata+{{layout.Symbol("Racedata.race.offset+Scenario.settings.offset")}}+12,0);
                    Memory::Write32(racedata+{{layout.Symbol("Racedata.race.offset+Scenario.settings.offset")}}+16,0);
                    CpuContext ctx{};ctx.gpr[1]=stack;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                    for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                    // Legacy cases retain every original active count through
                    // 24. Full-field cases also exercise both preferred and
                    // fallback selection with high pool ordinals.
                    for(active=minActive;active<=maxActive;active++)for(uint32_t ctor=0;ctor<2;ctor++){
                        readyAnimation=fullField&&ctor?0:1;
                        allocations=constructed=started=initialized=freed=0;std::memset(Memory::GetPointer(root-16,size+32),0x55,size+32);
                        Memory::Write8(karts+36,active);Memory::Write32(0x809bff98,0);
                        ctx.gpr[3]=root;if(ctor){allocations=1;func_80865454(&ctx);}else func_8086538C(&ctx);
                        if(allocations!=active*10+1 || constructed!=active*10 || started!=active*10 || !guards())return 4;
                        if(Memory::Read8(root)!=active || Memory::Read8(root+1)!=static_cast<uint8_t>(active*5) || Memory::Read8(root+2)!=static_cast<uint8_t>(active*10))return 5;
                        for(uint32_t i=0;i<active*10;i++)if(Memory::Read32(root+4+i*8)!=objects+i*512 || Memory::Read8(root+8+i*8)!=i/(active*5))return 6;
                        for(uint32_t repeat=0;repeat<2;repeat++){
                            ctx.gpr[3]=root;func_808655A8(&ctx);if(initialized!=(repeat+1)*active*10 || !guards())return 7;
                            for(uint32_t i=0;i<active;i++){
                                auto p=root+playerOffset+i*24;if(Memory::Read8(p)!=0)return 8;
                                for(uint32_t j=1;j<4;j++)if(Memory::Read8(p+j)!=0x55)return 8;
                                for(uint32_t j=4;j<24;j+=4)if(Memory::Read32(p+j)!=0)return 8;
                            }
                            for(uint32_t i=0;i<active*10;i++)if(Memory::Read32(root+4+i*8)!=objects+i*512 ||
                                Memory::Read8(root+8+i*8)!=i/(active*5) || Memory::Read8(root+9+i*8)!=active || Memory::Read8(root+10+i*8)!=5)return 9;
                            if(Memory::Read32(root+tick)!=0 || Memory::Read32(root+duration)!=120)return 10;
                            for(uint32_t i=0;i<capacity;i++)if(Memory::Read8(root+changed+i)!=0)return 11;
                        }
                        for(uint32_t p=0;p<active;p++){
                            ctx.gpr[3]=root;ctx.gpr[4]=p;ctx.gpr[5]=p%2;ctx.gpr[6]=0;ctx.gpr[7]=0;ctx.gpr[8]=5;ctx.gpr[9]=1;
                            func_808659C4(&ctx);if(Memory::Read8(root+playerOffset+p*24)!=5 || !records())return 19;
                        }
                        for(uint32_t p=0;p<active;p++){
                            ctx.gpr[3]=root;ctx.gpr[4]=p;ctx.gpr[5]=0;ctx.gpr[6]=0;ctx.gpr[7]=0;ctx.gpr[8]=1;ctx.gpr[9]=1;
                            func_80865BA0(&ctx);if(Memory::Read8(root+playerOffset+p*24)!=4 || !records())return 20;
                        }
                        for(uint32_t p=0;p+1<active;p+=2){
                            // Fill the receiver, then transfer to a full receiver:
                            // the source loses a balloon on both original paths.
                            for(uint32_t repeat=0;repeat<2;repeat++){
                                ctx.gpr[3]=root;ctx.gpr[4]=p;ctx.gpr[5]=p+1;ctx.gpr[6]=0;ctx.gpr[7]=1;ctx.gpr[8]=1;
                                func_80865CAC(&ctx);
                                if(Memory::Read8(root+playerOffset+p*24)!=5 || Memory::Read8(root+playerOffset+(p+1)*24)!=3-repeat || !records())return 21;
                            }
                        }
                        for(uint32_t p=0;p<active;p++){
                            ctx.gpr[3]=root;ctx.gpr[4]=p;ctx.gpr[5]=0;ctx.gpr[6]=0;ctx.gpr[7]=0;ctx.gpr[8]=5;ctx.gpr[9]=1;
                            func_80865BA0(&ctx);if(Memory::Read8(root+playerOffset+p*24)!=0 || !records())return 22;
                        }
                        updated=models=0;
                        for(uint32_t repeat=0;repeat<3;repeat++){
                            for(uint32_t i=0;i<capacity;i++)Memory::Write8(root+changed+i,1);
                            ctx.gpr[3]=root;func_80865818(&ctx);
                            if(updated!=(repeat+1)*active*10 || models!=updated || !guards())return 15;
                            if(Memory::Read32(root+tick)!=repeat+1 || Memory::Read32(root+duration)!=120)return 16;
                            for(uint32_t i=0;i<capacity;i++)if(Memory::Read8(root+changed+i)!=(i<active?0:1))return 17;
                            for(uint32_t i=0;i<active*10;i++)if(Memory::Read32(root+4+i*8)!=objects+i*512)return 18;
                        }
                        Memory::Write32(0x809bff98,root);func_80591BE8(&ctx);
                        for(uint32_t i=0;i<active*10;i++)if(Memory::Read8(objects+i*512+480)!=1 || Memory::Read8(0x81400000+i*16+12)!=0)return 23;
                        func_80865414(&ctx);if(freed!=1 || Memory::Read32(0x809bff98)!=0 || !guards())return 12;
                    }
                    for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r)return 13;
                    if(ctx.gpr[1]!=stack)return 14;
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Battle balloon compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(30000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Battle balloon execution timed out");
            Assert.True(run.ExitCode==0,$"Battle balloon returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
