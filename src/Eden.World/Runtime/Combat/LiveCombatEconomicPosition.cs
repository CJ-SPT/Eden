using Eden.Data.World.Docking;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Flight;

namespace Eden.World.Runtime.Combat;

internal sealed partial class LiveCombatRuntime
{
    private void ObserveEconomicPosition(long id)
    {
        var row = owner.Economy.Peek<FlightPosition>(id);
        var hull = owner.Economy.Peek<Ship>(id);
        int? destination = row?.SystemId ?? (hull is null ? null : database.Catalog.FindStation(hull.StationId)?.SolarSystemId);
        SpacePosition? position = row is null ? null : new(row.X, row.Y, row.Z);
        if (_shipPilots.TryGetValue(id, out long pilot) && _ships.TryGetValue(pilot, out var ship))
        {
            _ships[pilot] = ship with { Position = position, SystemId = destination ?? ship.SystemId };
            if (_savedShips.TryGetValue(pilot, out var saved))
            {
                // Only position moves to the economic baseline. Keep all unrelated
                // differences until their owning combat projection is captured.
                _savedShips[pilot] = saved with
                {
                    Position = position,
                    SystemId = destination ?? saved.SystemId,
                };
            }
        }
        bool belongs = systemId is null || destination == systemId;
        if (position is not null && belongs && (_positions.ContainsKey(id) || _shipPilots.ContainsKey(id)))
        {
            var current = new LivePosition(row!.SystemId, position);
            _positions[id] = current;
            _savedPositions[id] = current;
        }
        else
        {
            _positions.Remove(id);
            _savedPositions.Remove(id);
        }
        _dirtyPositions.Remove(id);
        _acquisitionShips.Clear();
        _acquisitions.Clear();
    }
}
