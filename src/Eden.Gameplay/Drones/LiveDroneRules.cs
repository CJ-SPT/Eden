using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Ships;

namespace Eden.Gameplay.Drones;

public sealed record LiveDrone(
    DroneContact Contact,
    long UpdatedAt,
    long ReadyAt,
    long CommandedAt,
    double ArmorDamage,
    double StructureDamage
);

public sealed record LiveDroneStep(LiveDrone Drone, bool Recall, DamageVector? Damage = null);

public sealed record LiveDroneImpact(LiveCombatNpc Target, double Applied);

public sealed record DroneTarget(long Id, SpacePosition Position, SpacePosition Velocity, double Radius, double SignatureRadius);

public static class LiveDroneRules
{
    public static LiveDrone Release(LiveDrone drone) =>
        drone with
        {
            Contact = drone.Contact with
            {
                ShipId = 0,
                TargetId = 0,
                Activity = DroneActivity.Idle,
                Orbiting = false,
                SpeedLimit = 0,
                ShotAt = 0,
                ShotTargetId = 0,
                Velocity = new(0, 0, 0),
            },
        };

    public static LiveDroneStep Advance(LiveDrone drone, LiveCombatShip ship, LiveCombatNpc? npc, long now, int stamp, double roll) =>
        AdvanceTarget(
            drone,
            ship,
            npc is null ? null : new(npc.Id, npc.Position, npc.Velocity, npc.Radius, npc.SignatureRadius),
            now,
            stamp,
            roll
        );

    public static LiveDroneStep AdvanceTarget(LiveDrone drone, LiveCombatShip ship, DroneTarget? enemy, long now, int stamp, double roll)
    {
        if (now <= drone.UpdatedAt)
        {
            return new(drone, false);
        }
        var contact = drone.Contact;
        var profile = contact.Profile;
        if (enemy is null && contact.Activity == DroneActivity.Fighting)
        {
            contact = contact with { Activity = DroneActivity.Idle, TargetId = ship.ShipId };
        }
        var target = enemy?.Position ?? ship.Position!;
        float radius = (float)(enemy?.Radius ?? ship.Radius);
        double distance = contact.Position.DistanceTo(target) - radius - profile.Radius;
        if (contact.Activity == DroneActivity.Returning && distance <= profile.OrbitRange + DroneRules.LaunchOffset)
        {
            contact = contact with { Activity = DroneActivity.Idle };
        }
        if (contact.Activity == DroneActivity.ReturningToBay && distance <= DroneRules.RecallRange)
        {
            return new(drone, true);
        }
        double seconds = Math.Min(1, (now - drone.UpdatedAt) / (double)TimeSpan.TicksPerSecond);
        contact = DroneRules.Move(contact, target, radius, contact.Activity != DroneActivity.ReturningToBay, stamp, seconds);
        if (contact.Position.DistanceTo(ship.Position!) > DroneRules.ShutdownDistance)
        {
            return new(Release(drone with { Contact = contact }), false);
        }
        contact = contact with
        {
            Health = contact.Health with
            {
                Remaining = contact.Health.Remaining with
                {
                    Shield = ChargedResource.Recharged(
                        contact.Health.Remaining.Shield,
                        profile.Defense.Shield,
                        profile.Defense.ShieldRechargeMilliseconds,
                        seconds * 1000
                    ),
                },
            },
        };
        var next = drone with { Contact = contact, UpdatedAt = now };
        if (
            enemy is null
            || drone.ReadyAt > now
            || contact.Position.DistanceTo(target) - radius - profile.Radius > profile.Optimal + profile.Falloff * 2
        )
        {
            return new(next, false);
        }
        var damage = DroneRules.Shot(contact, target, enemy.Velocity, enemy.SignatureRadius, roll);
        return new(
            next with
            {
                ReadyAt = checked(now + (long)Math.Ceiling(profile.CycleMilliseconds * TimeSpan.TicksPerMillisecond)),
                Contact = contact with { ShotAt = now, ShotTargetId = enemy.Id },
            },
            false,
            damage
        );
    }

    public static LiveDroneImpact Hit(LiveCombatNpc npc, LiveCombatShip ship, DroneContact drone, DamageVector damage, long now)
    {
        var result = CombatDamage.Apply(npc.Health.At(now), npc.Health.Defense, damage);
        var contributions = InteractionHistoryRules.AddDamage(
            npc.Contributions,
            new(ship.ShipId, ship.PilotId, ship.CorporationId, null, ship.TypeId, drone.Profile.TypeId, null, 0),
            result.Applied
        );
        return new(
            npc with
            {
                Contributions = contributions,
                Available = !result.Remaining.Destroyed,
                Health = npc.Health with
                {
                    Remaining = result.Remaining,
                    UpdatedAt = Math.Max(now, npc.Health.UpdatedAt),
                    Revision = npc.Health.Revision + 1,
                    ArmorDamage = npc.Health.Defense.Armor - result.Remaining.Armor,
                    StructureDamage = npc.Health.Defense.Structure - result.Remaining.Structure,
                },
            },
            result.Applied
        );
    }
}
