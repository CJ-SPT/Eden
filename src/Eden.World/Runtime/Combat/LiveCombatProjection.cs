using System.Text.Json;
using Eden.Data.Combat;
using Eden.Gameplay.Combat;
using Eden.World.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Combat;

internal sealed partial class LiveCombatRuntime
{
    internal static void ProjectShip(WorldDbContext world, LiveCombatShip before, LiveCombatShip after)
    {
        if (before.NpcAggression != after.NpcAggression && after.NpcAggression is { } npcTimer)
        {
            CrimewatchPersistence.Npc(world, after.PilotId, after.SystemId, npcTimer.ActiveUntil);
        }
        if (
            before.Capacitor != after.Capacitor
            || before.CapacitorAt != after.CapacitorAt
            || after.Turrets.Any(p => p.Value.ReadyAt != before.Turrets[p.Key].ReadyAt)
        )
        {
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldShipEnergy(ShipId,Charge,UpdatedAt) VALUES({after.ShipId},{after.Capacitor},{after.CapacitorAt})
                ON CONFLICT(ShipId) DO UPDATE SET Charge=excluded.Charge,UpdatedAt=excluded.UpdatedAt
                """
            );
        }
        if (before.Health != after.Health)
        {
            var health = after.Health;
            double armor = health.ArmorDamage ?? health.Defense.Armor - health.Remaining.Armor;
            double structure = health.StructureDamage ?? health.Defense.Structure - health.Remaining.Structure;
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldShipHealth(ShipId,ShieldCharge,ArmorDamage,StructureDamage,UpdatedAt)
                VALUES({after.ShipId},{health.Remaining.Shield},{armor},{structure},{health.UpdatedAt})
                ON CONFLICT(ShipId) DO UPDATE SET ShieldCharge=excluded.ShieldCharge,ArmorDamage=excluded.ArmorDamage,
                    StructureDamage=excluded.StructureDamage,UpdatedAt=excluded.UpdatedAt
                """
            );
        }
        foreach (var pair in after.Turrets)
        {
            var turret = pair.Value;
            var previous = before.Turrets[pair.Key];
            if (previous.ReadyAt != turret.ReadyAt)
            {
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldWeaponCooldown(ModuleId,ReadyAt,Kind) VALUES({pair.Key},{turret.ReadyAt},{(int)
                        ModuleCooldownKind.Cycle})
                    ON CONFLICT(ModuleId) DO UPDATE SET ReadyAt=excluded.ReadyAt,Kind=excluded.Kind
                    """
                );
            }
            if (
                turret.ChargeItemId is { } charge
                && (previous.Quantity != turret.Quantity || previous.CrystalDamage != turret.CrystalDamage)
            )
            {
                if (turret.Quantity == 0)
                {
                    world.Database.ExecuteSql($"DELETE FROM WorldItem WHERE Id={charge} AND OwnerId={after.PilotId}");
                }
                else
                {
                    world.Database.ExecuteSql(
                        $"""
                        UPDATE WorldItem SET Quantity={turret.Quantity},CrystalDamage={turret.CrystalDamage}
                        WHERE Id={charge} AND OwnerId={after.PilotId}
                        """
                    );
                }
            }
        }
        foreach (var repair in before.Repairs.Where(r => after.Repairs.All(current => current.ModuleId != r.ModuleId)))
        {
            world.Database.ExecuteSql($"DELETE FROM WorldArmorRepair WHERE ModuleId={repair.ModuleId} AND ShipId={after.ShipId}");
        }
        ProjectContributions(world, after.ShipId, before.Contributions, after.Contributions);
    }

    internal static void ProjectNpc(WorldDbContext world, LiveCombatNpc before, LiveCombatNpc after)
    {
        if (
            before.Health != after.Health
            || before.Position != after.Position
            || before.Velocity != after.Velocity
            || before.Navigation != after.Navigation
            || before.MotionAt != after.MotionAt
            || before.RepairState != after.RepairState
        )
        {
            bool healthChanged = before.Health != after.Health;
            var health = after.Health;
            double armor = health.ArmorDamage ?? health.Defense.Armor - health.Remaining.Armor;
            double structure = health.StructureDamage ?? health.Defense.Structure - health.Remaining.Structure;
            world.Database.ExecuteSql(
                $"""
                UPDATE WorldNpc SET
                    ShieldCharge=CASE WHEN {healthChanged} THEN {health.Remaining.Shield} ELSE ShieldCharge END,
                    ArmorDamage=CASE WHEN {healthChanged} THEN {armor} ELSE ArmorDamage END,
                    StructureDamage=CASE WHEN {healthChanged} THEN {structure} ELSE StructureDamage END,
                    UpdatedAt=CASE WHEN {healthChanged} THEN {health.UpdatedAt} ELSE UpdatedAt END,
                    X={after.Position.X},Y={after.Position.Y},Z={after.Position.Z},
                    VelocityX={after.Velocity.X},VelocityY={after.Velocity.Y},VelocityZ={after.Velocity.Z},
                    Navigation={(int)after.Navigation},MotionAt={after.MotionAt},
                    ShieldRepairAt={after.RepairState.ShieldReadyAt},ArmorRepairAt={after.RepairState.ArmorReadyAt},
                    RepairRevision={after.RepairState.Revision} WHERE Id={after.Id}
                """
            );
        }
        ProjectContributions(world, after.Id, before.Contributions, after.Contributions);
    }

    private static void ProjectContributions(
        WorldDbContext world,
        long victim,
        IReadOnlyDictionary<long, Eden.Gameplay.Characters.CombatParty> previous,
        IReadOnlyDictionary<long, Eden.Gameplay.Characters.CombatParty> current
    )
    {
        foreach (var party in current.Values)
        {
            if (previous.TryGetValue(party.EntityId, out var prior) && prior == party)
            {
                continue;
            }
            string data = JsonSerializer.Serialize(party with { Damage = 0 });
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCombatDamage(VictimId,AttackerId,PartyJson,Damage) VALUES({victim},{party.EntityId},{data},{party.Damage})
                ON CONFLICT(VictimId,AttackerId) DO UPDATE SET PartyJson=excluded.PartyJson,Damage=excluded.Damage
                """
            );
        }
    }
}
