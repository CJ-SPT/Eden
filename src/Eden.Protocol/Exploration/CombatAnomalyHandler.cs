using Eden.Gameplay.Exploration;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Exploration;

[Injectable(InjectionType.Scoped)]
internal sealed class CombatAnomalyHandler(ICombatAnomalies sites, TimeProvider clock) : ISelectedPilotCallHandler
{
    private int? _system;
    private IReadOnlyList<CombatAnomaly> _known = [];
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service == "dungeonInstanceCacheMgr" && args.Length == 0)
        {
            _ = sites.List(context.Pilot.SystemId, Now);
            if (method == "GetCombatAnomalyInstances")
            {
                return new AnomalyDirectory(sites.Directory(Now));
            }
            if (method == "GetCombatAnomaliesCount")
            {
                return new AnomalyCounts(sites.Directory(Now));
            }
        }
        if (service == "scanMgr" && method == "SignalTrackerRegister" && args.Length == 0)
        {
            _system = context.Pilot.SystemId;
            _known = sites.List(_system.Value, Now);
            context.Notifications.Add(new SignalTrackerFullState(_system.Value, _known));
            return PilotCallResult.None;
        }
        if (service == "scanMgr" && method == "GetSystemScanMgr" && args.Length == 0)
        {
            return context.Reference("@systemScan", PilotBindingLifetime.Location);
        }
        if (service == "@systemScan" && method == "GetFullState" && args.Length == 0)
        {
            return new SignalTrackerState(sites.List(context.Pilot.SystemId, Now));
        }
        return PilotCallResult.Unsupported;
    }

    internal void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            return;
        }
        int system = context.Pilot.SystemId;
        if (context.Flight.CanTarget && context.Pilot.Position is { } arrival)
        {
            sites.Arrive(system, arrival, Now);
        }
        var current = sites.List(system, Now);
        var position = context.Pilot.Position;
        context.Flight.RefreshAnomalies(
            current
                .SelectMany(s => s.Objects)
                .Where(o =>
                    position is not null && position.DistanceTo(o.Position) <= Eden.Gameplay.Combat.NpcEncounterDefinitions.VisibilityRange
                )
                .ToArray()
        );
        if (_system != system)
        {
            _known = [];
            _system = null;
            return;
        }
        var added = current.ExceptBy(_known.Select(s => s.Id), s => s.Id).ToArray();
        var removed = _known.ExceptBy(current.Select(s => s.Id), s => s.Id).Select(s => s.Id).ToArray();
        if (added.Length > 0 || removed.Length > 0)
        {
            context.Notifications.Add(new SignalTrackerAnomalyUpdate(system, added, removed));
        }
        _known = current;
    }
}
