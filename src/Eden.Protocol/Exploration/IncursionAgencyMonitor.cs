using Eden.Gameplay.Exploration;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Exploration;

[Injectable(InjectionType.Scoped)]
internal sealed class IncursionAgencyMonitor(IIncursionDirectory directory, TimeProvider clock)
{
    private long? _pilotId;
    private int _systemId;
    private IReadOnlyList<IncursionAgencyUpdate>? _publication;
    private readonly Dictionary<long, IncursionAgencyUpdate> _observed = [];
    private LocalIncursionStatus? _local;

    internal bool IsMonitoring => _pilotId.HasValue;

    internal void Watch(
        PilotCallContext context,
        IReadOnlyList<IncursionFocus> focuses,
        IReadOnlyList<IncursionAgencyUpdate> publicationBeforeRead
    )
    {
        _pilotId = context.Pilot.PilotId;
        // An empty report can precede maintenance's expiry publication. Remember the prior publication without losing races after the read.
        _observed.Clear();
        foreach (var update in publicationBeforeRead)
        {
            _observed.Add(update.TaleId, update);
        }
        foreach (var focus in focuses)
        {
            _observed[focus.TaleId] = new(focus.TaleId, focus.LastUpdated, focus.Influence, focus.HasFinalEncounter, IsActive: true);
        }
        _publication = null;
    }

    internal IReadOnlyList<IncursionAgencyUpdate>? Sample(PilotCallContext context) => context.Selected ? directory.AgencyUpdates : null;

    internal void Publish(PilotCallContext context, IReadOnlyList<IncursionAgencyUpdate>? current)
    {
        if (current is null)
        {
            return;
        }
        int systemId = context.Pilot.SystemId;
        bool publicationChanged = !ReferenceEquals(current, _publication);
        if (!publicationChanged && systemId == _systemId && (_local?.Focus.EndsAt is not long expiry || Now < expiry))
        {
            return;
        }

        long? localInfluence = PublishLocal(context, FindLocal(current, systemId, Now));
        _systemId = systemId;
        _publication = current;
        if (publicationChanged && _pilotId == context.Pilot.PilotId)
        {
            PublishAgency(context, current, localInfluence);
        }
    }

    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    private static LocalIncursionStatus? FindLocal(IReadOnlyList<IncursionAgencyUpdate> current, int systemId, long now)
    {
        foreach (var focus in current)
        {
            if (!focus.IsActive || focus.EndsAt <= now || focus.Systems is not { } systems)
            {
                continue;
            }
            foreach (var system in systems)
            {
                if (system.SystemId == systemId)
                {
                    return new(focus, system);
                }
            }
        }
        return null;
    }

    private long? PublishLocal(PilotCallContext context, LocalIncursionStatus? status)
    {
        var previous = _local;
        _local = status;
        if (
            previous is not null
            && (previous.Focus.TaleId != status?.Focus.TaleId || status is not null && EffectsChanged(previous.System, status.System))
        )
        {
            context.Notifications.Add(new IncursionTaleRemoved(previous.Focus.TaleId));
        }
        if (status is null)
        {
            return null;
        }
        if (
            previous?.Focus.TaleId != status.Focus.TaleId
            || previous.System != status.System
            || previous.Focus.HasFinalEncounter != status.Focus.HasFinalEncounter
        )
        {
            context.Notifications.Add(new IncursionTaleData(status));
        }
        else if (previous.Focus.Influence != status.Focus.Influence)
        {
            context.Notifications.Add(new IncursionInfluenceChanged(status.Focus));
            return status.Focus.TaleId;
        }
        return null;
    }

    // The stock HUD creates effect icons when the panel is opened. Reopen it when a scene has a different icon set.
    private static bool EffectsChanged(IncursionSystem previous, IncursionSystem current) =>
        (previous.SceneType == IncursionScene.Staging) != (current.SceneType == IncursionScene.Staging);

    private void PublishAgency(PilotCallContext context, IReadOnlyList<IncursionAgencyUpdate> current, long? localInfluence)
    {
        foreach (var update in current)
        {
            if (
                _observed.TryGetValue(update.TaleId, out var previous)
                && (update.LastUpdated < previous.LastUpdated || SamePublication(update, previous))
            )
            {
                continue;
            }
            _observed[update.TaleId] = update;
            if (update.TaleId != localInfluence)
            {
                // The Agency refreshes its directory on the influence notification, even when a focus has expired.
                context.Notifications.Add(new IncursionInfluenceChanged(update));
            }
        }
        foreach (long taleId in _observed.Keys.Except(current.Select(update => update.TaleId)).ToArray())
        {
            _observed.Remove(taleId);
        }
    }

    private static bool SamePublication(IncursionAgencyUpdate current, IncursionAgencyUpdate previous) =>
        current.LastUpdated == previous.LastUpdated
        && current.Influence == previous.Influence
        && current.IsActive == previous.IsActive
        && current.HasFinalEncounter == previous.HasFinalEncounter;

    internal void Reset()
    {
        _pilotId = null;
        _publication = null;
        _observed.Clear();
        _local = null;
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
