using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.PilotSessions;

[Injectable]
internal sealed class AccountCompatibilityHandler(
    TimeProvider clock,
    SharedStationGuests guests,
    ICharacterDirectory directory,
    INpcCorporationDirectory corporations,
    IPlayerCorporations players
) : IPilotCallHandler
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
            ("config", "GetMultiCorpTickerNamesEx") => CorporationRows(context, arguments),
            ("config", "GetBlackListedPlanets" or "GetOldStationData") => BlueEmpty.List,
            ("air_npe", "is_air_npe_enabled") => false,
            ("subscriptionMgr", "GetCloneGrade") => (int)context.Account.CloneGrade,
            ("map", "GetSecurityModifiedSystems") => new CompatibilityRowset(["solarSystemID", "security", "modifiedSecurity"], []),
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

    private CorporationTickerTable CorporationRows(PilotCallContext context, object? arguments)
    {
        var values = BlueRead.Tuple(arguments, 1)[0] as List<object?> ?? throw new InvalidDataException("Expected corporation IDs.");
        var ids = values.OfType<long>().Where(id => id is > 0 and <= int.MaxValue).Select(id => (int)id).Distinct().ToArray();
        // Pilot contacts carry only a ticker. Read saved corporations first so
        // roster/visible-pilot shortcuts cannot discard their logo layers.
        var known = ids.Select(players.Read)
            .OfType<PlayerCorporation>()
            .Select(c => new CorporationTickerRow(c.Id, c.Details.Ticker, c.Details.Logo))
            .ToArray();
        known = known
            .Concat(
                KnownPilots(context)
                    .Where(p => ids.Contains(p.CorpId) && !known.Any(c => c.CorporationId == p.CorpId))
                    .DistinctBy(p => p.CorpId)
                    .Select(p => new CorporationTickerRow(p.CorpId, p.CorporationTicker))
            )
            .ToArray();
        // Michelle primes every NPC corp before dispatching newly added balls.
        // The lookup must also work before selection and without visible pilots.
        return new(
            known
                .Concat(
                    corporations
                        .Read(ids.Except(known.Select(c => c.CorporationId)).ToArray())
                        .Select(c => new CorporationTickerRow(c.Id, c.Ticker))
                )
                .OrderBy(c => c.CorporationId)
                .ToArray()
        );
    }

    private OwnerTable OwnerRows(object? arguments)
    {
        var ids = BlueRead.Tuple(arguments, 1)[0] as List<object?> ?? throw new InvalidDataException("Expected owner IDs.");
        List<OwnerRow> rows = [];
        foreach (var pilot in directory.Read(ids.OfType<long>().ToArray()))
        {
            rows.Add(new(pilot.Id, pilot.Name, pilot.Gender, pilot.NameId, pilot.IsNpc));
        }

        foreach (long id in ids.OfType<long>().Where(id => id is > 0 and <= int.MaxValue).Distinct())
        {
            if (players.Read((int)id) is { } corporation)
            {
                rows.Add(new(id, corporation.Details.Name, 0, TypeId: CorporationRules.CorporationType));
            }
        }
        return new(rows);
    }
}
