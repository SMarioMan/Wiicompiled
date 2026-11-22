using System.Text.Json;
using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class GuestArrayFillCountTests
{
    private const uint Start=0x80001000;
    private static readonly GuestLayoutType[] Types={
        new("Header",4,8),new("Word",4,4),
        new("Object",4,Fields:new[]{new GuestLayoutField("header","Header"),
            new GuestLayoutField("first","Word",PerPlayer:true),new GuestLayoutField("second","Word",PerPlayer:true)})};
    private static GuestArrayFill Fill(int addend=-1)=>new(Start,new uint[]{0x90030008,0x90030010},"r3","r0",new[]{
        new GuestFillArray("Object.first.offset","Object.first.stride","players",4,CountAddend:addend),
        new GuestFillArray("Object.second.offset","Object.second.stride","players",4,CountAddend:addend)});
    private static GuestLayoutRelocations Layout(int players,GuestArrayFill? fill=null)=>new(Types,2,players,
        Array.Empty<GuestLayoutReference>(),arrayFills:new[]{fill??Fill()});

    [Theory]
    [InlineData(3)] [InlineData(13)] [InlineData(24)] [InlineData(100)]
    public void ShortAuthoredPatternGrowsByTheCapacityIncrease(int players)
    {
        var fill=Fill();var image=TranslatorCppTestHarness.CreateImage((Start,fill.ExpectedWords[0]),
            (Start+4,fill.ExpectedWords[1]),(Start+8,0x4e800020));
        var baseline=new FunctionTranslator(image).Translate(Start).CxxCode;
        Assert.Equal(baseline,new FunctionTranslator(image,layoutRelocations:Layout(2)).Translate(Start).CxxCode);
        var stores=new FunctionTranslator(image,layoutRelocations:Layout(players)).Discover(Start,TranslationOptions.Default)
            .LinearIr.Blocks.SelectMany(block=>block.Instructions).OfType<IrStore>().ToArray();
        Assert.Equal(2*(players-1),stores.Length);
        for(int i=0;i<players-1;i++){
            Assert.Equal(8+i*4,stores[i*2].Address.Offset);
            Assert.Equal(8+players*4+i*4,stores[i*2+1].Address.Offset);
        }
    }

    [Fact]
    public void InvalidCountsAndDifferentGrowthAreRejected()
    {
        Assert.Throws<InvalidDataException>(()=>Layout(3,Fill(-2)));
        Assert.Throws<OverflowException>(()=>Layout(3,Fill(int.MaxValue)));
        var mixed=Fill() with {Arrays=new[]{Fill().Arrays[0],Fill().Arrays[1] with {CountAddend=0}}};
        Assert.Throws<InvalidDataException>(()=>Layout(3,mixed));
    }

    [Fact]
    public void PositiveAdjustmentCanFillAPrefixOfPairedStorage()
    {
        var types=new[]{new GuestLayoutType("Word",4,4),new GuestLayoutType("Object",4,
            Fields:new[]{new GuestLayoutField("pairs","Word",Count:2,PerPlayer:true)})};
        var words=new uint[]{0x90030000,0x90030004,0x90030008};
        var fill=new GuestArrayFill(Start,words,"r3","r0",new[]{new GuestFillArray("Object.pairs.offset","Word.size","players",4,CountAddend:1)});
        var image=TranslatorCppTestHarness.CreateImage(words.Append(0x4e800020u).Select((word,i)=>(Start+(uint)i*4,word)).ToArray());
        var plan=new GuestLayoutRelocations(types,2,3,Array.Empty<GuestLayoutReference>(),arrayFills:new[]{fill});
        var stores=new FunctionTranslator(image,layoutRelocations:plan).Discover(Start,TranslationOptions.Default)
            .LinearIr.Blocks.SelectMany(block=>block.Instructions).OfType<IrStore>().ToArray();
        Assert.Equal(new[]{0,4,8,12},stores.Select(store=>store.Address.Offset));
    }

    [Fact]
    public void AdjustmentsRequireVersionThirtyAndRetainInstructionProofs()
    {
        var description=JsonSerializer.SerializeToElement(Layout(3).Describe());
        Assert.Equal(-1,description.GetProperty("ArrayFills")[0].GetProperty("Arrays")[0].GetProperty("CountAddend").GetInt32());
        var path=Path.GetTempFileName();
        try{
            foreach(var version in new[]{29,30,31,32,33,34}){
                File.WriteAllText(path,JsonSerializer.Serialize(new{Version=version,OriginalPlayers=2,Types,
                    References=Array.Empty<object>(),ArrayFills=new[]{new{Begin=$"0x{Start:X8}",
                        ExpectedWords=Fill().ExpectedWords.Select(word=>$"0x{word:X8}"),BaseRegister="r3",ValueRegister="r0",Arrays=Fill().Arrays}}}));
                if(version is 30 or 31 or 32 or 33)Assert.Equal(3,GuestLayoutRelocations.Read(path,3).Players);
                else Assert.Throws<InvalidDataException>(()=>GuestLayoutRelocations.Read(path,3));
            }
            var wrong=TranslatorCppTestHarness.CreateImage((Start,0x9003000cu),(Start+4,0x90030010u),(Start+8,0x4e800020u));
            Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(wrong,layoutRelocations:Layout(3)));
        }finally{File.Delete(path);}
    }
}
