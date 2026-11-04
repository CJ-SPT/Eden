using System.Numerics;
using System.Text;

namespace Eden.Protocol.Serialization;

// Only the inert wire types used by the local session. Class names are data.
internal sealed record BlueInstance(string Name, object? State);

// Outbound constructors for native data rows. Incoming packets can never invoke a constructor.
internal sealed record BlueConstructor(string Name, object?[] Arguments, object? State = null);

internal sealed record BlueGlobal(string Name);

// Explicit Python unicode; ordinary CLR strings retain their existing byte-string wire form.
internal sealed record BlueUnicode(string Value);

internal sealed record BlueNewObject(string Name, object? State)
{
    internal const string BrainEffectClass = "eve.common.script.dogma.effect.BrainEffect";
    internal const string IndustryLocationClass = "industry.job.Location";
    internal const string ContractClass = "contracts.contractStruct.ContractStruct";
}

internal sealed record PaperDollRow(string Name, object?[] Fields);

internal sealed record BlueStream(byte[] Bytes);

internal sealed record BlueObjectReference(string Id, int ReferenceId);

// Python dictionaries can use None as a key (station efficiency's default).
internal sealed record BlueDictionary(IReadOnlyList<(object? Key, object? Value)> Entries);

internal static class BlueMarshal
{
    private const string PaperDollNamespace = "eve.common.script.paperDoll.paperDollDefinitions.";

    public static byte[] Encode(object? value)
    {
        using MemoryStream buffer = new();
        using BinaryWriter writer = new(buffer);
        writer.Write((byte)126);
        writer.Write(0);
        Write(writer, value);
        return buffer.ToArray();
    }

    public static object? Decode(byte[] bytes)
    {
        using MemoryStream stream = new(bytes, writable: false);
        using BinaryReader reader = new(stream);
        if (reader.ReadByte() != 126)
        {
            throw new InvalidDataException("Expected a Blue version-zero stream.");
        }

        int count = reader.ReadInt32();
        if (count < 0 || count > (bytes.Length - 5) / 4)
        {
            throw new InvalidDataException("Invalid shared object count.");
        }

        int end = bytes.Length - 4 * count;
        int[] slots = new int[count];
        stream.Position = end;
        for (int i = 0; i < count; i++)
        {
            slots[i] = reader.ReadInt32() - 1;
        }

        stream.Position = 5;
        int nextSlot = 0;
        Dictionary<int, object?> shared = [];
        object? result = Read(0);
        if (stream.Position != end || nextSlot != count)
        {
            throw new InvalidDataException("Invalid Blue stream boundary.");
        }

        return result;

        int Length()
        {
            int length = reader.ReadByte();
            if (length == 255)
            {
                length = reader.ReadInt32();
            }

            if (length < 0 || length > end - stream.Position)
            {
                throw new InvalidDataException("Invalid Blue value length.");
            }

            return length;
        }

        byte[] Bytes(int length)
        {
            if (length < 0 || length > end - stream.Position)
            {
                throw new InvalidDataException("Truncated Blue byte value.");
            }
            return reader.ReadBytes(length);
        }

        object?[] Items(int size, int depth)
        {
            object?[] items = new object?[size];
            for (int i = 0; i < size; i++)
            {
                items[i] = Read(depth + 1);
            }

            return items;
        }

        object Map(int depth)
        {
            int size = Length();
            Dictionary<string, object?> values = [];
            Dictionary<long, object?> indexed = [];
            bool hasDefault = false;
            object? defaultValue = null;
            for (int i = 0; i < size; i++)
            {
                object? value = Read(depth + 1);
                object? key = Read(depth + 1);
                if (key is null)
                {
                    if (hasDefault)
                    {
                        throw new InvalidDataException("Duplicate None dictionary key.");
                    }
                    hasDefault = true;
                    defaultValue = value;
                }
                else if (key is long id)
                {
                    indexed.Add(id, value);
                }
                else
                {
                    values.Add(Text(key), value);
                }
            }

            if (indexed.Count > 0 && values.Count > 0)
            {
                throw new InvalidDataException("Mixed dictionary keys.");
            }
            if (hasDefault)
            {
                List<(object? Key, object? Value)> entries = [(null, defaultValue)];
                entries.AddRange(indexed.Select(pair => ((object?)pair.Key, pair.Value)));
                entries.AddRange(values.Select(pair => ((object?)pair.Key, pair.Value)));
                return new BlueDictionary(entries);
            }
            return indexed.Count > 0 ? indexed : values;
        }

