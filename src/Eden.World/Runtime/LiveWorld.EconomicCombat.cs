using Eden.World.Runtime.Combat;

namespace Eden.World.Runtime;

internal sealed partial class LiveWorld
{
    internal void AcknowledgeEconomicCombat(CombatCheckpoint checkpoint)
    {
        _shared.Combat?.AcknowledgeEconomicCombat(checkpoint);
        foreach (var entry in _systems.Existing(CombatSystems(checkpoint)))
        {
            using var ownership = entry.Enter("economic combat publication");
            entry.State.Combat?.AcknowledgeEconomicCombat(checkpoint);
        }
    }

    internal IEnumerable<int> CombatSystems(CombatCheckpoint checkpoint) =>
        checkpoint
            .Ships.Select(c => c.After.SystemId)
            .Concat(checkpoint.Npcs.Select(c => c.After.SystemId))
            .Concat(checkpoint.Drones.Select(d => d.Contact.SystemId))
            .Concat(checkpoint.DroneTimers.Select(t => t.SystemId))
            .Concat(
                checkpoint.DroneEngagements.SelectMany(e =>
                    new[] { _pilotSystems.GetValueOrDefault(e.LowerPilotId), _pilotSystems.GetValueOrDefault(e.HigherPilotId) }
                )
            )
            .Concat(
                checkpoint.DroneHostilities.SelectMany(h =>
                    new[] { _shipSystems.GetValueOrDefault(h.SourceShipId), _shipSystems.GetValueOrDefault(h.TargetShipId) }
                )
            )
            .Concat(checkpoint.DroneTriggers.Select(t => _shipSystems.GetValueOrDefault(t.ShipId)))
            .Concat(checkpoint.Positions.Select(p => _shipSystems.GetValueOrDefault(p.ShipId)))
            .Where(s => s > 0)
            .Distinct();
}
