using System.Text.Json;
using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class GuestLayoutAuthoredCountIndexTests
{
    private const uint Start=0x80001000,Word=0x381cfffe;
    private static readonly GuestLayoutType[] Types={new("Byte",1,1)};
    private static GuestAuthoredCountIndex Site()=>new(Start,Word,"r28","r0",2,12);
    private static GuestLayoutRelocations Plan(int players,GuestAuthoredCountIndex? site=null)=>new(Types,12,players,
        Array.Empty<GuestLayoutReference>(),authoredCountIndices:new[]{site??Site()});
    private static ProgramImage Image(uint word=Word)=>TranslatorCppTestHarness.CreateImage((Start,word),(Start+4,0x4e800020));

    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(48)] [InlineData(100)]
    public void FixedRuleAxesPreserveOriginalCountsAndBoundExpandedLookups(int players)
    {
        var translator=new FunctionTranslator(Image(),layoutRelocations:Plan(players));
        if(players==12)Assert.Equal(new FunctionTranslator(Image()).Translate(Start).CxxCode,translator.Translate(Start).CxxCode);
        var nodes=translator.Discover(Start).LinearIr.Blocks.SelectMany(block=>block.Instructions).OfType<IrBinary>().ToArray();
        foreach(var input in Enumerable.Range(0,players+3).Select(i=>(uint)i).Concat(new[]{0x80000000u,uint.MaxValue}))
        {
            var registers=new Dictionary<string,uint>{{"r28",input}};
            uint Value(IrValue value)=>value.Constant is {} constant?unchecked((uint)constant):registers[value.RegisterName!];
            foreach(var node in nodes)
            {
                var a=Value(node.Left);var b=Value(node.Right);
                registers[node.Destination]=node.Op switch{
                    "add"=>unchecked(a+b),"sub"=>unchecked(a-b),"and"=>a&b,"andc"=>a&~b,"or"=>a|b,"divu"=>a/b,
                    "sub_u"=>a<b?uint.MaxValue:a>b?1u:0u,_=>throw new InvalidOperationException(node.Op)};
            }
            Assert.Equal(input,registers["r28"]);
            Assert.Equal(players==12?unchecked(input-2):Math.Clamp(input,2u,12u)-2,registers["r0"]);
        }
        Assert.NotEmpty(translator.Translate(Start).CxxCode);
    }

    [Fact]
    public void SourceAndDestinationMayAliasWithoutChangingOtherEffects()
    {
        var site=Site() with {ExpectedWord=0x3b9cfffe,Destination="r28"};
        Assert.NotEmpty(new FunctionTranslator(Image(site.ExpectedWord),layoutRelocations:Plan(24,site)).Translate(Start).CxxCode);
    }

    [Fact]
    public void UnprovenConversionsRegistersBoundsAndOverlapsFail()
    {
        foreach(var word in new uint[]{0x381cfffd,0x381dfffe,0x383cfffe,0x301cfffe})
            Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(word),layoutRelocations:Plan(24,Site() with {ExpectedWord=word})));
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(Word+1),layoutRelocations:Plan(24)));
        foreach(var register in new[]{"r1","r2","r32","f3"}){
            Assert.Throws<InvalidDataException>(()=>Plan(24,Site() with {Input=register}));
            Assert.Throws<InvalidDataException>(()=>Plan(24,Site() with {Destination=register}));}
        Assert.Throws<InvalidDataException>(()=>Plan(24,Site() with {FirstCount=0}));
        Assert.Throws<InvalidDataException>(()=>Plan(24,Site() with {FirstCount=13}));
        Assert.Throws<InvalidDataException>(()=>Plan(24,Site() with {LastCount=13}));
        Assert.Throws<InvalidDataException>(()=>Plan(24,Site() with {Instruction=Start+1}));
        Assert.Throws<InvalidDataException>(()=>Plan(11));
        Assert.Throws<InvalidDataException>(()=>new GuestLayoutRelocations(Types,12,24,Array.Empty<GuestLayoutReference>(),authoredCountIndices:new[]{Site(),Site()}));
        Assert.Throws<InvalidDataException>(()=>new GuestLayoutRelocations(Types,12,24,
            new[]{new GuestLayoutReference(Start,Word,0,"right","players")},authoredCountIndices:new[]{Site()}));
        Assert.Throws<InvalidDataException>(()=>new GuestLayoutRelocations(Types,12,24,Array.Empty<GuestLayoutReference>(),
            normalizedRanks:new[]{new GuestNormalizedRank(Start,0x3863ffff,"r3","players",12)},authoredCountIndices:new[]{Site()}));
    }

    [Fact]
    public void ReadersRequireVersionSeventeen()
    {
        var path=Path.GetTempFileName();
        try{
            foreach(var version in new[]{16,17,18,19,20,21,22,23,24,25,26,27}){
                File.WriteAllText(path,JsonSerializer.Serialize(new{Version=version,OriginalPlayers=12,Types,References=Array.Empty<object>(),
                    AuthoredCountIndices=new[]{new{Instruction="0x80001000",ExpectedWord="0x381cfffe",Input="r28",Destination="r0",FirstCount=2,LastCount=12}}}));
                if(version is 17 or 18 or 19 or 20 or 21 or 22 or 23 or 24 or 25 or 26)Assert.Equal(24,GuestLayoutRelocations.Read(path,24).Players);
                else Assert.Throws<InvalidDataException>(()=>GuestLayoutRelocations.Read(path,24));}
        }finally{File.Delete(path);}
    }
}