        object ReadReduction(int depth)
        {
            // Bounty sets and inventory row descriptors reduce to inert arguments.
            // Interpret only these allowlisted shapes; never execute a global.
            if (
                Read(depth + 1) is not object?[] { Length: 2 or 3 } pair
                || pair[0] is not BlueGlobal global
                || pair[1] is not object?[] { Length: 1 } args
                || reader.ReadByte() != 45
                || reader.ReadByte() != 45
            )
            {
                throw new InvalidDataException("Unsupported Blue reduction.");
            }
            if (pair.Length == 2 && global.Name is "set" or "__builtin__.set" && args[0] is List<object?> ids && ids.All(id => id is long))
            {
                return ids.Cast<long>().ToHashSet();
            }
            if (global.Name == "blue.DBRowDescriptor" && args[0] is object?[] columns)
            {
                List<(string Name, BlueColumnType Type)> fields = [];
                HashSet<string> names = [];
                foreach (var column in columns)
                {
                    if (column is not object?[] { Length: 2 } field || field[1] is not long type)
                    {
                        throw new InvalidDataException("Invalid Blue row descriptor.");
                    }
                    string text = Text(field[0]);
                    if (text.StartsWith("__", StringComparison.Ordinal) || !names.Add(text))
                    {
                        throw new InvalidDataException("Invalid Blue row column name.");
                    }
                    fields.Add((text, (BlueColumnType)type));
                }
                bool inventoryVirtuals = pair.Length == 3;
                if (inventoryVirtuals && !BlueRowDescriptor.IsInventoryVirtuals(pair[2]))
                {
                    throw new InvalidDataException("Unsupported Blue virtual columns.");
                }
                BlueRowDescriptor descriptor = new(fields, inventoryVirtuals);
                _ = descriptor.DataLength;
                return descriptor;
            }
            throw new InvalidDataException("Unsupported Blue reduction.");
        }

        object ReadNewObject(int depth)
        {
            // Carbon NEWOBJ (35) stores ((class,), state) and empty iterators.
            // Retain only known brain/location/contract values as inert data; never allocate a
            // Python class or invoke a client-provided __new__ implementation.
            if (
                Read(depth + 1) is not object?[] { Length: 1 or 2 } parts
                || parts[0] is not object?[] { Length: >= 1 } args
                || args[0] is not BlueGlobal name
                || reader.ReadByte() != 45
                || reader.ReadByte() != 45
            )
            {
                throw new InvalidDataException("Unsupported Blue new-style object.");
            }
            if (
                parts.Length == 2
                && args.Length == 1
                && name.Name is BlueNewObject.BrainEffectClass or BlueNewObject.IndustryLocationClass or BlueNewObject.ContractClass
                && parts[1] is Dictionary<string, object?> state
            )
            {
                return new BlueNewObject(name.Name, state);
            }
            if (
                parts.Length == 1
                && args.Length >= 2
                && name.Name.StartsWith(PaperDollNamespace, StringComparison.Ordinal)
                && name.Name[PaperDollNamespace.Length..] is "AppearanceRow" or "ColorSelectionRow" or "ModifierRow" or "SculptingRow"
                && args.Skip(1).All(field => field is null or long or double)
            )
            {
                return new PaperDollRow(name.Name, args[1..]);
            }
            throw new InvalidDataException("Unsupported Blue new-style object.");
        }

        BluePackedRow ReadPackedRow(int depth)
        {
            if (Read(depth + 1) is not BlueRowDescriptor descriptor)
            {
                throw new InvalidDataException("Expected a Blue row descriptor.");
            }
            byte[] packed = Bytes(Length());
            BluePackedRow.Validate(packed, descriptor.DataLength);
            return new(descriptor, packed, Items(descriptor.ObjectCount, depth));
        }

        object? Read(int depth)
        {
            if (depth > 32 || stream.Position >= end)
            {
                throw new InvalidDataException("Invalid Blue object nesting.");
            }

            byte tag = reader.ReadByte();
            int slot = -1;
            if ((tag & 64) != 0)
            {
                if (nextSlot == count || (uint)(slot = slots[nextSlot++]) >= count || shared.ContainsKey(slot))
                {
                    throw new InvalidDataException("Invalid shared object index.");
                }

                tag ^= 64;
            }

