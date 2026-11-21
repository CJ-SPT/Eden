using Eden.Gameplay.Structures;

namespace Eden.Gameplay.Flight;

public sealed record ShipCompressionFacility(int TypeList, double Range);

public sealed record ShipCompressorCycle(
    long PilotId,
    long ShipId,
    long ModuleId,
    long StartedAt,
    long EndsAt,
    ShipCompressionFacility Facility
);

public sealed record ShipCompressorActivation(ShipCompressorCycle Cycle, double Capacitor);

public interface IPilotShipCompression
{
    ShipCompressorActivation Activate(long pilotId, long shipId, long moduleId);
    void Stop(long pilotId, long shipId, long moduleId);
    StructureCompressionResult Compress(long pilotId, long shipId, long itemId, long facilityShipId);
}

public static class ShipCompressionRules
{
    // Pinned Rorqual compressor types and 8364's fixed paid cycle.
    public const int Group = 4174;
    public const int CapitalAsteroidOre = 62632;
    public const int CapitalIce = 62633;
    public const int CapitalGas = 62634;
    public const int CapitalMercoxit = 62635;
    public const int CapitalMoonOre = 62636;
    public const int CycleMilliseconds = 60_000;
    public const int FleetCompressionLogistics = 62453;

    public static bool IsCompressor(int typeId) =>
        typeId is CapitalAsteroidOre or CapitalIce or CapitalGas or CapitalMercoxit or CapitalMoonOre;
}
