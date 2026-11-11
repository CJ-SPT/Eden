using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Repair;
using Eden.Gameplay.Combat;
using Eden.World.Combat;
using Eden.World.Runtime.Economy;

namespace Eden.World.Runtime.Combat;

internal sealed partial class LiveCombatRuntime
{
    internal static void ProjectShip(WorldAction world, LiveCombatShip before, LiveCombatShip after)
    {
        if (before.Capacitor != after.Capacitor || before.CapacitorAt != after.CapacitorAt)
        {
            var energy = world.Set<ShipEnergy>().Find(after.ShipId) ?? world.Add(new ShipEnergy { ShipId = after.ShipId }).Entity;
            energy.Charge = after.Capacitor;
            energy.UpdatedAt = after.CapacitorAt;
        }
        if (before.Health != after.Health)
        {
            var health = world.Set<ShipHealthState>().Find(after.ShipId) ?? world.Add(new ShipHealthState { ShipId = after.ShipId }).Entity;
            health.ShieldCharge = after.Health.Remaining.Shield;
            health.ArmorDamage = after.Health.ArmorDamage ?? after.Health.Defense.Armor - after.Health.Remaining.Armor;
            health.StructureDamage = after.Health.StructureDamage ?? after.Health.Defense.Structure - after.Health.Remaining.Structure;
            health.UpdatedAt = after.Health.UpdatedAt;
        }
        foreach (var (module, ready) in after.LocalCooldowns)
        {
            if (before.LocalCooldowns.GetValueOrDefault(module) != ready)
            {
                Cooldown(world, module, ready);
            }
        }
        foreach (var (module, turret) in after.Turrets)
        {
            var previous = before.Turrets[module];
            if (previous.ReadyAt != turret.ReadyAt)
            {
                Cooldown(world, module, turret.ReadyAt);
            }
            if (
                turret.ChargeItemId is long charge
                && (previous.Quantity != turret.Quantity || previous.CrystalDamage != turret.CrystalDamage)
            )
            {
                if (turret.Quantity == 0)
                {
                    world.Set<OwnedItem>().Remove(new OwnedItem { Id = charge });
                }
                else if (world.Set<OwnedItem>().Find(charge) is { } item)
                {
                    item.Quantity = turret.Quantity;
                    item.CrystalDamage = turret.CrystalDamage;
                }
            }
        }
        foreach (var repair in after.Repairs.Where(r => !before.Repairs.Contains(r)))
        {
            var row =
                world.Set<PendingLocalRepair>().Find(repair.ModuleId)
                ?? world.Add(new PendingLocalRepair { ModuleId = repair.ModuleId }).Entity;
            row.ShipId = after.ShipId;
            row.StartedAt = repair.StartedAt;
            row.CompletesAt = repair.CompletesAt;
            row.Amount = repair.Amount;
            row.Layer = repair.Layer;
        }
        foreach (var repair in before.Repairs.Where(r => after.Repairs.All(current => current.ModuleId != r.ModuleId)))
        {
            if (world.Set<PendingLocalRepair>().Find(repair.ModuleId) is { } row)
            {
                world.Remove(row);
            }
        }
        if (before.NpcAggression != after.NpcAggression && after.NpcAggression is { } timer)
        {
            CrimewatchPersistence.Npc(world, after.PilotId, after.SystemId, timer.ActiveUntil);
        }
        foreach (var party in after.Contributions.Values)
        {
            if (before.Contributions.GetValueOrDefault(party.EntityId) == party)
            {
                continue;
            }
            var row =
                world.Set<SavedCombatDamage>().Find(after.ShipId, party.EntityId)
                ?? world.Add(new SavedCombatDamage { VictimId = after.ShipId, AttackerId = party.EntityId }).Entity;
            row.PartyJson = JsonSerializer.Serialize(party with { Damage = 0 });
            row.Damage = party.Damage;
        }
    }

    private static void Cooldown(WorldAction world, long module, long ready)
    {
        var row = world.Set<ModuleCooldown>().Find(module) ?? world.Add(new ModuleCooldown { ModuleId = module }).Entity;
        row.ReadyAt = ready;
        row.Kind = ModuleCooldownKind.Cycle;
    }

    internal static void ProjectNpc(WorldAction world, LiveCombatNpc before, LiveCombatNpc after)
    {
        var npc = world.Set<WorldNpc>().Find(after.Id) ?? throw new InvalidDataException("The participating NPC no longer exists.");
        if (before.Health != after.Health)
        {
            npc.ShieldCharge = after.Health.Remaining.Shield;
            npc.ArmorDamage = after.Health.ArmorDamage ?? after.Health.Defense.Armor - after.Health.Remaining.Armor;
            npc.StructureDamage = after.Health.StructureDamage ?? after.Health.Defense.Structure - after.Health.Remaining.Structure;
            npc.UpdatedAt = after.Health.UpdatedAt;
        }
        (npc.X, npc.Y, npc.Z) = (after.Position.X, after.Position.Y, after.Position.Z);
        (npc.VelocityX, npc.VelocityY, npc.VelocityZ) = (after.Velocity.X, after.Velocity.Y, after.Velocity.Z);
        npc.Navigation = after.Navigation;
        npc.MotionAt = after.MotionAt;
        npc.ShieldRepairAt = after.RepairState.ShieldReadyAt;
        npc.ArmorRepairAt = after.RepairState.ArmorReadyAt;
        npc.RepairRevision = after.RepairState.Revision;
        foreach (var party in after.Contributions.Values)
        {
            if (before.Contributions.GetValueOrDefault(party.EntityId) == party)
            {
                continue;
            }
            var row =
                world.Set<SavedCombatDamage>().Find(after.Id, party.EntityId)
                ?? world.Add(new SavedCombatDamage { VictimId = after.Id, AttackerId = party.EntityId }).Entity;
            row.PartyJson = JsonSerializer.Serialize(party with { Damage = 0 });
            row.Damage = party.Damage;
        }
    }
}
