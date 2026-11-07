using Eden.Gameplay.Dogma;
using Eden.Gameplay.Economy;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Industry;

// Pinned build 3662168: industry.const and industryCommon, not database row IDs.
public enum IndustryActivity
{
    Manufacturing = 1,
}

public enum IndustryJobStatus
{
    Installed = 1,
    Ready = 3,
    Delivered = 101,
    Cancelled = 102,
}

public enum IndustrySkill
{
    Industry = 3380,
    MassProduction = 3387,
    AdvancedIndustry = 3388,
    AdvancedMassProduction = 24625,
}

public sealed record IndustryFacility(int Id, int TypeId, int OwnerId, int SystemId, double CostIndex = 1, double Tax = 0);

public sealed record OwnedBlueprint(InventoryItem Item, IndustryFacility Facility, long? JobId);

public sealed record IndustryCharacterBonus(int SkillId, DogmaAttributeId Attribute, double Value, bool Additive);

public sealed record ManufacturingRequest(
    long BlueprintId,
    int BlueprintTypeId,
    int FacilityId,
    int Runs,
    int? ProductTypeId,
    double QuotedSeconds,
    IReadOnlyDictionary<int, int> QuotedMaterials,
    ManufacturingCharge? QuotedCharge = null
);

public sealed record ManufacturingJob(
    long Id,
    long PilotId,
    long BlueprintId,
    int BlueprintTypeId,
    IndustryFacility Facility,
    int ProductTypeId,
    int Runs,
    int OutputQuantity,
    long StartTime,
    long EndTime,
    long DurationSeconds,
    IndustryJobStatus Status,
    long? CompletedAt,
    long CostCents = 0
);

public sealed record IndustryResult(ManufacturingJob Job, IReadOnlyList<InventoryChange> Changes, long? BalanceCents = null);

public interface IPilotIndustrySkills
{
    IReadOnlyList<IndustryCharacterBonus> CharacterBonuses(long pilotId);
}

public interface IPilotIndustry : IPilotIndustrySkills
{
    void CheckAccess(long pilotId, int stationId);
    void CheckStation(long pilotId, int stationId);
    IReadOnlyList<IndustryFacility> Facilities(long pilotId);
    IndustryFacility Facility(long pilotId, int stationId);
    IReadOnlyList<int> ProductTypes();
    IReadOnlyList<OwnedBlueprint> Blueprints(long pilotId, int? stationId = null);
    OwnedBlueprint Blueprint(long pilotId, long itemId);
    IReadOnlyDictionary<int, long> Materials(long pilotId, int stationId);
    IReadOnlyList<ManufacturingJob> Jobs(long pilotId, bool includeCompleted);
    ManufacturingJob Job(long pilotId, long jobId);
    IndustryResult Install(long pilotId, ManufacturingRequest request);
    IndustryResult Deliver(long pilotId, long jobId, int systemId);
    IndustryResult Cancel(long pilotId, long jobId, int systemId);
}

public static class IndustryDefinitions
{
    public const int BaseManufacturingSlots = 1;
    public const int ManufacturingTimePrecision = 0;
    public const long MaximumJobSeconds = 30 * 24 * 60 * 60;
    public const int MaximumJobRuns = 1_000_000;
}
