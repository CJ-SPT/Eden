namespace Eden.Protocol.Serialization;

// Empty Python containers have no domain element schema. Preserve their wire kind.
internal static class BlueEmpty
{
    public static BlueModel List { get; } = new BlueList<BlueModel>([]);
    public static BlueModel Dictionary { get; } = new BlueMap<string, BlueModel>(new Dictionary<string, BlueModel>());
    public static BlueModel IndexedDictionary { get; } = new BlueMap<long, BlueModel>(new Dictionary<long, BlueModel>());
    public static BlueModel Tuple { get; } = new EmptyTuple();

    private sealed record EmptyTuple : BlueModel
    {
        public override object ToBlue() => Array.Empty<object?>();
    }
}
