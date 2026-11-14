using Translator.Core.Analysis;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class GuestUnchangedFieldAuditTests
{
    [Fact]
    public void WidthInventoryCanFindAByteCounterWithoutInventingAMovedLayout()
    {
        var types=new[]{new GuestLayoutType("Byte",1,1),new GuestLayoutType("CounterHeader",4,Fields:new[]{
            new GuestLayoutField("players","Byte"),new GuestLayoutField("halfCount","Byte"),
            new GuestLayoutField("objectCount","Byte"),new GuestLayoutField("reserved","Byte")})};
        var image=TranslatorCppTestHarness.CreateImage((0x80001000u,0x88830002u),(0x80001004u,0x4e800020u));
        var discovery=new FunctionTranslator(image).Discover(0x80001000,TranslationOptions.Default);
        Assert.Empty(new GuestLayoutAudit(types,12,128).Analyze(discovery,"CounterHeader").Candidates);
        var site=Assert.Single(new GuestLayoutAudit(types,12,128,includeUnchanged:true).Analyze(discovery,"CounterHeader").Candidates);
        Assert.Equal("CounterHeader.objectCount.offset",site.Reference.Symbol);
        Assert.Equal(2,site.Before);Assert.Equal(site.Before,site.After);
        Assert.Equal("offset",site.Reference.Operand);
    }
}
