using Eden.Gameplay.Alliances;
using Eden.Gameplay.Sovereignty;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Sovereignty;

[Injectable(InjectionType.Scoped)]
internal sealed class SovereigntyMonitor(IPilotEntosis entosis, IAlliances alliances)
{
    private int _system;
    private SovereigntyHub? _local;
    private long _revision = -1;
    private int? _alliance;
    private IReadOnlyDictionary<int, SovereigntyHub> _owned = new Dictionary<int, SovereigntyHub>();

    private static bool Changed(SovereigntyHub? before, SovereigntyHub? after) =>
        before?.HubId != after?.HubId
        || before?.Claim != after?.Claim
        || before?.Conflict?.Phase != after?.Conflict?.Phase
        || before?.Conflict?.Window != after?.Conflict?.Window
        || before?.Conflict?.CampaignAt != after?.Conflict?.CampaignAt
        || before?.Conflict?.DefenderPoints != after?.Conflict?.DefenderPoints
        || before?.Conflict?.Capture.Value < 1 != after?.Conflict?.Capture.Value < 1;

    private static void Publish(PilotCallContext context, int system, SovereigntyHub? hub)
    {
        context.Notifications.Add(new SovereigntyStructuresUpdated(system, hub));
        context.Notifications.Add(new SovereigntyStatusChanged(system, hub?.Claim));
    }

    internal void Advance(PilotCallContext context)
    {
        if (!context.Selected || !entosis.Available)
        {
            return;
        }
        int system = context.Pilot.SystemId;
        var current = entosis.Hub(system);
        bool localChanged = _system == system && Changed(_local, current);
        _system = system;
        _local = current;
        if (localChanged)
        {
            Publish(context, system, current);
        }
        int? alliance = alliances.Affiliation(context.Pilot.CorpId);
        if (_revision == entosis.ViewRevision && _alliance == alliance)
        {
            return;
        }
        var owned = alliance is null
            ? new Dictionary<int, SovereigntyHub>()
            : entosis.AllianceHubs(context.Pilot.PilotId).ToDictionary(hub => hub.SystemId);
        if (_alliance == alliance && _revision >= 0)
        {
            foreach (int id in _owned.Keys.Union(owned.Keys))
            {
                if (id != system && Changed(_owned.GetValueOrDefault(id), owned.GetValueOrDefault(id)))
                {
                    Publish(context, id, owned.GetValueOrDefault(id) ?? entosis.Hub(id));
                }
            }
        }
        _owned = owned;
        _alliance = alliance;
        _revision = entosis.ViewRevision;
    }
}
