using Eden.Data.World.Combat;
using Eden.Data.World.Repair;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Repair;
using Eden.World.Runtime.Combat;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Combat;

internal sealed partial class PilotHealthStore
{
    internal static ShipHealth Read(WorldAction world, long shipId, HullDefense defense, long now)
    {
        var saved = world.Set<ShipHealthState>().Find(shipId);
        ShipHealth current = new(
            shipId,
            defense,
            saved is null
                ? defense.Undamaged
                : new(
                    ChargedResource.Recharged(
                        saved.ShieldCharge,
                        defense.Shield,
                        defense.ShieldRechargeMilliseconds,
                        Math.Max(0, (now - saved.UpdatedAt) / (double)TimeSpan.TicksPerMillisecond)
                    ),
                    Math.Max(0, defense.Armor - saved.ArmorDamage),
                    Math.Max(0, defense.Structure - saved.StructureDamage)
                )
        );
        var repairs = world.Set<PendingLocalRepair>().Where(r => r.ShipId == shipId && r.CompletesAt <= now).ToArray();
        if (repairs.Length > 0)
        {
            foreach (var repair in repairs)
            {
                current = LocalRepairRules.Apply(current, repair.Amount, repair.Layer);
            }
            if (saved is not null && !current.Remaining.Destroyed)
            {
                if (repairs.Any(r => r.Layer == RepairLayer.Armor))
                {
                    saved.ArmorDamage = Math.Max(0, defense.Armor - current.Remaining.Armor);
                }
                if (repairs.Any(r => r.Layer == RepairLayer.Hull))
                {
                    saved.StructureDamage = Math.Max(0, defense.Structure - current.Remaining.Structure);
                }
            }
            world.RemoveRange(repairs);
            // Every caller owns a world transaction. Settle the single paid
            // completion before combat, fitting or a resource snapshot uses it.
            world.SaveChanges();
        }
        return current;
    }

    internal static DamageResult Apply(
        WorldAction world,
        long shipId,
        HullDefense defense,
        DamageVector damage,
        long now,
        bool bypassResistances = false
    )
    {
        var current = Read(world, shipId, defense, now);
        var result = CombatDamage.Apply(current.Remaining, bypassResistances ? CombatDamage.WithoutResistances(defense) : defense, damage);
        var saved = world.Set<ShipHealthState>().Find(shipId) ?? world.Set<ShipHealthState>().Add(new() { ShipId = shipId }).Entity;
        saved.ShieldCharge = result.Remaining.Shield;
        saved.ArmorDamage = defense.Armor - result.Remaining.Armor;
        saved.StructureDamage = defense.Structure - result.Remaining.Structure;
        saved.UpdatedAt = Math.Max(now, saved.UpdatedAt);
        return result;
    }
}
