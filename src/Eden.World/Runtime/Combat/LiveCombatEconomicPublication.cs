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
        foreach (var change in checkpoint.Npcs)
        {
            if (_npcs.GetValueOrDefault(change.After.Id) == change.After)
            {
                _savedNpcs[change.After.Id] = change.After;
                _dirtyNpcs.Remove(change.After.Id);
            }
        }
        foreach (var aggression in checkpoint.Aggressions)
        {
            if (_aggressions?.GetValueOrDefault(aggression.NpcId) == aggression)
            {
                _dirtyAggressions.Remove(aggression.NpcId);
            }
        }
        foreach (var drone in checkpoint.Drones)
        {
            if (_liveDrones.GetValueOrDefault(drone.Contact.Id) == drone)
            {
                _dirtyDrones.Remove(drone.Contact.Id);
            }
        }
        foreach (var trigger in checkpoint.DroneTriggers)
        {
            if (_droneCombatTriggers.GetValueOrDefault(trigger.ShipId) == trigger)
            {
                _dirtyDroneCombatTriggers.Remove(trigger.ShipId);
            }
        }
        foreach (var timer in checkpoint.DroneTimers)
        {
            if (_droneTimers.GetValueOrDefault(timer.PilotId) == timer)
            {
                _droneTimers.Remove(timer.PilotId);
                foreach (long ship in _dronePlayers.Where(p => p.Value?.PilotId == timer.PilotId).Select(p => p.Key).ToArray())
                {
                    _dronePlayers.Remove(ship);
                }
            }
        }
        foreach (var engagement in checkpoint.DroneEngagements)
        {
            var key = (engagement.LowerPilotId, engagement.HigherPilotId);
            if (_droneEngagements.GetValueOrDefault(key) == engagement)
            {
                _droneEngagements.Remove(key);
            }
        }
        foreach (var hostility in checkpoint.DroneHostilities)
        {
            var key = (hostility.SourceShipId, hostility.TargetShipId);
            if (_droneHostilities.GetValueOrDefault(key) == hostility)
            {
                _droneHostilities.Remove(key);
                _residentAttackers.GetValueOrDefault(hostility.TargetShipId)?.Remove(hostility.SourceShipId);
                _droneAttackers.Remove(hostility.TargetShipId);
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
