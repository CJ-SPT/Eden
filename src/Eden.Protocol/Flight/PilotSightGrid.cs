using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Ships;

namespace Eden.Protocol.Flight;

// Reads sample bounded neighboring cells. Ships that stay in their cell need no index write.
internal sealed class PilotSightGrid
{
    private readonly record struct Cell(int System, long X, long Y, long Z);

    private sealed class Occupants
    {
        internal readonly List<long> Ids = [];
        internal readonly Dictionary<long, int> Slots = [];
    }

    private const int SamplesPerCell = 16;
    private const int MaximumSightings = 12;
    private readonly Dictionary<Cell, Occupants> _cells = [];
    private readonly object _gate = new();

    private static Cell? Key(int system, SpacePosition? position) =>
        position is null ? null : new(system, Coordinate(position.X), Coordinate(position.Y), Coordinate(position.Z));

    private static long Coordinate(double meters) => (long)Math.Floor(meters / NpcEncounterDefinitions.VisibilityRange);

    internal void Move(long id, int oldSystem, SpacePosition? before, int system, SpacePosition? after)
    {
        var old = Key(oldSystem, before);
        var next = Key(system, after);
        if (old == next)
        {
            return;
        }
        lock (_gate)
        {
            if (old is { } previous && _cells.TryGetValue(previous, out var from) && from.Slots.Remove(id, out int slot))
            {
                long last = from.Ids[^1];
                from.Ids[slot] = last;
                from.Ids.RemoveAt(from.Ids.Count - 1);
                if (last != id)
                {
                    from.Slots[last] = slot;
                }
                if (from.Ids.Count == 0)
                {
                    _cells.Remove(previous);
                }
            }
            if (next is { } destination)
            {
                if (!_cells.TryGetValue(destination, out var to))
                {
                    _cells.Add(destination, to = new());
                }
                if (to.Slots.TryAdd(id, to.Ids.Count))
                {
                    to.Ids.Add(id);
                }
            }
        }
    }

    internal PilotSighting[] Observe(IReadOnlyDictionary<long, ShipContact> ships, int system, long own, SpacePosition origin, long sample)
    {
        var cell = Key(system, origin)!.Value;
        List<PilotSighting>? seen = null;
        lock (_gate)
        {
            for (int x = -1; x <= 1; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int z = -1; z <= 1; z++)
                    {
                        if (!_cells.TryGetValue(new(system, cell.X + x, cell.Y + y, cell.Z + z), out var occupants))
                        {
                            continue;
                        }
                        int count = occupants.Ids.Count;
                        int start = (int)(TravelerRoutineRules.Mix(unchecked((ulong)(sample + own))) % (ulong)count);
                        for (int i = 0; i < Math.Min(count, SamplesPerCell); i++)
                        {
                            long id = occupants.Ids[(start + i) % count];
                            if (
                                id == own
                                || !ships.TryGetValue(id, out var ship)
                                || ship.Pilot.SystemId != system
                                || ship.Pilot.Position is not { } at
                                || ship.Cloaked
                                || ship.Arriving
                                || ship.DepartureGate is not null
                                || ship.Warp is not null
                                || ship.Health?.Remaining.Destroyed == true
                                || origin.DistanceTo(at) > NpcEncounterDefinitions.VisibilityRange
                            )
                            {
                                continue;
                            }
                            var pilot = ship.Pilot;
                            (seen ??= []).Add(
                                new(
                                    pilot.PilotId,
                                    id,
                                    pilot.ShipTypeId,
                                    (HullClass)pilot.ShipGroupId,
                                    pilot.CorpId,
                                    pilot.AllianceId,
                                    at,
                                    ship.Velocity,
                                    pilot.Flight.ShipRadius,
                                    ship.Camp
                                )
                            );
                        }
                    }
                }
            }
        }
        return seen is null ? [] : seen.OrderBy(s => origin.DistanceTo(s.Position)).ThenBy(s => s.ShipId).Take(MaximumSightings).ToArray();
    }
}
