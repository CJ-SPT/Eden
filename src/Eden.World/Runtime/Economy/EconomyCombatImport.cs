using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Docking;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Repair;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Combat;
using Eden.World.Combat;
using Eden.World.Runtime.Combat;
using Eden.World.Storage;
using Eden.World.Thermal;

namespace Eden.World.Runtime.Economy;

internal sealed partial class EconomyTransaction
{
    private CombatCheckpoint? _combat;
    private readonly List<(LiveShipHeat Owner, ShipHeatProjection Projection)> _heat = [];

    internal void ImportCombat(TransactionParticipants? participants)
    {
        // Capture only the participating ships. System-wide NPC/drone advancement
        // remains with its owner; it is not copied for every economic action.
        var ships = participants is null ? null : new TransactionParticipants(pilots: participants.Pilots, ships: participants.Ships);
        _combat = owner.Combat?.CaptureCheckpoint(ships);
        var heatShips = participants is null
            ? []
            : participants
                .Ships.Concat(participants.Pilots.Select(p => store.Peek<Eden.Data.World.Characters.Pilot>(p)?.ActiveShipId ?? 0))
                .Where(id => id > 0)
                .Distinct()
                .ToArray();
        foreach (long ship in heatShips)
        {
            var heatOwner = owner.HeatForKnownShip(ship);
            var projection = heatOwner.CaptureOwn()?.Ships.SingleOrDefault(s => s.ShipId == ship);
            if (projection is null)
            {
                continue;
            }
            _heat.Add((heatOwner, projection));
            foreach (var module in projection.State.Modules.Where(m => m.Condition.Damage > 0))
            {
                var row =
                    Set<SavedModuleCondition>().Find(module.ItemId) ?? Add(new SavedModuleCondition { ModuleId = module.ItemId }).Entity;
                row.Damage = module.Condition.Damage;
            }
            var saved = Set<SavedShipHeat>().Find(ship) ?? Add(new SavedShipHeat { ShipId = ship }).Entity;
            saved.StateJson = ShipHeatPersistence.Encode(projection.State);
        }
        if (_combat is null)
        {
            return;
        }
        foreach (var change in _combat.Ships)
        {
            var before = change.Before;
            var after = change.After;
            if (before.Capacitor != after.Capacitor || before.CapacitorAt != after.CapacitorAt)
            {
                var energy = Set<ShipEnergy>().Find(after.ShipId) ?? Add(new ShipEnergy { ShipId = after.ShipId }).Entity;
                energy.Charge = after.Capacitor;
                energy.UpdatedAt = after.CapacitorAt;
            }
            if (before.Health != after.Health)
            {
                var health = Set<ShipHealthState>().Find(after.ShipId) ?? Add(new ShipHealthState { ShipId = after.ShipId }).Entity;
                health.ShieldCharge = after.Health.Remaining.Shield;
                health.ArmorDamage = after.Health.ArmorDamage ?? after.Health.Defense.Armor - after.Health.Remaining.Armor;
                health.StructureDamage = after.Health.StructureDamage ?? after.Health.Defense.Structure - after.Health.Remaining.Structure;
                health.UpdatedAt = after.Health.UpdatedAt;
            }
            foreach (var (module, ready) in after.LocalCooldowns)
            {
                if (before.LocalCooldowns.GetValueOrDefault(module) != ready)
                {
                    Cooldown(module, ready);
                }
            }
            foreach (var (module, turret) in after.Turrets)
            {
                var previous = before.Turrets[module];
                if (previous.ReadyAt != turret.ReadyAt)
                {
                    Cooldown(module, turret.ReadyAt);
                }
                if (
                    turret.ChargeItemId is long charge
                    && (previous.Quantity != turret.Quantity || previous.CrystalDamage != turret.CrystalDamage)
                )
                {
                    if (turret.Quantity == 0)
                    {
                        Set<OwnedItem>().Remove(new OwnedItem { Id = charge });
                    }
                    else if (Set<OwnedItem>().Find(charge) is { } item)
                    {
                        item.Quantity = turret.Quantity;
                        item.CrystalDamage = turret.CrystalDamage;
                    }
                }
            }
            foreach (var repair in after.Repairs.Where(r => !before.Repairs.Contains(r)))
            {
                var row =
                    Set<PendingLocalRepair>().Find(repair.ModuleId) ?? Add(new PendingLocalRepair { ModuleId = repair.ModuleId }).Entity;
                row.ShipId = after.ShipId;
                row.StartedAt = repair.StartedAt;
                row.CompletesAt = repair.CompletesAt;
                row.Amount = repair.Amount;
                row.Layer = repair.Layer;
            }
            foreach (var repair in before.Repairs.Where(r => after.Repairs.All(current => current.ModuleId != r.ModuleId)))
            {
                if (Set<PendingLocalRepair>().Find(repair.ModuleId) is { } row)
                {
                    Remove(row);
                }
            }
            if (before.NpcAggression != after.NpcAggression && after.NpcAggression is { } timer)
            {
                CrimewatchPersistence.Npc(this, after.PilotId, after.SystemId, timer.ActiveUntil);
            }
            foreach (var party in after.Contributions.Values)
            {
                if (before.Contributions.GetValueOrDefault(party.EntityId) == party)
                {
                    continue;
                }
                var row =
                    Set<SavedCombatDamage>().Find(after.ShipId, party.EntityId)
                    ?? Add(new SavedCombatDamage { VictimId = after.ShipId, AttackerId = party.EntityId }).Entity;
                row.PartyJson = JsonSerializer.Serialize(party with { Damage = 0 });
                row.Damage = party.Damage;
            }
        }
        foreach (var position in _combat.Positions)
        {
            var row = Positions.Find(position.ShipId) ?? Add(new FlightPosition { ShipId = position.ShipId }).Entity;
            // Coordinates belong to movement; only an explicit travel command can
            // change membership. A retained movement baseline may predate a gate transfer.
            (row.X, row.Y, row.Z) = (position.Position.X, position.Position.Y, position.Position.Z);
        }
    }

    private void Cooldown(long module, long ready)
    {
        var row = Set<ModuleCooldown>().Find(module) ?? Add(new ModuleCooldown { ModuleId = module }).Entity;
        row.ReadyAt = ready;
        row.Kind = ModuleCooldownKind.Cycle;
    }
}
