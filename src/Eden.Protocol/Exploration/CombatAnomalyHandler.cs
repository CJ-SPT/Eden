using Eden.Gameplay.Agents;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
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
    private long _ghostSite;
    private int _ghostDungeon;
    private long? _ghostCountdown;
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
            sites.ArriveUncloaked(system, arrival, Now);
            missions.Arrive(system, arrival);
        }
        var current = sites.List(system, Now);
        var signatures = sites.Signatures(system, Now);
        var position = context.Pilot.Position;
        TrackGhostSite(context, position, signatures);
        context.Flight.RefreshAnomalies(
            current
                .Concat(signatures)
                .SelectMany(s => s.Objects)
                .Concat(missions.Sites(system).Where(s => !s.Private || s.PilotId == context.Pilot.PilotId).SelectMany(s => s.Objects))
                .Where(o =>
                    position is not null
                    && position.DistanceTo(o.Position)
                        <= (
                            MissionMiningRules.IsDeposit(o.Id)
                                ? AsteroidDefinitions.VisibilityRange
                                : Eden.Gameplay.Combat.NpcEncounterDefinitions.VisibilityRange
                        )
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

    private void TrackGhostSite(PilotCallContext context, SpacePosition? position, IReadOnlyList<CombatAnomaly> signatures)
    {
        var site =
            position is null || !context.Flight.InBallpark || context.Flight.InWarp
                ? null
                : signatures.FirstOrDefault(s =>
                    s.Exploration == ExplorationKind.Ghost
                    && s.Position.DistanceTo(position) <= Eden.Gameplay.Combat.NpcEncounterDefinitions.VisibilityRange
                );
        if (site?.Id != _ghostSite && _ghostSite != 0)
        {
            context.Notifications.Add(new ExitingExplorationDungeon(_ghostDungeon));
            _ghostSite = 0;
            _ghostCountdown = null;
        }
        if (site is null)
        {
            return;
        }
        if (site.Id != _ghostSite)
        {
            context.Notifications.Add(new EnteringGhostDungeon(site));
            _ghostSite = site.Id;
            _ghostDungeon = site.DungeonId;
        }
        else if (site.GhostCountdownEnd != _ghostCountdown)
        {
            context.Notifications.Add(new GhostDungeonValues(site.DungeonId, site.GhostCountdownEnd));
        }
        _ghostCountdown = site.GhostCountdownEnd;
    }
}
