using Eden.Gameplay.Dogma;
using Eden.Gameplay.Economy;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Industry;

// Pinned build 3662168: industry.const and industryCommon, not database row IDs.
public enum IndustryActivity
{
    Manufacturing = 1,
    ResearchTime = 3,
    ResearchMaterial = 4,
    Copying = 5,
    Invention = 8,
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
    Science = 3402,
    Research = 3403,
    LaboratoryOperation = 3406,
    Metallurgy = 3409,
    AdvancedLaboratoryOperation = 24624,
    MassProduction = 3387,
    AdvancedIndustry = 3388,
    AdvancedMassProduction = 24625,
}

public sealed record IndustryFacility(
    int Id,
    int TypeId,
    int OwnerId,
    int SystemId,
    double CostIndex = 1,
    double Tax = 0,
    bool Manufacturing = true,
    bool Laboratory = false
);

public sealed record OwnedBlueprint(
    InventoryItem Item,
    IndustryFacility Facility,
    long? JobId,
    int MaterialEfficiency = 0,
    int TimeEfficiency = 0,
    int RunsRemaining = -1
);

public sealed record IndustryCharacterBonus(int SkillId, DogmaAttributeId Attribute, double Value, bool Additive);

public sealed record IndustryRequest(
    long BlueprintId,
    int BlueprintTypeId,
    int FacilityId,
    int Runs,
    int? ProductTypeId,
    double QuotedSeconds,
    IReadOnlyDictionary<int, int> QuotedMaterials,
    ManufacturingCharge? QuotedCharge = null,
    IndustryActivity Activity = IndustryActivity.Manufacturing,
    int LicensedRuns = 0
);

public sealed record IndustryJobDetails(
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
    long CostCents = 0,
    IndustryActivity Activity = IndustryActivity.Manufacturing,
    int LicensedRuns = 0,
    bool BlueprintCopy = false,
    double Probability = 1,
    int? SuccessfulRuns = null,
    int? OptionalTypeId = null
);

public sealed record IndustryResult(IndustryJobDetails Job, IReadOnlyList<InventoryChange> Changes, long? BalanceCents = null);

public interface IIndustryRandom
{
    IReadOnlyList<double> Rolls(int count);
}

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
    IReadOnlyList<int> ScienceProductCategories();
    IReadOnlyList<OwnedBlueprint> Blueprints(long pilotId, int? stationId = null);
    OwnedBlueprint Blueprint(long pilotId, long itemId);
    IReadOnlyDictionary<int, long> Materials(long pilotId, int stationId);
    IReadOnlyList<IndustryJobDetails> Jobs(long pilotId, bool includeCompleted);
    IndustryJobDetails Job(long pilotId, long jobId);
    IndustryResult Install(long pilotId, IndustryRequest request);
    IndustryResult Deliver(long pilotId, long jobId, int systemId);
    IndustryResult Cancel(long pilotId, long jobId, int systemId);
}

public static class IndustryDefinitions
{
    public const int BaseManufacturingSlots = 1;
    public const int BaseLaboratorySlots = 1;
    public const int MaximumScienceRuns = 500;
    public const int MaximumMaterialEfficiency = 10;
    public const int MaximumTimeEfficiency = 20;
    public const int TimeEfficiencyStep = 2;
    public const double ScienceCostPercentage = 0.02;
    public const int InventionMaterialEfficiency = 2;
    public const int InventionTimeEfficiency = 4;
    public const int GenericDecryptorGroup = 1304;
    public static IReadOnlyList<IndustryActivity> Activities { get; } = Array.AsReadOnly(Enum.GetValues<IndustryActivity>());
    public const int ManufacturingTimePrecision = 0;
    public const long MaximumJobSeconds = 30 * 24 * 60 * 60;
    public const int MaximumJobRuns = 1_000_000;
}
