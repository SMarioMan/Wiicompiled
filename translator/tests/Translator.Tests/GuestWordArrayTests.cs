using System.Text.Json;
using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class GuestWordArrayTests
{
    private const uint Start=0x80001000,Table=0x80003000;
    private static readonly GuestLayoutType[] Types={new("Word",4,4),new("Head",4,80),new("Words",4,Fields:new[]{
        new GuestLayoutField("head","Head"),new GuestLayoutField("items","Word",PerPlayer:true,PlayersPerElement:12)})};
    private static GuestWordComparison Compare()=>new(Start,new uint[]{0x80830050,0x80a60050,0x7c042840},"r3","Words.items.offset","r4","Words.items.count",RightLoad:Start+4,RightBase:"r6",RightOffset:"Words.items.offset",RightValue:"r5");
    private static ProgramImage Image(params uint[] words)=>TranslatorCppTestHarness.CreateImage(words.Append(0x4e800020u).Select((w,i)=>(Start+(uint)i*4,w)).ToArray());
    private static GuestLayoutRelocations Plan(int players,GuestWordComparison? site=null)=>new(Types,12,players,Array.Empty<GuestLayoutReference>(),wordComparisons:new[]{site??Compare()});

    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(48)] [InlineData(100)]
    public void VerifiedComparisonsPreserveStockEmissionAndOnlyReplaceTheCrOperation(int players)
    {
        var image=Image(Compare().ExpectedWords.ToArray());var translator=new FunctionTranslator(image,layoutRelocations:Plan(players));
        var source=translator.Translate(Start).CxxCode;
        if(players==12)Assert.Equal(new FunctionTranslator(image).Translate(Start).CxxCode,source);
        else {Assert.Contains("layout_word_compare_80001008",source);Assert.Contains("Memory::Read32",source);}
        var nodes=translator.Discover(Start,TranslationOptions.Default).LinearIr.Blocks.SelectMany(b=>b.Instructions).ToArray();
        Assert.Equal(2,nodes.OfType<IrLoad>().Count());
        if(players!=12){Assert.Single(nodes.OfType<IrCall>());Assert.Single(nodes.OfType<IrSetCrField>());}
    }

    [Fact]
    public void MismatchedLoadsClobbersComparisonKindsAndPartialEntriesAreRejected()
    {
        var words=Compare().ExpectedWords.ToArray();
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(words),layoutRelocations:Plan(24,Compare() with {LeftValue="r7"})));
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(words),layoutRelocations:Plan(24,Compare() with {IsUnsigned=false})));
        Assert.Throws<InvalidDataException>(()=>Plan(24,Compare() with {RightBase="r4"}));
        Assert.Throws<InvalidDataException>(()=>Plan(24,Compare() with {RightLoad=null}));
        Assert.Throws<InvalidDataException>(()=>Plan(24,Compare() with {Field=8}));
        var clobber=Compare() with {ExpectedWords=new uint[]{0x80830050,0x38630001,0x80a60050,0x7c042840},RightLoad=Start+8};
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(clobber.ExpectedWords.ToArray()),layoutRelocations:Plan(24,clobber)));
        var translator=new FunctionTranslator(Image(words),layoutRelocations:Plan(24));
        Assert.Throws<InvalidDataException>(()=>translator.Translate(Start+4));
        var entry=TranslatorCppTestHarness.CreateImage((Start-4,0x48000008),(Start,words[0]),(Start+4,words[1]),(Start+8,words[2]),(Start+12,0x4e800020));
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(entry,layoutRelocations:Plan(24)).Translate(Start-4));
    }

    [Fact]
    public void SymbolicTablesValidateAllDataAndBothLoadForms()
    {
        var values=new uint[]{16,4,56};
        var table=new GuestWordTable(Table,values,new[]{new GuestWordTableEntry(1,"Words.items.size")},new[]{new GuestWordTableLoad(Start,0x80830000,"r3","r4")});
        var image=TranslatorCppTestHarness.CreateImage((Start,0x80830000),(Start+4,0x4e800020),(Table,16),(Table+4,4),(Table+8,56));
        GuestLayoutRelocations Make(GuestWordTable? t=null,int players=24)=>new(Types,12,players,Array.Empty<GuestLayoutReference>(),wordTables:new[]{t??table});
        Assert.Contains("layout_word_table_80001000",new FunctionTranslator(image,layoutRelocations:Make()).Translate(Start).CxxCode);
        Assert.Equal(new FunctionTranslator(image).Translate(Start).CxxCode,new FunctionTranslator(image,layoutRelocations:Make(players:12)).Translate(Start).CxxCode);
        Assert.Throws<InvalidDataException>(()=>Make(table with {ExpectedValues=new uint[]{16,8,56}}));
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(image,layoutRelocations:Make(table with {ExpectedValues=new uint[]{17,4,56}})));
        Assert.Throws<InvalidDataException>(()=>Make(table with {Entries=new[]{new GuestWordTableEntry(3,"Words.items.size")}}));
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(image,layoutRelocations:Make(table with {Loads=new[]{new GuestWordTableLoad(Start,0x80830000,"r5","r4")}})));
        var indexed=TranslatorCppTestHarness.CreateImage((Start,0x7c83002e),(Start+4,0x4e800020),(Table,16),(Table+4,4),(Table+8,56));
        Assert.Contains("layout_word_table_80001000",new FunctionTranslator(indexed,layoutRelocations:Make(table with {
            Loads=new[]{new GuestWordTableLoad(Start,0x7c83002e,"r3","r4",Index:"r0")}})).Translate(Start).CxxCode);
    }

    [Fact]
    public void PackedArenaRetainsBytePlacementAndContainedElementPadding()
    {
        var types=new[]{new GuestLayoutType("Byte",1,1),new GuestLayoutType("Wide",8,8),
            new GuestLayoutType("Arena",1,Fields:new[]{new GuestLayoutField("first","Byte"),
                new GuestLayoutField("words","Wide",PerPlayer:true,PlayersPerElement:12,Packed:true),new GuestLayoutField("last","Byte")})};
        var a=new GuestLayouts(types,12);var b=new GuestLayouts(types,24);
        Assert.Equal(1,a.Symbol("Arena.words.offset"));Assert.Equal(8,a.Symbol("Arena.words.stride"));
        Assert.Equal(9,a.Symbol("Arena.last.offset"));Assert.Equal(10,a.Symbol("Arena.size"));Assert.Equal(18,b.Symbol("Arena.size"));
        var unpacked=types.ToArray();unpacked[2]=unpacked[2] with {Fields=new[]{new GuestLayoutField("words","Wide")}};
        Assert.Throws<InvalidDataException>(()=>new GuestLayouts(unpacked,12));
        var path=Path.GetTempFileName();
        try{foreach(var version in new[]{27,28}){
            File.WriteAllText(path,JsonSerializer.Serialize(new{Version=version,OriginalPlayers=12,Types=types,References=Array.Empty<object>()}));
            if(version is >= 28 and <= GuestLayoutRelocations.CurrentFormatVersion)Assert.Equal(24,GuestLayoutRelocations.Read(path,24).Players);else Assert.Throws<InvalidDataException>(()=>GuestLayoutRelocations.Read(path,24));
        }}finally{File.Delete(path);}
    }

    [Fact]
    public void NewFeaturesRequireVersionTwentyEight()
    {
        var path=Path.GetTempFileName();
        try{foreach(var version in Enumerable.Range(27, GuestLayoutRelocations.CurrentFormatVersion - 27 + 2)){
            File.WriteAllText(path,JsonSerializer.Serialize(new{Version=version,OriginalPlayers=12,Types,References=Array.Empty<object>(),
                WordComparisons=new[]{new{Begin="0x80001000",ExpectedWords=new[]{"0x80830050","0x2c040000"},LeftBase="r3",LeftOffset="Words.items.offset",LeftValue="r4",Count="Words.items.count",IsUnsigned=false}}}));
            if(version is >= 28 and <= GuestLayoutRelocations.CurrentFormatVersion)Assert.Equal(24,GuestLayoutRelocations.Read(path,24).Players);
            else Assert.Throws<InvalidDataException>(()=>GuestLayoutRelocations.Read(path,24));
        }}finally{File.Delete(path);}
    }
}
