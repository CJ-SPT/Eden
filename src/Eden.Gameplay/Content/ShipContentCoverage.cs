using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;

namespace Eden.Gameplay.Content;

public enum ContentSupportState
{
    Complete,
    Partial,
    Planned,
    EvidenceBlocked,
    ExternalDependency,
    DeferredSpecial,
    Nonfunctional,
}

public enum ShipContentMilestone
{
    Coverage = 1,
    FittingCapabilities = 2,
    CombatDrones = 3,
    EnergyAndRemoteSupport = 4,
    AdvancedDefenses = 5,
    Overheating = 6,
    InformationAndSalvage = 7,
    Mining = 8,
    ProbeScanning = 9,
    Exploration = 10,
    CloakingAndMicroJumps = 11,
    FleetServices = 12,
    AreaWeaponsAndInterdiction = 13,
    WeaponMechanics = 14,
    TechTwoSubcapitals = 15,
    TechThree = 16,
    UtilityDrones = 17,
    CapitalTravel = 18,
    CapitalModes = 19,
    Fighters = 20,
    IndustrialCommand = 21,
    Supercapitals = 22,
    StaticCatalogClosure = 23,
}

public enum ContentOperation
{
    Assembly,
    Fitting,
    Operation,
    Bonuses,
    Role,
    ChargeLoading,
    Overheating,
    ConsumerCoverage,
}

public sealed record ContentCapabilityCheck(
    ContentOperation Operation,
    bool Implemented,
    ShipContentMilestone Milestone,
    string Evidence,
    HullCapability? Role = null
);

public sealed record ContentCoverageIssue(ContentSupportState State, ShipContentMilestone Milestone, string Reason);

public sealed record ShipContentCoverageEntry(
    int TypeId,
    string Name,
    int GroupId,
    string GroupName,
    InventoryCategory Category,
    int? TechLevel,
    int? MetaGroup,
    bool Admitted,
    ContentSupportState State,
    IReadOnlyList<int> Effects,
    IReadOnlyList<ContentCapabilityCheck> Checks,
    IReadOnlyList<ContentCoverageIssue> Issues
);

public sealed record ContentCoverageTotal(InventoryCategory Category, ContentSupportState State, int Count);

public sealed record ShipContentCoverageReport(
    int FormatVersion,
    int Build,
    string SourceFingerprint,
    IReadOnlyList<ContentCoverageTotal> Totals,
    IReadOnlyList<ShipContentCoverageEntry> Entries
);