            object? value = tag switch
            {
                1 => null,
                2 => new BlueGlobal(Encoding.ASCII.GetString(Bytes(Length()))),
                3 => reader.ReadInt64(),
                4 => (long)reader.ReadInt32(),
                5 => (long)reader.ReadInt16(),
                6 => (long)reader.ReadSByte(),
                7 => -1L,
                8 => 0L,
                9 => 1L,
                10 => reader.ReadDouble(),
                11 => 0d,
                14 => Array.Empty<byte>(),
                15 => Bytes(1),
                17 => MarshalStrings.Get(reader.ReadByte()),
                16 or 19 => Bytes(Length()),
                18 => Encoding.Unicode.GetString(Bytes(checked(Length() * sizeof(char)))),
                20 => Items(Length(), depth),
                21 => Items(Length(), depth).ToList(),
                22 => Map(depth),
                23 => new BlueInstance(Text(Read(depth + 1)), Read(depth + 1)),
                27 => shared.TryGetValue(ReadIndex() - 1, out object? reference)
                    ? reference
                    : throw new InvalidDataException("Unknown shared reference."),
                31 => true,
                32 => false,
                34 => ReadReduction(depth),
                35 => ReadNewObject(depth),
                36 => Array.Empty<object?>(),
                37 => Items(1, depth),
                38 => new List<object?>(),
                39 => Items(1, depth).ToList(),
                40 => string.Empty,
                41 => Encoding.Unicode.GetString(Bytes(sizeof(char))),
                42 => ReadPackedRow(depth),
                43 => new BlueStream(Bytes(Length())),
                44 => Items(2, depth),
                46 => Encoding.UTF8.GetString(Bytes(Length())),
                47 => ReadInteger(Bytes(Length())),
                _ => throw new InvalidDataException($"Unsupported Blue tag 0x{tag:X2} at {stream.Position - 1}."),
            };
            if (slot >= 0)
            {
                shared.Add(slot, value);
            }

            return value;
        }

        int ReadIndex()
        {
            byte index = reader.ReadByte();
            return index == 255 ? reader.ReadInt32() : index;
        }
    }

    private static object ReadInteger(byte[] bytes)
    {
        BigInteger value = new(bytes);
        if (value >= long.MinValue && value <= long.MaxValue)
        {
            return (long)value;
        }
        return value;
    }

    public static string Text(object? value) =>
        value switch
        {
            string text => text,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            _ => throw new InvalidDataException("Expected a byte string."),
        };

    private static void Size(BinaryWriter writer, int size)
    {
        if (size < 255)
        {
            writer.Write((byte)size);
            return;
        }

        writer.Write((byte)255);
        writer.Write(size);
    }

