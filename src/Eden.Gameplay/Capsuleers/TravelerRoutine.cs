namespace Eden.Gameplay.Capsuleers;

public enum TravelPace
{
    Brisk,
    Regular,
    Leisurely,
}

public sealed record TravelerThreat(int SystemId, long ExpiresAt);

public sealed record TravelerRoutine(ulong Seed, TravelPace Pace)
{
    public int[] RecentStations { get; init; } = [];
    public TravelerThreat[] Threats { get; init; } = [];
    public bool ShelterPending { get; init; }
}

public static class TravelerRoutineRules
{
    public const int RememberedStations = 3;
    public const int RememberedThreats = 4;
    public static readonly TimeSpan ThreatMemory = TimeSpan.FromMinutes(30);
    private const int MinimumShelterSeconds = 300;
    private const int ShelterSpreadSeconds = 601;

    public static ulong Mix(ulong value)
    {
        value = unchecked((value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL);
        value = unchecked((value ^ (value >> 27)) * 0x94D049BB133111EBUL);
        return value ^ (value >> 31);
    }

    public static TravelerRoutine Create(long pilotId)
    {
        ulong seed = Mix(unchecked((ulong)pilotId + 0x9E3779B97F4A7C15UL));
        var pace = (seed % 6) switch
        {
            < 2 => TravelPace.Brisk,
            < 5 => TravelPace.Regular,
            _ => TravelPace.Leisurely,
        };
        return new(seed, pace);
    }

    public static TravelerRoutine Arrive(TravelerRoutine routine, int station) =>
        routine with
        {
            RecentStations = routine.RecentStations.Prepend(station).Distinct().Take(RememberedStations).ToArray(),
        };

    public static TravelerRoutine Threatened(TravelerRoutine routine, int system, long elapsed) =>
        routine with
        {
            Threats = routine
                .Threats.Where(threat => threat.ExpiresAt > elapsed && threat.SystemId != system)
                .Prepend(new(system, checked(elapsed + ThreatMemory.Ticks)))
                .Take(RememberedThreats)
                .ToArray(),
            ShelterPending = true,
        };

    public static int DestinationWeight(TravelerRoutine routine, int station, int geographicWeight)
    {
        // A pilot's favorite stations stay stable; the independent trip draw supplies variety.
        int affinity = 1 + (int)(Mix(routine.Seed ^ (uint)station) % 4);
        int weight = geographicWeight * affinity * 4;
        return routine.RecentStations.Contains(station) ? Math.Max(1, weight / 4) : weight;
    }

    public static ulong Draw(TravelerRoutine routine, long sequence) => Mix(unchecked(routine.Seed + (ulong)sequence));

    public static int LayoverSeconds(TravelerRoutine routine, TrafficJourneyKind kind, long pilot, long sequence)
    {
        double pace = routine.Pace switch
        {
            TravelPace.Brisk => .5,
            TravelPace.Leisurely => 2,
            _ => 1,
        };
        double variation = .8 + (Draw(routine, sequence) % 401) / 1000d;
        return Math.Max(1, (int)Math.Round(TrafficJourneyRules.LayoverSeconds(kind, pilot, sequence) * pace * variation));
    }

    public static int ShelterSeconds(TravelerRoutine routine, long sequence) =>
        MinimumShelterSeconds + (int)(Draw(routine, sequence) % ShelterSpreadSeconds);
}
