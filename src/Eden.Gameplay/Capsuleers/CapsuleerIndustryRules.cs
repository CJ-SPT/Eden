using Eden.Gameplay.Industry;
using Eden.Gameplay.Planetary;

namespace Eden.Gameplay.Capsuleers;

public static class CapsuleerIndustryRules
{
    private const int MaximumDemandRuns = 5;
    public const int MaximumFactorySupplyJumps = 3;
    public const int MaximumResearchSupplyJumps = 12;
    public const int ResearchCourierFieldLevel = 4;
    public const int ResearchCourierProjects = 2;
    public const int MaximumMercoxitSupplyJumps = TrafficJourneyRules.MaximumJumps;
    public const int Morphite = 11399;
    public const int MaximumPlanetarySupplyJumps = 8;

    public static int DemandRuns(int demand, int unitsPerRun, long secondsPerRun, int? licensedRuns)
    {
        int runLimit = licensedRuns is >= 0
            ? Math.Min(MaximumDemandRuns, licensedRuns.Value)
            : (int)Math.Min(MaximumDemandRuns, Math.Ceiling((double)IndustryDefinitions.MaximumJobSeconds / secondsPerRun));
        return Math.Min(runLimit, demand / unitsPerRun);
    }
}

public sealed record FactorySupplyNeed(int TypeId, long HangarTarget, int RequiredQuantity, int PlannedUnits, long MaximumUnitPriceCents);

public sealed record FactorySupplyDemand(long OrderId, long MinimumUnitPriceCents, int Quantity);

public sealed record FactorySupplyRequest(int CorporationId, int StationId, FactorySupplyDemand? Demand, FactorySupplyNeed[] Materials)
{
    public bool ResearchDatacores { get; init; }
    public bool Mercoxit { get; init; }
    public int MaximumJumps =>
        Mercoxit ? CapsuleerIndustryRules.MaximumMercoxitSupplyJumps : CapsuleerIndustryRules.MaximumFactorySupplyJumps;
}

public sealed record FactorySupplyShipment(long ManufacturerId, int FactoryStationId, int SupplierStationId, long? DemandOrderId)
{
    public FactoryResearchSource? Research { get; init; }
    public FactoryPlanetaryVisit? Planetary { get; init; }
    public bool Mercoxit { get; init; }
    public int MaximumJumps =>
        Research is not null ? CapsuleerIndustryRules.MaximumResearchSupplyJumps
        : Mercoxit ? CapsuleerIndustryRules.MaximumMercoxitSupplyJumps
        : CapsuleerIndustryRules.MaximumFactorySupplyJumps;
}

public sealed record FactoryResearchSource(long AgentId, int SkillId, int DatacoreTypeId);

public enum PlanetaryVisitPhase
{
    Warehouse,
    Approach,
    Orbit,
    Return,
    DeliverStock,
}

public sealed record FactoryPlanetaryProduction(
    int CorporationId,
    long ManufacturerId,
    int FactoryStationId,
    int StagingStationId,
    int PlanetId,
    int RootProduct,
    int RootQuantity,
    PlanetaryProductionStep Step,
    int CenterType,
    int MachineType,
    long ReadyAt = 0
);

public sealed record FactoryPlanetaryVisit(FactoryPlanetaryProduction Production, PlanetaryVisitPhase Phase, FreightStack[]? Inputs = null);

public static class CapsuleerMinerTwoRules
{
    // Build 3662168 blueprint activity chain from Miner I to Miner II.
    public const int Original = 785;
    public const int InventedBlueprint = 784;
    public const int Module = 482;
    public const int BatchSize = 5;
    public static readonly TimeSpan FailureRetry = TimeSpan.FromHours(1);
}
