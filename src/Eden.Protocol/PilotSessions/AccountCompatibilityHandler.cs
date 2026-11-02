using Eden.Protocol.Docking;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.PilotSessions;

[Injectable]
internal sealed class AccountCompatibilityHandler(TimeProvider clock) : IPilotCallHandler
{
    public object? Call(PilotCallContext context, string service, string method, object? arguments)
    {
        return (service, method) switch
        {
            ("machoNet", "GetServiceInfo") => new Dictionary<string, object?>(),
            ("machoNet", "GetTime") => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(),
            ("config", "GetMultiOwnersEx") => OwnerRows(arguments, context.Flight.VisiblePilots()),
            ("config", "GetMultiCorpTickerNamesEx") => new object?[] { new object?[] { "corporationID", "tickerName" }, new List<object?> { new object?[] { 1000044, "SWA" } } },
            ("config", "GetBlackListedPlanets" or "GetOldStationData") => new List<object?>(),
            ("config", "GetAverageMarketPrices") => new Dictionary<long, object?>(),
            ("air_npe", "is_air_npe_enabled") => false,
            ("subscriptionMgr", "GetCloneGrade") => 0,
            ("map", "GetSecurityModifiedSystems") => new BlueInstance("eve.common.script.sys.rowset.Rowset",
                new Dictionary<string, object?> { ["header"] = new List<object?> { "solarSystemID", "security", "modifiedSecurity" }, ["lines"] = new List<object?>() }),
            ("userSvc", "GetRedeemTokens") => new List<object?>(),
            ("seasonalLoginCampaignManager", "get_active_campaign") => new object?[] { null, null, null, 0 },
            ("alert", "BeanCount") => new object?[] { 1, 0 },
            ("loginCampaignManager", "get_client_campaign_state") => null,
            ("multiLoginBlocker", "Login") => true,
            _ => PilotCallRouter.Unsupported,
        };
    }

    private static object?[] OwnerRows(object? arguments, IEnumerable<DockedPilot> pilots)
    {
        var ids = PilotLogin.Tuple(arguments, 1)[0] as List<object?> ?? throw new InvalidDataException("Expected owner IDs.");
        List<object?> rows = [];
        foreach (var pilot in pilots)
        {
            if (ids.Contains(pilot.PilotId))
            {
                rows.Add(new object?[] { pilot.PilotId, pilot.Name, 1373, 1, null });
            }
        }

        return [new object?[] { "ownerID", "ownerName", "typeID", "gender", "ownerNameID" }, rows];
    }
}
