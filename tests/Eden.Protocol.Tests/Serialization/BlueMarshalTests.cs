using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Tests.Serialization;

public sealed class BlueMarshalTests
{
    [Fact]
    public void InventoryVirtualColumnsSurvivePackedRowsWithoutBecomingPhysicalFields()
    {
        var header = InventoryHandler.ItemDescriptor();
        var descriptor = Assert.IsType<BlueRowDescriptor>(BlueMarshal.Decode(BlueMarshal.Encode(header)));
        Assert.True(descriptor.InventoryVirtuals);
        Assert.Equal(9, descriptor.Columns.Count);
        Assert.Equal(7, descriptor.Columns.Count(c => c.Name is not ("quantity" or "customInfo")));
        Assert.Equal(1, descriptor.ObjectCount);
        var row = new BluePackedRow(descriptor, [], [""]);
        byte[] encoded = BlueMarshal.Encode(row);
        var decoded = Assert.IsType<BluePackedRow>(BlueMarshal.Decode(encoded));
        Assert.True(decoded.Descriptor.InventoryVirtuals);
        Assert.Equal(encoded, BlueMarshal.Encode(decoded));

        var getters = BlueRowDescriptor.InventoryVirtualColumns();
        getters[0] = new object?[] { "stacksize", new BlueGlobal("untrusted.Call") };
        Assert.Throws<InvalidDataException>(() => BlueMarshal.Decode(BlueMarshal.Encode(header with { State = getters })));
        Assert.Throws<InvalidDataException>(() => BlueMarshal.Decode(BlueMarshal.Encode(header with { State = new List<object?>() })));
    }

    [Fact]
    public void ReadsPackedRowsAsInertDataWithDescriptorAndTrailingObjects()
    {
        // TY_DBROW, descriptor(itemID I8, name STR), one literal byte 42 and
        // implicit zero tail, followed by the row's separate string object.
        var fixture = Convert.FromHexString(
            "7E000000002A222C0214626C75652E4442526F7744657363726970746F72252C2C13066974656D494406142C13046E616D650581002D2D02072A13036F7265"
        );
        var row = Assert.IsType<BluePackedRow>(BlueMarshal.Decode(fixture));
        Assert.Equal(9, row.Descriptor.DataLength);
        Assert.Equal(1, row.Descriptor.ObjectCount);
        Assert.Equal("ore", BlueMarshal.Text(Assert.Single(row.Objects)));
        Assert.Equal(new byte[] { 7, 42 }, row.Packed);
        Assert.Equal(fixture, BlueMarshal.Encode(row));
        Assert.Throws<InvalidDataException>(() => BluePackedRow.Validate([0, 1], 9));
        Assert.Throws<InvalidDataException>(() => BluePackedRow.Validate([15], 7));
        Assert.Throws<InvalidDataException>(() => BluePackedRow.Validate([0, 1, 2, 3, 4, 5, 6, 7, 8, 0], 8));
        var invalid = new BlueConstructor("blue.DBRowDescriptor", [new object?[] { new object?[] { "itemID", 999L } }]);
        Assert.Throws<InvalidDataException>(() => BlueMarshal.Decode(BlueMarshal.Encode(invalid)));
    }

    [Fact]
    public void PreservesNoneDictionaryKeyAndDistinguishesItFromZero()
    {
        // Carbon's dictionary token writes each value before its key. The native
        // reprocessing client indexes its default station efficiency with None.
        byte[] fixture = Convert.FromHexString("7E0000000016010A000000000000E03F01");
        var map = Assert.IsType<BlueDictionary>(BlueMarshal.Decode(fixture));
        Assert.Null(Assert.Single(map.Entries).Key);
        Assert.Equal(0.5, map.Entries[0].Value);
        Assert.Equal(fixture, BlueMarshal.Encode(map));
        var nested = new object?[] { new BlueDictionary([(null, 0.5), (0L, 0.7), (1230L, 0.8)]) };
        var roundTrip = Assert.IsType<BlueDictionary>(Assert.IsType<object?[]>(BlueMarshal.Decode(BlueMarshal.Encode(nested)))[0]);
        Assert.Equal(new object?[] { null, 0L, 1230L }, roundTrip.Entries.Select(p => p.Key));
        Assert.Throws<InvalidDataException>(() => BlueMarshal.Decode(BlueMarshal.Encode(new BlueDictionary([(null, 1), (null, 2)]))));
    }

