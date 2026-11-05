using System.Text.Json;
using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class GuestLayoutTraversalMarkerTests
{
    private const uint Start=0x80001000;
    private static readonly uint[] Words={0x38000001,0x7cbf0734,0x7c162030};
    private static readonly GuestLayoutType[] Types={new("Word",4,4)};
    private static GuestTraversalMarker Site()=>new(Start,Words,"r0","r4","r22");
    private static GuestLayoutRelocations Layout(int players,GuestTraversalMarker? site=null)=>
        new(Types,12,players,Array.Empty<GuestLayoutReference>(),traversalMarkers:new[] {site??Site()});
    private static ProgramImage Image(IReadOnlyList<uint>? words=null)=>TranslatorCppTestHarness.CreateImage(
        (words??Words).Append(0x4e800020u).Select((word,index)=>(Start+(uint)index*4,word)).ToArray());

    [Fact]
    public void ExpandedTraversalUsesTheUnitMarkerAndOriginalCodeIsUnchanged()
    {
        var image=Image();
        Assert.Equal(new FunctionTranslator(image).Translate(Start,TranslationOptions.Default).CxxCode,
            new FunctionTranslator(image,layoutRelocations:Layout(12)).Translate(Start,TranslationOptions.Default).CxxCode);
        var nodes=new FunctionTranslator(image,layoutRelocations:Layout(24)).Discover(Start,TranslationOptions.Default)
            .LinearIr.Blocks.SelectMany(block=>block.Instructions).ToArray();
        Assert.Contains(nodes,node=>node is IrAssign {Destination:"r22",Value.RegisterName:"r0"});
        Assert.DoesNotContain(nodes,node=>node is IrBinary {Op:"ppc_slw"});
        Assert.Contains(nodes,node=>node is IrBinary {Destination:"r31",Op:"sext"});
    }

    [Fact]
    public void IncorrectWordsUnitRedefinitionExtraEffectsAndPartialEntryAreRejected()
    {
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(new uint[] {0x38000002,Words[1],Words[2]}),layoutRelocations:Layout(24)));
        foreach(var words in new[] {
            new uint[] {0x38000002,Words[1],Words[2]},
            new uint[] {Words[0],0x38a00001,Words[2]},
            new uint[] {Words[0],0x7ca00734,Words[2]},
            new uint[] {Words[0],0x7ca40734,Words[2]},
            new uint[] {Words[0],0x90030000,Words[2]},
            new uint[] {Words[0],Words[1],0x7c162031}})
            Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(words),layoutRelocations:Layout(24,Site() with {ExpectedWords=words})));
        var translator=new FunctionTranslator(Image(),layoutRelocations:Layout(24));
        Assert.Throws<InvalidDataException>(()=>translator.Discover(Start+4,TranslationOptions.Default));
        Assert.Throws<InvalidDataException>(()=>Layout(11));
        Assert.Throws<InvalidDataException>(()=>new GuestLayoutRelocations(Types,12,24,Array.Empty<GuestLayoutReference>(),
            traversalMarkers:new[] {Site(),Site()}));
    }

    [Fact]
    public void ReadersRequireVersionEight()
    {
        var path=Path.GetTempFileName();
        try
        {
            foreach(var version in new[] {7,8,9})
            {
                File.WriteAllText(path,JsonSerializer.Serialize(new {Version=version,OriginalPlayers=12,Types,
                    References=Array.Empty<object>(),TraversalMarkers=new[] {new {Begin="0x80001000",
                        ExpectedWords=Words.Select(word=>$"0x{word:X8}"),Source="r0",Player="r4",Destination="r22"}}}));
                if(version==8)Assert.Equal(24,GuestLayoutRelocations.Read(path,24).Players);
                else Assert.Throws<InvalidDataException>(()=>GuestLayoutRelocations.Read(path,24));
            }
        }
        finally{File.Delete(path);}
    }
}
