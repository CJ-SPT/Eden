using System.Collections.Immutable;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Industry;

internal sealed record EconomicPriceReply(MarketValuation Price) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["typeID"] = Price.TypeId,
                ["adjustedPrice"] = Price.AdjustedPriceIsk is { } adjusted ? (double)adjusted : null,
                ["averagePrice"] = Price.AveragePriceIsk is { } average ? (double)average : null,
            }
        );
}

internal sealed record EconomicPricesReply : BlueModel
{
    private readonly ImmutableArray<MarketValuation> _prices;
    private readonly Lazy<byte[]> _encoded;

    internal EconomicPricesReply(IReadOnlyList<MarketValuation> prices)
    {
        _prices = prices.ToImmutableArray();
        _encoded = new(() => BlueMarshal.Encode(this));
    }

    internal bool Matches(IReadOnlyList<MarketValuation> prices) => _prices.SequenceEqual(prices);

    public override object ToBlue() =>
        new BlueMap<int, EconomicPriceReply>(_prices.ToDictionary(p => p.TypeId, p => new EconomicPriceReply(p))).ToBlue();

    internal override byte[] EncodeReply() => _encoded.Value;
}

[Injectable(InjectionType.Singleton)]
public sealed class EconomicPriceCache(IMarketValuations prices)
{
    private readonly object _gate = new();
    private EconomicPricesReply? _reply;

    public void Warm() => ReadReply().EncodeReply();

    internal EconomicPricesReply ReadReply()
    {
        var current = prices.Prices();
        lock (_gate)
        {
            // Prices are public, but administrative adjustments and policy changes
            // must be visible on the very next request, including within one tick.
            if (_reply is null || !_reply.Matches(current))
            {
                _reply = new(current);
            }
            return _reply;
        }
    }
}

[Injectable]
internal sealed class EconomicPriceHandler(EconomicPriceCache prices) : IPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object? arguments,
        Dictionary<string, object?>? keywords
    )
    {
        return
            service == "config"
            && method == "GetAverageMarketPrices"
            && arguments is object?[] { Length: 0 }
            && keywords is not { Count: > 0 }
            ? prices.ReadReply()
            : PilotCallResult.Unsupported;
    }
}
