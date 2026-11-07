using Eden.Gameplay.Economy;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Industry;

internal sealed record EconomicPriceReply(AdjustedPrice Price) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["typeID"] = Price.TypeId,
                ["adjustedPrice"] = (double)Price.PriceIsk,
                ["averagePrice"] = (double)Price.PriceIsk,
            }
        );
}

[Injectable]
internal sealed class EconomicPriceHandler(IEconomicPrices prices) : IPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object? arguments,
        Dictionary<string, object?>? keywords
    )
    {
        return service == "config" && method == "GetAverageMarketPrices" && arguments is object?[] { Length: 0 }
            ? new BlueMap<int, EconomicPriceReply>(prices.Prices().ToDictionary(p => p.TypeId, p => new EconomicPriceReply(p)))
            : PilotCallResult.Unsupported;
    }
}
