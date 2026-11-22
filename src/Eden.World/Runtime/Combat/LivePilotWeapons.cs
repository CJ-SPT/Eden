using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.World.Storage;

namespace Eden.World.Runtime.Combat;

internal sealed partial class LiveCombatRuntime
{
    internal CapsuleerAttacker[] DefenseNpcAttackers(DockedPilot pilot, long now, IReadOnlyList<NpcEwarContact> incoming)
    {
        return Execute(() => DefenseNpcAttackersOwned(pilot, now, incoming), nameof(DefenseNpcAttackers));
    }

    private CapsuleerAttacker[] DefenseNpcAttackersOwned(DockedPilot pilot, long now, IReadOnlyList<NpcEwarContact> incoming)
    {
        using var ownership = owner.Enter();
        if (CanProbeDefense(pilot) && !HasNpcDefenseAttack(pilot.ShipId, now, incoming))
        {
            Trim();
            owner.TrackShip(pilot.ShipId, pilot.SystemId, pilot.PilotId);
            return [];
        }
        var ship = Ship(pilot.PilotId, now);
        if (ship.Position is null)
        {
            return [];
        }
        var recent = DroneTriggers(ship.ShipId).Incoming;
        var attempts = owner.Reads?.NpcEwarAttempts(ship.ShipId) ?? [];
        var aggressionById = Aggression();
        bool launched = recent.At > 0 && recent.At + CrimewatchRules.PvpDuration.Ticks > now && aggressionById.ContainsKey(recent.OtherId);
        var aggressors = Aggressors(ship.ShipId) ?? [];
        if (aggressors.Count == 0 && incoming.Count == 0 && attempts.Count == 0 && !launched)
        {
            return [];
        }
        List<CapsuleerAttacker> result = [];
        foreach (
            long id in aggressors
                .Concat(incoming.Select(effect => effect.SourceId))
                .Concat(attempts.Select(attempt => attempt.SourceId))
                .Concat(launched ? [recent.OtherId] : [])
                .Distinct()
        )
        {
            var aggression = aggressionById.GetValueOrDefault(id);
            var npc = Npc(id, now);
            long attacked = Math.Max(
                aggression?.LastShotAt ?? 0,
                incoming.Where(effect => effect.SourceId == id).Select(effect => effect.StartedAt).DefaultIfEmpty().Max()
            );
            if (recent.OtherId == id)
            {
                attacked = Math.Max(attacked, recent.At);
            }
            attacked = Math.Max(
                attacked,
                attempts.Where(attempt => attempt.SourceId == id).Select(attempt => attempt.StartedAt).DefaultIfEmpty().Max()
            );
            if (
                npc is not { Available: true }
                || attacked <= 0
                || npc.Transit is not null
                || npc.Position.DistanceTo(ship.Position) > AsteroidDefinitions.VisibilityRange
            )
            {
                continue;
            }
            result.Add(
                new(
                    0,
                    npc.Id,
                    npc.TypeId,
                    npc.Position,
                    npc.Velocity,
                    npc.Radius,
                    npc.SignatureRadius,
                    attacked,
                    incoming.Any(effect => effect.SourceId == id && effect.Applied.WarpStrength > 0),
                    true
                )
            );
        }
        return result.ToArray();
    }

    private bool HasNpcDefenseAttack(long ship, long now, IReadOnlyList<NpcEwarContact> incoming)
    {
        if (incoming.Count > 0 || owner.Reads?.NpcEwarAttempts(ship).Count > 0)
        {
            return true;
        }
        var recent = DroneTriggers(ship).Incoming;
        var aggression = Aggression();
        return (recent.At > 0 && recent.At + CrimewatchRules.PvpDuration.Ticks > now && aggression.ContainsKey(recent.OtherId))
            || aggression.Values.Any(attack => attack.ShipId == ship);
    }

    internal LiveCombatNpc? RattingNpc(long id, long now) => Npc(id, now);

    internal LiveCombatShip? DefenseShip(long pilot, long now)
    {
        return Execute(() => DefenseShipOwned(pilot, now), nameof(DefenseShip));
    }

    private LiveCombatShip? DefenseShipOwned(long pilot, long now)
    {
        using var ownership = owner.Enter();
        return CanRun ? Ship(pilot, now).SettleRepairs(now) : null;
    }

