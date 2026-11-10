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

[Injectable]
internal sealed class EconomicPriceHandler(IMarketValuations prices) : IPilotCallHandler
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
            ? new BlueMap<int, EconomicPriceReply>(prices.Prices().ToDictionary(p => p.TypeId, p => new EconomicPriceReply(p)))
            : PilotCallResult.Unsupported;
    }
}
