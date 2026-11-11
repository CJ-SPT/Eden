namespace Eden.World.Runtime.Combat;

internal sealed partial class LiveCombatRuntime
{
    internal void AcknowledgeEconomicCombat(CombatCheckpoint checkpoint)
    {
        foreach (var change in checkpoint.Ships)
        {
            long pilot = change.After.PilotId;
            if (_ships.GetValueOrDefault(pilot) == change.After)
            {
                _savedShips[pilot] = change.After;
                _dirtyShips.Remove(pilot);
            }
        }
        foreach (var position in checkpoint.Positions)
        {
            if (
                _positions.TryGetValue(position.ShipId, out var current)
                && current.Position == position.Position
                && current.System == position.SystemId
            )
            {
                _savedPositions[position.ShipId] = current;
                _dirtyPositions.Remove(position.ShipId);
            }
        }
    }
}
