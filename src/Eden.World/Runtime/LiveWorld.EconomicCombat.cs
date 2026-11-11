using Eden.World.Runtime.Combat;

namespace Eden.World.Runtime;

internal sealed partial class LiveWorld
{
    internal void AcknowledgeEconomicCombat(CombatCheckpoint checkpoint)
    {
        _shared.Combat?.AcknowledgeEconomicCombat(checkpoint);
        var systems = checkpoint
            .Ships.Select(c => c.After.SystemId)
            .Concat(checkpoint.Npcs.Select(c => c.After.SystemId))
            .Concat(checkpoint.Positions.Select(p => _shipSystems.GetValueOrDefault(p.ShipId)))
            .Where(s => s > 0)
            .Distinct();
        foreach (var entry in _systems.Existing(systems))
        {
            using var ownership = entry.Enter("economic combat publication");
            entry.State.Combat?.AcknowledgeEconomicCombat(checkpoint);
        }
    }
}