    [Fact]
    public void DecodesValueBeforeKeyAndUsesPinnedInternTable()
    {
        // One dictionary entry with integer value 1 and interned key 135.
        var values = BlueRead.Fields(BlueMarshal.Decode(Convert.FromHexString("7E000000001601091187")));
        Assert.Equal(1L, values["machoVersion"]);
    }

    [Fact]
    public void ReadsSharedStringsUsingTheTrailingReferenceTable()
    {
        var tuple = BlueRead.Tuple(BlueMarshal.Decode(Convert.FromHexString("7E010000002C530261621B0101000000")), 2);
        Assert.Same(tuple[0], tuple[1]);
        Assert.Equal("ab", BlueMarshal.Text(tuple[1]));
    }

    [Fact]
    public void ReadsOwnerIdSetWithoutInvokingPythonConstructors()
    {
        var set = new BlueConstructor("__builtin__.set", [new List<object?> { 1000044 }]);
        Assert.Equal(1000044L, Assert.Single(Assert.IsType<HashSet<long>>(BlueMarshal.Decode(BlueMarshal.Encode(set)))));
        Assert.Throws<InvalidDataException>(() => BlueMarshal.Decode(BlueMarshal.Encode(set with { Name = "unknown" })));
    }

    [Theory]
    [InlineData("7E0000000016FFFFFFFFFF")]
    [InlineData("7E010000002C530261621B0102000000")]
    [InlineData("7E000000000801")]
    [InlineData("7E000000000F")]
    [InlineData("7E010000004F01000000")]
    public void RejectsMalformedStreams(string hex)
    {
        Assert.Throws<InvalidDataException>(() => BlueMarshal.Decode(Convert.FromHexString(hex)));
    }

    [Theory]
    [InlineData("7E000000000E", "")]
    [InlineData("7E000000000F41", "41")]
    [InlineData("7E010000004F4101000000", "41")]
    public void SingleByteAndEmptyStringsRemainDistinct(string input, string expected)
    {
        Assert.Equal(Convert.FromHexString(expected), Assert.IsType<byte[]>(BlueMarshal.Decode(Convert.FromHexString(input))));
    }

    [Theory]
    [InlineData(BlueNewObject.BrainEffectClass)]
    [InlineData(BlueNewObject.IndustryLocationClass)]
    public void NewStyleDataObjectsCarryStateWithoutExecutingIncomingGlobals(string className)
    {
        BlueNewObject value = new(className, new Dictionary<string, object?> { ["value"] = 5d });
        byte[] encoded = BlueMarshal.Encode(value);
        Assert.Equal(35, encoded[5]);
        var decoded = Assert.IsType<BlueNewObject>(BlueMarshal.Decode(encoded));
        Assert.Equal(value.Name, decoded.Name);
        Assert.Equal(5d, Assert.IsType<Dictionary<string, object?>>(decoded.State)["value"]);
        Assert.Equal(encoded, BlueMarshal.Encode(decoded));
        Assert.Throws<InvalidDataException>(() => BlueMarshal.Decode(BlueMarshal.Encode(value with { Name = "untrusted.Call" })));
        Assert.Throws<InvalidDataException>(() => BlueMarshal.Decode(BlueMarshal.Encode(value with { State = null })));
        encoded[^1] = 36;
        Assert.Throws<InvalidDataException>(() => BlueMarshal.Decode(encoded));
    }

    [Fact]
    public void EncodesNestedSessionFieldsAsBlueValues()
    {
        var value = new BlueInstance("utillib.KeyVal", new Dictionary<string, object?> { ["userid"] = 1000001 });
        var decoded = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(value)));
        Assert.Equal(value.Name, decoded.Name);
        Assert.Equal(1000001L, BlueRead.Fields(decoded.State)["userid"]);
    }
}
