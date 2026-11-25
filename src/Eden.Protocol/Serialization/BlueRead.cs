namespace Eden.Protocol.Serialization;

// Validation at the boundary of the inert Python value graph.
internal static class BlueRead
{
    internal static string Text(object? value) =>
        value is string or byte[] ? BlueMarshal.Text(value) : throw new InvalidDataException("Expected text.");

    internal static byte[] Bytes(object? value) => value as byte[] ?? throw new InvalidDataException("Expected bytes.");

    internal static double Number(object? value) =>
        value switch
        {
            double number when double.IsFinite(number) => number,
            long integer => integer,
            _ => throw new InvalidDataException("Expected a finite number."),
        };

    internal static Dictionary<string, object?> Fields(object? value) =>
        value as Dictionary<string, object?> ?? throw new InvalidDataException("Expected fields.");

    internal static long[] PositiveIds(List<object?> values, string idName = "character IDs")
    {
        var ids = new long[values.Count];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = values[i] is long and > 0 and var id ? id : throw new InvalidDataException($"Supply a list of positive {idName}.");
        }
        return ids;
    }

    internal static object?[] Tuple(object? value, int length) =>
        value is object?[] tuple && tuple.Length == length
            ? tuple
            : throw new InvalidDataException($"Expected a tuple with {length} fields.");
}
