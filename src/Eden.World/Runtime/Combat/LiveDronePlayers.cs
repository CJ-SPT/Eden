using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Combat;

internal sealed record DronePilotTimers(long PilotId, int SystemId, CrimewatchTimers Timers);

internal sealed record DronePilotEngagement(long LowerPilotId, long HigherPilotId, long ActiveUntil, long ExpiresAt);

internal sealed record DronePilotHostility(long SourceShipId, long TargetShipId, long LastAttackAt, long ExpiresAt);

internal sealed partial class LiveCombatRuntime
{
    private sealed record DroneLogoff(long ShipId, int SystemId, long ExpiresAt);

    private sealed record DronePlayer(
        long PilotId,
        long ShipId,
        int SystemId,
        bool Capsule,
        bool Alpha,
        ShipSafetyLevel Safety,
        bool Outlaw,
        DroneLogoff? Logoff,
        IReadOnlyList<LimitedEngagement> Engagements,
        CrimewatchTimers Timers
    );

    private readonly ResidentMap<long, DronePlayer?> _dronePlayers = [];
    private readonly ResidentMap<long, DronePilotTimers> _droneTimers = [];
    private readonly ResidentMap<(long Lower, long Higher), DronePilotEngagement> _droneEngagements = [];
    private readonly ResidentMap<(long Source, long Target), DronePilotHostility> _droneHostilities = [];
    private readonly ResidentMap<long, DronePilotHostility[]> _droneAttackers = [];
    private readonly ResidentMap<int, double> _droneSecurity = [];

    private DronePlayer? DronePlayerFor(long shipId)
    {
        if (!_dronePlayers.TryGetValue(shipId, out var value))
        {
            value = Loading(() =>
            {
                return owner.Economy.Inspect(world => ReadDronePlayer(world, shipId));
            });
            _dronePlayers[shipId] = value;
        }
        return value;
    }

    private CrimewatchTimers DroneTimers(DronePlayer pilot) => _droneTimers.GetValueOrDefault(pilot.PilotId)?.Timers ?? pilot.Timers;

