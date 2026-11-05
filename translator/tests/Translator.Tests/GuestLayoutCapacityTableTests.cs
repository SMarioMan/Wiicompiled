using System.Text.Json;
using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class GuestLayoutCapacityTableTests
{
    private const uint Start=0x80001000,Table=0x80003000,Word=0x7ca4002e;
    private static readonly uint[] Values={1,1,2,2,3,3,4,4,5,5,5,5};
    private static readonly GuestLayoutType[] Types={new("Word",4,4)};
    private static GuestCapacityLookup Site()=>new(Start,Word,"r4","r0","r5","r6");
    private static GuestCapacityTable Curve()=>new(Table,Values,new[] {Site()});
    private static GuestLayoutRelocations Layout(int players,GuestCapacityTable? table=null)=>new(Types,12,players,
        Array.Empty<GuestLayoutReference>(),capacityTables:new[] {table??Curve()});
    private static ProgramImage Image(uint word=Word,uint[]? values=null)=>TranslatorCppTestHarness.CreateImage(
        new[] {(Start,word),(Start+4,0x4e800020u)}.Concat((values??Values).Select((value,index)=>(Table+(uint)index*4,value))).ToArray());

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
                if(version is 10 or 11)Assert.Equal(24,GuestLayoutRelocations.Read(path,24).Players);
                else Assert.Throws<InvalidDataException>(()=>GuestLayoutRelocations.Read(path,24));
            }
        }
        finally{File.Delete(path);}
    }
}
