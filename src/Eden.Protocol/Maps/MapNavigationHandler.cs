using Eden.Gameplay.Maps;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Maps;

// Pinned build 3662168 eve/common/lib/appConst.pyj mapHistoryStat*.
internal enum MapHistoryStatId
{
    Jumps = 1,
    Kills = 3,
    FactionWarfareKills = 5,
}

[Injectable]
internal sealed class MapNavigationHandler(IMapStatistics statistics) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service == "machoNet" && method == "GetClusterGameStatisticsForClient")
        {
            return
                keywords is not { Count: > 0 } && args is [string or byte[], object?[] { Length: 3 }] && BlueMarshal.Text(args[0]) == "EVE"
                ? new MapPopulationReply(statistics.Population())
                : new PilotCallResult.Failure("Choose the EVE cluster statistics.");
        }
        if (service != "map" || method is not ("GetHistory" or "GetAllRoamingWeatherSystems" or "GetSolarSystemVisits"))
        {
            return PilotCallResult.Unsupported;
        }
        if (keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Unexpected map information options.");
        }
        if (method == "GetAllRoamingWeatherSystems")
        {
            return args.Length == 0
                ? new MapRoamingWeatherReply()
                : new PilotCallResult.Failure("Roaming weather information does not accept arguments.");
        }
        if (method == "GetSolarSystemVisits")
        {
            return args.Length == 0
                ? new MapSystemVisitsReply(statistics.SystemVisits(context.Pilot.PilotId))
                : new PilotCallResult.Failure("Solar system visits do not accept arguments.");
        }
        if (
            args is not [long statistic, long hours]
            || statistic is < int.MinValue or > int.MaxValue
            || !Enum.IsDefined((MapHistoryStatId)statistic)
            || hours is <= 0 or > int.MaxValue
        )
        {
            return new PilotCallResult.Failure("Choose a known map statistic and a positive history duration.");
        }
        var id = (MapHistoryStatId)statistic;
        var snapshot = id switch
        {
            MapHistoryStatId.Jumps => statistics.History(MapHistoryStatistic.Jumps, (int)hours),
            MapHistoryStatId.Kills => statistics.History(MapHistoryStatistic.Kills, (int)hours),
            _ => MapHistorySnapshot.Empty,
        };
        return new MapHistoryReply(id, snapshot);
    }
}

internal sealed record MapHistoryReply(MapHistoryStatId Statistic, MapHistorySnapshot Snapshot) : BlueModel
{
    public override object ToBlue()
    {
        byte[] payload = BlueMarshal.Encode(
            new CompatibilityRowset(
                ["solarSystemID", "value1", "value2", "value3"],
                Snapshot.Rows.Select(row => new MapHistoryWireRow(Statistic, row)).ToArray()
            )
        );
        return new BlueInstance(
            "carbon.common.script.net.objectCaching.CachedMethodCallResult",
            new object?[]
            {
                new Dictionary<string, object?> { ["versionCheck"] = "1 minute" },
                payload,
                new object?[] { Snapshot.UpdatedAt, CachedMethodPayload.Checksum(payload) },
            }
        );
    }
}

internal sealed record MapHistoryWireRow(MapHistoryStatId Statistic, MapHistoryRow Row) : BlueModel
{
    // Both ship-loss filters subtract NPC losses (value2) from the combined ship count (value1).
    public override object ToBlue() =>
        Statistic == MapHistoryStatId.Jumps
            ? new object?[] { Row.SystemId, Row.Counts.Jumps, 0, 0 }
            : new object?[] { Row.SystemId, Row.Counts.Ships + Row.Counts.NpcShips, Row.Counts.NpcShips, Row.Counts.Capsules };
}

internal sealed record MapPopulationReply(MapPilotPopulation Population) : BlueModel
{
    private const int SystemIdBase = 30_000_000;

    public override object ToBlue()
    {
        Dictionary<long, object?> totals = [];
        Dictionary<long, object?> docked = [];
        foreach (var (system, counts) in Population.Systems.OrderBy(pair => pair.Key))
        {
            long key = system - SystemIdBase;
            totals.Add(key, counts.InSpace + counts.Docked);
            if (counts.Docked > 0)
            {
                docked.Add(key, counts.Docked);
            }
        }
        return new object?[] { totals, docked, Population.Samples };
    }
}

internal sealed record MapRoamingWeatherReply : BlueModel
{
    // MapSvc and both map color handlers iterate locationID/sceneType rows.
    public override object ToBlue() => new CompatibilityRowset(["locationID", "sceneType"], []).ToBlue();
}

internal sealed record MapSystemVisitsReply(IReadOnlyList<MapSystemVisit> Visits) : BlueModel
{
    // The pinned legacy and current map filters read these attributes from each visit.
    public override object ToBlue() =>
        new CompatibilityRowset(
            ["lastDateTime", "solarSystemID", "visits"],
            Visits.Select(visit => new MapSystemVisitRow(visit)).ToArray()
        ).ToBlue();
}

internal sealed record MapSystemVisitRow(MapSystemVisit Visit) : BlueModel
{
    public override object ToBlue() => new object?[] { Visit.LastVisitedAt, Visit.SystemId, Visit.Visits };
}
