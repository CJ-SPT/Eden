using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Maps;

// Pinned build 3662168 eve/common/lib/appConst.pyj mapHistoryStat*.
internal enum MapHistoryStatistic
{
    Jumps = 1,
    Kills = 3,
    FactionWarfareKills = 5,
}

[Injectable]
internal sealed class MapNavigationHandler : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "map" || method is not ("GetHistory" or "GetAllRoamingWeatherSystems"))
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
        if (
            args is not [long statistic, long hours]
            || statistic is < int.MinValue or > int.MaxValue
            || !Enum.IsDefined((MapHistoryStatistic)statistic)
            || hours is <= 0 or > int.MaxValue
        )
        {
            return new PilotCallResult.Failure("Choose a known map statistic and a positive history duration.");
        }
        return new MapHistoryReply();
    }
}

internal sealed record MapHistoryReply : BlueModel
{
    // This immutable empty baseline is shared by all statistics and pilots. Activity
    // telemetry is not collected yet. A wall-clock version would make the route planner
    // rebuild its pod-kill avoidance list on every read (UpdatePodKillList).
    private const long EmptyHistoryVersion = 1;

    public override object ToBlue() =>
        new BlueInstance(
            "carbon.common.script.net.objectCaching.CachedMethodCallResult",
            new object?[]
            {
                new Dictionary<string, object?> { ["versionCheck"] = "run" },
                BlueMarshal.Encode(new MapHistoryRows()),
                new object?[] { EmptyHistoryVersion, 0L },
            }
        );
}

internal sealed record MapHistoryRows : BlueModel
{
    public override object ToBlue() => new CompatibilityRowset(["solarSystemID", "value1", "value2", "value3"], []).ToBlue();
}

internal sealed record MapRoamingWeatherReply : BlueModel
{
    // MapSvc and both map color handlers iterate locationID/sceneType rows.
    public override object ToBlue() => new CompatibilityRowset(["locationID", "sceneType"], []).ToBlue();
}
