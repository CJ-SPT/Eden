using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Drones;

// Build 3662168 appConst entityIdle/Combat/Approaching/Departing/Departing2.
public enum DroneActivity
{
    Idle = 0,
    Fighting = 1,
    Approaching = 3,
    Returning = 4,
    ReturningToBay = 5,
}

public enum DroneOrder
{
    Engage,
    Orbit,
    ReturnToBay,
    Reconnect,
    Abandon,
}

public enum DroneSkill
{
    Drones = 3436,
    DroneAvionics = 3437,
    DroneInterfacing = 3442,
    DroneSharpshooting = 23606,
    DroneNavigation = 12305,
    DroneDurability = 23618,
    AdvancedDroneAvionics = 23566,
    LightDroneOperation = 24241,
    MediumDroneOperation = 33699,
    AmarrDroneSpecialization = 12484,
    MinmatarDroneSpecialization = 12485,
    GallenteDroneSpecialization = 12486,
    CaldariDroneSpecialization = 12487,
}

public sealed record DroneProfile(
    int TypeId,
    double Volume,
    double Bandwidth,
    double Mass,
    float Radius,
    float Agility,
    float MaximumSpeed,
    float OrbitSpeed,
    double OrbitRange,
    double Optimal,
    double Falloff,
    double Tracking,
    double SignatureReference,
    double CycleMilliseconds,
    DamageVector Damage,
    HullDefense Defense
);

public sealed record DroneContact(
    long Id,
    long PilotId,
    long ShipId,
    int CorporationId,
    int SystemId,
    DroneProfile Profile,
    DroneActivity Activity,
    long TargetId,
    SpacePosition Position,
    SpacePosition Velocity,
    ShipHealth Health,
    long ShotAt = 0,
    long ShotTargetId = 0,
    float SpeedLimit = 0,
    bool Orbiting = true
);

public sealed record DroneLaunch(long StackId, IReadOnlyList<long> DroneIds);

public sealed record DroneStack(long Id, int Quantity);

public sealed record DroneOperation(IReadOnlyList<InventoryChange> Changes, IReadOnlyList<DroneLaunch> Launches);

public sealed record DroneHit(
    long DroneId,
    int TypeId,
    long TargetId,
    double Applied,
    DamageVector Damage,
    ShipHealth Health,
    double Quality
);

public sealed record DroneUpdate(IReadOnlyList<InventoryChange> Changes, IReadOnlyList<DroneHit> Hits, bool ControlChanged = false);

public sealed record DroneSettings(bool Aggressive = true, bool FocusFire = false);

public sealed record DroneControl(int MaximumDrones, int Range, double UsedBandwidth, DroneSettings Settings);

public sealed record DroneNpcWeapon(
    DamageVector Damage,
    double Optimal,
    double Falloff,
    double Tracking,
    double SignatureReference,
    double CycleMilliseconds
);

public sealed record DroneRetaliation(DamageResult Result, double Quality);

public interface IPilotDrones
{
    DroneOperation Launch(long pilotId, IReadOnlyList<DroneStack> stacks, long now);
    void Order(long pilotId, IReadOnlyList<long> drones, DroneOrder order, long? targetId, IReadOnlySet<long> locks, long now);
    IReadOnlyList<InventoryChange> Scoop(long pilotId, IReadOnlyList<long> drones, InventoryFlag hold, long now);
    bool HasControlled(long pilotId, long shipId);
    DroneUpdate Advance(long pilotId, long now, int stamp);
    IReadOnlyList<DroneContact> List(int systemId, long now);
    bool WasDestroyed(long droneId);
    IReadOnlyDictionary<long, ShipHealth> BayHealth(long pilotId, long shipId, IReadOnlySet<long> ids);
    DroneControl Control(long pilotId);
    DroneProfile? Profile(long pilotId, int typeId);
    void ChangeSettings(long pilotId, bool? aggressive, bool? focusFire);
    void Release(long pilotId, long shipId);
}
