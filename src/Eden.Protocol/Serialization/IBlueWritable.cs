namespace Eden.Protocol.Serialization;

// Explicit adapters preserve Python tuple/list, class, and numeric semantics.
// Implementations return only the inert values understood by BlueMarshal.
public interface IBlueWritable
{
    object? ToBlue();
}

internal sealed record BlueList<T>(IReadOnlyList<T> Items) : IBlueWritable
{
    public object ToBlue() => Items.Select(item => (object?)item).ToList();
}

internal sealed record BlueMap<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> Entries) : IBlueWritable
    where TKey : notnull
{
    public object ToBlue() => new BlueDictionary(Entries.Select(pair => ((object?)pair.Key, (object?)pair.Value)).ToArray());
}