    private static void Write(BinaryWriter writer, object? value)
    {
        switch (value)
        {
            case IBlueWritable model:
                Write(writer, model.ToBlue());
                break;
            case null:
                writer.Write((byte)1);
                break;
            case bool flag:
                writer.Write((byte)(flag ? 31 : 32));
                break;
            case int number:
                Write(writer, (long)number);
                break;
            case long number:
                switch (number)
                {
                    case -1 or 0 or 1:
                        writer.Write((byte)(8 + number));
                        break;
                    case >= sbyte.MinValue and <= sbyte.MaxValue:
                        writer.Write((byte)6);
                        writer.Write((sbyte)number);
                        break;
                    case >= short.MinValue and <= short.MaxValue:
                        writer.Write((byte)5);
                        writer.Write((short)number);
                        break;
                    case >= int.MinValue and <= int.MaxValue:
                        writer.Write((byte)4);
                        writer.Write((int)number);
                        break;
                    default:
                        writer.Write((byte)3);
                        writer.Write(number);
                        break;
                }

                break;
            case double number:
                writer.Write((byte)10);
                writer.Write(number);
                break;
            case string text:
                Write(writer, Encoding.UTF8.GetBytes(text));
                break;
            case BlueUnicode text:
                // Carbon TY_UTF8 constructs Python unicode, including empty strings.
                writer.Write((byte)46);
                byte[] utf8 = Encoding.UTF8.GetBytes(text.Value);
                Size(writer, utf8.Length);
                writer.Write(utf8);
                break;
            case byte[] bytes:
                if (bytes.Length == 0)
                {
                    writer.Write((byte)14);
                    break;
                }

                writer.Write((byte)(bytes.Length == 1 ? 15 : 19));
                if (bytes.Length != 1)
                {
                    Size(writer, bytes.Length);
                }

                writer.Write(bytes);
                break;
            case PaperDollRow row:
                writer.Write((byte)35);
                writer.Write((byte)37);
                Write(writer, (object?[])[new BlueGlobal(row.Name), .. row.Fields]);
                writer.Write((byte)45);
                writer.Write((byte)45);
                break;
            case BlueGlobal global:
                writer.Write((byte)2);
                byte[] name = Encoding.ASCII.GetBytes(global.Name);
                Size(writer, name.Length);
                writer.Write(name);
                break;
            case BlueRowDescriptor descriptor:
                Write(
                    writer,
                    new BlueConstructor(
                        "blue.DBRowDescriptor",
                        [descriptor.Columns.Select(c => (object?)new object?[] { c.Name, (long)c.Type }).ToArray()],
                        descriptor.InventoryVirtuals ? BlueRowDescriptor.InventoryVirtualColumns() : null
                    )
                );
                break;
            case BluePackedRow row:
                BluePackedRow.Validate(row.Packed, row.Descriptor.DataLength);
                if (row.Objects.Length != row.Descriptor.ObjectCount)
                {
                    throw new InvalidDataException("Invalid Blue row object count.");
                }
                writer.Write((byte)42);
                Write(writer, row.Descriptor);
                Size(writer, row.Packed.Length);
                writer.Write(row.Packed);
                foreach (var item in row.Objects)
                {
                    Write(writer, item);
                }
                break;
            case BlueConstructor constructor:
                writer.Write((byte)34); // Reduce: (callable, arguments), followed by two empty iterators.
                if (constructor.State is null)
                {
                    writer.Write((byte)44);
                }
                else
                {
                    writer.Write((byte)20);
                    Size(writer, 3);
                }
                writer.Write((byte)2);
                byte[] globalName = Encoding.ASCII.GetBytes(constructor.Name);
                Size(writer, globalName.Length);
                writer.Write(globalName);
                Write(writer, constructor.Arguments);
                if (constructor.State is not null)
                {
                    Write(writer, constructor.State);
                }
                writer.Write((byte)45);
                writer.Write((byte)45);
                break;
            case BlueDictionary dictionary:
                writer.Write((byte)22);
                Size(writer, dictionary.Entries.Count);
                foreach (var (key, item) in dictionary.Entries)
                {
                    Write(writer, item);
                    Write(writer, key);
                }
                break;
            case Dictionary<long, object?> indexed:
                writer.Write((byte)22);
                Size(writer, indexed.Count);
                foreach (var (id, item) in indexed)
                {
                    Write(writer, item);
                    Write(writer, id);
                }
                break;
            case BlueObjectReference reference:
                writer.Write((byte)25);
                Write(writer, Encode(new object?[] { reference.Id, reference.ReferenceId }));
                break;
            case BlueStream stream:
                writer.Write((byte)43);
                Size(writer, stream.Bytes.Length);
                writer.Write(stream.Bytes);
                break;
            case BlueInstance instance:
                writer.Write((byte)23);
                Write(writer, instance.Name);
                Write(writer, instance.State);
                break;
            case BlueNewObject instance:
                writer.Write((byte)35); // Carbon's protocol-2 __newobj__ special case.
                Write(writer, new object?[] { new object?[] { new BlueGlobal(instance.Name) }, instance.State });
                writer.Write((byte)45);
                writer.Write((byte)45);
                break;
            case object?[] tuple:
                writer.Write(
                    (byte)(
                        tuple.Length switch
                        {
                            0 => 36,
                            1 => 37,
                            2 => 44,
                            _ => 20,
                        }
                    )
                );
                if (tuple.Length > 2)
                {
                    Size(writer, tuple.Length);
                }

                foreach (object? item in tuple)
                {
                    Write(writer, item);
                }

                break;
            case List<object?> list:
                writer.Write((byte)21);
                Size(writer, list.Count);
                foreach (object? item in list)
                {
                    Write(writer, item);
                }

                break;
            case Dictionary<string, object?> fields:
                writer.Write((byte)22);
                Size(writer, fields.Count);
                foreach (var (key, item) in fields)
                {
                    Write(writer, item);
                    Write(writer, key);
                }

                break;
            case BigInteger number:
                byte[] integer = number.ToByteArray();
                writer.Write((byte)47);
                Size(writer, integer.Length);
                writer.Write(integer);
                break;
            default:
                throw new InvalidDataException("Unsupported Blue value.");
        }
    }
}
