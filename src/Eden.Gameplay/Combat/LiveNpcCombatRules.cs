using Eden.Gameplay.Characters;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Combat;

public sealed record LiveNpcShot(LiveCombatShip Ship, NpcShot Shot);

public static class LiveNpcCombatRules
{
    public static LiveNpcShot Fire(LiveCombatShip ship, LiveCombatNpc npc, SpacePosition velocity, long now, double roll)
    {
        ship = ship.SettleRepairs(now);
        var attributes = npc.Attributes;
        double duration = attributes.GetValueOrDefault(DogmaAttributeId.WeaponCycleTime);
        if (attributes.Values.Any(v => !double.IsFinite(v)) || duration <= 0)
        {
            throw new InvalidDataException("Invalid NPC weapon cycle attributes.");
        }
        var separation = ship.Position! - npc.Position;
        double range = Math.Max(0, separation.Length - ship.Radius - npc.Radius);
        DamageVector damage = new(
            attributes.GetValueOrDefault(DogmaAttributeId.EmDamage),
            attributes.GetValueOrDefault(DogmaAttributeId.ExplosiveDamage),
            attributes.GetValueOrDefault(DogmaAttributeId.KineticDamage),
            attributes.GetValueOrDefault(DogmaAttributeId.ThermalDamage)
        );
        damage = damage.Scale(attributes.GetValueOrDefault(DogmaAttributeId.DamageMultiplier));
        double chance = CombatDamage.HitChance(
            range,
            CombatDamage.AngularVelocity(separation, velocity - npc.Velocity),
            ship.SignatureRadius,
            attributes.GetValueOrDefault(DogmaAttributeId.MaxRange),
            attributes.GetValueOrDefault(DogmaAttributeId.AccuracyFalloff),
            attributes.GetValueOrDefault(DogmaAttributeId.TrackingSpeed),
            attributes.GetValueOrDefault(DogmaAttributeId.WeaponSignatureResolution)
        );
        double quality = CombatDamage.ShotQuality(chance, roll);
        var applied = CombatDamage.Apply(ship.Health.At(now), ship.Health.Defense, damage.Scale(quality));
        int turret = checked((int)attributes.GetValueOrDefault(DogmaAttributeId.EntityTurretType));
        var contributors = InteractionHistoryRules.AddDamage(
            ship.Contributions,
            new(npc.Id, null, npc.CorporationId, npc.FactionId == 0 ? null : npc.FactionId, npc.TypeId, turret, null, 0),
            applied.Applied
        );
        var next = ship with
        {
            Health = ship.Health with
            {
                Remaining = applied.Remaining,
                UpdatedAt = Math.Max(now, ship.Health.UpdatedAt),
                Revision = ship.Health.Revision + 1,
                ArmorDamage = ship.Health.Defense.Armor - applied.Remaining.Armor,
                StructureDamage = ship.Health.Defense.Structure - applied.Remaining.Structure,
            },
            Contributions = contributors,
        };
        NpcShot shot = new(
            npc.Id,
            npc.TypeId,
            new(ship.ShipId, turret, now, duration),
            damage,
            applied.Applied,
            quality,
            new(ship.ShipId, ship.Health.Defense, applied.Remaining),
            null
        );
        return new(next, shot);
    }
}
