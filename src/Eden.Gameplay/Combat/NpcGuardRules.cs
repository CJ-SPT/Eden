using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Combat;

public enum NpcEmpireFaction
{
    Caldari = 500001,
    Minmatar = 500002,
    Amarr = 500003,
    Gallente = 500004,
}

// Explicit build-3662168 navy hulls and customs officials; their behavior here
// is local defense, without mission scripting or police enforcement.
public enum NpcGuardType
{
    ImperialSergeant = 9998,
    ImperialOmen = 12439,
    AmarrCustoms = 19372,
    CaldariCaptain = 10046,
    CaldariMoa = 15320,
    CaldariCustoms = 19385,
    FederationSergeant = 10052,
    FederationThorax = 15325,
    GallenteCustoms = 19383,
    RepublicPrivate = 10058,
    RepublicRupture = 15324,
    MinmatarCustoms = 19388,
}

public sealed record NpcGuardRoster(NpcGuardType Frigate, NpcGuardType Cruiser, NpcGuardType Customs);

public sealed record NpcGuardObstacle(SpacePosition Position, double Radius);

public static class NpcGuardRules
{
    public const double ReactionMilliseconds = 2500;
    public const double PatrolRadius = 3000;
    public const double AnchorClearance = 15000;
    public const double FormationMargin = 6000;
    public const int PatrolLegSeconds = 60;

    public static NpcGuardRoster? Roster(int factionId) =>
        (NpcEmpireFaction)factionId switch
        {
            NpcEmpireFaction.Amarr => new(NpcGuardType.ImperialSergeant, NpcGuardType.ImperialOmen, NpcGuardType.AmarrCustoms),
            NpcEmpireFaction.Caldari => new(NpcGuardType.CaldariCaptain, NpcGuardType.CaldariMoa, NpcGuardType.CaldariCustoms),
            NpcEmpireFaction.Gallente => new(NpcGuardType.FederationSergeant, NpcGuardType.FederationThorax, NpcGuardType.GallenteCustoms),
            NpcEmpireFaction.Minmatar => new(NpcGuardType.RepublicPrivate, NpcGuardType.RepublicRupture, NpcGuardType.MinmatarCustoms),
            _ => null,
        };

    public static SpacePosition PatrolDestination(SpacePosition home, long npcId, long now)
    {
        long phase = (now / TimeSpan.TicksPerSecond / PatrolLegSeconds + npcId % 4) % 4;
        var offset = phase switch
        {
            0 => new SpacePosition(PatrolRadius, 0, 0),
            1 => new(0, 0, PatrolRadius),
            2 => new(-PatrolRadius, 0, 0),
            _ => new(0, 0, -PatrolRadius),
        };
        return home + offset;
    }

    public static double DistanceToLane(SpacePosition point, StationUndockPoint lane)
    {
        var offset = point - lane.Position;
        return (offset - lane.Direction.Unit * Math.Max(0, offset.Dot(lane.Direction.Unit))).Length;
    }

    public static SpacePosition? Home(
        SpacePosition anchor,
        double radius,
        IReadOnlyList<StationUndockPoint> lanes,
        IReadOnlyList<NpcGuardObstacle> obstacles
    )
    {
        double margin = PatrolRadius + FormationMargin;
        SpacePosition? best = null;
        double bestScore = double.NegativeInfinity;
        for (int x = -1; x <= 1; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                for (int z = -1; z <= 1; z++)
                {
                    if (x == 0 && y == 0 && z == 0)
                    {
                        continue;
                    }
                    var local = new SpacePosition(x, y, z).Unit * (radius + AnchorClearance + margin);
                    var point = anchor + local;
                    if (obstacles.Any(o => point.DistanceTo(o.Position) < o.Radius + margin))
                    {
                        continue;
                    }
                    double score = lanes.Count == 0 ? AnchorClearance : lanes.Min(lane => DistanceToLane(local, lane));
                    if (score > bestScore && score >= margin)
                    {
                        best = point;
                        bestScore = score;
                    }
                }
            }
        }
        return best;
    }
}
