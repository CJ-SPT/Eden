using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Combat;
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
        bool Alpha,
        ShipSafetyLevel Safety,
        bool Outlaw,
        DroneLogoff? Logoff,
        IReadOnlyList<LimitedEngagement> Engagements,
        CrimewatchTimers Timers
    );

    private readonly Dictionary<long, DronePlayer?> _dronePlayers = [];
    private readonly Dictionary<long, DronePilotTimers> _droneTimers = [];
    private readonly Dictionary<(long Lower, long Higher), DronePilotEngagement> _droneEngagements = [];
    private readonly Dictionary<(long Source, long Target), DronePilotHostility> _droneHostilities = [];
    private readonly Dictionary<long, DronePilotHostility[]> _droneAttackers = [];
    private readonly Dictionary<int, double> _droneSecurity = [];

    private DronePlayer? DronePlayerFor(long shipId)
    {
        if (!_dronePlayers.TryGetValue(shipId, out var value))
        {
            value = Loading(() =>
            {
                using WorldDbContext world = new(database.Path);
                using CatalogDbContext catalog = new(database.CatalogPath);
                var pilot = world
                    .Pilots.Include(p => p.ActiveShip)
                        .ThenInclude(s => s.Position)
                    .SingleOrDefault(p => p.ActiveShipId == shipId);
                if (
                    pilot?.ActiveShip.Position is null
                    || catalog.RequireItem(pilot.ActiveShip.TypeId).GroupId == FleetDefinitions.CapsuleGroup
                    || world.Set<ShipLoss>().Any(l => l.ShipId == shipId)
                )
                {
                    return null;
                }
                var engagements = world
                    .Set<SavedEngagement>()
                    .Where(e => e.LowerPilotId == pilot.Id || e.HigherPilotId == pilot.Id)
                    .AsEnumerable()
                    .Select(e => new LimitedEngagement(
                        e.LowerPilotId == pilot.Id ? e.HigherPilotId : e.LowerPilotId,
                        e.ActiveUntil,
                        e.ExpiresAt
                    ))
                    .ToArray();
                var logoff = world.Set<SavedLogoff>().Find(pilot.Id);
                return new DronePlayer(
                    pilot.Id,
                    shipId,
                    world.Set<Account>().Find(pilot.AccountId)!.CloneGrade == AccountCloneGrade.Alpha,
                    pilot.SafetyLevel,
                    (world.Set<SavedSecurityStatus>().Find(pilot.Id)?.Value ?? 0) <= CrimewatchRules.OutlawSecurity,
                    logoff is null ? null : new(logoff.ShipId, logoff.SystemId, logoff.ExpiresAt),
                    engagements,
                    world.Set<SavedCrimewatch>().Find(pilot.Id)?.Timers() ?? CrimewatchTimers.Empty
                );
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
                new(security, source.Safety, source.Alpha, false, DroneTimers(victim).Flag(now), victim.Outlaw, engagement > now)
            );
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private PilotCombatPresence? DronePresence(DronePlayer victim, IPilotCombatPresence presence, int system, long now)
    {
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
        if (!_droneAttackers.TryGetValue(ship.ShipId, out var attackers))
        {
            attackers = Loading(() =>
            {
                using WorldDbContext world = new(database.Path);
                return world
                    .Set<SavedPilotHostility>()
                    .Where(h => h.TargetShipId == ship.ShipId)
                    .AsEnumerable()
                    .Select(h => new DronePilotHostility(h.SourceShipId, h.TargetShipId, h.LastAttackAt, h.ExpiresAt))
                    .ToArray();
            });
            _droneAttackers[ship.ShipId] = attackers;
        }
        foreach (
            var attack in attackers
                .Concat(_droneHostilities.Values.Where(h => h.TargetShipId == ship.ShipId))
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
    }

    internal CrimewatchSnapshot DroneCrimewatch(long pilot, int system, CrimewatchSnapshot snapshot)
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

    private void ClearProjectedDroneCombat()
    {
        _droneTimers.Clear();
        _droneEngagements.Clear();
        _droneHostilities.Clear();
        ClearDronePlayers();
    }
}
