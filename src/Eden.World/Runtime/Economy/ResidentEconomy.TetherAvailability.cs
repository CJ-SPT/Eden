using System.Collections.Immutable;
using Eden.Data.World.Characters;
using Eden.Data.World.Docking;
using Eden.Data.World.Structures;
using Eden.World.Runtime.Combat;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private sealed record TetherAvailability(
        ImmutableDictionary<long, long> ActiveShips,
        ImmutableDictionary<long, int> FlightSystems,
        ImmutableHashSet<int> StructureSystems,
        ImmutableHashSet<long> TetheredShips
    );

    private TetherAvailability? _tetherAvailability;

    internal bool TetherUnavailable(long pilot, bool inSpace) =>
        !_owner.HasPersistenceOwnership
        && _loaded
        && Volatile.Read(ref _tetherAvailability) is { } state
        && state.ActiveShips.TryGetValue(pilot, out long ship)
        && !state.TetheredShips.Contains(ship)
        && (!inSpace || state.FlightSystems.TryGetValue(ship, out int system) && !state.StructureSystems.Contains(system));

    internal bool TetherProtected(long ship, long now)
    {
        if (
            !_owner.HasPersistenceOwnership
            && _loaded
            && Volatile.Read(ref _tetherAvailability) is { } state
            && !state.TetheredShips.Contains(ship)
        )
        {
            return false;
        }
        return Inspect(world => world.TetherProtected(ship, now));
    }

    private EconomyTable<Pilot, EconomyPilot> TetherPilots => (EconomyTable<Pilot, EconomyPilot>)_tables[typeof(Pilot)];
    private EconomyTable<FlightPosition, EconomyFlightPosition> TetherPositions =>
        (EconomyTable<FlightPosition, EconomyFlightPosition>)_tables[typeof(FlightPosition)];

    private ImmutableHashSet<int> TetherStructureSystems() =>
        _structures.Values.Select(structure => structure.SystemId).ToImmutableHashSet();

    private ImmutableHashSet<long> TetheredShips() =>
        ((EconomyTable<SavedPilotTether, EconomyPilotTether>)_tables[typeof(SavedPilotTether)])
            .Keys.Select(key => key.Id)
            .ToImmutableHashSet();

    private void LoadTetherAvailability() =>
        Volatile.Write(
            ref _tetherAvailability,
            new(
                TetherPilots.Keys.ToImmutableDictionary(key => key.Id, key => TetherPilots.Snapshot(key)!.ActiveShipId),
                TetherPositions
                    .Keys.Select(key => TetherPositions.Snapshot(key)!)
                    .Where(position => position.SystemId.HasValue)
                    .ToImmutableDictionary(position => position.ShipId, position => position.SystemId!.Value),
                TetherStructureSystems(),
                TetheredShips()
            )
        );

    private void ObserveTetherAvailability(IReadOnlyCollection<CombatRowChange> changes, bool resident)
    {
        if (Volatile.Read(ref _tetherAvailability) is not { } before)
        {
            return;
        }
        if (!resident && changes.Any(change => change.Table == "WorldPositionKey"))
        {
            LoadTetherAvailability();
            return;
        }

        var ships = before.ActiveShips;
        var systems = before.FlightSystems;
        bool structuresChanged = false;
        bool tethersChanged = false;
        foreach (var change in changes)
        {
            switch (change.Table)
            {
                case "WorldPilot":
                    ships = TetherPilots.Snapshot(new(change.Id)) is { } pilot
                        ? ships.SetItem(change.Id, pilot.ActiveShipId)
                        : ships.Remove(change.Id);
                    break;
                case "WorldPositionMembership":
                case "WorldPosition" when !resident:
                    systems = TetherPositions.Snapshot(new(change.Id))?.SystemId is int system
                        ? systems.SetItem(change.Id, system)
                        : systems.Remove(change.Id);
                    break;
                case "WorldStructure":
                    structuresChanged = true;
                    break;
                case "WorldPilotTether":
                    tethersChanged = true;
                    break;
            }
        }
        if (ships != before.ActiveShips || systems != before.FlightSystems || structuresChanged || tethersChanged)
        {
            // Readers need a single committed image of membership, structure presence and persisted protection.
            Volatile.Write(
                ref _tetherAvailability,
                new(
                    ships,
                    systems,
                    structuresChanged ? TetherStructureSystems() : before.StructureSystems,
                    tethersChanged ? TetheredShips() : before.TetheredShips
                )
            );
        }
    }
}
