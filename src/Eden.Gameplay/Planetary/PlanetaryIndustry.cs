using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;

namespace Eden.Gameplay.Planetary;

// Pinned build 3662168 inventory groups and planetCommon/commandStream constants.
public enum PlanetaryPinKind
{
    Command = 1027,
    Processor = 1028,
    Storage = 1029,
    Spaceport = 1030,
    Extractor = 1063,
}

public sealed record PlanetaryCommodity(int TypeId, int Quantity);

public sealed record PlanetaryHead(int Id, double Latitude, double Longitude);

public readonly record struct PlanetaryId(long Value, int TemporaryKind = 0);

public sealed record PlanetaryPlanet(int Id, int SystemId, int TypeId, int Index, double Radius, SpacePosition Position, bool Colonizable);

public sealed record PlanetaryType(
    int TypeId,
    PlanetaryPinKind Kind,
    int PlanetTypeId,
    double Capacity,
    double Cpu,
    double Power,
    long CostCents,
    double HeadCpu = 0,
    double HeadPower = 0,
    double HeadRange = 0,
    double Decay = 0,
    double Noise = 0
);

public sealed record PlanetarySchematic(
    int Id,
    int CycleSeconds,
    IReadOnlyList<int> PinTypes,
    IReadOnlyList<PlanetaryCommodity> Inputs,
    PlanetaryCommodity Output
);

public sealed record PlanetaryCatalog(
    IReadOnlyDictionary<int, PlanetaryType> Pins,
    IReadOnlyDictionary<int, PlanetarySchematic> Schematics,
    IReadOnlyDictionary<int, double> Volumes,
    IReadOnlyDictionary<int, int> CommodityTiers,
    PlanetaryLinkProfile Link
);

public sealed record PlanetaryLinkProfile(
    int TypeId,
    double Cpu,
    double Power,
    double CpuPerKm,
    double PowerPerKm,
    double CpuLevelMultiplier,
    double PowerLevelMultiplier,
    double CapacityPerHour
);

public sealed record PlanetaryLinkUsage(double Cpu, double Power);

public sealed record PlanetaryProgram(int TypeId, long InstalledAt, long ExpiresAt, long CycleTicks, int BaseQuantity, double HeadRadius);

public sealed record PlanetaryPin(
    long Id,
    int TypeId,
    double Latitude,
    double Longitude,
    long LastRunAt,
    IReadOnlyList<PlanetaryCommodity> Contents,
    int? SchematicId = null,
    long? NextRunAt = null,
    PlanetaryProgram? Program = null,
    IReadOnlyList<PlanetaryHead>? Heads = null,
    long TransferReadyAt = 0,
    long LastLaunchAt = 0
);

public sealed record PlanetaryLink(long First, long Second, int Level);

public sealed record PlanetaryRoute(long Id, IReadOnlyList<long> Path, int TypeId, int Quantity);

public sealed record PlanetaryColony(
    long OwnerId,
    int PlanetId,
    int Level,
    long SimulatedAt,
    long NextPin,
    long NextRoute,
    IReadOnlyList<PlanetaryPin> Pins,
    IReadOnlyList<PlanetaryLink> Links,
    IReadOnlyList<PlanetaryRoute> Routes
);

public sealed record PlanetarySummary(PlanetaryPlanet Planet, int PinCount);

public sealed record PlanetarySnapshot(PlanetaryPlanet Planet, PlanetaryColony? Colony);

public sealed record PlanetaryEditResult(PlanetaryColony Colony, long CostCents);

public sealed record PlanetaryChange(PlanetaryColony Colony, IReadOnlyList<InventoryChange> Items, long? BalanceCents = null);

public sealed record PlanetaryTransfer(PlanetaryColony Colony, long SourceReadyAt);

public sealed record PlanetaryProgramQuote(int BaseQuantity, long CycleTicks, int Cycles);

public sealed record PlanetaryResourceField(int Bands, int Proximity, IReadOnlyList<float> Coefficients);

public sealed record PlanetarySurveySkills(int RemoteSensing, int Planetology, int AdvancedPlanetology);

