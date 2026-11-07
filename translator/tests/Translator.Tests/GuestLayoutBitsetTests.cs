using System.Text.Json;
using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class GuestLayoutBitsetTests
{
    private const uint Start=0x80001000;
    private static readonly uint[] Words={0xa0e30050,0x7d400030,0x7ce00378,0xb0030050};
    private static readonly GuestLayoutType[] Types={new("Header",4,80),new("Half",2,2),new("Byte",1,1),
        new("Record",4,Fields:new[] {new GuestLayoutField("header","Header"),
            new GuestLayoutField("finished","Half",PerPlayer:true,PlayersPerElement:16),new GuestLayoutField("order","Byte")})};
    private static GuestBitsetStore Site()=>new(Start,Words,"r3","r12","r10","r0","r7","Record.finished.offset","Record.finished.count");
    private static GuestLayoutRelocations Layout(int players,GuestBitsetStore? site=null)=>new(Types,12,players,
        Array.Empty<GuestLayoutReference>(),bitsetStores:new[] {site??Site()});
    private static ProgramImage Image(IReadOnlyList<uint>? words=null)=>TranslatorCppTestHarness.CreateImage(
        (words??Words).Append(0x4e800020u).Select((word,index)=>(Start+(uint)index*4,word)).ToArray());

    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(16)] [InlineData(17)] [InlineData(24)] [InlineData(48)] [InlineData(100)]
    public void GroupedWordsMoveFollowingFieldsAndInsertEveryBitWithoutWrapping(int players)
    {
        var layout=new GuestLayouts(Types,players);var count=(players+15)/16;
        Assert.Equal(count,layout.Symbol("Record.finished.count"));
        Assert.Equal(80+count*2,layout.Symbol("Record.order.offset"));
        var image=Image();var translator=new FunctionTranslator(image,layoutRelocations:Layout(players));
        if(players==12)Assert.Equal(new FunctionTranslator(image).Translate(Start,TranslationOptions.Default).CxxCode,
            translator.Translate(Start,TranslationOptions.Default).CxxCode);
        var nodes=translator.Discover(Start,TranslationOptions.Default).LinearIr.Blocks.SelectMany(block=>block.Instructions).ToArray();
        for(var index=0;index<players;index++)
        {
            var registers=new Dictionary<string,uint>{{"r3",0x1000},{"r0",(uint)index},{"r12",(uint)index},{"r10",1}};
            var memory=Enumerable.Range(-1,count+2).ToDictionary(i=>0x1050u+unchecked((uint)(i*2)),i=>(uint)(0x8421^i*137)&0xffff);
            var before=new Dictionary<uint,uint>(memory);
            uint Value(IrValue value)=>value.RegisterName is {} register?registers[register]:unchecked((uint)value.Constant!.Value);
            uint Address(IrAddress address)=>registers[address.Base]+unchecked((uint)address.Offset);
            foreach(var node in nodes)
            {
                switch(node)
                {
                    case IrLoad load:Assert.Equal(2,load.SizeBytes);registers[load.Destination]=memory[Address(load.Address)];break;
                    case IrStore store:Assert.Equal(2,store.SizeBytes);memory[Address(store.Address)]=Value(store.Source)&0xffff;break;
                    case IrBinary binary:
                        var a=Value(binary.Left);var b=Value(binary.Right);
                        registers[binary.Destination]=binary.Op switch {"add"=>unchecked(a+b),"mul"=>unchecked(a*b),"or"=>a|b,
                            "sub"=>unchecked(a-b),"divu"=>a/b,"and"=>a&b,"shr"=>a>>(int)b,"shl"=>a<<(int)b,"ppc_slw"=>(b&63)>=32?0:a<<(int)(b&31),
                            _=>throw new InvalidOperationException(binary.Op)};break;
                    case IrComment or IrReturn or IrTracePpc:break;
                    default:throw new InvalidOperationException(node.ToString());
                }
            }
            foreach(var address in before.Keys)
                Assert.Equal(address==0x1050+(index/16)*2?before[address]|(1u<<(index%16)):before[address],memory[address]);
            Assert.Equal(before[0x1050],registers["r7"]);
            Assert.Equal(before[0x1050]|((index&63)<32?1u<<(index&31):0),registers["r0"]);
            Assert.Equal(0x1000u,registers["r3"]);Assert.Equal((uint)index,registers["r12"]);Assert.Equal(1u,registers["r10"]);
        }
        Assert.NotEmpty(translator.Translate(Start,TranslationOptions.Default).CxxCode);
    }

    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(48)] [InlineData(100)]
    public void PackedConsoleTeamBitsPreserveReservedBitsAndIndependentCounters(int players)
    {
        var types=new[] {Types[0],new GuestLayoutType("Word",4,4),Types[2],new GuestLayoutType("Record",4,
            Fields:new[] {new GuestLayoutField("header","Header"),new GuestLayoutField("teams","Word",PerPlayer:true,PlayersPerElement:12),new GuestLayoutField("tail","Byte")})};
        var words=new uint[]{0x80e30050,0x7d400030,0x38a50001,0x7ce00378,0x90030050};
        var site=new GuestBitsetStore(Start,words,"r3","r12","r10","r0","r7","Record.teams.offset","Record.teams.count",24,4,2);
        var layout=new GuestLayoutRelocations(types,12,players,Array.Empty<GuestLayoutReference>(),bitsetStores:new[] {site});
        var image=Image(words);var translator=new FunctionTranslator(image,layoutRelocations:layout);
        if(players==12)Assert.Equal(new FunctionTranslator(image).Translate(Start,TranslationOptions.Default).CxxCode,
            translator.Translate(Start,TranslationOptions.Default).CxxCode);
        var nodes=translator.Discover(Start,TranslationOptions.Default).LinearIr.Blocks.SelectMany(block=>block.Instructions).ToArray();
        var count=(players+11)/12;
        for(var index=0;index<players*2;index++){
            var registers=new Dictionary<string,uint>{{"r3",0x1000},{"r0",(uint)index},{"r12",(uint)index},{"r10",1},{"r5",17}};
            var memory=Enumerable.Range(-1,count+2).ToDictionary(i=>0x1050u+unchecked((uint)(i*4)),i=>0xa5000000u|((uint)(0x6421^i*137)&0xffffff));
            var before=new Dictionary<uint,uint>(memory);
            uint Value(IrValue value)=>value.RegisterName is {} register?registers[register]:unchecked((uint)value.Constant!.Value);
            uint Address(IrAddress address)=>registers[address.Base]+unchecked((uint)address.Offset);
            foreach(var node in nodes)switch(node){
                case IrLoad load:Assert.Equal(4,load.SizeBytes);registers[load.Destination]=memory[Address(load.Address)];break;
                case IrStore store:Assert.Equal(4,store.SizeBytes);memory[Address(store.Address)]=Value(store.Source);break;
                case IrBinary binary:
                    var a=Value(binary.Left);var b=Value(binary.Right);
                    registers[binary.Destination]=binary.Op switch {"add"=>unchecked(a+b),"sub"=>unchecked(a-b),"mul"=>unchecked(a*b),"divu"=>a/b,
                        "or"=>a|b,"shl"=>a<<(int)b,"ppc_slw"=>(b&63)>=32?0:a<<(int)(b&31),_=>throw new InvalidOperationException(binary.Op)};break;
                case IrComment or IrReturn or IrTracePpc:break;
                default:throw new InvalidOperationException(node.ToString());
            }
            foreach(var address in before.Keys)Assert.Equal(address==0x1050+(index/24)*4?before[address]|(1u<<(index%24)):before[address],memory[address]);
            Assert.Equal(18u,registers["r5"]);Assert.Equal((uint)index,registers["r12"]);
        }
        Assert.NotEmpty(translator.Translate(Start,TranslationOptions.Default).CxxCode);
        if(players==24){
            var path=Path.GetTempFileName();
            try{foreach(var version in new[] {20,21,22,23}){
                File.WriteAllText(path,JsonSerializer.Serialize(new {Version=version,OriginalPlayers=12,Types=types,References=Array.Empty<object>(),
                    BitsetStores=new[] {new {Begin=$"0x{Start:X8}",ExpectedWords=words.Select(word=>$"0x{word:X8}"),
                        site.Base,site.Index,site.Unit,site.Shift,site.Loaded,site.Offset,site.Count,site.WordBits,site.WordBytes,site.BitsPerPlayer}}}));
                if(version is 21 or 22)Assert.Equal(24,GuestLayoutRelocations.Read(path,24).Players);
                else Assert.Throws<InvalidDataException>(()=>GuestLayoutRelocations.Read(path,24));
            }}finally{File.Delete(path);}
        }
        foreach(var invalid in new[] {site with {WordBits=33},site with {WordBits=0},site with {WordBytes=1},site with {BitsPerPlayer=0},site with {BitsPerPlayer=3}})
            Assert.Throws<InvalidDataException>(()=>new GuestLayoutRelocations(types,12,players,Array.Empty<GuestLayoutReference>(),bitsetStores:new[] {invalid}));
        var corrupt=words.ToArray();corrupt[2]=0x398c0001; // Incrementing the protected bit index is not independent.
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(corrupt),layoutRelocations:new GuestLayoutRelocations(types,12,players,
            Array.Empty<GuestLayoutReference>(),bitsetStores:new[] {site with {ExpectedWords=corrupt}})));
    }

    [Fact]
    public void WrongEffectsAliasedInputsOverlapsAndInteriorEntriesAreRejected()
    {
        foreach(var index in Enumerable.Range(0,4))
        {
            var words=Words.ToArray();words[index]^=4;
            Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(words),layoutRelocations:Layout(24)));
            Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(words),layoutRelocations:Layout(24,Site() with {ExpectedWords=words})));
        }
        Assert.Throws<InvalidDataException>(()=>Layout(11));
        Assert.Throws<InvalidDataException>(()=>Layout(24,Site() with {Index="r0"}));
        Assert.Throws<InvalidDataException>(()=>Layout(24,Site() with {Base="r1"}));
        Assert.Throws<InvalidDataException>(()=>Layout(24,Site() with {Count="players"}));
        Assert.Throws<InvalidDataException>(()=>new GuestLayoutRelocations(Types,12,24,
            new[] {new GuestLayoutReference(Start,Words[0],0,"offset","Record.finished.offset")},bitsetStores:new[] {Site()}));
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(),layoutRelocations:Layout(24)).Discover(Start+4,TranslationOptions.Default));
        var branch=TranslatorCppTestHarness.CreateImage(new[] {(Start-4,0x4800000cu)}.Concat(
            Words.Append(0x4e800020u).Select((word,index)=>(Start+(uint)index*4,word))).ToArray());
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(branch,layoutRelocations:Layout(24)).Discover(Start-4,TranslationOptions.Default));
    }

    [Fact]
    public void GroupingRequiresAPlayerArrayAndVersionThirteen()
    {
        foreach(var grouping in new[] {0,-1,65536})Assert.Throws<InvalidDataException>(()=>new GuestLayouts(
            new[] {Types[1],new GuestLayoutType("Bad",2,Fields:new[] {new GuestLayoutField("bits","Half",PerPlayer:true,PlayersPerElement:grouping)})},24));
        Assert.Throws<InvalidDataException>(()=>new GuestLayouts(new[] {Types[1],new GuestLayoutType("Bad",2,
            Fields:new[] {new GuestLayoutField("bits","Half",PlayersPerElement:16)})},24));
        var path=Path.GetTempFileName();
        try
        {
            foreach(var version in new[] {12,13,14,15,16,17,18,19,20,21,22,23})
            {
                File.WriteAllText(path,JsonSerializer.Serialize(new {Version=version,OriginalPlayers=12,Types,References=Array.Empty<object>(),
                    BitsetStores=new[] {new {Begin="0x80001000",ExpectedWords=Words.Select(word=>$"0x{word:X8}"),
                        Base="r3",Index="r12",Unit="r10",Shift="r0",Loaded="r7",Offset="Record.finished.offset",Count="Record.finished.count"}}}));
                if(version is 13 or 14 or 15 or 16 or 17 or 18 or 19 or 20 or 21 or 22)Assert.Equal(24,GuestLayoutRelocations.Read(path,24).Players);
                else Assert.Throws<InvalidDataException>(()=>GuestLayoutRelocations.Read(path,24));
            }
        }
        finally{File.Delete(path);}
    }
}