    private AggressionDecision? DroneLegality(DronePlayer source, DronePlayer victim, int system, long now)
    {
        if (!_droneSecurity.TryGetValue(system, out double security))
        {
            security = Loading(() =>
            {
                using CatalogDbContext catalog = new(database.CatalogPath);
                return CrimewatchPersistence.Security(catalog, system);
            });
            _droneSecurity[system] = security;
        }
        var key = (Math.Min(source.PilotId, victim.PilotId), Math.Max(source.PilotId, victim.PilotId));
        long engagement =
            _droneEngagements.GetValueOrDefault(key)?.ExpiresAt
            ?? source.Engagements.FirstOrDefault(e => e.OtherPilotId == victim.PilotId)?.ExpiresAt
            ?? 0;
        try
        {
            return CrimewatchRules.Decide(
                new(security, source.Safety, source.Alpha, victim.Capsule, DroneTimers(victim).Flag(now), victim.Outlaw, engagement > now)
            );
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private PilotCombatPresence? DronePresence(DronePlayer victim, IPilotCombatPresence presence, int system, long now)
    {
        if (victim.SystemId != system)
        {
            return null;
        }
        var ship = Ship(victim.PilotId, now);
        if (ship.SystemId != system || ship.Position is null)
        {
            return null;
        }
        long logoff = victim.Logoff is null
            ? 0
            : Math.Max(victim.Logoff.ExpiresAt, _droneTimers.GetValueOrDefault(victim.PilotId)?.Timers.Pvp.ExpiresAt ?? 0);
        if (logoff != 0 && logoff <= now)
        {
            return null;
        }
        if (presence.FindOnline(system, victim.ShipId) is { } online)
        {
            return online;
        }
        return logoff > now && victim.Logoff!.ShipId == victim.ShipId && victim.Logoff.SystemId == system && ship.Position is { } position
            ? new(victim.ShipId, position, SpacePosition.Zero)
            : null;
    }

    private IEnumerable<long> DroneAutomaticTargets(
        LiveCombatShip ship,
        IPilotCombatPresence presence,
        long commanded,
        double range,
        long now
    )
    {
        foreach (
            var attack in PlayerAttacks(ship.ShipId, now)
                .Where(h => h.LastAttackAt > commanded && h.ExpiresAt > now)
                .DistinctBy(h => h.SourceShipId)
        )
        {
            if (
                DronePlayerFor(attack.SourceShipId) is { } victim
                && DronePlayerFor(ship.ShipId) is { } source
                && DronePresence(victim, presence, ship.SystemId, now) is { } contact
                && ship.Position!.DistanceTo(contact.Position) <= range
                && DroneLegality(source, victim, ship.SystemId, now) is not null
            )
            {
                yield return attack.SourceShipId;
            }
        }
    }

    private readonly ResidentMap<long, ResidentMap<long, DronePilotHostility>> _residentAttackers = [];

    private IEnumerable<DronePilotHostility> PlayerAttacks(long shipId, long now)
    {
        if (!_droneAttackers.TryGetValue(shipId, out var attackers))
        {
            attackers = Loading(() =>
                WorldActionScope.Inspect(
                    database,
                    world =>
                        world
                            .Filter<SavedPilotHostility>(h => h.TargetShipId == shipId && h.ExpiresAt > now)
                            .Select(h => new DronePilotHostility(h.SourceShipId, h.TargetShipId, h.LastAttackAt, h.ExpiresAt))
                            .ToArray()
                )
            );
            _droneAttackers[shipId] = attackers;
        }
        return attackers
            .Concat(_residentAttackers.TryGetValue(shipId, out var pending) ? pending.Values : [])
            .GroupBy(attack => attack.SourceShipId)
            .Select(group => group.MaxBy(attack => attack.LastAttackAt)!);
    }

    private void RecordDroneAttack(DronePlayer source, DronePlayer victim, int system, AggressionDecision decision, long now, long end)
    {
        _droneTimers[source.PilotId] = new(
            source.PilotId,
            system,
            CrimewatchRules.Player(DroneTimers(source), end, decision.Flag, true, now)
        );
        _droneTimers[victim.PilotId] = new(
            victim.PilotId,
            system,
            CrimewatchRules.Player(DroneTimers(victim), end, CrimewatchFlag.None, false, now)
        );
        var pair = (Math.Min(source.PilotId, victim.PilotId), Math.Max(source.PilotId, victim.PilotId));
        if (decision.Engagement)
        {
            var previous = _droneEngagements.GetValueOrDefault(pair);
            var saved = source.Engagements.FirstOrDefault(e => e.OtherPilotId == victim.PilotId);
            _droneEngagements[pair] = new(
                pair.Item1,
                pair.Item2,
                Math.Max(previous?.ActiveUntil ?? saved?.ActiveUntil ?? 0, end),
                Math.Max(previous?.ExpiresAt ?? saved?.ExpiresAt ?? 0, checked(end + CrimewatchRules.EngagementDuration.Ticks))
            );
        }
        var key = (source.ShipId, victim.ShipId);
        var prior = _droneHostilities.GetValueOrDefault(key);
        _droneHostilities[key] = new(
            source.ShipId,
            victim.ShipId,
            Math.Max(prior?.LastAttackAt ?? 0, now),
            Math.Max(prior?.ExpiresAt ?? 0, checked(end + CrimewatchRules.PvpDuration.Ticks))
        );
        if (!_residentAttackers.TryGetValue(victim.ShipId, out var attackers))
        {
            attackers = [];
            _residentAttackers[victim.ShipId] = attackers;
        }
        attackers[source.ShipId] = _droneHostilities[key];
    }

    internal CrimewatchSnapshot DroneCrimewatch(long pilot, int system, CrimewatchSnapshot snapshot)
    {
        return Execute(() => DroneCrimewatchOwned(pilot, system, snapshot), nameof(DroneCrimewatch));
    }

    private CrimewatchSnapshot DroneCrimewatchOwned(long pilot, int system, CrimewatchSnapshot snapshot)
    {
        using var ownership = owner.Enter();
        if (_droneTimers.Count == 0)
        {
            return snapshot;
        }
        var state = _droneTimers.GetValueOrDefault(pilot);
        var engagements = snapshot.Engagements.ToDictionary(e => e.OtherPilotId);
        foreach (var engagement in _droneEngagements.Values.Where(e => e.LowerPilotId == pilot || e.HigherPilotId == pilot))
        {
            long other = engagement.LowerPilotId == pilot ? engagement.HigherPilotId : engagement.LowerPilotId;
            engagements[other] = new(other, engagement.ActiveUntil, engagement.ExpiresAt);
        }
        var flags = snapshot.Flags.ToDictionary(f => f.PilotId);
        foreach (
            var pending in _droneTimers.Values.Where(p =>
                p.SystemId == system && (p.Timers.Suspect.ExpiresAt > 0 || p.Timers.Criminal.ExpiresAt > 0)
            )
        )
        {
            flags[pending.PilotId] = new(pending.PilotId, pending.Timers);
        }
        return snapshot with
        {
            Timers = state is null ? snapshot.Timers : MergeTimers(snapshot.Timers, state.Timers),
            Engagements = engagements.Values.OrderBy(e => e.OtherPilotId).ToArray(),
            Flags = flags.Values.OrderBy(f => f.PilotId).ToArray(),
        };
    }

    internal long DroneLogoffDeadline(long pilot, long deadline)
    {
        return Execute(() => DroneLogoffDeadlineOwned(pilot, deadline), nameof(DroneLogoffDeadline));
    }

    private long DroneLogoffDeadlineOwned(long pilot, long deadline)
    {
        using var ownership = owner.Enter();
        return Math.Max(deadline, _droneTimers.GetValueOrDefault(pilot)?.Timers.Pvp.ExpiresAt ?? 0);
    }

    private static CrimewatchTimers MergeTimers(CrimewatchTimers left, CrimewatchTimers right)
    {
        static CombatTimer Merge(CombatTimer a, CombatTimer b) =>
            new(Math.Max(a.ActiveUntil, b.ActiveUntil), Math.Max(a.ExpiresAt, b.ExpiresAt));
        return new(
            Merge(left.Weapons, right.Weapons),
            Merge(left.Pvp, right.Pvp),
            Merge(left.Npc, right.Npc),
            Merge(left.Suspect, right.Suspect),
            Merge(left.Criminal, right.Criminal)
        );
    }

    internal static void ProjectDronePlayers(
        WorldDbContext world,
        IReadOnlyList<DronePilotTimers> timers,
        IReadOnlyList<DronePilotEngagement> engagements,
        IReadOnlyList<DronePilotHostility> hostilities
    )
    {
        foreach (var state in timers)
        {
            var saved = CrimewatchPersistence.State(world, state.PilotId);
            saved.SystemId = state.SystemId;
            saved.Apply(MergeTimers(saved.Timers(), state.Timers));
            if (world.Set<SavedLogoff>().Find(state.PilotId) is { } logoff)
            {
                logoff.ExpiresAt = Math.Max(logoff.ExpiresAt, state.Timers.Pvp.ExpiresAt);
            }
        }
        foreach (var engagement in engagements)
        {
            var saved =
                CrimewatchPersistence.Engagement(world, engagement.LowerPilotId, engagement.HigherPilotId)
                ?? world
                    .Add(new SavedEngagement { LowerPilotId = engagement.LowerPilotId, HigherPilotId = engagement.HigherPilotId })
                    .Entity;
            saved.ActiveUntil = Math.Max(saved.ActiveUntil, engagement.ActiveUntil);
            saved.ExpiresAt = Math.Max(saved.ExpiresAt, engagement.ExpiresAt);
        }
        foreach (var hostility in hostilities)
        {
            var saved =
                world.Set<SavedPilotHostility>().Find(hostility.SourceShipId, hostility.TargetShipId)
                ?? world
                    .Add(new SavedPilotHostility { SourceShipId = hostility.SourceShipId, TargetShipId = hostility.TargetShipId })
                    .Entity;
            saved.LastAttackAt = Math.Max(saved.LastAttackAt, hostility.LastAttackAt);
            saved.ExpiresAt = Math.Max(saved.ExpiresAt, hostility.ExpiresAt);
        }
    }

    private void ClearDronePlayers()
    {
        _dronePlayers.Clear();
        _droneAttackers.Clear();
        _droneSecurity.Clear();
    }

    private void ObserveDronePlayers(IReadOnlyCollection<CombatRowChange> changes)
    {
        if (changes.Any(c => c.Table is "WorldAccount" or "WorldEngagement" or "WorldPilotHostility" or "schema"))
        {
            ClearDronePlayers();
            return;
        }
        foreach (var change in changes)
        {
            if (change.Table is "WorldPilot" or "WorldCrimewatch" or "WorldLogoff" or "WorldPilotSecurity")
            {
                foreach (long ship in _dronePlayers.Where(p => p.Value?.PilotId == change.Id).Select(p => p.Key).ToArray())
                {
                    _dronePlayers.Remove(ship);
                    _droneAttackers.Remove(ship);
                }
            }
            if (change.Table is "WorldShip" or "WorldPositionMembership" or "WorldShipLoss")
            {
                _dronePlayers.Remove(change.Id);
                _droneAttackers.Remove(change.Id);
            }
        }
    }
}