public sealed record PlanetarySurveyAccess(bool LocalOrOwned, bool RemoteAllowed, SpacePosition? Observer, SpacePosition? Target);

public sealed record PlanetaryLaunch(int Id, long OwnerId, int PlanetId, int SystemId, long Time, long ExpiresAt, SpacePosition Position);

public sealed record PlanetaryOrbitInventory(InventoryItem Container, IReadOnlyList<InventoryItem> Items);

public abstract record PlanetaryCommand;

public sealed record CreatePlanetaryPin(PlanetaryId Pin, int TypeId, double Latitude, double Longitude) : PlanetaryCommand;

public sealed record RemovePlanetaryPin(PlanetaryId Pin) : PlanetaryCommand;

public sealed record CreatePlanetaryLink(PlanetaryId First, PlanetaryId Second, int Level) : PlanetaryCommand;

public sealed record RemovePlanetaryLink(PlanetaryId First, PlanetaryId Second) : PlanetaryCommand;

public sealed record UpgradePlanetaryLink(PlanetaryId First, PlanetaryId Second, int Level) : PlanetaryCommand;

public sealed record CreatePlanetaryRoute(PlanetaryId Route, IReadOnlyList<PlanetaryId> Path, int TypeId, int Quantity) : PlanetaryCommand;

public sealed record RemovePlanetaryRoute(PlanetaryId Route) : PlanetaryCommand;

public sealed record SetPlanetarySchematic(PlanetaryId Pin, int SchematicId) : PlanetaryCommand;

public sealed record UpgradePlanetaryCommand(PlanetaryId Pin, int Level) : PlanetaryCommand;

public sealed record AddPlanetaryHead(PlanetaryId Pin, PlanetaryHead Head) : PlanetaryCommand;

public sealed record RemovePlanetaryHead(PlanetaryId Pin, int HeadId) : PlanetaryCommand;

public sealed record MovePlanetaryHead(PlanetaryId Pin, PlanetaryHead Head) : PlanetaryCommand;

public sealed record InstallPlanetaryProgram(PlanetaryId Pin, int? TypeId, double HeadRadius) : PlanetaryCommand;

public interface IPilotPlanetaryIndustry
{
    PlanetarySnapshot Read(long pilotId, int planetId);
    IReadOnlyList<PlanetarySummary> List(long pilotId);
    IReadOnlyList<PlanetaryColony> Networks(int planetId);
    PlanetaryResourceField ResourceField(long pilotId, int planetId, int typeId, int bands);
    double CustomsTaxRate(long pilotId, long officeId);
    PlanetaryChange Update(long pilotId, int planetId, IReadOnlyList<PlanetaryCommand> commands);
    void Abandon(long pilotId, int planetId);
    PlanetaryProgramQuote Quote(
        long pilotId,
        int planetId,
        PlanetaryId pinId,
        int typeId,
        IReadOnlyList<PlanetaryHead> heads,
        double radius
    );
    PlanetaryTransfer Transfer(long pilotId, int planetId, IReadOnlyList<long> path, IReadOnlyList<PlanetaryCommodity> commodities);
    PlanetaryChange Launch(long pilotId, int planetId, long pinId, IReadOnlyList<PlanetaryCommodity> commodities);
    IReadOnlyList<PlanetaryLaunch> Launches(long pilotId);
    void DeleteLaunch(long pilotId, int launchId);
    IReadOnlyList<WreckContact> OrbitContacts(int systemId, long now);
    PlanetaryOrbitInventory OrbitInventory(long pilotId, long containerId);
    IReadOnlyList<InventoryChange> MoveOrbitCargo(long pilotId, long containerId, bool toOrbit, InventoryMoveRequest request);
    PlanetaryChange ImportExport(
        long pilotId,
        long officeId,
        long pinId,
        IReadOnlyList<PlanetaryCargoRequest> imports,
        IReadOnlyList<PlanetaryCommodity> exports
    );
}

public sealed record PlanetaryCargoRequest(long ItemId, int Quantity);
