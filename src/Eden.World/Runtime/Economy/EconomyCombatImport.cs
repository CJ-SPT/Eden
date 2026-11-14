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
        var live = owner ?? throw new InvalidOperationException("Combat import requires a live world.");
        // Capture named participants and resources, without copying every NPC in their systems.
        var ships = participants is null
            ? null
            : new TransactionParticipants(pilots: participants.Pilots, ships: participants.Ships, resources: participants.Resources);
        _combat = live.Combat?.CaptureCheckpoint(ships);
        var heatShips = participants is null
            ? []
            : participants
                .Ships.Concat(participants.Pilots.Select(p => store.Peek<Eden.Data.World.Characters.Pilot>(p)?.ActiveShipId ?? 0))
                .Where(id => id > 0)
                .Distinct()
                .ToArray();
        foreach (long ship in heatShips)
        {
            var heatOwner = live.HeatForKnownShip(ship);
            var projection = heatOwner.CaptureShip(ship);
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
        if (_combat is not null)
        {
            StageCombat(_combat);
        }
    }

    internal void ImportCheckpoint(CombatCheckpoint checkpoint)
    {
        if (_combat?.HasChanges == true)
        {
            throw new InvalidOperationException("The private action already imported participating combat.");
        }
        _combat = checkpoint;
        StageCombat(checkpoint);
    }

    private void StageCombat(CombatCheckpoint checkpoint)
    {
        LiveCombatRuntime.ProjectDrones(this, checkpoint.Drones);
        LiveCombatRuntime.ProjectDroneTriggers(this, checkpoint.DroneTriggers);
        LiveCombatRuntime.ProjectDronePlayers(this, checkpoint.DroneTimers, checkpoint.DroneEngagements, checkpoint.DroneHostilities);
        foreach (var change in checkpoint.Ships)
        {
            LiveCombatRuntime.ProjectShip(this, change.Before, change.After);
        }
        foreach (var change in checkpoint.Npcs)
        {
            LiveCombatRuntime.ProjectNpc(this, change.Before, change.After);
        }
        foreach (var change in checkpoint.Aggressions)
        {
            var row =
                Set<Eden.Data.World.Combat.NpcAggression>().Find(change.NpcId)
                ?? Add(new Eden.Data.World.Combat.NpcAggression { NpcId = change.NpcId }).Entity;
            row.TargetShipId = change.ShipId;
            row.ReadyAt = change.ReadyAt;
            row.MissileReadyAt = change.MissileReadyAt;
            row.LastShotAt = change.LastShotAt;
            row.LastTargetId = change.LastTargetId;
        }
        foreach (var position in checkpoint.Positions)
        {
            var row = Positions.Find(position.ShipId) ?? Add(new FlightPosition { ShipId = position.ShipId }).Entity;
            // Coordinates belong to movement; only an explicit travel command can
            // change membership. A retained movement baseline may predate a gate transfer.
            (row.X, row.Y, row.Z) = (position.Position.X, position.Position.Y, position.Position.Z);
        }
    }
}
