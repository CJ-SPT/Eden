using Eden.Gameplay.Docking;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.PilotSessions;

[Injectable]
internal sealed class AccountCompatibilityHandler(TimeProvider clock) : IPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object? arguments,
        Dictionary<string, object?>? keywords
    )
    {
        return (service, method) switch
        {
            ("machoNet", "GetServiceInfo") => BlueEmpty.Dictionary,
            ("machoNet", "GetTime") => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(),
            ("config", "GetMultiOwnersEx") => OwnerRows(
                arguments,
                context.ActiveFlight?.VisiblePilots() ?? context.Roster.Select(PilotContact.From)
            ),
            ("config", "GetMultiCorpTickerNamesEx")
                when arguments is object?[] { Length: 1 } corpArgs && corpArgs[0] is List<object?> ids => new CorporationTickerTable(
                (context.ActiveFlight?.VisiblePilots() ?? context.Roster.Select(PilotContact.From))
                    .DistinctBy(pilot => pilot.CorpId)
                    .Where(pilot => ids.Contains((long)pilot.CorpId))
                    .Select(pilot => new CorporationTickerRow(pilot.CorpId, pilot.CorporationTicker))
                    .ToArray()
            ),
            ("config", "GetBlackListedPlanets" or "GetOldStationData") => BlueEmpty.List,
            ("config", "GetAverageMarketPrices") => BlueEmpty.IndexedDictionary,
            ("air_npe", "is_air_npe_enabled") => false,
            ("subscriptionMgr", "GetCloneGrade") => (int)context.Account.CloneGrade,
            ("map", "GetSecurityModifiedSystems") => new CompatibilityRowset(["solarSystemID", "security", "modifiedSecurity"], []),
            ("userSvc", "GetRedeemTokens") => BlueEmpty.List,
            ("seasonalLoginCampaignManager", "get_active_campaign") => new SeasonalCampaignState(),
            ("alert", "BeanCount") => new AlertCountReply(),
            ("loginCampaignManager", "get_client_campaign_state") => PilotCallResult.None,
            ("multiLoginBlocker", "Login") => true,
            _ => PilotCallResult.Unsupported,
        };
    }

    private static OwnerTable OwnerRows(object? arguments, IEnumerable<PilotContact> pilots)
    {
        var ids = BlueRead.Tuple(arguments, 1)[0] as List<object?> ?? throw new InvalidDataException("Expected owner IDs.");
        List<OwnerRow> rows = [];
        foreach (var pilot in pilots)
        {
            if (ids.Contains(pilot.PilotId))
            {
                rows.Add(new(pilot.PilotId, pilot.Name, pilot.GenderId));
            }
        }

        return new(rows);
    }
}
