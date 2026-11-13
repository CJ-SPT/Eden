using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Structures;

public sealed record MoonMiningPoint(int MoonId, SpacePosition Position);

public sealed record MoonOreShare(int TypeId, int Percentage);

public sealed record MoonExtraction(
    long Id,
    long StructureId,
    int MoonId,
    int SystemId,
    int CorporationId,
    long StartedAt,
    long ReadyAt,
    long AutoFractureAt
);

public interface IMoonExtractions
{
    MoonExtraction? Current(long pilotId, long structureId);
    IReadOnlyList<MoonExtraction> CorporationExtractions(long pilotId, int corporationId);
    IReadOnlyList<MoonOreShare> Resources(long pilotId, long structureId);
    MoonExtraction Start(long pilotId, long structureId, long seconds);
    void Cancel(long pilotId, long structureId);
    void Fracture(long pilotId, long structureId);
    IReadOnlyList<MoonMiningObserver> Observers(long pilotId);
    IReadOnlyList<Mining.PersonalMiningLog> Ledger(long pilotId, long structureId);
}

public sealed record MoonMiningObserver(long Id, string Name, int SystemId);

public static class MoonMiningRules
{
    public const int BeaconType = 46329;
    public const int BeaconGroup = 1915;
    public const double PlacementRange = 250_000;
    public const double MiningSecurityLimit = 0.55;
    public const long MinimumExtractionSeconds = 6 * 24 * 60 * 60;
    public const long MaximumExtractionSeconds = 56 * 24 * 60 * 60;
    public const long AutoFractureDelay = 3 * TimeSpan.TicksPerHour;
    public const long FieldLifetime = 2 * TimeSpan.TicksPerDay;
    public const int CubicMetersPerHour = 30_000;
    public const int MaximumRocks = 64;
    public const long FirstRockId = 8_600_000_000_000;
    public const long RockEndId = 8_700_000_000_000;

    public static bool IsMoonRock(long id) => id >= FirstRockId && id < RockEndId;

    public static long ExtractionId(long rockId) => (rockId - FirstRockId) / MaximumRocks;

    public static long RockId(long extractionId, int index)
    {
        long id = checked(FirstRockId + extractionId * MaximumRocks + index);
        if (index is < 0 or >= MaximumRocks || id >= RockEndId)
        {
            throw new InvalidDataException("The moon asteroid identity range is exhausted.");
        }
        return id;
    }

    private static readonly int[][] s_ores =
    [
        [45490, 45491, 45492, 45493],
        [45494, 45495, 45496, 45497],
        [45498, 45499, 45500, 45501],
        [45502, 45503, 45504, 45505],
        [45506, 45507, 45508, 45509],
    ];

    public static int? FindPoint(SpacePosition position, IReadOnlyList<MoonMiningPoint> points) =>
        points
            .Select(point => (point.MoonId, Distance: point.Position.DistanceTo(position)))
            .Where(point => point.Distance <= PlacementRange)
            .OrderBy(point => point.Distance)
            .ThenBy(point => point.MoonId)
            .Select(point => (int?)point.MoonId)
            .FirstOrDefault();

    // Eden composition policy v1. The client catalog supplies the positions and ore types.
    public static IReadOnlyList<MoonOreShare> Composition(int moonId, double security)
    {
        if (moonId <= 0 || !double.IsFinite(security) || security >= MiningSecurityLimit)
        {
            return [];
        }
        uint seed = unchecked((uint)moonId * 2654435761u);
        int tier = security >= StructureRules.HighSecurityBoundary ? 0 : (int)((seed >> 16) % s_ores.Length);
        int primary = s_ores[tier][(int)((seed >> 24) % s_ores[tier].Length)];
        int secondary = s_ores[0][(int)((seed >> 12) % s_ores[0].Length)];
        return primary == secondary ? [new(primary, 100)] : [new(primary, 70), new(secondary, 30)];
    }
}
