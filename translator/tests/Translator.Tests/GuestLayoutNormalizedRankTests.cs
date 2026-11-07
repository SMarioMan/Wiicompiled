using System.Text.Json;
using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class GuestLayoutNormalizedRankTests
{
    private const uint Start=0x80001000;
    private static readonly GuestLayoutType[] Types={new("Byte",1,1)};
    private static GuestNormalizedRank Site()=>new(Start,0x3863ffff,"r3","players",12);
    private static GuestLayoutRelocations Layout(int players,GuestNormalizedRank? site=null)=>new(Types,12,players,
        Array.Empty<GuestLayoutReference>(),normalizedRanks:new[] {site??Site()});
    private static ProgramImage Image(uint word=0x3863ffff)=>TranslatorCppTestHarness.CreateImage((Start,word),(Start+4,0x4e800020));

    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(48)] [InlineData(100)]
    public void AuthoredRankBandsPreserveOriginalCodeAndCoverExpandedRanks(int players)
    {
        var image=Image();
        var translator=new FunctionTranslator(image,layoutRelocations:Layout(players));
        if(players==12) Assert.Equal(new FunctionTranslator(image).Translate(Start,TranslationOptions.Default).CxxCode,
            translator.Translate(Start,TranslationOptions.Default).CxxCode);
        var operations=translator.Discover(Start,TranslationOptions.Default).LinearIr.Blocks
            .SelectMany(block=>block.Instructions).OfType<IrBinary>().ToArray();
        Assert.Equal(players==12?1:3,operations.Length);
        Assert.All(operations,node=>Assert.Equal("r3",node.Destination));
        for(var rank=1;rank<=players;rank++)
        {
            long result=rank;
            foreach(var node in operations)
            {
                Assert.Equal("r3",node.Left.RegisterName);
                var right=node.Right.Constant!.Value;
                result=node.Op switch {"add"=>result+right,"mul"=>result*right,"divu"=>result/right,
                    _=>throw new InvalidOperationException(node.Op)};
            }
            Assert.Equal((rank-1)*12/players,result);
            Assert.InRange(3-result/4,1,3);
        }
        Assert.NotEmpty(translator.Translate(Start,TranslationOptions.Default).CxxCode);
    }

    [Fact]
    public void UnprovenInstructionsDimensionsRegistersAndOverlapsAreRejected()
    {
        foreach(var word in new uint[] {0x3863fffe,0x3864ffff,0x3463ffff})
            Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(word),layoutRelocations:Layout(24,Site() with {ExpectedWord=word})));
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(0x3863fffe),layoutRelocations:Layout(24)));
        foreach(var count in new[] {1,11}) Assert.Throws<InvalidDataException>(()=>Layout(count));
        Assert.Throws<ArgumentOutOfRangeException>(()=>Layout(65536));
        foreach(var register in new[] {"r1","r2","r32","f3"})
            Assert.Throws<InvalidDataException>(()=>Layout(24,Site() with {Register=register}));
        Assert.Throws<InvalidDataException>(()=>Layout(24,Site() with {SourceCount=13}));
        Assert.Throws<InvalidDataException>(()=>Layout(24,Site() with {Instruction=Start+1}));
        Assert.Throws<InvalidDataException>(()=>new GuestLayoutRelocations(Types,12,24,Array.Empty<GuestLayoutReference>(),
            normalizedRanks:new[] {Site(),Site()}));
        Assert.Throws<InvalidDataException>(()=>new GuestLayoutRelocations(Types,12,24,
            new[] {new GuestLayoutReference(Start,0x3863ffff,0,"right","players")},normalizedRanks:new[] {Site()}));
    }

    [Fact]
    public void ReadersRequireVersionTwelve()
    {
        var path=Path.GetTempFileName();
        try
        {
            foreach(var version in new[] {11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27})
            {
                File.WriteAllText(path,JsonSerializer.Serialize(new {Version=version,OriginalPlayers=12,Types,
                    References=Array.Empty<object>(),NormalizedRanks=new[] {new {Instruction="0x80001000",ExpectedWord="0x3863ffff",
                        Register="r3",Count="players",SourceCount=12}}}));
                if(version is 12 or 13 or 14 or 15 or 16 or 17 or 18 or 19 or 20 or 21 or 22 or 23 or 24 or 25 or 26) Assert.Equal(24,GuestLayoutRelocations.Read(path,24).Players);
                else Assert.Throws<InvalidDataException>(()=>GuestLayoutRelocations.Read(path,24));
            }
        }
        finally{File.Delete(path);}
    }
}
