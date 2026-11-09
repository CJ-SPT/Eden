using Eden.Gameplay.Capsuleers;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerScheduler
{
    private readonly Dictionary<long, long> _inboundLanes = [];
    private readonly HashSet<(long Gate, bool BothSystemsObserved)> _deferredInbound = [];
    private readonly HashSet<long> _lanePilots = [];

    private CapsuleerJob RestoreInbound(CapsuleerJob job, HaulerProgress work, long elapsed)
    {
        long due = Math.Max(elapsed + TimeSpan.TicksPerSecond, work.DueTicks);
        var paused = work with
        {
            ActiveTicks = elapsed,
            Stamp = CapsuleerScheduleRules.Stamp(elapsed, work.ClockOffsetTicks),
            DueTicks = due,
            InboundPaused = true,
        };
        var next = job.Next(due, CapsuleerAction.GateTransfer);
        using CapsuleerOperation operation = new(database, job, marketSignal);
        operation.Commit(next, null, new HaulerCheckpoint(job.PilotId, paused));
        return next;
    }

    private HashSet<long> ReconcileInbound(HumanActivitySnapshot activity)
    {
        Dictionary<long, WatchedTrafficGate> watches = [];
        foreach (var gate in gateWatch.Read(activity))
        {
            long key = Math.Min(gate.ArrivalGateId, gate.DepartureGateId);
            if (!_inboundLanes.TryGetValue(key, out long pilot))
            {
                bool bothObserved = activity.ActiveSystems.Contains(gate.SourceSystemId);
                if (_deferredInbound.Contains((key, bothObserved)))
                {
                    continue;
                }
                var prepared = new CapsuleerProvisioner(database, clock).EnsureInbound(gate, bothObserved);
                if (prepared is not long created)
                {
                    _deferredInbound.Add((key, bothObserved));
                    continue;
                }
                pilot = created;
                if (!_haulers.ContainsKey(pilot))
                {
                    Load(preparedPilot: pilot);
                }
                _inboundLanes[key] = pilot;
            }
            _lanePilots.Add(pilot);
            watches.TryAdd(pilot, gate);
        }
        HashSet<long> inbound = [];
        var relevant = watches
            .Keys.Concat(_running.Where(_lanePilots.Contains))
            .Concat(
                activity
                    .ActiveSystems.SelectMany(system => _systemAgents.GetValueOrDefault(system) ?? [])
                    .Where(pilot => _lanePilots.Contains(pilot) && activity.ActiveSystems.Contains(_locations[pilot]))
            )
            .ToHashSet();
        foreach (long pilot in relevant)
        {
            var hauler = _haulers[pilot];
            int system = hauler.SystemId;
            if (activity.ActiveSystems.Contains(system))
            {
                Current[pilot] = hauler.ReleaseInbound(Current[pilot], activity.ElapsedTicks);
            }
            else if (watches.TryGetValue(pilot, out var gate) && system == gate.SourceSystemId)
            {
                Current[pilot] = hauler.ArmInbound(Current[pilot], activity.ElapsedTicks, gate);
                IndexLocation(pilot, hauler.SystemId);
                if (hauler.IsInbound)
                {
                    inbound.Add(pilot);
                }
            }
            else
            {
                Current[pilot] = hauler.PauseInbound(Current[pilot], activity.ElapsedTicks);
            }
        }
        return inbound;
    }
}
