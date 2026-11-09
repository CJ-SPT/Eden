namespace Eden.Gameplay.Capsuleers;

public enum MinerEncounterOpening
{
    Docked,
    Returning,
    Mining,
}

public enum HaulerEncounterOpening
{
    Docked,
    Inbound,
    ApproachingStation,
}

public static class CapsuleerEncounterRules
{
    public const double GateWatchDistanceMeters = 250_000;

    // EVE University's trade-hub station links, verified against the 3662168 station catalog.
    private const int JitaNavyAssemblyPlant = 60003760;
    private const int AmarrEmperorFamilyAcademy = 60008494;
    private const int RensBrutorTreasury = 60004588;
    private const int DodixieNavyAssemblyPlant = 60011866;
    private const int HekBoundlessCreationFactory = 60005686;

    public static bool IsTradeHubStation(int stationId) =>
        stationId
            is JitaNavyAssemblyPlant
                or AmarrEmperorFamilyAcademy
                or RensBrutorTreasury
                or DodixieNavyAssemblyPlant
                or HekBoundlessCreationFactory;

    public static int Haulers(long seed, int systemId, int gates, int stations)
    {
        // These are encounter density settings. Only reciprocal gates count towards a corridor.
        int variation = (int)(CapsuleerProvisioningRules.Seed(seed, systemId, CapsuleerCareer.Hauling) & 1);
        return (gates, stations) switch
        {
            (>= 6, _) or (_, >= 8) => 5 + variation,
            (>= 4, _) or (_, >= 4) => 3 + variation,
            (>= 3, _) or (_, >= 3) => 2 + variation,
            (2, _) or (0, >= 2) => 1 + variation,
            _ => ((seed ^ systemId) & 1) == 0 ? 1 : 0,
        };
    }

    public static HaulerEncounterOpening TrafficOpening(long seed, int systemId, int slot, int count)
    {
        // Every busy cast has a ship at the gate and another near the station on its first visit.
        if (count > 1)
        {
            return (slot % 3) switch
            {
                0 => HaulerEncounterOpening.Inbound,
                1 => HaulerEncounterOpening.ApproachingStation,
                _ => HaulerEncounterOpening.Docked,
            };
        }
        return (HaulerEncounterOpening)(CapsuleerProvisioningRules.Seed(seed, systemId, CapsuleerCareer.Hauling) % 3);
    }

    public static MinerEncounterOpening Opening(long seed, int systemId) =>
        ((seed + systemId) & 3) switch
        {
            0 => MinerEncounterOpening.Docked,
            1 => MinerEncounterOpening.Returning,
            _ => MinerEncounterOpening.Mining,
        };
}