    internal CapsuleerAttacker? PatrolTarget(
        DockedPilot pilot,
        long target,
        SpacePosition anchor,
        IPilotCombatPresence presence,
        long now
    ) =>
        Execute(
            () =>
            {
                using var ownership = owner.Enter();
                if (
                    target == pilot.ShipId
                    || pilot.Position is null
                    || DronePlayerFor(target) is not { Capsule: false } victim
                    || IsCapsuleerCorporationAlly(pilot.PilotId, victim.PilotId)
                    || DronePresence(victim, presence, pilot.SystemId, now) is not { } contact
                    || contact.Position.DistanceTo(anchor) > PatrolRules.PursuitMeters
                    || contact.Position.DistanceTo(pilot.Position) > PatrolRules.PursuitMeters
                    || DronePlayerFor(pilot.ShipId) is not { } source
                    || DroneLegality(source, victim, pilot.SystemId, now)?.Flag != CrimewatchFlag.None
                )
                {
                    return null;
                }
                var other = Ship(victim.PilotId, now);
                return other.Health.At(now).Destroyed
                    ? null
                    : new CapsuleerAttacker(
                        victim.PilotId,
                        target,
                        other.TypeId,
                        contact.Position,
                        contact.Velocity,
                        other.Radius,
                        other.SignatureRadius,
                        0,
                        false,
                        false
                    );
            },
            nameof(PatrolTarget)
        );

    internal bool IsCapsuleerCorporationAlly(long pilot, long other)
    {
        var left = owner.Economy.CorporationAffiliation(pilot);
        var right = owner.Economy.CorporationAffiliation(other);
        return PatrolRules.CorporateAllies(left.Corporation, left.Alliance, right.Corporation, right.Alliance);
    }

    internal CapsuleerAttacker[] DefenseAttackers(DockedPilot pilot, IPilotCombatPresence presence, long now)
    {
        return Execute(() => DefenseAttackersOwned(pilot, presence, now), nameof(DefenseAttackers));
    }

    private CapsuleerAttacker[] DefenseAttackersOwned(DockedPilot pilot, IPilotCombatPresence presence, long now)
    {
        using var ownership = owner.Enter();
        if (CanProbeDefense(pilot) && !HasPlayerDefenseAttack(pilot.ShipId, now))
        {
            owner.TrackShip(pilot.ShipId, pilot.SystemId, pilot.PilotId);
            return [];
        }
        var ship = Ship(pilot.PilotId, now);
        if (ship.Position is null)
        {
            return [];
        }
        List<CapsuleerAttacker> result = [];
        foreach (var attack in PlayerAttacks(ship.ShipId, now))
        {
            if (attack.ExpiresAt <= now || attack.LastAttackAt <= 0)
            {
                continue;
            }
            var player = DronePlayerFor(attack.SourceShipId);
            if (player is null || DronePresence(player, presence, ship.SystemId, now) is not { } contact)
            {
                continue;
            }
            var other = Ship(player.PilotId, now);
            if (other.Health.At(now).Destroyed || ship.Position.DistanceTo(contact.Position) > AsteroidDefinitions.VisibilityRange)
            {
                continue;
            }
            result.Add(
                new(
                    player.PilotId,
                    player.ShipId,
                    other.TypeId,
                    contact.Position,
                    contact.Velocity,
                    other.Radius,
                    other.SignatureRadius,
                    attack.LastAttackAt,
                    false,
                    false
                )
            );
        }
        return result.ToArray();
    }

    private bool CanProbeDefense(DockedPilot pilot)
    {
        int system = pilot.SystemId;
        bool loaded = _ships.TryGetValue(pilot.PilotId, out var ship);
        return CanRun
            && pilot.Position is not null
            && (systemId is null || systemId == system)
            && (!loaded || ship!.ShipId == pilot.ShipId && ship.SystemId == system)
            && (!_positions.TryGetValue(pilot.ShipId, out var position) || position.System is null || position.System == system)
            && owner.Economy.CanProbeDefenseShip(pilot.PilotId, pilot.ShipId, system, cold: !loaded);
    }

    private bool HasPlayerDefenseAttack(long ship, long now)
    {
        static bool Active(DronePilotHostility attack, long time) => attack.ExpiresAt > time && attack.LastAttackAt > 0;

        // The exact path still merges by source and applies presence, visibility and damage gates.
        if (_residentAttackers.TryGetValue(ship, out var pending) && pending.Values.Any(attack => Active(attack, now)))
        {
            return true;
        }
        if (_droneAttackers.TryGetValue(ship, out var saved))
        {
            return saved.Any(attack => Active(attack, now));
        }
        if (owner.Economy.HasUnexpiredPlayerHostility(ship, now))
        {
            return true;
        }
        // The belt safety probe also uses the cached absence of a saved hostility.
        _droneAttackers[ship] = [];
        return false;
    }

