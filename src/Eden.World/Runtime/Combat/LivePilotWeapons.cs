using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.World.Storage;

namespace Eden.World.Runtime.Combat;

internal sealed partial class LiveCombatRuntime
{
    internal CapsuleerAttacker[] DefenseNpcAttackers(long pilot, long now, IReadOnlyList<NpcEwarContact> incoming)
    {
        using var ownership = owner.Enter();
        var ship = Ship(pilot, now);
        if (ship.Position is null)
        {
            return [];
        }
        List<CapsuleerAttacker> result = [];
        var recent = DroneTriggers(ship.ShipId).Incoming;
        var attempts = owner.Reads?.NpcEwarAttempts(ship.ShipId) ?? [];
        var aggressionById = Aggression();
        var launched =
            recent.At > 0 && recent.At + CrimewatchRules.PvpDuration.Ticks > now && aggressionById.ContainsKey(recent.OtherId)
                ? new[] { recent.OtherId }
                : [];
        foreach (
            long id in (Aggressors(ship.ShipId) ?? [])
                .Concat(incoming.Select(effect => effect.SourceId))
                .Concat(attempts.Select(attempt => attempt.SourceId))
                .Concat(launched)
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

    internal LiveCombatShip? DefenseShip(long pilot, long now)
    {
        using var ownership = owner.Enter();
        return CanRun ? Ship(pilot, now).SettleRepairs(now) : null;
    }

    internal CapsuleerAttacker[] DefenseAttackers(long pilot, IPilotCombatPresence presence, long now)
    {
        using var ownership = owner.Enter();
        var ship = Ship(pilot, now);
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
        if (decision.Flag == CrimewatchFlag.Criminal)
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
            new Dictionary<Eden.Gameplay.Dogma.DogmaAttributeId, double>()
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
