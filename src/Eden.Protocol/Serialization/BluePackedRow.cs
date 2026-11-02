namespace Eden.Protocol.Serialization;

// Carbon Blue af0d65b, PyRowSet.cpp: OLE DB column tags and packed DBRow data.
internal enum BlueColumnType : long
{
    Int16 = 2,
    Int32 = 3,
    Float32 = 4,
    Float64 = 5,
    Currency = 6,
    Boolean = 11,
    Int8 = 16,
    UInt8 = 17,
    UInt16 = 18,
    UInt32 = 19,
    Int64 = 20,
    UInt64 = 21,
    FileTime = 64,
    Bytes = 128,
    String = 129,
    Unicode = 130,
    Timestamp = 135,
}

internal sealed record BlueRowDescriptor(IReadOnlyList<(string Name, BlueColumnType Type)> Columns)
{
    public int ObjectCount => Columns.Count(c => IsObject(c.Type));
    public int DataLength => Columns.Sum(c => Size(c.Type)) + (Columns.Count + Columns.Count(c => c.Type == BlueColumnType.Boolean) + 7) / 8;
    private static bool IsObject(BlueColumnType type) => type is BlueColumnType.Bytes or BlueColumnType.String or BlueColumnType.Unicode;
    private static int Size(BlueColumnType type) => type switch
    {
        BlueColumnType.Int8 or BlueColumnType.UInt8 => sizeof(byte),
        BlueColumnType.Int16 or BlueColumnType.UInt16 => sizeof(short),
        BlueColumnType.Int32 or BlueColumnType.UInt32 or BlueColumnType.Float32 => sizeof(int),
        BlueColumnType.Int64 or BlueColumnType.UInt64 or BlueColumnType.Float64 or BlueColumnType.Currency or
            BlueColumnType.FileTime or BlueColumnType.Timestamp => sizeof(long),
        BlueColumnType.Boolean or BlueColumnType.Bytes or BlueColumnType.String or BlueColumnType.Unicode => 0,
        _ => throw new InvalidDataException("Unsupported Blue row column type."),
    };
}

// Keep client-supplied rows as inert wire data. Gameplay reads the authoritative
// item by ID; it never uses these client-supplied quantities or ownership fields.
internal sealed record BluePackedRow(BlueRowDescriptor Descriptor, byte[] Packed, object?[] Objects)
{
    public static void Validate(byte[] packed, int length)
    {
        int input = 0;
        int output = 0;
        bool high = false;
        int marker = 0;
        while (input < packed.Length && output < length)
        {
            if (!high) { marker = packed[input++]; }
            int nibble = high ? marker >> 4 : marker & 15;
            high = !high;
            const int RunBoundary = 8;
            int count = nibble >= RunBoundary ? nibble - RunBoundary + 1 : RunBoundary - nibble;
            if (count > length - output) { throw new InvalidDataException("Blue row run exceeds its descriptor."); }
            if (nibble < RunBoundary)
            {
                if (count > packed.Length - input) { throw new InvalidDataException("Truncated Blue row literal run."); }
                input += count;
            }
            output += count;
        }
        // Carbon permits an omitted zero tail. Never consume bytes belonging to
        // trailing Python objects or allow unused encoded runs after a full row.
        if (input != packed.Length) { throw new InvalidDataException("Trailing Blue row packed data."); }
    }
}