    internal CombatVolley? FirePilot(
        long pilot,
        long module,
        PilotWeaponTarget target,
        SpacePosition velocity,
        long now,
        double roll,
        WeaponActivation? activation,
        double? crystalRoll,
        bool banked,
        bool overloaded,
        IPilotDamageEvents events
    )
    {
        return Execute(
            () => FirePilotOwned(pilot, module, target, velocity, now, roll, activation, crystalRoll, banked, overloaded, events),
            nameof(FirePilot)
        );
    }

    private CombatVolley? FirePilotOwned(
        long pilot,
        long module,
        PilotWeaponTarget target,
        SpacePosition velocity,
        long now,
        double roll,
        WeaponActivation? activation,
        double? crystalRoll,
        bool banked,
        bool overloaded,
        IPilotDamageEvents events
    )
    {
        using var ownership = owner.Enter();
        if (!CanRun || overloaded)
        {
            return null;
        }
        if (!double.IsFinite(roll) || roll is < 0 or >= 1 || !double.IsFinite(target.Velocity.Length))
        {
            throw new InvalidDataException("Invalid weapon accuracy draw.");
        }
        var source = DronePlayerFor(Ship(pilot, now).ShipId);
        var victim = DronePlayerFor(target.ShipId);
        if (source is null || victim is null || source.PilotId == victim.PilotId)
        {
            return null;
        }
        var ship = Ship(pilot, now);
        RequireIndustryOffense(ship.ShipId, now);
        var other = Ship(victim.PilotId, now).SettleRepairs(now);
        if (
            other.Position is null
            || other.Health.At(now).Destroyed
            || victim.Logoff is { } logoff
                && Math.Max(logoff.ExpiresAt, _droneTimers.GetValueOrDefault(victim.PilotId)?.Timers.Pvp.ExpiresAt ?? 0) <= now
        )
        {
            throw new InvalidDataException("The target ship is unavailable.");
        }
        var decision = DroneLegality(source, victim, ship.SystemId, now);
        if (decision is null)
        {
            throw new InvalidDataException("Your ship safety setting prevents this hostile action.");
        }
        if (decision.Flag != CrimewatchFlag.None)
        {
            return null;
        }
        var members = banked ? ship.Banks.Values.FirstOrDefault(bank => bank.Contains(module)) ?? [module] : [module];
        if (members.Any(id => owner.Heat.Selected(pilot, id, ship.ShipId)))
        {
            return null;
        }
        owner.Nanites.RequireAvailable(members, now);
        var draws = members.Select((_, index) => new TurretDraw(index == 0 ? roll : Draw(), crystalRoll ?? Draw())).ToArray();
        LiveCombatNpc adapter = new(
            other.ShipId,
            other.TypeId,
            other.SystemId,
            other.Position,
            target.Velocity,
            other.Radius,
            other.SignatureRadius,
            other.Health,
            true,
            other.Contributions,
            NpcRole.Pirate,
            0,
            0,
            other.CorporationId,
            0,
            other.Position,
            new ResidentMap<Eden.Gameplay.Dogma.DogmaAttributeId, double>()
        );
        var plan = LiveCombatRules.Fire(ship, adapter, module, banked, velocity, now, draws, activation);
        if (plan.Volley.Shot.Destroyed)
        {
            // The ordinary transactional path settles fatal damage, insurance and ownership atomically.
            return null;
        }
        long end = checked(now + (long)Math.Ceiling(plan.Volley.Shot.Weapon.CycleMilliseconds * TimeSpan.TicksPerMillisecond));
        RecordDroneAttack(source, victim, ship.SystemId, decision, now, end);
        RecordDroneModule(ship.ShipId, other.ShipId, now);
        RecordDroneIncoming(other.ShipId, ship.ShipId, now);
        _ships[pilot] = plan.Ship;
        _ships[victim.PilotId] = other with { Health = plan.Target.Health, Contributions = plan.Target.Contributions };
        _dirtyShips.Add(pilot);
        _dirtyShips.Add(victim.PilotId);
        owner.ObjectsChanged();
        var shot = plan.Volley.Shot;
        events.Publish(
            new(
                victim.PilotId,
                pilot,
                ship.ShipId,
                shot.Health,
                plan.Damage,
                shot.Applied,
                shot.Quality,
                shot.Weapon.TypeId,
                members.Count > 1,
                null,
                null
            )
        );
        return plan.Volley;
    }
}
