using System.Text.Json;
using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class GuestLayoutCapacityTableTests
{
    private static readonly uint[] Overtakes={1,1,1,1,2,2,3,3,4,4,5,5};
    private const uint Start=0x80001000,Table=0x80003000,Word=0x7ca4002e;
    private static readonly uint[] Values={1,1,2,2,3,3,4,4,5,5,5,5};
    private static readonly GuestLayoutType[] Types={new("Word",4,4)};
    private static GuestCapacityLookup Site()=>new(Start,Word,"r4","r0","r5","r6");
    private static GuestCapacityTable Curve()=>new(Table,Values,new[] {Site()});
    private static GuestLayoutRelocations Layout(int players,GuestCapacityTable? table=null)=>new(Types,12,players,
        Array.Empty<GuestLayoutReference>(),capacityTables:new[] {table??Curve()});
    private static ProgramImage Image(uint word=Word,uint[]? values=null)=>TranslatorCppTestHarness.CreateImage(
        new[] {(Start,word),(Start+4,0x4e800020u)}.Concat((values??Values).Select((value,index)=>(Table+(uint)index*4,value))).ToArray());

    private static GuestCapacityTable Halfwords()=>new(Table,Overtakes,
        new[]{Site() with {ExpectedWord=0x7ca402ae}},true,new(-1,2));
    private static ProgramImage HalfwordImage()=>TranslatorCppTestHarness.CreateImage(
        new[]{(Start,0x7ca402aeu),(Start+4,0x4e800020u)}.Concat(
            Enumerable.Range(0,6).Select(i=>(Table+(uint)i*4,(Overtakes[i*2]<<16)|Overtakes[i*2+1]))).ToArray());

    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(48)] [InlineData(100)]
    public void CheckedHalfwordFormulaRetainsAuthoredCountsAndExtendsTheActiveField(int players)
    {
        var plan=Layout(players,Halfwords());var image=HalfwordImage();
        for(var count=1;count<=players;count++)Assert.Equal((uint)Math.Max(1,(count-1)/2),plan.CapacityThreshold(0,count));
        Assert.Equal(0u,plan.CapacityThreshold(0,0));Assert.Equal(0u,plan.CapacityThreshold(0,players+1));
        var source=new FunctionTranslator(image,layoutRelocations:plan).Translate(Start).CxxCode;
        if(players==12)Assert.Equal(new FunctionTranslator(image).Translate(Start).CxxCode,source);
        else {Assert.Contains("layout_capacity_80001000",source);Assert.DoesNotContain("FlatRead16",source);}
    }

    [Fact]
    public void FormulasMustMatchEveryAuthoredEntryAndHalfwordLoadShape()
    {
        foreach(var formula in new[]{new GuestCapacityFormula(-1,0),new(-1,-2),new(1,2),new(-13,2),new(-2,2)})
            Assert.Throws<InvalidDataException>(()=>Layout(24,Halfwords() with {Formula=formula}));
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(),layoutRelocations:Layout(24,Halfwords())));
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(HalfwordImage(),layoutRelocations:Layout(24,
            Halfwords() with {Lookups=new[]{Site() with {ExpectedWord=0x7ca402ae,Destination="r6"}}})));
        Assert.Throws<InvalidDataException>(()=>Layout(65535,Halfwords() with {ExpectedValues=Enumerable.Range(1,12).Select(i=>(uint)i).ToArray(),Formula=new(0,1)}));
        Assert.Throws<InvalidDataException>(()=>Layout(65535,Halfwords() with {ExpectedValues=Enumerable.Range(1,12).Select(i=>(uint)i).ToArray(),Formula=null}));
        var bad=HalfwordImage();bad.Memory[bad.GetOffset(Table+2,1)]=0x80;
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(bad,layoutRelocations:Layout(24,Halfwords())));
        var unsigned=HalfwordImage();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(unsigned.Memory.AsSpan(unsigned.GetOffset(Start,4),4),0x7ca4022e);
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(unsigned,layoutRelocations:Layout(24,
            Halfwords() with {Lookups=new[]{Site() with {ExpectedWord=0x7ca4022e}}})));
    }

    [Fact]
    public void HalfwordTablesAndFormulasRequireVersionTwentySeven()
    {
        var path=Path.GetTempFileName();
        try{foreach(var version in new[]{26,27,28,29,30})foreach(var halfwords in new[]{false,true}){
            File.WriteAllText(path,JsonSerializer.Serialize(new{Version=version,OriginalPlayers=12,Types,References=Array.Empty<object>(),
                CapacityTables=new[]{new{Address="0x80003000",ExpectedValues=Overtakes,SignedHalfwords=halfwords,
                    Formula=new{Bias=-1,Divisor=2},Lookups=new[]{new{Instruction="0x80001000",ExpectedWord=halfwords?"0x7ca402ae":"0x7ca4002e",
                        Base="r4",Index="r0",Destination="r5",Count="r6"}}}}}));
            if(version is 27 or 28 or 29)Assert.Equal(24,GuestLayoutRelocations.Read(path,24).Players);
            else Assert.Throws<InvalidDataException>(()=>GuestLayoutRelocations.Read(path,24));
        }}finally{File.Delete(path);}
    }

    [Fact]
    public void AllAuthoredRowsAreExactAndLargerFieldsKeepTheTerminalCutoffProportion()
    {
        var layout=Layout(100);for(var i=1;i<=12;i++)Assert.Equal(Values[i-1],layout.CapacityThreshold(0,i));
        Assert.Equal(0u,layout.CapacityThreshold(0,0));Assert.Equal(0u,layout.CapacityThreshold(0,101));
        Assert.Equal(5u,layout.CapacityThreshold(0,13));Assert.Equal(10u,layout.CapacityThreshold(0,24));
        Assert.Equal(20u,layout.CapacityThreshold(0,48));Assert.Equal(41u,layout.CapacityThreshold(0,100));
        var image=Image();
        Assert.Equal(new FunctionTranslator(image).Translate(Start,TranslationOptions.Default).CxxCode,
            new FunctionTranslator(image,layoutRelocations:Layout(12)).Translate(Start,TranslationOptions.Default).CxxCode);
        var translated=new FunctionTranslator(image,layoutRelocations:Layout(24)).Translate(Start,TranslationOptions.Default);
        Assert.Contains("layout_capacity_80001000",translated.CxxCode);
        Assert.Contains(translated.LinearIr.Blocks.SelectMany(block=>block.Instructions),node=>node is IrCall {Destination:"r5",Target:"layout_capacity_80001000"});
    }

    [Fact]
    public void WrongTableWordsLoadShapeRegistersAndOverlappingRewritesAreRejected()
    {
        var values=Values.ToArray();values[0]=0;
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(values:values),layoutRelocations:Layout(24)));
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(Word^1),layoutRelocations:Layout(24)));
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(),layoutRelocations:Layout(24,
            Curve() with {Lookups=new[] {Site() with {Destination="r6"}}})));
        Assert.Throws<InvalidDataException>(()=>Layout(24,Curve() with {ExpectedValues=new uint[] {1}}));
        Assert.Throws<InvalidDataException>(()=>Layout(24,Curve() with {ExpectedValues=Values.Reverse().ToArray()}));
        Assert.Throws<InvalidDataException>(()=>Layout(24,Curve() with {Lookups=new[] {Site() with {Count="r32"}}}));
        Assert.Throws<InvalidDataException>(()=>new GuestLayoutRelocations(Types,12,24,
            new[] {new GuestLayoutReference(Start,Word,0,"right","Word.size")},capacityTables:new[] {Curve()}));
    }

    [Fact]
    public void ReadersRequireVersionTen()
    {
        var path=Path.GetTempFileName();
        try
        {
            foreach(var version in new[] {9,10,11,12})
            {
                File.WriteAllText(path,JsonSerializer.Serialize(new {Version=version,OriginalPlayers=12,Types,
                    References=Array.Empty<object>(),CapacityTables=new[] {new {Address="0x80003000",ExpectedValues=Values,
                        Lookups=new[] {new {Instruction="0x80001000",ExpectedWord="0x7CA4002E",Base="r4",Index="r0",Destination="r5",Count="r6"}}}}}));
                if(version is 10 or 11 or 12)Assert.Equal(24,GuestLayoutRelocations.Read(path,24).Players);
                else Assert.Throws<InvalidDataException>(()=>GuestLayoutRelocations.Read(path,24));
            }
        }
        finally{File.Delete(path);}
    }
}
