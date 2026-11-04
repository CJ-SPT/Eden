using Eden.Gameplay.Characters;
using Eden.Gameplay.Market;
using Eden.Protocol.Docking;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;

namespace Eden.Protocol.Characters;

[Injectable]
internal sealed class HomeStationHandler(IPilotHomeStation homes, DeferredRefitPrimes refits, PilotBrain brain) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (
            service != "home_station"
            || method
                is not (
                    "get_home_station"
                    or "get_home_station_candidates"
                    or "get_next_remote_change_time"
                    or "set_home_station"
                    or "self_destruct_clone"
                )
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected home station arguments.");
            }
            long pilot = context.Pilot.PilotId;
            switch (method, args)
            {
                case ("get_home_station", []):
                    var home = homes.Read(pilot);
                    return new HomeStationInfo(home.Id, home.TypeId, home.SystemId, home.IsFallback);
                case ("get_home_station_candidates", []):
                    return new BlueList<HomeStationCandidateReply>(
                        homes.Candidates(pilot).Select(candidate => new HomeStationCandidateReply(candidate)).ToArray()
                    );
                case ("get_next_remote_change_time", []):
                    return homes.NextRemoteChange(pilot) is long time ? time : PilotCallResult.None;
                case ("set_home_station", [long id, var remote]) when id is > 0 and <= int.MaxValue:
                    bool allow = remote switch
                    {
                        bool flag => flag,
                        0L => false,
                        1L => true,
                        _ => throw new InvalidDataException("Choose a valid remote change confirmation."),
                    };
                    var changed = homes.Set(pilot, (int)id, allow);
                    context.Notifications.Add(new HomeStationChanged(changed.Station.Id));
                    context.Notifications.Add(
                        new AccountBalanceChanged(pilot, (double)changed.BalanceCents / MarketDefinitions.CentsPerIsk)
                    );
                    return PilotCallResult.None;
                case ("self_destruct_clone", []):
                    var transfer = homes.SelfDestruct(pilot, context.Pilot.ShipId);
                    refits.Clear();
                    context.ChangeShip(transfer.Pilot);
                    foreach (var change in transfer.Changes)
                    {
                        InventoryChangesWire.NotifyItemChange(context, change);
                    }
                    context.Notifications.Add(new ServerBrainUpdated(brain.Update(pilot, transfer.Pilot.ShipId)));
                    return PilotCallResult.None;
                default:
                    throw new InvalidDataException("Invalid home station request.");
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record HomeStationCandidateReply(HomeStationCandidate Candidate) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["id"] = Candidate.Station.Id,
                ["type_id"] = Candidate.Station.TypeId,
                ["solar_system_id"] = Candidate.Station.SystemId,
                ["is_current_station"] = Candidate.Current,
                ["is_school_hq"] = Candidate.School,
                ["errors"] = Candidate.Errors.Select(error => (object?)(long)error).ToList(),
            }
        );
}

internal sealed record HomeStationChanged(int Id) : PilotNotification("OnHomeStationChanged")
{
    public override object?[] Arguments => [Id];
}

[Injectable]
internal sealed class NpcCorporationHandler(INpcCorporationDirectory corporations) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if ((service, method) is not (("corpRegistry", "GetCorporation") or ("corpmgr", "GetCorporations")))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected corporation arguments.");
            }
            int id = (service, args) switch
            {
                ("corpRegistry", []) => context.Pilot.CorpId,
                ("corpmgr", [long corporation]) when corporation is > 0 and <= int.MaxValue => (int)corporation,
                _ => throw new InvalidDataException("Choose a valid NPC corporation."),
            };
            return new NpcCorporationReply(corporations.Read(id));
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record NpcCorporationReply(NpcCorporationInfo Corporation) : BlueModel
{
    public override object ToBlue() =>
        new BlueConstructor(
            "blue.DBRow",
            [
                BlueRows.Descriptor([
                    ("corporationID", BlueColumnType.Int32),
                    ("corporationName", BlueColumnType.Unicode),
                    ("tickerName", BlueColumnType.String),
                    ("stationID", BlueColumnType.Int32),
                    ("solarSystemID", BlueColumnType.Int32),
                    ("factionID", BlueColumnType.Int32),
                    ("ceoID", BlueColumnType.Int64),
                    ("taxRate", BlueColumnType.Float64),
                    ("shares", BlueColumnType.Int64),
                    ("memberLimit", BlueColumnType.Int64),
                    ("url", BlueColumnType.String),
                ]),
                new object?[]
                {
                    Corporation.Id,
                    new BlueUnicode(Corporation.Name),
                    Corporation.Ticker,
                    Corporation.StationId,
                    Corporation.SystemId,
                    Corporation.FactionId,
                    Corporation.CeoId,
                    Corporation.TaxRate,
                    Corporation.Shares,
                    Corporation.MemberLimit,
                    Corporation.Url,
                },
            ]
        );
}
