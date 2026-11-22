using Translator.Core.Disassembly;
using Translator.Core.Ir;
using Translator.Core.Lifting;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class GuestByteOperationTests
{
    private static readonly GuestLayoutType[] Types={
        new("Byte",1,1),new("Pool",1,Fields:new[]{new GuestLayoutField("objects","Byte",Count:10,PerPlayer:true)}),
        new("Header",1,Fields:new[]{new GuestLayoutField("players","Byte"),new GuestLayoutField("half","Byte"),new GuestLayoutField("total","Byte")})};
    private static IReadOnlyList<IrInstruction> Apply(int capacity,GuestByteOperation site,uint? word=null){
        var plan=new GuestLayoutRelocations(Types,12,capacity,Array.Empty<GuestLayoutReference>(),byteOperations:new[]{site});
        var instruction=PpcDecoder.Decode(site.Instruction,word??site.ExpectedWord);
        return plan.Apply(instruction,new PpcLifter().Lift(new[]{instruction})[0].Ir);
    }

    [Theory]
    [InlineData(12)] [InlineData(24)] [InlineData(25)] [InlineData(26)] [InlineData(52)] [InlineData(128)]
    public void DerivedCountersKeepTheByteAbiAndWidenOnlyBeyondTheByteDomain(int capacity){
        var site=new GuestByteOperation(0x80001000,0x88640002,"derived-count","Pool.objects.count","Header.total.offset","Header.players.offset",10);
        var nodes=Apply(capacity,site);var load=Assert.IsType<IrLoad>(nodes[0]);
        Assert.Equal(1,load.SizeBytes);Assert.Equal(capacity<=25?2:0,load.Address.Offset);
        Assert.Equal(capacity<=25?1:2,nodes.Count);
        if(capacity>25){var multiply=Assert.IsType<IrBinary>(nodes[1]);Assert.Equal("mul",multiply.Op);Assert.Equal(10,multiply.Right.Constant);}
    }

    [Fact]
    public void IndexAndLocalOrdinalOperationsDoNotWrapAt256(){
        var index=Assert.IsType<IrAssign>(Assert.Single(Apply(128,new(0x80001000,0x5483063e,"index32","Pool.objects.count"))));
        Assert.Equal("r3",index.Destination);Assert.Equal("r4",index.Value.RegisterName);
        var offset=Assert.IsType<IrBinary>(Assert.Single(Apply(128,new(0x80001004,0x54831d78,"offset32","Pool.objects.count"))));
        Assert.Equal("shl",offset.Op);Assert.Equal(3,offset.Right.Constant);
        var store=Apply(128,new(0x80001008,0x7cea49ae,"scratch-store16","Pool.objects.count",Elements:5,AvailableBytes:16));
        Assert.Equal(1,Assert.IsType<IrBinary>(store[0]).Right.Constant);Assert.Equal(2,Assert.IsType<IrStore>(store[2]).SizeBytes);
        var load=Apply(128,new(0x8000100c,0x7c7f18ae,"scratch-load16","Pool.objects.count",Elements:5,AvailableBytes:16));
        Assert.Equal(1,Assert.IsType<IrBinary>(load[0]).Right.Constant);Assert.Equal(2,Assert.IsType<IrLoad>(load[2]).SizeBytes);
    }

    [Fact]
    public void ConditionalScratchWideningUsesBlockLocalTemporariesInSsa(){
        var image=TranslatorCppTestHarness.CreateImage((0x80001000u,0x2c060000u),(0x80001004u,0x41820010u),
            (0x80001008u,0x7cea49aeu),(0x8000100cu,0x38c6ffffu),(0x80001010u,0x4bfffff0u),(0x80001014u,0x4e800020u));
        var plan=new GuestLayoutRelocations(Types,12,128,Array.Empty<GuestLayoutReference>(),byteOperations:new[]{
            new GuestByteOperation(0x80001008,0x7cea49ae,"scratch-store16","Pool.objects.count",Elements:5,AvailableBytes:16)});
        var result=new FunctionTranslator(image,layoutRelocations:plan).Translate(0x80001000,TranslationOptions.Default);
        Assert.Contains("Write16",result.CxxCode);
    }

    [Fact]
    public void ByteOperationsRequireFormatThirtyOneAndRejectFutureFormats(){
        var path=Path.GetTempFileName();
        try{foreach(var version in Enumerable.Range(30, GuestLayoutRelocations.CurrentFormatVersion - 30 + 2)){
            File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(new{Version=version,OriginalPlayers=12,Types,References=Array.Empty<object>(),
                ByteOperations=new[]{new{Instruction="0x80001000",ExpectedWord="0x5483063E",Kind="index32",Count="Pool.objects.count"}}}));
            if(version is >= 31 and <= GuestLayoutRelocations.CurrentFormatVersion)Assert.Equal(128,GuestLayoutRelocations.Read(path,128).Players);
            else Assert.Throws<InvalidDataException>(()=>GuestLayoutRelocations.Read(path,128));
        }}finally{File.Delete(path);}
    }

    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(25)] [InlineData(26)]
    [InlineData(48)] [InlineData(52)] [InlineData(64)] [InlineData(100)]
    [InlineData(116)] [InlineData(118)] [InlineData(127)] [InlineData(128)] [InlineData(254)]
    public void OneBasedByteRanksStayPositivePast127AndRetainTheNoneSentinel(int capacity)
    {
        foreach(var word in new[]{0x7d080774u,0x7d280774u})
        {
            var nodes=Apply(capacity,new(0x80001000,word,"rank8","players"));
            if(capacity<=127)Assert.Equal("sext",Assert.IsType<IrBinary>(Assert.Single(nodes)).Op);
            for(uint low=0;low<256;low++)foreach(var input in new[]{low,low|0x12340000u})
            {
                var regs=new Dictionary<string,uint>{{"r8",input},{"r9",input}};
                uint Value(IrValue v)=>v.RegisterName is {} name?regs[name]:unchecked((uint)v.Constant!.Value);
                foreach(var node in nodes)
                {
                    var binary=Assert.IsType<IrBinary>(node);var a=Value(binary.Left);var b=Value(binary.Right);
                    regs[binary.Destination]=binary.Op switch {
                        "and"=>a&b,"or"=>a|b,"add"=>unchecked(a+b),"sub"=>unchecked(a-b),"divu"=>a/b,
                        "sext"=>unchecked((uint)(sbyte)(byte)a),_=>throw new InvalidOperationException(binary.Op)};
                }
                var expected=capacity<=127?unchecked((uint)(sbyte)(byte)input):low==255?uint.MaxValue:low;
                Assert.Equal(expected,regs["r8"]);Assert.Equal(input,regs["r9"]);
            }
        }
        Assert.Throws<InvalidDataException>(()=>Apply(capacity,new(0x80001000,0x7d080775,"rank8","players")));
        Assert.Throws<InvalidDataException>(()=>Apply(255,new(0x80001000,0x7d080774,"rank8","players")));
    }

    [Fact]
    public void UnsignedRanksRequireFormatThirtyTwo()
    {
        var path=Path.GetTempFileName();
        try{foreach(var version in new[]{31,32}){
            File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(new{Version=version,OriginalPlayers=12,Types,References=Array.Empty<object>(),
                ByteOperations=new[]{new{Instruction="0x80001000",ExpectedWord="0x7D080774",Kind="rank8",Count="players"}}}));
            if(version==32)Assert.Equal(128,GuestLayoutRelocations.Read(path,128).Players);
            else Assert.Throws<InvalidDataException>(()=>GuestLayoutRelocations.Read(path,128));
        }}finally{File.Delete(path);}
    }

    [Fact]
    public void WideningRejectsWrongInstructionsAndInsufficientLocalStorage(){
        var site=new GuestByteOperation(0x80001000,0x5483063e,"index32","Pool.objects.count");
        Assert.Throws<InvalidDataException>(()=>Apply(128,site,0x5483067e));
        Assert.Throws<InvalidDataException>(()=>Apply(128,site with {ExpectedWord=0x5483067e}));
        var scratch=new GuestByteOperation(0x80001008,0x7cea49ae,"scratch-store16","Pool.objects.count",Elements:5,AvailableBytes:9);
        Assert.Throws<InvalidDataException>(()=>Apply(128,scratch));
        Assert.Throws<InvalidDataException>(()=>Apply(6554,scratch with {AvailableBytes=16}));
        Assert.Throws<InvalidDataException>(()=>new GuestLayoutRelocations(Types,12,128,
            new[]{new GuestLayoutReference(site.Instruction,site.ExpectedWord,0,"right","Pool.objects.count",135)},
            byteOperations:new[]{site}));
    }
}
