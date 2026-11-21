using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Structures;

namespace Eden.Gameplay.Flight;

public enum CapitalJumpTarget
{
    FleetModule,
    Structure,
    FleetDeployable,
    FleetPortal,
}

public sealed record CapitalJumpRequest(
    long EmitterId,
    long BeaconId,
    int SystemId,
    CapitalJumpTarget Target = CapitalJumpTarget.FleetModule,
    long PortalShipId = 0,
    bool Conduit = false
);

public sealed record StructureCynoBeacon(long Id, int TypeId, int OwnerId, int SystemId, StructureState State, string Name);

public interface ICapitalNavigation
{
    IReadOnlyList<StructureCynoBeacon> AccessibleBeacons(long? pilotId);
    long? JammerOnlineAt(int systemId);
}

public sealed record JumpTimers(long ActivationUntil, long FatigueUntil, long UpdatedAt);

public sealed record CynosuralField(
    long PilotId,
    long ShipId,
    long ModuleId,
    long FleetId,
    int SystemId,
    long StartedAt,
    long EndsAt,
    StaticSpaceObject Beacon,
    long? DeployableId = null
);

public sealed record CynoActivation(CynosuralField Field, IReadOnlyList<InventoryChange> FuelChanges, double Capacitor);

public sealed record JumpPortal(
    long PilotId,
    long ShipId,
    long ModuleId,
    long FleetId,
    int Origin,
    CapitalJumpRequest Target,
    long StartedAt,
    long EndsAt,
    int ModuleTypeId = CapitalTravelRules.CovertPortalGenerator
);

public sealed record JumpPortalActivation(JumpPortal Portal, double Capacitor, IReadOnlyList<InventoryChange> FuelChanges);

public interface ICapitalInventoryEvents
{
    void Publish(long pilotId, IReadOnlyList<InventoryChange> changes);
}

public interface IConduitArrivalEvents
{
    void Publish(int origin, CapitalJump arrival);
}

public sealed record CapitalJump(DockedPilot Pilot, JumpTimers Timers, double Capacitor, IReadOnlyList<InventoryChange> Changes);

public interface IPilotCapitalTravel
{
    CapitalModeActivation ActivateMode(long pilotId, long shipId, long moduleId, DogmaEffect effect);
    CapitalModeCycle? Mode(long shipId);
    IReadOnlyList<ShipCompressorCycle> Compressors(long shipId);
    IndustrialProtectionActivation ProtectIndustry(long pilotId, long shipId, long moduleId, long asteroid);
    bool IndustrialProtectionActive(long shipId);
    CynoActivation Light(long pilotId, long shipId, long moduleId, double speed);
    void CheckJump(long pilotId, long shipId, CapitalJumpRequest request);
    CapitalJump Jump(long pilotId, long shipId, CapitalJumpRequest request);
    JumpTimers? Timers(long pilotId);
    CynosuralField? Field(long pilotId);
    IReadOnlyList<CynosuralField> Fields(int systemId);
    IReadOnlyList<CynosuralField> FleetFields(long pilotId);
    IReadOnlyDictionary<int, int> BeaconCounts();
    CynoDeployment Deploy(long pilotId, long shipId, IReadOnlyList<long> itemIds, SpacePosition heading);
    IReadOnlyList<StaticSpaceObject> Deployables(int systemId);
    bool DoomsdayMovementLocked(long shipId);
    JumpPortalActivation OpenPortal(long pilotId, long shipId, CapitalJumpRequest request);
    JumpPortal? Portal(long pilotId);
    IReadOnlyList<JumpPortal> FleetPortals(long pilotId);
}

public sealed record CynoDeployment(IReadOnlyDictionary<long, long> Launched, IReadOnlyList<InventoryChange> Changes);

public sealed record CynoDeployableActivation(bool Active, long OnlineAt, long ExpiresAt, ShipHealth Health, long DamagedAt);

