using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;
namespace Translator.Tests;
public class GuestCountConversionTests
{
    private static readonly GuestLayoutType[] Types={new("Byte",1,1)};
    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(48)] [InlineData(100)]
    public void CheckedCounterTransferPreservesStockAndSourceRegister(int players)
    {
        var image=TranslatorCppTestHarness.CreateImage(new[]{(0x80001000u,0x7c8903a6u),(0x80001004u,0x4e800020u)});
        var plan=new GuestLayoutRelocations(Types,12,players,Array.Empty<GuestLayoutReference>(),countConversions:new[]{new GuestCountConversion(0x80001000,0x7c8903a6,"r4","ceil(players/6)")});
        plan.ValidateImage(image);var source=new FunctionTranslator(image,layoutRelocations:plan).Translate(0x80001000).CxxCode;
        if(players==12)Assert.Equal(new FunctionTranslator(image).Translate(0x80001000).CxxCode,source);
        else {Assert.Contains("count==2u",source);Assert.Contains($"? {(players+5)/6}u : count",source);}
        var wrong=new GuestLayoutRelocations(Types,12,players,Array.Empty<GuestLayoutReference>(),countConversions:new[]{new GuestCountConversion(0x80001000,0x7c8903a6,"r5","ceil(players/6)")});
        Assert.Throws<InvalidDataException>(()=>wrong.ValidateImage(image));
    }
    [Fact]
    public void CounterConversionsRequireVersionTwentyNine()
    {
        var path=Path.GetTempFileName();try{foreach(var version in new[]{28,29,30}){
            File.WriteAllText(path,JsonSerializer.Serialize(new{Version=version,OriginalPlayers=12,Types,References=Array.Empty<object>(),CountConversions=new[]{new{Instruction="0x80001000",ExpectedWord="0x7c8903a6",Input="r4",Count="ceil(players/6)"}}}));
            if(version==29)Assert.Equal(24,GuestLayoutRelocations.Read(path,24).Players);else Assert.Throws<InvalidDataException>(()=>GuestLayoutRelocations.Read(path,24));
        }}finally{File.Delete(path);}
    }
}
