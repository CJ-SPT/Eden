using Eden.Protocol;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public sealed class ShipHealthState
{
    public long ShipId { get; set; }
    public double ShieldCharge { get; set; }
    public double ArmorDamage { get; set; }
    public double StructureDamage { get; set; }
    public long UpdatedAt { get; set; }
}

[Injectable]
internal sealed class PilotHealthStore(WorldDatabase database, IPilotFitting fitting) : IPilotHealth
{
    public ShipHealth Read(long pilotId, long now)
    {
        using WorldDbContext world = new(database.Path);
        long shipId = world.Pilots.Where(p => p.Id == pilotId).Select(p => p.ActiveShipId).Single();
        return Read(world, shipId, ShipHealth.DefenseFrom(fitting.ShipAttributes(pilotId)), now);
    }

    internal static ShipHealth Read(WorldDbContext world, long shipId, HullDefense defense, long now)
    {
        var saved = world.Set<ShipHealthState>().Find(shipId);
        return new(shipId, defense, saved is null ? defense.Undamaged : new(
            ChargedResource.Recharged(saved.ShieldCharge, defense.Shield, defense.ShieldRechargeMilliseconds,
                Math.Max(0, (now - saved.UpdatedAt) / (double)TimeSpan.TicksPerMillisecond)),
            Math.Max(0, defense.Armor - saved.ArmorDamage), Math.Max(0, defense.Structure - saved.StructureDamage)));
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
