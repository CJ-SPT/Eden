using Eden.Gameplay.Industry;

namespace Eden.Gameplay.Capsuleers;

public static class CapsuleerIndustryRules
{
    private const int MaximumDemandRuns = 5;
    public const int MaximumFactorySupplyJumps = 3;
    public const int MaximumResearchSupplyJumps = 12;
    public const int ResearchCourierFieldLevel = 4;
    public const int ResearchCourierProjects = 2;

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
}

public sealed record FactorySupplyShipment(long ManufacturerId, int FactoryStationId, int SupplierStationId, long? DemandOrderId)
{
    public FactoryResearchSource? Research { get; init; }
}

public sealed record FactoryResearchSource(long AgentId, int SkillId, int DatacoreTypeId);

public static class CapsuleerMinerTwoRules
{
    // Build 3662168 blueprint activity chain from Miner I to Miner II.
    public const int Original = 785;
    public const int InventedBlueprint = 784;
    public const int Module = 482;
    public const int BatchSize = 5;
    public static readonly TimeSpan FailureRetry = TimeSpan.FromHours(1);
}
