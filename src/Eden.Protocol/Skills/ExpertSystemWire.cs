using Eden.Gameplay.Skills;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Skills;

internal sealed record ExpertSystemsReply(IReadOnlyList<InstalledExpertSystem> Systems) : BlueModel
{
    public override object ToBlue() =>
        new BlueMap<int, BlueList<long>>(
            Systems.ToDictionary(system => system.TypeId, system => new BlueList<long>([system.InstalledAt, system.ExpiresAt]))
        ).ToBlue();
}

internal sealed record ExpertSystemsUpdated(ExpertSystemsReply Systems, ExpertSystemEvent Event)
    : PilotNotification("OnExpertSystemsUpdated")
{
    public override object?[] Arguments => [Systems, Event.Added, Event.TypeId];
}
