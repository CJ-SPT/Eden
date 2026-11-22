using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Flight;

public sealed record TranslocationTarget(long Id, FlightSystem System, int? StationId, SpacePosition Position, double Radius)
{
    public long? StructureId { get; init; }
    public bool Docked => StationId.HasValue || StructureId.HasValue;
    public bool Coordinate { get; init; }
}

public sealed record PilotTranslocation(DockedPilot Pilot, InventoryChange ShipChange);

public interface IPilotTranslocation
{
    TranslocationTarget Resolve(long targetId);
    TranslocationTarget ResolveSystem(string name);
    PilotTranslocation Move(long pilotId, long expectedShipId, long targetId);

    // A delayed camera handoff commits the resolved destination snapshot, even
    // when the target has since moved, docked or disappeared.
    PilotTranslocation Move(long pilotId, long expectedShipId, TranslocationTarget target);
}

public static class TranslocationRules
{
    private const int MaximumCompoundClearancePasses = 64;

    // Eden's GM travel policy places ships 10 km outside the destination's surface.
    public const double ArrivalClearance = 10_000;

    public static SpacePosition Arrival(TranslocationTarget target, double shipRadius, IReadOnlyList<StaticSpaceObject> obstacles)
    {
        if (!double.IsFinite(shipRadius) || shipRadius < 0 || !double.IsFinite(target.Radius) || target.Radius < 0)
        {
            throw new InvalidDataException("The destination has invalid collision data.");
        }
        var position = target.Coordinate
            ? target.Position
            : target.Position + new SpacePosition(target.Radius + shipRadius + ArrivalClearance, 0, 0);
        // Moving only along +X passes each intersected body's far surface once.
        // Recheck earlier bodies when another body's clearance overlaps them.
        for (int pass = 0; pass <= Math.Max(obstacles.Count, MaximumCompoundClearancePasses); pass++)
        {
            bool moved = false;
            foreach (var body in obstacles)
            {
                if (!body.Solid)
                {
                    continue;
                }
                if (body.Collision is { } geometry)
                {
                    var local = position - body.Position;
                    var cleared = geometry.ClearAlongRay(local, new(1, 0, 0), shipRadius + ArrivalClearance);
                    if (cleared != local)
                    {
                        position = body.Position + cleared;
                        moved = true;
                    }
                    continue;
                }
                double surface = Math.Max(0, body.Radius) + shipRadius + ArrivalClearance;
                if (position.DistanceTo(body.Position) < surface)
                {
                    position = position with { X = body.Position.X + surface };
                    moved = true;
                }
            }
            if (!moved)
            {
                if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) || !double.IsFinite(position.Z))
                {
                    throw new InvalidDataException("The destination has invalid coordinates.");
                }
                return position;
            }
        }
        throw new InvalidDataException("No clear arrival point is available.");
    }
}
