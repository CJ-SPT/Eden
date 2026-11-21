using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Docking;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Maps;
using Eden.World.Maps;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    internal IReadOnlyDictionary<int, MapPilotCounts> MapPopulation(IReadOnlyList<SelectedHumanPilot> players)
    {
        Dictionary<int, MapPilotCounts> counts = [];
        if (players.Count == 0)
        {
            return counts;
        }
        var pilots = (EconomyTable<Pilot, EconomyPilot>)_tables[typeof(Pilot)];
        var ships = (EconomyTable<Ship, EconomyShip>)_tables[typeof(Ship)];
        var positions = (EconomyTable<FlightPosition, EconomyFlightPosition>)_tables[typeof(FlightPosition)];
        var agents = (EconomyTable<SavedCapsuleerAgent, EconomySavedCapsuleerAgent>)_tables[typeof(SavedCapsuleerAgent)];
        var suspensions = _tables[typeof(SavedCapsuleerSuspension)];
        var catalog = _database.Catalog;
        foreach (var pilot in players.DistinctBy(pilot => pilot.PilotId))
        {
            Count(new(pilot.PilotId));
        }
        if (_database.Settings.Capsuleers.Enabled)
        {
            foreach (var agent in agents.Keys)
            {
                if (!suspensions.Contains(agent))
                {
                    Count(agent);
                }
            }
        }
        return counts;

        void Count(EconomyKey key)
        {
            if (pilots.Snapshot(key) is not { } pilot || ships.Snapshot(new(pilot.ActiveShipId)) is not { } ship)
            {
                return;
            }
            var position = positions.Snapshot(new(ship.Id));
            int system =
                position?.SystemId
                ?? (ship.StructureId is long structure ? _structures.GetValueOrDefault(structure)?.SystemId : null)
                ?? catalog.FindStation(ship.StationId)?.SolarSystemId
                ?? 0;
            if (MapStatistics.IsKnownSpace(system))
            {
                counts[system] = counts.GetValueOrDefault(system).Add(position is null ? new(0, 1) : new(1, 0));
            }
        }
    }
}
