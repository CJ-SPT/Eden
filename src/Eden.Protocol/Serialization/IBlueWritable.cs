namespace Eden.Protocol.Serialization;

// Explicit adapters preserve Python tuple/list, class, and numeric semantics.
// Implementations return only the inert values understood by BlueMarshal.
public interface IBlueWritable
{
    object? ToBlue();
}

// Outbound models use this base so call outcomes can accept models without
// accepting an arbitrary CLR object. The decoder still uses the inert vocabulary.
public abstract record BlueModel : IBlueWritable
{
    public abstract object? ToBlue();

    internal virtual byte[] EncodeReply() => BlueMarshal.Encode(this);
}

internal sealed record BlueList<T>(IReadOnlyList<T> Items) : BlueModel
{
    public override object ToBlue() => Items.Select(item => (object?)item).ToList();
}

internal sealed record BlueMap<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> Entries) : BlueModel
    where TKey : notnull
{
    public override object ToBlue()
    {
        if (typeof(TKey) == typeof(string))
        {
            return Entries.ToDictionary(pair => (string)(object)pair.Key, pair => (object?)pair.Value);
        }
        if (typeof(TKey) == typeof(long) || typeof(TKey) == typeof(int) || default(TKey) is Enum)
        {
            return Entries.ToDictionary(pair => Convert.ToInt64(pair.Key), pair => (object?)pair.Value);
        }
        return new BlueDictionary(Entries.Select(pair => ((object?)pair.Key, (object?)pair.Value)).ToArray());
    }
}
