using System.Diagnostics;
using System.Buffers.Binary;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class FriendRoomStorageTests
{
    [Fact]
    [Trait("Requires", "USA game assets")]
    public void EveryMovedFriendRoomMemberAccessHasACheckedRelocation()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "translator/Translator.sln"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "projects/mkwii-ntsc-u/layouts/racedata.json")));
        var layout = new GuestLayouts(doc.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!, 12);
        var references = doc.RootElement.GetProperty("References").EnumerateArray()
            .Select(reference => Convert.ToUInt32(reference.GetProperty("Instruction").GetString(), 16)).ToHashSet();
        var fills = doc.RootElement.GetProperty("ArrayFills").EnumerateArray().Select(fill => (
            Begin: Convert.ToUInt32(fill.GetProperty("Begin").GetString(), 16),
            Bytes: (uint)fill.GetProperty("ExpectedWords").GetArrayLength() * 4)).ToArray();
        var image = new ProgramImageBuilder().Build(Path.Combine(root, "Assets/main.dol"), Path.Combine(root, "Assets/StaticR.rel"), 0x8050bf60);
        // These are the manager's own methods. In particular, its queue cursor
        // can lose an exact receiver proof on a loop back edge; absence of a
        // pointer-analysis suggestion does not make its old offsets safe.
        foreach (var (begin, end) in new[] { (0x8062a0acu, 0x8062b224u), (0x8062b3a4u, 0x8062b3f0u) })
        for (var pc = begin; pc < end; pc += 4)
        {
            var word = BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(pc, 4), 4));
            var opcode = word >> 26;
            var memberOffset = word & 0xffff;
            var memberAccess = opcode is >= 32 and <= 55 || opcode == 14 && (word >> 16 & 31) != 0;
            if (!memberAccess || memberOffset < layout.Symbol("FriendRoomManager.controls.end") || memberOffset >= layout.Symbol("FriendRoomManager.size")) continue;
            Assert.True(references.Contains(pc) || fills.Any(fill => fill.Begin <= pc && pc < fill.Begin + fill.Bytes),
                $"Unchecked moved friend-room access at 0x{pc:X8}: 0x{word:X8}");
        }
    }

    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(48)] [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealFriendRoomLifecycleConstructsLoadsResetsAndDestroysEveryConsoleControl(int players)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName ?? throw new DirectoryNotFoundException();
        var manifest=Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json");
        using var doc=JsonDocument.Parse(File.ReadAllText(manifest));
        var layout=new GuestLayouts(doc.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!,players);
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));
        var translator=new FunctionTranslator(image,layoutRelocations:GuestLayoutRelocations.Read(manifest,players));
        var temp=Path.Combine(Path.GetTempPath(),"mkw-friend-room-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try {
            uint[] entries={0x8062a0ac,0x8062a140,0x8062a1e8,0x8062a324,0x806299d0,0x80629a44,0x80020494,0x8002058c,0x8062ae74,0x8062a814};
            uint[] fixtures={0x805dd09c,0x805d4094,0x8060c378,0x805d9350,0x805d9594,0x805ddb94,0x80517a5c,0x805d9768,
                0x805ddb7c,0x805ddb8c,0x808413a4,0x808417a4,0x80629a9c,0x805d93e4,0x805d9a8c,0x805d9674,0x805d938c,
                0x8060c4a0,0x805d40b8,0x805dd124,0x80229a90};
            var sources=new List<string>();
            foreach(var pc in entries){var file=Path.Combine(temp,$"{pc:X8}.cpp");File.WriteAllText(file,translator.Translate(pc,
                new TranslationOptions(MaxBytes:pc==0x8062ae74 ? 0x5c : pc==0x8062a814 ? 0x40 : 0x10000,KnownFunctionEntryPoints:map.Addresses.Concat(new uint[]{0x8062aed0,0x8062a854}).ToHashSet(),LeafInliningBlockedTargets:fixtures.Concat(new uint[]{0x8062aed0,0x8062a854}).ToHashSet())).CxxCode);sources.Add(file);}
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var header=Path.Combine(temp,"layout.h");File.WriteAllText(header,GuestLayoutRelocations.Read(manifest,players).EmitNativeConstants());
            var glue=Path.Combine(temp,"glue.cpp");File.WriteAllText(glue,$"#define MKW_TRANSLATED_PLAYER_CAPACITY {players}\n#define MKW_TRANSLATED_GUEST_LAYOUT_HEADER \"{header}\"\n#include \"{root}/runtime/game/guest_layout_values.cpp\"\n");
            sources.Add(glue);sources.Add(Path.Combine(root,"runtime/src/hle/friend_room.cpp"));sources.Add(Path.Combine(root,"runtime/src/guest_region.cpp"));
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <algorithm>
                #include <bit>
                #include <set>
                #include <vector>
                #include <cstdio>
                #include <cstring>
                #define CHECK(value) do { if(!(value)){std::fprintf(stderr,"friend room failure at line %d\n",__LINE__);std::abort();} } while(false)
                {{string.Join("\n",entries.Select(pc=>$"extern \"C\" void func_{pc:X8}(CpuContext*);"))}}
                extern "C" void FriendRoomManager_UpdateExpanded(CpuContext*);
                constexpr uint32_t page=0x81010000,stack=0x81f00000,scene=0x81080000,heap=0x81090000,vtable=0x810a0000,
                    paneBase=0x81200000,n={{players}},slots=2*n,size={{layout.Symbol("FriendRoomManager.size")}},
                    controls={{layout.Symbol("FriendRoomManager.controls.offset")}},stride={{layout.Symbol("FriendMatchingPlayer.size")}},
                    message={{layout.Symbol("FriendRoomManager.message.offset")}},miis={{layout.Symbol("FriendRoomManager.miis.offset")}},
                    ids={{layout.Symbol("FriendRoomManager.playerIds.offset")}},countOffset={{layout.Symbol("FriendRoomManager.playerCount.offset")}},
                    sequences={{layout.Symbol("FriendRoomManager.consoleSequences.offset")}},timers={{layout.Symbol("FriendRoomManager.timers.offset")}},
                    queueCount={{layout.Symbol("FriendRoomManager.queueCount.offset")}},queue={{layout.Symbol("FriendRoomManager.queue.offset")}};
                uint32_t loaded=0,baseControls=0,destroyed=0,resets=0,groups=0,updates=0;
                std::set<uint32_t> added;
                void noop(CpuContext*){}
                void checkpoint(CpuContext*){throw 42;}
                void expectedWord(std::vector<uint8_t>& bytes,uint32_t at,uint32_t value){for(unsigned i=0;i<4;i++)bytes[at+i]=static_cast<uint8_t>(value>>(24-i*8));}
                void pageCtor(CpuContext* c){CHECK(c->gpr[3]==page);std::memset(Memory::GetPointer(page,0x1c8),0,0x1c8);}
                void inputCtor(CpuContext* c){CHECK(c->gpr[3]==page+0x44);Memory::Write32(c->gpr[3],vtable);}
                void controlCtor(CpuContext* c){++baseControls;std::memset(Memory::GetPointer(c->gpr[3],0x174),0,0x174);}
                void messageCtor(CpuContext* c){CHECK(c->gpr[3]==page+message);}
                void miiCtor(CpuContext* c){CHECK(c->gpr[3]==page+miis);}
                void currentScene(CpuContext* c){c->gpr[3]=scene;}
                void miiInit(CpuContext* c){CHECK(c->gpr[3]==page+miis && c->gpr[4]==slots && c->gpr[5]==4 && c->gpr[6]==heap);++groups;}
                void controlCount(CpuContext* c){CHECK(c->gpr[3]==page && c->gpr[4]==slots+2);}
                void addControl(CpuContext* c){const auto index=c->gpr[4];CHECK(c->gpr[3]==page && index<=slots+1 && added.insert(index).second);
                    CHECK(c->gpr[5]==(index==0 ? page+0x54 : index==slots+1 ? page+message : page+controls+(index-1)*stride));}
                void loadControl(CpuContext* c){CHECK(loaded<slots && c->gpr[3]==page+controls+loaded*stride && c->gpr[4]==page+miis);
                    CHECK(c->gpr[5]==loaded/2 && c->gpr[6]==loaded%2);
                    Memory::Write32(c->gpr[3]+0x194,paneBase+loaded*0x80);Memory::Write32(paneBase+loaded*0x80+0x30,std::bit_cast<uint32_t>(140.f));++loaded;}
                void reset(CpuContext* c){CHECK(c->gpr[3]==page+miis);++resets;
                    CHECK(Memory::Read32(page+countOffset)==0 && Memory::Read32(page+queueCount)==0);
                    for(uint32_t i=0;i<n;i++)CHECK(Memory::Read8(page+ids+i)==255 && Memory::Read8(page+sequences+i)==0);
                    for(uint32_t i=0;i<slots;i++)CHECK(Memory::Read32(page+timers+i*4)==0);}
                void destroyControl(CpuContext*){++destroyed;}
                void afterUpdate(CpuContext* c){CHECK(c->gpr[3]==page);++updates;c->gpr[3]=0x12345678;}
                void entry(uint32_t pc,void(*f)(CpuContext*)){TranslatedFunctionInfo info{};info.address=pc;info.name="friend room lifecycle fixture";info.rawCpuInvoker=f;info.entryPoint=reinterpret_cast<void*>(f);TranslatedFunctionRegistry::Register(info);}
                int main(int argc,char** argv){if(argc!=2)return 1;Memory::Init(32*1024*1024);
                    auto file=std::fopen(argv[1],"rb");if(!file)return 2;auto bytes=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},file);std::fclose(file);if(bytes!={{image.Memory.Length}})return 3;
                    {{string.Join("\n",entries.Select(pc=>$"entry(0x{pc:X8},func_{pc:X8});"))}}
                    {{string.Join("\n",new uint[]{0x805ddb94,0x808413a4,0x808417a4,0x805d93e4,0x805d9674,0x805d938c,0x805d40b8,0x805dd124,0x80229a90}.Select(pc=>$"entry(0x{pc:X8},noop);"))}}
                    entry(0x805dd09c,pageCtor);entry(0x805d4094,inputCtor);entry(0x8060c378,controlCtor);entry(0x805d9350,messageCtor);entry(0x805d9594,miiCtor);
                    entry(0x80517a5c,currentScene);entry(0x805d9768,miiInit);entry(0x805ddb7c,controlCount);entry(0x805ddb8c,addControl);entry(0x80629a9c,loadControl);
                    entry(0x8062aed0,checkpoint);entry(0x8062a854,checkpoint);
                    entry(0x805d9a8c,reset);entry(0x8060c4a0,destroyControl);entry(0x805da140,afterUpdate);
                    entry(0x810b0000,noop);Memory::Write32(vtable+0x10,0x810b0000);Memory::Write32(vtable+0x24,0x810b0000);Memory::Write32(scene+0xc98,heap);
                    std::memset(Memory::GetPointer(page-16,size+32),0xa5,size+32);
                    CpuContext c{};CpuContextScope scope(&c);c.gpr[1]=stack;c.gpr[2]=0x8038ac20;c.gpr[13]=0x80388880;
                    for(uint32_t r=14;r<32;r++)c.gpr[r]=0x12340000+r;
                    c.gpr[3]=page;func_8062A0AC(&c);CHECK(c.gpr[3]==page && baseControls==slots+1);
                    c.gpr[3]=page;func_8062A1E8(&c);CHECK(loaded==slots && added.size()==slots+2 && groups==1);
                    Memory::Write32(page,vtable);c.gpr[3]=page;func_8062A324(&c);CHECK(resets==1);
                    if(n>12 && n<=24)for(uint32_t active:{n,12u,13u}){
                        Memory::Write32(page+countOffset,active);
                        for(uint32_t i=0;i<active;i++)Memory::Write8(page+ids+i,i==active-1 ? slots-1 : i);
                        c.gpr[3]=page;FriendRoomManager_UpdateExpanded(&c);CHECK(c.gpr[3]==0x12345678);
                        std::set<std::pair<float,float>> positions;
                        for(uint32_t i=0;i<active;i++){
                            const uint32_t slot=Memory::Read8(page+ids+i),control=page+controls+slot*stride,pane=Memory::Read32(control+0x194);
                            const float x=std::bit_cast<float>(Memory::Read32(control+0x198)),y=std::bit_cast<float>(Memory::Read32(pane+0x30));
                            CHECK(positions.emplace(x,y).second && x>=-275 && x<=275 && (y==140 || y==40));
                            if(active<=12)CHECK(y==140);
                        }
                    }
                    // Run the original dequeue loop through a function boundary
                    // immediately before message processing. No game words are patched.
                    for(uint32_t queued:{0u,1u,2u,12u,24u,30u}){
                        auto saved=c;Memory::Write32(page+queueCount,queued);
                        for(uint32_t i=0;i<30;i++){Memory::Write32(page+queue+i*8,0x01020300+i);Memory::Write8(page+queue+i*8+4,i);Memory::Write8(page+queue+i*8+5,30-i);}
                        std::vector<uint8_t> expected(size);std::memcpy(expected.data(),Memory::GetPointer(page,size),size);
                        if(queued){for(uint32_t i=0;i+1<queued;i++)for(uint32_t byte=0;byte<6;byte++)expected[queue+i*8+byte]=Memory::Read8(page+queue+(i+1)*8+byte);expectedWord(expected,queueCount,queued-1);}
                        c.gpr[29]=page;
                        try{func_8062AE74(&c);CHECK(false);}catch(int marker){CHECK(marker==42);}
                        CHECK(std::memcmp(expected.data(),Memory::GetPointer(page,size),size)==0);c=saved;
                    }
                    for(uint32_t active:{1u,12u,n})for(uint32_t removed:{0u,active/2,active-1}){
                        auto saved=c;Memory::Write32(page+countOffset,active);
                        for(uint32_t i=0;i<active;i++)Memory::Write8(page+ids+i,i==active-1 ? slots-1 : i*2);
                        std::vector<uint8_t> expected(size);std::memcpy(expected.data(),Memory::GetPointer(page,size),size);
                        for(uint32_t i=removed;i+1<active;i++)expected[ids+i]=Memory::Read8(page+ids+i+1);
                        expected[ids+active-1]=255;expectedWord(expected,countOffset,active-1);
                        c.gpr[29]=page;c.gpr[18]=255;c.gpr[4]=removed;
                        try{func_8062A814(&c);CHECK(false);}catch(int marker){CHECK(marker==42);}
                        CHECK(std::memcmp(expected.data(),Memory::GetPointer(page,size),size)==0);c=saved;
                    }
                    c.gpr[3]=page;c.gpr[4]=0;func_8062A140(&c);CHECK(destroyed==slots+1);
                    CHECK(c.gpr[1]==stack);for(uint32_t r=14;r<32;r++)CHECK(c.gpr[r]==0x12340000+r);
                    for(uint32_t i=0;i<16;i++)CHECK(Memory::Read8(page-16+i)==0xa5 && Memory::Read8(page+size+i)==0xa5);
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!) {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);if(!done)compile.Kill(entireProcessTree:true);
                Assert.True(done,"Friend room compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());}
            using var run=Process.Start(new ProcessStartInfo(exe,binary){RedirectStandardError=true})!;var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(15000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Friend room execution timed out");Assert.True(run.ExitCode==0,$"Friend room lifecycle returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        } finally {Directory.Delete(temp,recursive:true);}
    }
}
