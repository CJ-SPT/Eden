using System.Buffers.Binary;
using System.Text;
using CommunityToolkit.HighPerformance.Buffers;
using Eden.Protocol.Docking;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Tests.Serialization;

public sealed class BlueMarshalTests
{
    [Theory]
    [InlineData(-1L, "07")]
    [InlineData(0L, "08")]
    [InlineData(1L, "09")]
    [InlineData(-128L, "0680")]
    [InlineData(127L, "067F")]
    [InlineData(-129L, "057FFF")]
    [InlineData(128L, "058000")]
    [InlineData(-32768L, "050080")]
    [InlineData(32767L, "05FF7F")]
    [InlineData(-32769L, "04FF7FFFFF")]
    [InlineData(32768L, "0400800000")]
    [InlineData(int.MinValue, "0400000080")]
    [InlineData(int.MaxValue, "04FFFFFF7F")]
    [InlineData((long)int.MinValue - 1, "03FFFFFF7FFFFFFFFF")]
    [InlineData((long)int.MaxValue + 1, "030000008000000000")]
    [InlineData(long.MinValue, "030000000000000080")]
    [InlineData(long.MaxValue, "03FFFFFFFFFFFFFF7F")]
    public void DirectIntegerWritesKeepNativeWidthsAndLittleEndianBytes(long number, string payload)
    {
        byte[] expected = Convert.FromHexString("7E00000000" + payload);
        Assert.Equal(expected, BlueMarshal.Encode(number));
        Assert.Equal(number, BlueMarshal.Decode(expected));
    }

    [Theory]
    [InlineData("0000000000000080")]
    [InlineData("010000000000F87F")]
    [InlineData("000000000000F07F")]
    [InlineData("000000000000F0FF")]
    [InlineData("000000000000F83F")]
    [InlineData("0100000000000000")]
    public void DirectFloatingPointWritesKeepEveryPayloadBit(string payload)
    {
        long bits = BinaryPrimitives.ReadInt64LittleEndian(Convert.FromHexString(payload));
        double number = BitConverter.Int64BitsToDouble(bits);
        byte[] expected = Convert.FromHexString("7E000000000A" + payload);
        Assert.Equal(expected, BlueMarshal.Encode(number));
        Assert.Equal(bits, BitConverter.DoubleToInt64Bits(Assert.IsType<double>(BlueMarshal.Decode(expected))));
    }

