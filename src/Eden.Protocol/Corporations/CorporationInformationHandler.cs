using Eden.Gameplay.Alliances;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Alliances;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

[Injectable]
internal sealed class CorporationInformationHandler(
    INpcCorporationDirectory npcs,
    IPlayerCorporations players,
    ICorporationMarketInformation markets,
    IAllianceMembership alliances
) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if ((service, method) is not (("corporationSvc", "GetCorpInfo") or ("allianceRegistry", "GetEmploymentRecord")))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected corporation information arguments.");
            }
            if (args is not [long id] || id is <= 0 or > int.MaxValue)
            {
                throw new InvalidDataException("Choose a valid corporation.");
            }
            var player = players.Read((int)id);
            if (player is null)
            {
                _ = npcs.Read((int)id);
            }
            return method switch
            {
                "GetCorpInfo" => new CorporationMarketActivityReply(markets.Read((int)id)),
                // The last history row is the founding event, not an alliance membership.
                "GetEmploymentRecord" => new AllianceHistoryReply(player is null ? [] : History(player)),
                _ => BlueEmpty.List,
            };
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private IReadOnlyList<AllianceHistory> History(PlayerCorporation corporation)
    {
        var history = alliances.History(corporation.Id);
        return history.Count == 0 ? [new(null, corporation.CreatedAt)] : history;
    }
}

internal sealed record CorporationMarketActivityReply(IReadOnlyList<CorporationMarketActivity> Activity) : BlueModel
{
    public override object ToBlue() =>
        new CompatibilityRowset(
            ["typeID", "sellPrice", "sellQuantity", "sellDate", "sellStationID", "buyPrice", "buyQuantity", "buyDate", "buyStationID"],
            Activity.Select(activity => new CorporationMarketActivityRow(activity)).ToArray()
        ).ToBlue();
}

internal sealed record CorporationMarketActivityRow(CorporationMarketActivity Activity) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            Activity.TypeId,
            Price(Activity.Sell),
            Activity.Sell?.Quantity,
            Activity.Sell?.Time,
            Activity.Sell?.StationId,
            Price(Activity.Buy),
            Activity.Buy?.Quantity,
            Activity.Buy?.Time,
            Activity.Buy?.StationId,
        };

    private static double? Price(CorporationMarketTrade? trade) =>
        trade is null ? null : (double)trade.PriceCents / MarketDefinitions.CentsPerIsk;
}
