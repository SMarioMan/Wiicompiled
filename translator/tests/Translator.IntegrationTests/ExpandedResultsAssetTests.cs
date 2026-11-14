using System.Diagnostics;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;
namespace Translator.Tests;
public class ExpandedResultsAssetTests
{
    [Theory]
    [InlineData(24)]
    [InlineData(128)]
    [Trait("Requires","USA disc data, Python and Clang")]
    public void OriginalVariantAndBmgLoadersReadEveryGeneratedResultsRowAndRank(int players)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName??throw new DirectoryNotFoundException();
        var disc=Environment.GetEnvironmentVariable("MKW_TEST_DATA")??Path.Combine(root,"Assets/DATA");
        var temp=Path.Combine(Path.GetTempPath(),"mkw-results-assets-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try {
            var ui=Path.Combine(temp,"DATA/files/Scene/UI");Directory.CreateDirectory(ui);
            foreach(var file in Directory.GetFiles(Path.Combine(disc,"files/Scene/UI"),"Race*.szs"))File.Copy(file,Path.Combine(ui,Path.GetFileName(file)));
            foreach(var name in new[]{"MenuMulti.szs","MenuSingle.szs"})File.Copy(Path.Combine(disc,"files/Scene/UI",name),Path.Combine(ui,name));
            var script=Path.Combine(temp,"assets.py");
            File.WriteAllText(script,$$"""
                import importlib.util,pathlib,sys
                spec=importlib.util.spec_from_file_location('ui',sys.argv[1]);m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
                root=pathlib.Path(sys.argv[2]);original=m.Archive((root/'files/Scene/UI/Race.szs').read_bytes());m.build(root,{{players}})
                if {{players}}>24:
                    compact=m.Archive((root/'files/Scene/UI/Race.szs').read_bytes())
                    for layout in ('position_12players','position_12players_time_attack','common_w051_team_blue_red_chara_point'):
                        key='result/blyt/'+layout+'.brlyt';before=original.files[key];after=compact.files[key]
                        assert len(before)==len(after)
                        pos=16
                        while pos<len(after):
                            size=m.u32(after,pos+4);tag=after[pos:pos+4]
                            if tag in (b'txl1',b'fnl1',b'mat1'):assert before[pos:pos+size]==after[pos:pos+size]
                            if tag==b'txt1' and after[pos+12:pos+28].split(b'\0')[0] in (b'position',b'player_name',b'mii_name',b'point'):
                                import struct
                                font_height=struct.unpack_from('>f',after,pos+104)[0]
                                assert font_height*m.results_grid({{players}},team=layout.startswith('common_'))[2]>=9
                            pos+=size
                    art=m.Archive((root/'files/Scene/UI/Race_U.szs').read_bytes())
                    source=m.Archive((pathlib.Path(sys.argv[3])/'files/Scene/UI/Race_U.szs').read_bytes())
                    for key,value in source.files.items():
                        if key.startswith('game_image/timg/') and key.endswith('.tpl'):assert art.files[key]==value
                    for rank in (100,127,128):
                        keys=[k for k in art.files if k.startswith('game_image/timg/') and k.endswith('_'+str(rank)+'.tpl')]
                        assert len(keys)==2
                        for key in keys:assert m.decode_rank_texture(art.files[key]).getbbox()

                for name in ('Race.szs','Race_U.szs','Race_M.szs','Race_Q.szs'):
                    a=m.Archive((root/'files/Scene/UI'/name).read_bytes())
                    for key in ('result/ctrl/ResultVS.brctr','result/ctrl/ResultGP.brctr','result/ctrl/ResultTA.brctr','result/ctrl/ResultVSTeam.brctr','result/ctrl/ResultVSTeamNULL.brctr','message/Race.bmg','game_image/anim/game_image_position_texture_pattern.brlan','game_image/anim/game_image_position_multi_texture_pattern.brlan'):
                        if key in a.files:(root.parent/(name+'-'+pathlib.Path(key).name)).write_bytes(a.files[key])
                for name in ('MenuMulti.szs','MenuSingle.szs'):
                    a=m.Archive((root/'files/Scene/UI'/name).read_bytes())
                    for control in ('Vote','WifiMemberConfirm'):
                        (root.parent/(name+'-'+control+'.brctr')).write_bytes(a.files['control/ctrl/'+control+'.brctr'])
                """);
            Run("python3",new[]{script,Path.Combine(root,"mods/expanded-ui/build.py"),Path.Combine(temp,"DATA"),disc},180000);
            var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
            var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));var translator=new FunctionTranslator(image);
            uint[] entries={0x805ba994,0x805baa14,0x805d831c,0x805d83a4,0x805d840c,0x80082640};var sources=new List<string>();
            foreach(var pc in entries){var path=Path.Combine(temp,$"{pc:X8}.cpp");File.WriteAllText(path,translator.Translate(pc,new TranslationOptions(EnableLeafInlining:false,KnownFunctionEntryPoints:map.Addresses.ToHashSet())).CxxCode);sources.Add(path);}
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var glue=Path.Combine(temp,"glue.cpp");File.WriteAllText(glue,"#include \"mkw_guest_layout.h\"\nnamespace MkwGuestLayout {const Layout kLayout{"+players+"};}\n");
            sources.AddRange(new[]{glue,Path.Combine(root,"runtime/src/hle/rank_messages.cpp"),Path.Combine(root,"runtime/src/guest_region.cpp")});
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <array>
                #include <algorithm>
                #include <cstdio>
                #include <cstring>
                #include <bit>
                #include <cmath>
                {{string.Join("\n",entries.Select(pc=>$"extern \"C\" void func_{pc:X8}(CpuContext*);"))}}
                extern "C" void GetPositionMessage_Expanded(CpuContext*);
                constexpr uint32_t players={{players}},asset=0x81010000,loader=0x81030000,control=0x81040000,holder=0x81050000,systemState=0x81060000,text=0x81070000,stack=0x81f00000;
                void entry(uint32_t address,void(*fn)(CpuContext*)){TranslatedFunctionInfo info{};info.address=address;info.name="results asset fixture";info.rawCpuInvoker=fn;info.entryPoint=reinterpret_cast<void*>(fn);TranslatedFunctionRegistry::Register(info);}
                void compare(CpuContext* c){c->gpr[3]=std::strcmp(reinterpret_cast<const char*>(Memory::GetPointer(c->gpr[3])),reinterpret_cast<const char*>(Memory::GetPointer(c->gpr[4])));}
                void matrix(CpuContext*){}
                void original_rank(CpuContext* c){c->gpr[3]+=0x514;}
                bool load(const char* path,uint32_t address){auto file=std::fopen(path,"rb");if(!file)return false;std::fseek(file,0,SEEK_END);auto size=std::ftell(file);std::rewind(file);auto n=std::fread(Memory::GetPointer(address,size),1,size,file);std::fclose(file);return n==size;}
                float f(uint32_t address){return std::bit_cast<float>(Memory::Read32(address));}
                int main(int argc,char** argv){if(argc!=16)return 1;Memory::Init(32*1024*1024);if(!load(argv[1],0x80000000))return 2;
                    entry(0x8001273c,compare);entry(0x8060bfac,matrix);entry(0x80833764,original_rank);
                    Memory::Write32(0x80381c80,systemState);Memory::Write32(loader,control);
                    CpuContext c{};CpuContextScope scope(&c);c.gpr[1]=stack;c.gpr[2]=0x8038ac20;c.gpr[13]=0x80388880;c.lr=0x80006000;
                    for(uint32_t r=14;r<32;r++)c.gpr[r]=0x12340000+r;
                    for(uint32_t layout=2;layout<=5;layout++){
                        if(!load(argv[layout],asset))return 3;auto section=asset+Memory::Read16(asset+14),strings=asset+Memory::Read16(asset+16);
                        Memory::Write32(loader+0x14,section);Memory::Write32(loader+0x18,section+Memory::Read16(section));Memory::Write32(loader+0x24,strings);
                        for(uint32_t aspect=0;aspect<2;aspect++)for(uint32_t rank=1;rank<=(layout==5?(players+1)/2:players);rank++){
                            char name[16];std::snprintf(name,sizeof(name),layout==5?"BlueRed%u":"rank%u",layout==5?rank-1:rank);std::memcpy(Memory::GetPointer(text,sizeof(name)),name,sizeof(name));
                            c.gpr[3]=loader;c.gpr[4]=text;func_805BA994(&c);auto record=c.gpr[3];
                            if(record!=section+12+(rank-1)*60)return 4;
                            Memory::Write32(systemState+0x58,aspect);c.gpr[3]=loader;c.gpr[4]=record;func_805BAA14(&c);
                            float x=layout==5?0.f:rank<=12?-155.f:155.f,y=layout==5?70.f-225.f*(rank-1)/((players+1)/2-1):187.f-((rank-1)%12)*34.f;
                            float scale=layout==5?6.f/((players+1)/2):.5f;
                            if(players==128 && layout==5){x=(int((rank-1)/16)-1.5f)*76.f;y=70.f-(((rank-1)%16)+.5f)*225.f/16.f;scale=72.f/(272.f*1.03f);}
                            if(players==128 && layout!=5){x=(int((rank-1)/16)-3.5f)*76.f;y=(15.f-2*((rank-1)%16))*374.f/32.f;scale=72.f/272.f;}
                            if(std::abs(f(control+4)-x)>.0001f || std::abs(f(control+8)-y)>.0001f || std::abs(f(control+0x10)-scale)>.0001f || std::abs(f(control+0x14)-scale)>.0001f)return 5;
                        }
                    }
                    for(uint32_t language=6;language<=8;language++){
                        if(!load(argv[language],asset))return 6;c.gpr[3]=holder;c.gpr[4]=asset;func_805D831C(&c);
                        for(uint32_t rank=1;rank<=players;rank++){
                            c.gpr[3]=rank;GetPositionMessage_Expanded(&c);auto id=c.gpr[3];if(id!=(rank<=12?0x514+rank:0x70000000+rank))return 7;
                            c.gpr[3]=holder;c.gpr[4]=id;func_805D83A4(&c);if(c.gpr[3]==0xffffffffu)return 8;
                            c.gpr[4]=c.gpr[3];c.gpr[3]=holder;func_805D840C(&c);auto message=c.gpr[3];if(!message)return 9;
                            // Verify digits for all ranks, then the generated locale suffix.
                            char digits[16];std::snprintf(digits,sizeof(digits),"%u",rank);uint32_t n=std::strlen(digits);
                            for(uint32_t i=0;i<n;i++)if(Memory::Read16(message+i*2)!=uint32_t(digits[i]))return 10;
                            if(rank>12){const char* suffix=rank%100>=11&&rank%100<=13?"th":rank%10==1?"st":rank%10==2?"nd":rank%10==3?"rd":"th";
                                if(language==6){if(Memory::Read16(message+n*2)!=suffix[0] || Memory::Read16(message+n*2+2)!=suffix[1] || Memory::Read16(message+n*2+4))return 11;}
                                else if(Memory::Read16(message+n*2)!=(language==7?0xbau:'e') || Memory::Read16(message+n*2+2))return 12;
                            }
                        }
                    }
                    for(uint32_t animation=9;animation<=10;animation++){
                        if(!load(argv[animation],asset))return 15;
                        auto pai=asset+16+Memory::Read32(asset+20);
                        if(Memory::Read16(pai+8)!=players || Memory::Read16(pai+12)!=players)return 16;
                        auto entries=Memory::Read16(pai+14);auto table=Memory::Read32(pai+16);
                        for(uint32_t material=0;material<entries;material++){
                            auto object=pai+Memory::Read32(pai+table+material*4),track=object+40,keys=track+12;
                            if(Memory::Read16(track+4)!=players)return 17;
                            for(uint32_t rank=1;rank<=players;rank++)for(double fraction:{0.,.25}){
                                c.gpr[3]=keys;c.gpr[4]=players;c.fpr[1].d=rank-1+fraction;func_80082640(&c);
                                if(c.gpr[3]!=rank-1)return 18;
                            }
                            for(double frame:{-1.,1000.}){c.gpr[3]=keys;c.gpr[4]=players;c.fpr[1].d=frame;func_80082640(&c);if(c.gpr[3]!=(frame<0?0u:players-1))return 19;}
                        }
                    }
                    for(uint32_t selection=11;selection<=14;selection++){
                        if(!load(argv[selection],asset))return 20;
                        auto section=asset+Memory::Read16(asset+14),strings=asset+Memory::Read16(asset+16);
                        Memory::Write32(loader+0x14,section);Memory::Write32(loader+0x18,section+Memory::Read16(section));Memory::Write32(loader+0x24,strings);
                        bool vote=selection%2==1;
                        for(uint32_t aspect=0;aspect<2;aspect++)for(uint32_t index=0;index<players;index++){
                            char name[16];std::snprintf(name,sizeof(name),vote?"Vote%02u":"Member%02u",index);std::memcpy(Memory::GetPointer(text,sizeof(name)),name,sizeof(name));
                            c.gpr[3]=loader;c.gpr[4]=text;func_805BA994(&c);auto record=c.gpr[3];
                            if(record!=section+12+index*60)return 21;
                            Memory::Write32(systemState+0x58,aspect);c.gpr[3]=loader;c.gpr[4]=record;func_805BAA14(&c);
                            uint32_t columns=std::max(2u,(players+7)/8),rows=(players+columns-1)/columns;
                            float scale=std::min({1.f,2.f/columns,5.f/(rows-1)});
                            float x=(int(index%columns)-(columns-1)/2.f)*520.f/columns,y=140.f-(vote?225.f:200.f)*(index/columns)/(rows-1);
                            if(std::abs(f(control+4)-x)>.0001f || std::abs(f(control+8)-y)>.0001f || std::abs(f(control+0x10)-scale)>.0001f || std::abs(f(control+0x14)-scale)>.0001f)return 22;
                        }
                    }
                    if(!load(argv[15],asset))return 23;
                    auto section=asset+Memory::Read16(asset+14),strings=asset+Memory::Read16(asset+16);
                    Memory::Write32(loader+0x14,section);Memory::Write32(loader+0x18,section+Memory::Read16(section));Memory::Write32(loader+0x24,strings);
                    for(uint32_t aspect=0;aspect<2;aspect++)for(uint32_t team=0;team<2;team++){
                        char name[24];std::snprintf(name,sizeof(name),"ResultVSTeamNULL%u",team);std::memcpy(Memory::GetPointer(text,sizeof(name)),name,sizeof(name));
                        c.gpr[3]=loader;c.gpr[4]=text;func_805BA994(&c);auto record=c.gpr[3];
                        Memory::Write32(systemState+0x58,aspect);c.gpr[3]=loader;c.gpr[4]=record;func_805BAA14(&c);
                        float expected=players>24?152.f:aspect?190.f:145.f;
                        if(std::abs(f(control+4)-(team?expected:-expected))>.0001f)return 24;
                    }
                    if(c.gpr[1]!=stack || c.lr!=0x80006000)return 13;for(uint32_t r=14;r<32;r++)if(c.gpr[r]!=0x12340000+r)return 14;
                    return 0;
                }
                """);
            Run(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false),120000);
            Run(exe,new[]{binary,Path.Combine(temp,"Race.szs-ResultVS.brctr"),Path.Combine(temp,"Race.szs-ResultGP.brctr"),Path.Combine(temp,"Race.szs-ResultTA.brctr"),Path.Combine(temp,"Race.szs-ResultVSTeam.brctr"),Path.Combine(temp,"Race_U.szs-Race.bmg"),Path.Combine(temp,"Race_M.szs-Race.bmg"),Path.Combine(temp,"Race_Q.szs-Race.bmg"),Path.Combine(temp,"Race_U.szs-game_image_position_texture_pattern.brlan"),Path.Combine(temp,"Race_U.szs-game_image_position_multi_texture_pattern.brlan"),Path.Combine(temp,"MenuMulti.szs-Vote.brctr"),Path.Combine(temp,"MenuMulti.szs-WifiMemberConfirm.brctr"),Path.Combine(temp,"MenuSingle.szs-Vote.brctr"),Path.Combine(temp,"MenuSingle.szs-WifiMemberConfirm.brctr"),Path.Combine(temp,"Race.szs-ResultVSTeamNULL.brctr")},10000);
        }finally{Directory.Delete(temp,recursive:true);}
    }
    private static void Run(string command,IEnumerable<string> args,int timeout){var start=new ProcessStartInfo(command){RedirectStandardError=true};foreach(var arg in args)start.ArgumentList.Add(arg);Run(start,timeout);}
    private static void Run(string command,string args,int timeout)=>Run(new ProcessStartInfo(command,args){RedirectStandardError=true},timeout);
    private static void Run(ProcessStartInfo start,int timeout){using var process=Process.Start(start)!;var errors=process.StandardError.ReadToEndAsync();var done=process.WaitForExit(timeout);if(!done)process.Kill(entireProcessTree:true);Assert.True(done,$"{start.FileName} timed out");Assert.True(process.ExitCode==0,$"{start.FileName} returned {process.ExitCode}: {errors.GetAwaiter().GetResult()}");}
}
