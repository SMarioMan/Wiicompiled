using System.Text.Json;
using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class GuestLayoutCursorTests
{
    private const uint Start=0x80001000;
    private static readonly GuestLayoutType[] Types={new("Raw",4,32),new("Action",4,8)};
    private static GuestResampledCursor Site()=>new(Start,0x38840020,"r4","r6","Action.size","Raw.size","players",2);
    private static GuestLayoutRelocations Layout(int players,GuestResampledCursor? site=null,GuestLayoutReference[]? references=null)=>
        new(Types,2,players,references??Array.Empty<GuestLayoutReference>(),resampledCursors:new[] {site??Site()});
    private static ProgramImage Image(uint word=0x38840020)=>TranslatorCppTestHarness.CreateImage((Start,word),(Start+4,0x4e800020));

    [Fact]
    public void OriginalCursorIsByteExactAndExpandedStepUsesIndexVariablesWithoutClobberingOtherGprs()
    {
        var image=Image();
        Assert.Equal(new FunctionTranslator(image).Translate(Start,TranslationOptions.Default).CxxCode,
            new FunctionTranslator(image,layoutRelocations:Layout(2)).Translate(Start,TranslationOptions.Default).CxxCode);
        var result=new FunctionTranslator(image,layoutRelocations:Layout(3)).Discover(Start,TranslationOptions.Default);
        var assignments=result.LinearIr.Blocks.SelectMany(block=>block.Instructions).OfType<IrBinary>().ToArray();
        var cursor=Assert.Single(assignments.Where(node=>node.Destination=="r4"));
        Assert.NotNull(cursor.Right.RegisterName);
        Assert.All(assignments.Where(node=>node.Destination.StartsWith('r')),node=>Assert.Equal("r4",node.Destination));
        // A real loop must not manufacture incoming definitions for scratch
        // values that exist only inside this cursor-address update.
        var loop=TranslatorCppTestHarness.CreateImage((Start,0x38c60008),(Start+4,0x38840020),
            (Start+8,0x2c060018),(Start+12,0x4082fff4),(Start+16,0x4e800020));
        var loopLayout=Layout(3,Site() with {Instruction=Start+4});
        Assert.NotEmpty(new FunctionTranslator(loop,layoutRelocations:loopLayout).Translate(Start,TranslationOptions.Default).CxxCode);
    }

    [Fact]
    public void StaleWordsOtherEffectsInvalidDimensionsAndOverlappingRewritesAreRejected()
    {
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(0x38840021),layoutRelocations:Layout(3)));
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(0x38840021),layoutRelocations:Layout(3,Site() with {ExpectedWord=0x38840021})));
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(0x34840020),layoutRelocations:Layout(3,Site() with {ExpectedWord=0x34840020})));
        Assert.Throws<InvalidDataException>(()=>Layout(1));
        Assert.Throws<InvalidDataException>(()=>Layout(3,Site() with {NextIndex="r4"}));
        Assert.Throws<InvalidDataException>(()=>Layout(3,Site() with {SourceCount=3}));
        Assert.Throws<InvalidDataException>(()=>Layout(3,references:new[] {new GuestLayoutReference(Start,0x38840020,0,"right","Raw.size")}));
    }

    [Fact]
    public void ReadersRequireVersionSixForAuthoredCursorResampling()
    {
        var path=Path.GetTempFileName();
        try
        {
            foreach(var version in new[] {5,6,7})
            {
                var site=Site();
                File.WriteAllText(path,JsonSerializer.Serialize(new {
                    Version=version,OriginalPlayers=2,Types,References=Array.Empty<object>(),
                    ResampledCursors=new[] {new {Instruction="0x80001000",ExpectedWord="0x38840020",site.Cursor,
                        site.NextIndex,site.IndexStride,site.SourceStride,site.Count,site.SourceCount}}
                }));
                if(version==6)Assert.Equal(3,GuestLayoutRelocations.Read(path,3).Players);
                else Assert.Throws<InvalidDataException>(()=>GuestLayoutRelocations.Read(path,3));
            }
        }
        finally {File.Delete(path);}
    }
}
