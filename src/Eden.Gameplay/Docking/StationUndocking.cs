using Eden.Gameplay.Flight;
using Eden.Gameplay.Ships;

namespace Eden.Gameplay.Docking;

public enum UndockCategory
{
    Any,
    Small,
    Large,
    Capital,
    Supercapital,
}

public sealed record StationUndockPoint(UndockCategory Category, SpacePosition Position, SpacePosition Direction);

public sealed record UndockLaunch(SpacePosition Position, SpacePosition Direction, SpacePosition Velocity);

public static class StationUndocking
{
    // Build 3662168 ballparkCommon/docking.pyj, ShipUndockVelocity.
    private const double VelocityMultiplier = 1.75;
    private const double MaximumLaunchSpeed = 700;
    private const double MinimumClearance = 500;
    private const double FallbackLaneSpacing = 1500;

    public static UndockCategory Category(int shipGroupId) =>
        (HullClass)shipGroupId switch
        {
            HullClass.Freighter
            or HullClass.Hauler
            or HullClass.DeepSpaceTransport
            or HullClass.BlockadeRunner
            or HullClass.JumpFreighter
            or HullClass.Battleship
            or HullClass.EliteBattleship
            or HullClass.BlackOps
            or HullClass.Marauder
            or HullClass.IndustrialCommand => UndockCategory.Large,
            HullClass.Carrier
            or HullClass.CommandCarrier
            or HullClass.ForceAuxiliary
            or HullClass.Dreadnought
            or HullClass.LancerDreadnought
            or HullClass.CapitalIndustrial => UndockCategory.Capital,
            HullClass.Titan or HullClass.Supercarrier => UndockCategory.Supercapital,
            _ => UndockCategory.Small,
        };

    public static StationUndockPoint? SelectPoint(long shipId, int shipGroupId, IReadOnlyList<StationUndockPoint> points)
    {
        var candidates = points.Where(point => point.Category == Category(shipGroupId)).ToArray();
        if (candidates.Length == 0)
        {
            candidates = points.ToArray();
        }
        return candidates.Length > 0 ? candidates[(int)((ulong)shipId % (uint)candidates.Length)] : null;
    }

    public static UndockLaunch Launch(long shipId, PilotStation station, FlightProperties hull)
    {
        // Native GetUndockDirection falls back to local origin and +Z when metadata is absent.
        var selected =
            station.UndockPoint
            ?? new StationUndockPoint(UndockCategory.Any, new(shipId % 2 == 0 ? FallbackLaneSpacing : 0, 0, 0), new(0, 0, 1));
        var direction = selected.Direction.Unit;
        var offset = selected.Position;
        // Station collision currently uses the catalog's enclosing sphere. Extend the real exit ray
        // past that sphere, retaining its lateral locator offset and room to accelerate into warp.
        double clearance = Math.Max(MinimumClearance, hull.MaxSpeed * hull.InertiaTime);
        double radius = hull.StationRadius + hull.ShipRadius + clearance;
        double along = offset.Dot(direction);
        double discriminant = along * along + radius * radius - offset.Dot(offset);
        if (discriminant >= 0)
        {
            offset += direction * Math.Max(0, -along + Math.Sqrt(discriminant));
        }
        var velocity = direction * Math.Min(hull.MaxSpeed * VelocityMultiplier, MaximumLaunchSpeed);
        return new(station.Position + offset, direction, velocity);
    }
}
