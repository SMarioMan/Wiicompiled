using Translator.Core.Analysis;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class GuestLayoutThunkAuditTests
{
    private const uint Start=0x80001000,Thunk=0x80002000;
    private static readonly GuestLayoutType[] Types={new("Word",4,4),new("Owner",4,Fields:new[] {
        new GuestLayoutField("players","Word",PerPlayer:true),new GuestLayoutField("tail","Word")})};
    private static ProgramImage Image(uint opcode,bool receiverBeforeCall=false,bool corrupt=false)
    {
        var code=new List<(uint,uint)>();var call=receiverBeforeCall?Start+4:Start;
        if(receiverBeforeCall)code.Add((Start,0x7c7e1b78)); // mr r30,r3
        code.Add((call,0x48000001u|((Thunk-call)&0x03fffffc)));
        code.Add((call+4,receiverBeforeCall?0x809e0008u:0x80830008u));code.Add((call+8,0x4e800020));
        for(var register=14;register<32;register++){
            var floating=opcode is 50 or 54;
            var word=(opcode<<26)|((uint)register<<21)|(11u<<16)|unchecked((ushort)(-(32-register)*(floating?8:4)));
            if(corrupt && register==20)word^=1u<<16;
            code.Add((Thunk+(uint)(register-14)*4,word));
        }
        code.Add((Thunk+18*4,0x4e800020));
        return TranslatorCppTestHarness.CreateImage(code.ToArray());
    }

    [Theory]
    [InlineData(36u)]
    [InlineData(32u)]
    [InlineData(54u)]
    [InlineData(50u)]
    public void ReceiverFactsSurviveOnlyInstructionVerifiedSaveRestoreSequences(uint opcode)
    {
        var image=Image(opcode);
        var discovered=new FunctionTranslator(image).Discover(Start,new TranslationOptions(EnableLeafInlining:false));
        Assert.Empty(new GuestLayoutAudit(Types,2,3).Analyze(discovered,"Owner").Candidates);
        var candidate=Assert.Single(new GuestLayoutAudit(Types,2,3,image:image).Analyze(discovered,"Owner").Candidates);
        Assert.Equal("Owner.tail.offset",candidate.Reference.Symbol);Assert.Equal(8,candidate.Before);Assert.Equal(12,candidate.After);
        var corrupt=Image(opcode,corrupt:true);
        var corrupted=new FunctionTranslator(corrupt).Discover(Start,new TranslationOptions(EnableLeafInlining:false));
        Assert.Empty(new GuestLayoutAudit(Types,2,3,image:corrupt).Analyze(corrupted,"Owner").Candidates);
    }

    [Fact]
    public void IntegerRestoresInvalidateTheirActualDestinationFacts()
    {
        foreach(var opcode in new uint[] {36,32,54,50})
        {
            var image=Image(opcode,receiverBeforeCall:true);
            var discovered=new FunctionTranslator(image).Discover(Start,new TranslationOptions(EnableLeafInlining:false));
            var result=new GuestLayoutAudit(Types,2,3,image:image).Analyze(discovered,"Owner");
            if(opcode==32)Assert.Empty(result.Candidates);else Assert.Single(result.Candidates);
        }
    }
}
