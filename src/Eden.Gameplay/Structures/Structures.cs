using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Structures;

// Build 3662168 structures.const, structures.services and inventory flags.
public enum StructureState
{
    Unanchored = 1,
    Anchoring = 2,
    Fitting = 101,
    Onlining = 102,
    ShieldVulnerable = 110,
    ArmorReinforced = 111,
    ArmorVulnerable = 112,
    HullReinforced = 113,
    HullVulnerable = 114,
    AnchorVulnerable = 115,
    DeployVulnerable = 116,
    Destroyed = 119,
}

public enum StructureService
{
    Docking = 1,
    Fitting = 2,
    Offices = 3,
    Reprocessing = 4,
    Market = 5,
    Medical = 6,
    Repair = 8,
    Insurance = 9,
    JumpClone = 10,
    LoyaltyStore = 11,
    MoonMining = 14,
    JumpBridge = 15,
    CynoBeacon = 16,
    CynoJammer = 17,
    AutomaticMoonMining = 18,
    Industry = 20,
    Manufacturing = 21,
    BasicManufacturing = 22,
    CapitalManufacturing = 23,
    SupercapitalManufacturing = 24,
    Laboratory = 30,
    TimeResearch = 31,
    MaterialResearch = 32,
    Copying = 33,
    Invention = 34,
    Reactions = 40,
    CompositeReactions = 41,
    BiochemicalReactions = 42,
    HybridReactions = 43,
}

public enum StructureFamily
{
    Citadel,
    EngineeringComplex,
    Refinery,
    JumpBridge,
    CynoBeacon,
    CynoJammer,
    MoonDrill,
    Skyhook,
    SovereigntyHub,
}

public enum StructureSize
{
    Flex,
    Medium,
    Large,
    ExtraLarge,
}

public sealed record StructureProfile(
    int TypeId,
    string Name,
    int GroupId,
    StructureFamily Family,
    StructureSize Size,
    double Radius,
    int? CoreTypeId,
    int? BuiltinModuleTypeId,
    int ServiceSlots,
    double Cpu,
    double Power,
    double FuelCapacity,
    double Shield,
    double Armor,
    double Hull,
    bool DisallowHighSecurity
)
{
    public bool Dockable => Family is StructureFamily.Citadel or StructureFamily.EngineeringComplex or StructureFamily.Refinery;
    public int? BuiltinCoreTypeId { get; init; }
    public int CategoryId { get; init; } = StructureRules.CategoryId;
}

public sealed record StructureServiceProfile(
    int TypeId,
    IReadOnlyList<StructureService> Services,
    IReadOnlyList<int> AllowedGroups,
    IReadOnlyList<int> AllowedTypes,
    double Cpu,
    double Power,
    int FuelGroupId,
    int HourlyFuel,
    int OnlineFuel,
    int MaximumFitted
);

public sealed record StructureModule(long ItemId, int TypeId, int Slot, bool Online, long? NextFuelAt);

public sealed record StructureAccess(long MemberId, bool Allowed);

public sealed record StructureLocation(long Id, string Name, int SystemId, SpacePosition Position);

public sealed record StructureContact(StructureState State, long StateStartedAt, long? StateEndsAt, long? UnanchorAt, bool HasCore);

public sealed record StructureSnapshot(
    long Id,
    StructureProfile Profile,
    string Name,
    string Description,
    int CorporationId,
    int SystemId,
    SpacePosition Position,
    double Yaw,
    StructureState State,
    long? StateEndsAt,
    long? UnanchorAt,
    bool HasCore,
    bool PublicAccess,
    long? LowPowerAt,
    IReadOnlyList<StructureModule> Modules,
    IReadOnlyList<StructureService> Services,
    int? MoonId = null,
    long StateStartedAt = 0,
    int ReinforceWeekday = 0,
    int ReinforceHour = 18
);

public sealed record StructureDeployment(
    long ItemId,
    SpacePosition Position,
    double Yaw,
    string Name,
    string Description = "",
    int? MoonId = null,
    int ReinforceWeekday = 0,
    int ReinforceHour = 18
);

public sealed record StructureResult(StructureSnapshot Structure, IReadOnlyList<Inventory.InventoryChange> Changes);

public interface IPilotStructures
{
    IReadOnlyList<StructureSnapshot> List(long pilotId, int? systemId = null);
    StructureSnapshot? Read(long pilotId, long structureId);
    IReadOnlyList<StructureLocation> Names(IReadOnlyList<long> structureIds);
    StructureResult Deploy(long pilotId, StructureDeployment deployment);
    StructureResult Rename(long pilotId, long structureId, string name);
    StructureResult Describe(long pilotId, long structureId, string description);
    StructureResult SetAccess(long pilotId, long structureId, bool publicAccess, IReadOnlyList<StructureAccess> members);
    bool HasService(long pilotId, long structureId, StructureService service);
    IReadOnlyList<Inventory.InventoryItem> Inventory(long pilotId, long structureId);
    StructureResult InstallCore(long pilotId, long structureId, long itemId);
    StructureResult FitService(long pilotId, long structureId, long itemId, int slot);
    StructureResult SetServiceOnline(long pilotId, long structureId, long moduleId, bool online);
    StructureResult RemoveService(long pilotId, long structureId, long moduleId);
    StructureResult DepositFuel(long pilotId, long structureId, long itemId, int quantity);
    StructureResult Unanchor(long pilotId, long structureId, bool cancel = false);
    StructureResult Recover(long pilotId, long structureId);
    StructureResult Withdraw(long pilotId, long structureId, long itemId, int quantity, bool toCargo);
    void Advance();
}

public interface IStructureContacts
{
    IReadOnlyList<StaticSpaceObject> Contacts(int systemId);
}
