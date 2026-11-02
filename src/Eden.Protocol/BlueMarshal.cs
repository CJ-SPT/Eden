using System.Numerics;
using System.Text;

namespace Eden.Protocol;

// Only the inert wire types used by the local session. Class names are data.
internal sealed record BlueInstance(string Name, object? State);
internal sealed record BlueStream(byte[] Bytes);
internal sealed record BlueObjectReference(string Id, int ReferenceId);

internal static class BlueMarshal
{
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

        object?[] Items(int size, int depth)
        {
            object?[] items = new object?[size];
            for (int i = 0; i < size; i++)
            {
                items[i] = Read(depth + 1);
            }

            return items;
        }

        Dictionary<string, object?> Map(int depth)
        {
            int size = Length();
            Dictionary<string, object?> values = [];
            for (int i = 0; i < size; i++)
            {
                object? value = Read(depth + 1);
                values.Add(Text(Read(depth + 1)), value);
            }

            return values;
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
                15 => reader.ReadBytes(1),
                17 => MarshalStrings.Get(reader.ReadByte()),
                16 or 19 => reader.ReadBytes(Length()),
                20 => Items(Length(), depth),
                21 => Items(Length(), depth).ToList(),
                22 => Map(depth),
                23 => new BlueInstance(Text(Read(depth + 1)), Read(depth + 1)),
                27 => shared.TryGetValue(ReadIndex() - 1, out object? reference) ? reference : throw new InvalidDataException("Unknown shared reference."),
                31 => true,
                32 => false,
                36 => Array.Empty<object?>(),
                37 => Items(1, depth),
                38 => new List<object?>(),
                39 => Items(1, depth).ToList(),
                43 => new BlueStream(reader.ReadBytes(Length())),
                44 => Items(2, depth),
                46 => Encoding.UTF8.GetString(reader.ReadBytes(Length())),
                47 => new BigInteger(reader.ReadBytes(Length())),
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

    public static string Text(object? value) => value switch
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
            case object?[] tuple:
                writer.Write((byte)(tuple.Length switch { 0 => 36, 1 => 37, 2 => 44, _ => 20 }));
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
