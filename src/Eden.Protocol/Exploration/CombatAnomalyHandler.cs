using Eden.Gameplay.Exploration;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Exploration;

[Injectable(InjectionType.Scoped)]
internal sealed class CombatAnomalyHandler(
    ICombatAnomalies sites,
    Eden.Gameplay.Agents.IPilotMissions missions,
    TimeProvider clock,
    ProbeScanningHandler? probes = null
) : ISelectedPilotCallHandler
{
    private int? _system;
    private IReadOnlyList<CombatAnomaly> _known = [];
    private IReadOnlyList<CombatAnomaly> _signatures = [];
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service == "dungeonInstanceCacheMgr" && method is "GetCombatAnomalyInstances" or "GetCombatAnomaliesCount")
        {
            if (args.Length != 0 || keywords is { Count: > 0 })
            {
                return new PilotCallResult.Failure("Combat anomaly directory requests do not accept arguments.");
            }
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
            _signatures = sites.Signatures(_system.Value, Now);
            context.Notifications.Add(new SignalTrackerFullState(_system.Value, _known, _signatures));
            probes?.Enter(context);
            return PilotCallResult.None;
        }
        if (service == "scanMgr" && method == "GetSystemScanMgr" && args.Length == 0)
        {
            return context.Reference("@systemScan", PilotBindingLifetime.Location);
        }
        if (service == "@systemScan" && method == "GetFullState" && args.Length == 0)
        {
            probes?.Enter(context);
            return new SignalTrackerState(sites.List(context.Pilot.SystemId, Now), sites.Signatures(context.Pilot.SystemId, Now));
        }
        return PilotCallResult.Unsupported;
    }

    internal void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            return;
        }
        probes?.Advance(context);
        int system = context.Pilot.SystemId;
        if (context.Flight.CanTarget && context.Pilot.Position is { } arrival)
        {
            sites.Arrive(system, arrival, Now);
            missions.Arrive(system, arrival);
        }
        var current = sites.List(system, Now);
        var signatures = sites.Signatures(system, Now);
        var position = context.Pilot.Position;
        context.Flight.RefreshAnomalies(
            current
                .Concat(signatures)
                .SelectMany(s => s.Objects)
                .Concat(missions.Sites(system).SelectMany(s => s.Objects))
                .Where(o =>
                    position is not null && position.DistanceTo(o.Position) <= Eden.Gameplay.Combat.NpcEncounterDefinitions.VisibilityRange
                )
                .ToArray()
        );
        if (_system != system)
        {
            _known = [];
            _signatures = [];
            _system = null;
            return;
        }
        var added = current.ExceptBy(_known.Select(s => s.Id), s => s.Id).ToArray();
        var removed = _known.ExceptBy(current.Select(s => s.Id), s => s.Id).Select(s => s.Id).ToArray();
        if (added.Length > 0 || removed.Length > 0)
        {
            context.Notifications.Add(new SignalTrackerAnomalyUpdate(system, added, removed));
        }
        var signatureAdded = signatures.ExceptBy(_signatures.Select(s => s.Id), s => s.Id).ToArray();
        var signatureRemoved = _signatures.ExceptBy(signatures.Select(s => s.Id), s => s.Id).Select(s => s.Id).ToArray();
        if (signatureAdded.Length > 0 || signatureRemoved.Length > 0)
        {
            context.Notifications.Add(new SignalTrackerSignatureUpdate(system, signatureAdded, signatureRemoved));
        }
        _signatures = signatures;
        _known = current;
    }
}
