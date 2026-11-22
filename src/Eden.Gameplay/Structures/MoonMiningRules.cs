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

public enum MoonExtractionPhase
{
    Midway,
    Stopped,
    Ready,
    Decay,
}

public interface IMoonExtractionGameMaster
{
    MoonExtraction AdvanceExtraction(long pilotId, long structureId, MoonExtractionPhase phase);
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

    // EVE.js 261108.1 reference uses distinct previews just before eligibility and auto-fracture.
    public static MoonExtraction Advance(MoonExtraction extraction, MoonExtractionPhase phase, long now)
    {
        long shift = Math.Min(
            0,
            phase switch
            {
                MoonExtractionPhase.Midway => now - (extraction.StartedAt + (extraction.ReadyAt - extraction.StartedAt) / 2),
                MoonExtractionPhase.Stopped => now + TimeSpan.TicksPerMinute - extraction.ReadyAt,
                MoonExtractionPhase.Ready => now - extraction.ReadyAt,
                MoonExtractionPhase.Decay => now + 5 * TimeSpan.TicksPerSecond - extraction.AutoFractureAt,
                _ => throw new InvalidDataException("Unknown moon extraction phase."),
            }
        );
        return extraction with
        {
            StartedAt = extraction.StartedAt + shift,
            ReadyAt = extraction.ReadyAt + shift,
            AutoFractureAt = extraction.AutoFractureAt + shift,
        };
    }

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

    // Build 3662168 client/types: moon ore IDs are not contiguous after Pollucite.
    // In particular, 45505 and 45507-45509 are SKINs rather than asteroids.
    private enum MoonOre
    {
        Zeolites = 45490,
        Sylvite = 45491,
        Bitumens = 45492,
        Coesite = 45493,
        Cobaltite = 45494,
        Euxenite = 45495,
        Titanite = 45496,
        Scheelite = 45497,
        Otavite = 45498,
        Sperrylite = 45499,
        Vanadinite = 45500,
        Chromite = 45501,
        Carnotite = 45502,
        Zircon = 45503,
        Pollucite = 45504,
        Cinnabar = 45506,
        Xenotime = 45510,
        Monazite = 45511,
        Loparite = 45512,
        Ytterbite = 45513,
    }

    private static readonly MoonOre[][] s_ores =
    [
        [MoonOre.Zeolites, MoonOre.Sylvite, MoonOre.Bitumens, MoonOre.Coesite],
        [MoonOre.Cobaltite, MoonOre.Euxenite, MoonOre.Titanite, MoonOre.Scheelite],
        [MoonOre.Otavite, MoonOre.Sperrylite, MoonOre.Vanadinite, MoonOre.Chromite],
        [MoonOre.Carnotite, MoonOre.Zircon, MoonOre.Pollucite, MoonOre.Cinnabar],
        [MoonOre.Xenotime, MoonOre.Monazite, MoonOre.Loparite, MoonOre.Ytterbite],
    ];

    public static IEnumerable<int> OreTypes => s_ores.SelectMany(tier => tier).Select(ore => (int)ore);

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
        int primary = (int)s_ores[tier][(int)((seed >> 24) % s_ores[tier].Length)];
        int secondary = (int)s_ores[0][(int)((seed >> 12) % s_ores[0].Length)];
        return primary == secondary ? [new(primary, 100)] : [new(primary, 70), new(secondary, 30)];
    }
}