public static class CapitalTravelRules
{
    // Build 3662168 client/types, client/typedogma and jumpFatigue/const.pyj.
    public const int IndustrialGenerator = 52694;
    public const int IndustrialField = 52696;
    public const int StandardGenerator = 21096;
    public const int StandardField = 21094;
    public const int CovertGenerator = 28646;
    public const int CovertField = 28650;
    public const int CovertFieldGroup = 897;
    public const int TitanPortalGenerator = 23953;
    public const int TitanPortalGroup = 590;
    public const int JumpPortalGenerationSkill = 24562;
    public const int CovertPortalGenerator = 28652;
    public const int CovertPortalGroup = 4127;
    public const int IndustrialPortalGenerator = 63140;
    public const int IndustrialPortalGroup = 4184;
    public const int CapitalJumpPortalGenerationSkill = 83094;
    public const double ConduitRange = 10_000;
    public const int MobileBeacon = 57319;
    public const int MobileField = 57425;
    public const int CovertMobileBeacon = 59630;
    public const int CovertMobileField = 59631;
    public const int MobileInhibitor = 33476;
    public const double MobileArrivalClearance = 5_000;
    public const int GeneratorGroup = 658;
    public const int FieldGroup = 2020;
    public const int IndustrialCycleMilliseconds = 600_000;
    public const int EconomizerGroup = 1299;
    public const int CynosuralFieldTheory = 21603;
    public const int JumpDriveOperation = 3456;
    public const int JumpDriveCalibration = 21611;
    public const int JumpFuelConservation = 21610;
    public const int Pharolux = 35840;
    public const int Tenebrex = 37534;
    public const int PharoluxService = 35912;
    public const int TenebrexService = 35914;
    public const int CynosuralNavigationUpgrade = 81615;
    public const int CynosuralSuppressionUpgrade = 81619;
    public const double StructureArrivalClearance = 12_000;
    public const double LightYearMeters = 9_460_730_472_580_800;

    public static int? ModuleField(int typeId) =>
        typeId switch
        {
            IndustrialGenerator => IndustrialField,
            StandardGenerator => StandardField,
            CovertGenerator => CovertField,
            _ => null,
        };

    public static bool Covert(int fieldType) => fieldType is CovertField or CovertMobileField;

    public static bool JumpDrive(int groupId) =>
        groupId is (int)HullClass.JumpFreighter or (int)HullClass.BlackOps || RestrictedCapital(groupId);

    public static bool RestrictedCapital(int groupId) =>
        groupId
            is (int)HullClass.Dreadnought
                or (int)HullClass.LancerDreadnought
                or (int)HullClass.ForceAuxiliary
                or (int)HullClass.Carrier
                or (int)HullClass.CapitalIndustrial
        || Supercapital(groupId);

    public static bool CarrierConduitHull(int groupId) =>
        (HullClass)groupId is HullClass.Carrier or HullClass.Supercarrier or HullClass.CommandCarrier;

    public static bool CarrierConduitPassengerHull(int groupId) =>
        (HullClass)groupId
            is HullClass.Capsule
                or HullClass.Shuttle
                or HullClass.Corvette
                or HullClass.Frigate
                or HullClass.Cruiser
                or HullClass.Battleship
                or HullClass.AssaultFrigate
                or HullClass.HeavyAssaultCruiser
                or HullClass.CombatBattlecruiser
                or HullClass.Destroyer
                or HullClass.CommandShip
                or HullClass.Interdictor
                or HullClass.CovertOps
                or HullClass.Interceptor
                or HullClass.Logistics
                or HullClass.ForceRecon
                or HullClass.StealthBomber
                or HullClass.ElectronicAttack
                or HullClass.HeavyInterdictionCruiser
                or HullClass.BlackOps
                or HullClass.Marauder
                or HullClass.CombatReconShip
                or HullClass.StrategicCruiser
                or HullClass.PrototypeExploration
                or HullClass.AttackBattlecruiser
                or HullClass.ExpeditionFrigate
                or HullClass.TacticalDestroyer
                or HullClass.LogisticsFrigate
                or HullClass.CommandDestroyer
                or HullClass.FlagCruiser;

    public static bool Supercapital(int groupId) => groupId is (int)HullClass.Supercarrier or (int)HullClass.Titan;

    public static int Isotopes(double distance, double rate)
    {
        double quantity = distance * rate;
        if (!double.IsFinite(quantity) || quantity is <= 0 or > int.MaxValue)
        {
            throw new InvalidDataException("Invalid jump fuel requirements.");
        }
        return Math.Max(1, (int)quantity);
    }

    public static JumpTimers AfterJump(long now, long previousFatigue, double distance, double multiplier)
    {
        double factor = 1 + distance * multiplier;
        double remaining = Math.Max(0, previousFatigue - now) / (double)TimeSpan.TicksPerSecond;
        long activation = (long)Math.Min(1_800, Math.Max(60 * factor, remaining / 10));
        long fatigue = (long)Math.Min(18_000, Math.Max(600 * factor, remaining * factor));
        return new(now + activation * TimeSpan.TicksPerSecond, now + fatigue * TimeSpan.TicksPerSecond, now);
    }
}
