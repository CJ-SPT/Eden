using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.PilotSessions;

[Injectable]
internal sealed class AccountCompatibilityHandler(TimeProvider clock, SharedStationGuests guests, ICharacterDirectory directory)
    : IPilotCallHandler
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
            // Build 3662168 ClientStatsSvc.OnProcessExit also sends this before pilot selection.
            // It records exit intent; the transport closing owns disconnect cleanup.
            ("eventLog", "LogPlayerRequestedDisconnect") when arguments is object?[] { Length: 0 } && keywords is null or { Count: 0 } =>
                PilotCallResult.None,
            ("config", "GetMultiOwnersEx") => OwnerRows(arguments),
            ("config", "GetMultiCorpTickerNamesEx")
                when arguments is object?[] { Length: 1 } corpArgs && corpArgs[0] is List<object?> ids => new CorporationTickerTable(
                KnownPilots(context)
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

    private IEnumerable<PilotContact> KnownPilots(PilotCallContext context) =>
        context.ActiveFlight is { } flight
            ? flight.VisiblePilots().Concat(guests.List(flight.Pilot)).DistinctBy(pilot => pilot.PilotId)
            : context.Roster.Select(PilotContact.From);

    private OwnerTable OwnerRows(object? arguments)
    {
        var ids = BlueRead.Tuple(arguments, 1)[0] as List<object?> ?? throw new InvalidDataException("Expected owner IDs.");
        List<OwnerRow> rows = [];
        foreach (var pilot in directory.Read(ids.OfType<long>().ToArray()))
        {
            rows.Add(new(pilot.Id, pilot.Name, pilot.Gender));
        }

        return new(rows);
    }
}
