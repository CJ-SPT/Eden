using Eden.Gameplay.Exploration;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Exploration;

[Injectable(InjectionType.Scoped)]
internal sealed class IncursionAgencyMonitor(IIncursionDirectory directory)
{
    private long? _pilotId;
    private IncursionAgencyUpdate? _observed;

    internal bool IsMonitoring => _pilotId.HasValue;

    internal void Watch(PilotCallContext context, IncursionFocus? focus, IncursionAgencyUpdate? publicationBeforeRead)
    {
        _pilotId = context.Pilot.PilotId;
        // An empty report can precede maintenance's expiry publication. Remember the prior publication without losing races after the read.
        _observed = focus is null
            ? publicationBeforeRead
            : new(focus.TaleId, focus.LastUpdated, focus.Influence, focus.HasFinalEncounter, IsActive: true);
    }

    internal IncursionAgencyUpdate? Sample(PilotCallContext context) =>
        context.Selected && _pilotId == context.Pilot.PilotId ? directory.AgencyUpdate : null;

    internal void Publish(PilotCallContext context, IncursionAgencyUpdate? current)
    {
        if (current is null || current == _observed)
        {
            return;
        }
        if (
            _observed is { } previous
            && (current.TaleId < previous.TaleId || current.TaleId == previous.TaleId && current.LastUpdated < previous.LastUpdated)
        )
        {
            return;
        }

        _observed = current;
        // The Agency invalidates the whole directory on this event, including a retired or replaced focus.
        context.Notifications.Add(new IncursionInfluenceChanged(current));
    }

    internal void Reset()
    {
        _pilotId = null;
        _observed = null;
    }
}

internal sealed record IncursionInfluenceChanged(IncursionAgencyUpdate Update) : PilotNotification("OnInfluenceUpdate")
{
    public override object?[] Arguments => [Update.TaleId, new IncursionInfluenceData(Update)];
}

internal sealed record IncursionInfluenceData(IncursionAgencyUpdate Update) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "util.KeyVal",
            new Dictionary<string, object?>
            {
                ["influence"] = 1 - Update.Influence,
                ["lastUpdated"] = Update.LastUpdated,
                ["graceTime"] = 0,
                ["decayRate"] = 0d,
            }
        );
}
