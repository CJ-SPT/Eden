using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Tests.Serialization;

public sealed class BlueMarshalTests
{
    [Fact]
    public void DecodesValueBeforeKeyAndUsesPinnedInternTable()
    {
        // One dictionary entry with integer value 1 and interned key 135.
        var values = PilotLogin.Fields(BlueMarshal.Decode(Convert.FromHexString("7E000000001601091187")));
        Assert.Equal(1L, values["machoVersion"]);
    }

    [Fact]
    public void ReadsSharedStringsUsingTheTrailingReferenceTable()
    {
        var tuple = PilotLogin.Tuple(BlueMarshal.Decode(Convert.FromHexString("7E010000002C530261621B0101000000")), 2);
        Assert.Same(tuple[0], tuple[1]);
        Assert.Equal("ab", BlueMarshal.Text(tuple[1]));
    }

    [Theory]
    [InlineData("7E0000000016FFFFFFFFFF")]
    [InlineData("7E010000002C530261621B0102000000")]
    [InlineData("7E000000000801")]
    public void RejectsMalformedStreams(string hex)
    {
        Assert.Throws<InvalidDataException>(() => BlueMarshal.Decode(Convert.FromHexString(hex)));
    }

    [Fact]
    public void EncodesNestedSessionFieldsAsBlueValues()
    {
        var value = new BlueInstance("utillib.KeyVal", new Dictionary<string, object?> { ["userid"] = 1000001 });
        var decoded = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(value)));
        Assert.Equal(value.Name, decoded.Name);
        Assert.Equal(1000001L, PilotLogin.Fields(decoded.State)["userid"]);
    }
}
