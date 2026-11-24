using Eden.Gameplay.Exploration;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Exploration;

[Injectable]
internal sealed class IncursionHandler(IIncursionDirectory directory, TimeProvider clock) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if ((service, method) is ("rewardMgr", "GetDelayedRewardsByGroupIDs"))
        {
            if (keywords is { Count: > 0 } || args is not [object?[] groupIds] || groupIds.Any(id => id is not (long and > 0)))
            {
                return new PilotCallResult.Failure("Supply a tuple of positive incursion reward group IDs.");
            }
            if (groupIds.Length == 0)
            {
                return BlueEmpty.IndexedDictionary;
            }
            var loyalty = directory.PendingLoyalty(context.Pilot.PilotId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
            return loyalty is { Amount: > 0 } reward && groupIds.Contains(reward.TaleId)
                ? new IncursionDelayedReward(reward)
                : BlueEmpty.IndexedDictionary;
        }
        if ((service, method) is not ("map", "GetIncursionGlobalReport" or "GetSystemsInIncursions"))
        {
            return PilotCallResult.Unsupported;
        }
        if (args.Length != 0 || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Incursion directory requests do not accept arguments.");
        }
        var focuses = directory.Read(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
        return method == "GetIncursionGlobalReport"
            ? new BlueList<IncursionReport>(focuses.Select(focus => new IncursionReport(focus)).ToArray())
            : new IncursionSystemList(focuses.SelectMany(focus => focus.Systems).ToArray());
    }
}
