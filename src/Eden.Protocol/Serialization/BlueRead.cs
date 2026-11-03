namespace Eden.Protocol.Serialization;

// Validation at the boundary of the inert Python value graph.
internal static class BlueRead
{
    internal static byte[] Bytes(object? value) => value as byte[] ?? throw new InvalidDataException("Expected bytes.");

    internal static Dictionary<string, object?> Fields(object? value) =>
        value as Dictionary<string, object?> ?? throw new InvalidDataException("Expected fields.");

    internal static object?[] Tuple(object? value, int length) =>
        value is object?[] tuple && tuple.Length == length
            ? tuple
            : throw new InvalidDataException($"Expected a tuple with {length} fields.");
}
