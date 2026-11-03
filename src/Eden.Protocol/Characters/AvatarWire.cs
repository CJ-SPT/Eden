using System.Text;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Characters;

internal static class AvatarWire
{
    // Local persistence limit, independent of the compressed/shared input size.
    // Charge every occurrence, including shared subgraphs, before the writer expands them.
    private const int MaximumBytes = 1024 * 1024;
    private const int MaximumDepth = 32;
    private const int NodeOverhead = 128;

    public static byte[] Encode(object? value)
    {
        int remaining = MaximumBytes;
        if (value is not BlueInstance { Name: "utillib.KeyVal" } || !Validate(value, 0, ref remaining))
        {
            throw new InvalidDataException("Invalid or oversized character appearance data.");
        }
        return BlueMarshal.Encode(value);
    }

    private static bool Validate(object? value, int depth, ref int remaining)
    {
        remaining -= NodeOverhead;
        if (depth > MaximumDepth || remaining < 0)
        {
            return false;
        }
        switch (value)
        {
            case null or bool or long or double:
                return true;
            case string text:
                remaining -= Encoding.UTF8.GetByteCount(text);
                return remaining >= 0;
            case byte[] bytes:
                remaining -= bytes.Length;
                return remaining >= 0;
            case BlueInstance { Name: "utillib.KeyVal", State: Dictionary<string, object?> fields }:
                return Validate(fields, depth + 1, ref remaining);
            case PaperDollRow row:
                remaining -= Encoding.UTF8.GetByteCount(row.Name);
                return remaining >= 0 && Validate(row.Fields, depth + 1, ref remaining);
            case Dictionary<string, object?> fields:
                foreach (var (key, field) in fields)
                {
                    if (!Validate(key, depth + 1, ref remaining) || !Validate(field, depth + 1, ref remaining))
                    {
                        return false;
                    }
                }
                return true;
            case object?[] items:
                return ValidateItems(items, depth, ref remaining);
            case List<object?> items:
                return ValidateItems(items, depth, ref remaining);
            default:
                return false;
        }
    }

    private static bool ValidateItems(IEnumerable<object?> items, int depth, ref int remaining)
    {
        foreach (var item in items)
        {
            if (!Validate(item, depth + 1, ref remaining))
            {
                return false;
            }
        }
        return true;
    }
}
