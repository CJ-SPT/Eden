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
using Eden.World.Storage;

namespace Eden.World.Combat;

[Injectable]
internal sealed class PilotHealthStore(WorldDatabase database) : IPilotHealth
{
    public bool WasDestroyed(long shipId)
    {
        using WorldDbContext world = new(database.Path);
        return world.Set<ShipLoss>().Any(l => l.ShipId == shipId);
    }

    public ShipHealth Read(long pilotId, long now)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("health.read");
        if (LiveCombatRuntime.Find(database.Path, pilotId)?.Health(pilotId, now) is { } resident)
        {
            return resident;
        }
        using WorldDbContext world = new(database.Path) { Participants = new(pilots: [pilotId]) };
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        long shipId = world.Pilots.Where(p => p.Id == pilotId).Select(p => p.ActiveShipId).Single();
        var health = Read(world, shipId, ShipHealth.DefenseFrom(PilotFittingStore.ShipAttributes(world, catalog, pilotId)), now);
        transaction.Commit();
        return health;
    }

    internal static ShipHealth Read(WorldDbContext world, long shipId, HullDefense defense, long now)
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

    // Internal combat persistence primitive: callers keep health, destruction
    // and rewards in their owning world transaction. There is no damage RPC.
    internal static DamageResult Apply(WorldDbContext world, long shipId, HullDefense defense, DamageVector damage, long now)
    {
        var current = Read(world, shipId, defense, now);
        var result = CombatDamage.Apply(current.Remaining, defense, damage);
        var saved = world.Set<ShipHealthState>().Find(shipId) ?? world.Set<ShipHealthState>().Add(new() { ShipId = shipId }).Entity;
        saved.ShieldCharge = result.Remaining.Shield;
        saved.ArmorDamage = defense.Armor - result.Remaining.Armor;
        saved.StructureDamage = defense.Structure - result.Remaining.Structure;
        saved.UpdatedAt = Math.Max(now, saved.UpdatedAt);
        return result;
    }
}