    [Fact]
    public void WarmedBufferEncodingDoesNotAllocateAnArrayForEachTextField()
    {
        const int fields = 256;
        const int repeats = 16;
        const int allocationBudgetPerEncode = 1024;
        const string text = "星間旅行 — pilot 🚀";
        var values = Enumerable.Repeat<object?>(new BlueUnicode(text), fields).ToList();
        using ArrayPoolBufferWriter<byte> buffer = new();
        for (int index = 0; index < 2; index++)
        {
            buffer.Clear();
            BlueMarshal.Encode(buffer, values);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < repeats; index++)
        {
            buffer.Clear();
            BlueMarshal.Encode(buffer, values);
        }
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, repeats * allocationBudgetPerEncode);
        var decoded = Assert.IsType<List<object?>>(BlueMarshal.Decode(buffer.WrittenSpan.ToArray()));
        Assert.Equal(fields, decoded.Count);
        Assert.All(decoded, value => Assert.Equal(text, value));
    }

    [Theory]
    [InlineData("", "7E000000000E", "7E000000002E00")]
    [InlineData("A", "7E000000000F41", "7E000000002E0141")]
    [InlineData("星", "7E000000001303E6989F", "7E000000002E03E6989F")]
    public void PooledOutputKeepsGoldenByteStringAndUnicodeEncodings(string text, string bytesHex, string unicodeHex)
    {
        using ArrayPoolBufferWriter<byte> buffer = new();
        BlueMarshal.Encode(buffer, text);
        Assert.Equal(Convert.FromHexString(bytesHex), buffer.WrittenSpan.ToArray());
        buffer.Clear();
        BlueMarshal.Encode(buffer, new BlueUnicode(text));
        Assert.Equal(Convert.FromHexString(unicodeHex), buffer.WrittenSpan.ToArray());
    }

    [Theory]
    [InlineData(254)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(65536)]
    public void PooledTextRetainsLengthBoundariesAndOwnedResultsAcrossReuse(int length)
    {
        string text = new('x', length);
        byte[] expectedText = Encoding.UTF8.GetBytes(text);
        byte[] owned = BlueMarshal.Encode(new BlueUnicode(text));
        using BinaryReader reader = new(new MemoryStream(owned));
        Assert.Equal(Convert.FromHexString("7E000000002E"), reader.ReadBytes(6));
        if (length < byte.MaxValue)
        {
            Assert.Equal(length, reader.ReadByte());
        }
        else
        {
            Assert.Equal(byte.MaxValue, reader.ReadByte());
            Assert.Equal(length, reader.ReadInt32());
        }
        Assert.Equal(expectedText, reader.ReadBytes(length));
        Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
        using ArrayPoolBufferWriter<byte> reused = new();
        BlueMarshal.Encode(reused, new BlueUnicode(new string('y', length)));
        Assert.Equal(text, BlueMarshal.Decode(owned));
        reused.Clear();
        BlueMarshal.Encode(reused, new object?[] { new BlueGlobal("name"), new BlueUnicode("🚀"), "\ud800" });
        var tuple = Assert.IsType<object?[]>(BlueMarshal.Decode(reused.WrittenSpan.ToArray()));
        Assert.Equal(new BlueGlobal("name"), tuple[0]);
        Assert.Equal("🚀", tuple[1]);
        Assert.Equal(Encoding.UTF8.GetBytes("\ud800"), Assert.IsType<byte[]>(tuple[2]));
    }

    [Fact]
    public void BillingDictionaryPreservesNumericAndTextKeysAlongsideNone()
    {
        // Carbon TY_DICT is a Python dictionary: numeric billing IDs and the
        // divisionID string may coexist without converting their key types.
        var request = new BlueDictionary([(2L, true), ("divisionID", 1006L), (null, false)]);
        var decoded = Assert.IsType<BlueDictionary>(BlueMarshal.Decode(BlueMarshal.Encode(request)));
        Assert.Equal(3, decoded.Entries.Count);
        Assert.Equal(true, decoded.Entries.Single(e => Equals(e.Key, 2L)).Value);
        Assert.Equal(1006L, decoded.Entries.Single(e => Equals(e.Key, "divisionID")).Value);
        Assert.Equal(false, decoded.Entries.Single(e => e.Key is null).Value);
        var noDefault = new BlueDictionary([(2L, false), ("divisionID", 1000L)]);
        var mixed = Assert.IsType<BlueDictionary>(BlueMarshal.Decode(BlueMarshal.Encode(noDefault)));
        Assert.Equal(2, mixed.Entries.Count);
        Assert.DoesNotContain(mixed.Entries, e => e.Key is null);
        Assert.IsType<Dictionary<long, object?>>(BlueMarshal.Decode(BlueMarshal.Encode(new Dictionary<long, object?> { [2] = true })));
        Assert.IsType<Dictionary<string, object?>>(BlueMarshal.Decode(BlueMarshal.Encode(new Dictionary<string, object?> { ["a"] = 1 })));
    }

    [Theory]
    [InlineData("7E0000000028", "")]
    [InlineData("7E00000000294100", "A")]
    [InlineData("7E0000000012021F66DC66", "星曜")]
    public void ReadsNativeUnicodeTokens(string hex, string expected)
    {
        Assert.Equal(expected, BlueMarshal.Decode(Convert.FromHexString(hex)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Explorer")]
    [InlineData("星間旅行 — пилот 🚀")]
    public void BiographyUnicodeRetainsItsPythonTypeAndText(string text)
    {
        byte[] bytes = BlueMarshal.Encode(new BlueUnicode(text));
        Assert.Equal(46, bytes[5]);
        Assert.Equal(text, BlueMarshal.Decode(bytes));
        Assert.IsType<byte[]>(BlueMarshal.Decode(BlueMarshal.Encode(text)));
    }

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
    [InlineData(BlueNewObject.ContractClass)]
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
