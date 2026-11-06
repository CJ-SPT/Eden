using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Combat;

public sealed record NpcConvoyRoute(SpacePosition Near, SpacePosition Far);

public static class NpcConvoyRules
{
    // Build 3662168 group 297 contains civilian convoy haulers. Group 298
    // contains armed escorts and is outside this passive traffic policy.
    public const int GroupId = 297;
    public const int Members = 2;
    public const double MemberSpacing = 3000;
    public const double LegDistance = 30000;
    public const int CooldownSeconds = 600;
    private const double SettlingInertiaTimes = 8;

    // Static anchor IDs are positive. A negative station ID gives traffic its
    // own encounter without sharing the station navy's slots or hostility.
    public static long EncounterId(int stationId) => -(long)stationId;

    public static bool Eligible(double security) => NpcSpawnRules.SecurityBand(security) is NpcSecurityBand.High or NpcSecurityBand.Safest;

    public static SpacePosition Start(NpcConvoyRoute route, int generation) => generation % 2 == 0 ? route.Near : route.Far;

    public static SpacePosition Destination(NpcConvoyRoute route, int generation) => generation % 2 == 0 ? route.Far : route.Near;

    public static SpacePosition FormationOffset(int slot) => new(0, slot * MemberSpacing, 0);

    public static double DurationSeconds(double mass, IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        Math.Ceiling(
            LegDistance / (attributes.GetValueOrDefault(DogmaAttributeId.MaxVelocity) * NpcMotionRules.SpeedFraction(attributes))
                + SettlingInertiaTimes * mass * NpcMotionRules.Agility(attributes) / NpcEncounterDefinitions.InertiaMassScale
        );

    public static NpcConvoyRoute? Route(
        SpacePosition station,
        double radius,
        double memberRadius,
        IReadOnlyList<StationUndockPoint> lanes,
        IReadOnlyList<NpcGuardObstacle> obstacles
    )
    {
        var near = NpcGuardRules.Home(station, radius, lanes, obstacles);
        if (near is null)
        {
            return null;
        }
        var far = near + (near - station).Unit * LegDistance;
        for (int slot = 0; slot < Members; slot++)
        {
            var start = near + FormationOffset(slot);
            var end = far + FormationOffset(slot);
            if (obstacles.Any(o => DistanceToSegment(o.Position, start, end) <= o.Radius + memberRadius))
            {
                return null;
            }
            foreach (var lane in lanes)
            {
                if (DistanceToLane(start - station, end - station, lane) < memberRadius + MemberSpacing)
                {
                    return null;
                }
            }
        }
        return new(near, far);
    }

    public static double DistanceToLane(SpacePosition start, SpacePosition end, StationUndockPoint lane)
    {
        var direction = lane.Direction.Unit;
        var offset = start - lane.Position;
        var segment = end - start;
        var perpendicular = segment - direction * segment.Dot(direction);
        var radial = offset - direction * offset.Dot(direction);
        double square = perpendicular.Dot(perpendicular);
        double closest = square == 0 ? 0 : Math.Clamp(-radial.Dot(perpendicular) / square, 0, 1);
        // In front of the exit, minimize distance to its infinite line. Behind
        // it, the closest point is the ray origin; endpoints bound both pieces.
        return Math.Min(
            DistanceToSegment(lane.Position, start, end),
            Math.Min(
                NpcGuardRules.DistanceToLane(start + segment * closest, lane),
                Math.Min(NpcGuardRules.DistanceToLane(start, lane), NpcGuardRules.DistanceToLane(end, lane))
            )
        );
    }

    public static double DistanceToSegment(SpacePosition point, SpacePosition start, SpacePosition end)
    {
        var segment = end - start;
        double square = segment.Dot(segment);
        double fraction = square == 0 ? 0 : Math.Clamp((point - start).Dot(segment) / square, 0, 1);
        return point.DistanceTo(start + segment * fraction);
    }
}
