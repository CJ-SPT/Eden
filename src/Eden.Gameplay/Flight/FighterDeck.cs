using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Structures;

namespace Eden.Gameplay.Flight;

public enum FighterClass
{
    Light,
    Support,
    Heavy,
}

public sealed record FighterTube(int Flag, long SquadronId, int TypeId, int Count);

public sealed record FighterInSpace(int Flag, long SquadronId, int TypeId, int Count, DroneActivity Activity)
{
    public FighterMobilityCycle? Mobility { get; init; }
    public bool MobilityActive { get; init; }
    public FighterSalvoCycle? Salvo { get; init; }
    public bool SalvoActive { get; init; }
    public FighterTackleCycle? Tackle { get; init; }
}

public sealed record FighterDeckSnapshot(IReadOnlyList<FighterTube> Tubes, IReadOnlyList<FighterInSpace> Squadrons);

public sealed record FighterLaunch(FighterInSpace Squadron, InventoryChange Inventory);

public sealed record FighterTubeChange(int Flag, FighterTube? Tube, IReadOnlyList<InventoryChange> Inventory);

public interface IPilotFighterDeck
{
    FighterDeckSnapshot Read(long pilotId, long shipId);
    IReadOnlyList<FighterLaunch> Launch(long pilotId, long shipId, IReadOnlyList<int> flags, long now);
    FighterTubeChange Load(long pilotId, long shipId, long stackId, int flag);
    FighterTubeChange Unload(long pilotId, long shipId, int flag);
    void SetMobility(long pilotId, long shipId, IReadOnlyList<long> squadrons, bool activate, long now, SpacePosition? destination = null);
}

public interface IPilotFighterOrders
{
    void OrderFighters(long pilotId, IReadOnlyList<long> squadrons, DroneOrder order, long? target, IReadOnlySet<long> locks, long now);
    void SetSecondaryAbility(
        long pilotId,
        long shipId,
        IReadOnlyList<long> squadrons,
        long? target,
        bool locked,
        long now,
        SpacePosition? point = null
    );
}

public sealed record StandupFighterBay(double Capacity, double Used);

public interface IStandupFighterInventory
{
    StandupFighterBay Bay(long pilotId, long structureId);
    IReadOnlyList<InventoryChange> MoveStandupFighters(long pilotId, long structureId, long itemId, int? quantity, bool deposit);
}

public interface IStandupFighterFlight
{
    void Assign(long pilotId, long structureId);
    void Release(long pilotId, long structureId, long now);
    void Recall(long pilotId, long structureId, IReadOnlyList<long> squadrons, long now);
    void SetMobility(
        StructureControlToken token,
        IReadOnlyList<long> squadrons,
        bool activate,
        long now,
        SpacePosition? destination = null
    );
    void AdvanceReturns(long now);
}

public static class FighterDeckRules
{
    // Build 3662168 inventory flags, from the client inventory constants.
    public const int FirstTube = 159;
    public const int MaximumTubes = 5;
    public const int SecondarySlot = 2;
    public const int Caiman = 45647;
    public const int Komodo = 45649;

    public static bool IsTube(int flag) => flag is >= FirstTube and < FirstTube + MaximumTubes;

    public static bool Supports(int group, int typeId) =>
        CapitalTravelRules.CarrierConduitHull(group)
        || (group, typeId) is ((int)HullClass.Dreadnought, Caiman) or ((int)HullClass.Titan, Komodo);

    public static bool FighterHullModifier(int skill, DogmaAttributeId attribute, DogmaOperation operation) =>
        (skill, operation) switch
        {
            ((int)HullBonusSkill.Fighters, DogmaOperation.PostPercent) => IsDamageBonus(attribute)
                || attribute
                    is DogmaAttributeId.MaxVelocity
                        or DogmaAttributeId.ShieldCapacity
                        or DogmaAttributeId.FighterAttackMissileExplosionRadius
                        or DogmaAttributeId.FighterAttackMissileExplosionVelocity
                        or DogmaAttributeId.FighterMissilesExplosionRadius
                        or DogmaAttributeId.FighterMissilesExplosionVelocity,
            ((int)HullBonusSkill.SupportFighters, DogmaOperation.PostPercent) => attribute
                is DogmaAttributeId.ShieldCapacity
                    or DogmaAttributeId.MaxVelocity
                    or DogmaAttributeId.FighterNeutralizeAmount
                    or DogmaAttributeId.FighterWebStrength
                    or DogmaAttributeId.FighterAfterburnerSpeed
                    or DogmaAttributeId.FighterEcmRadar
                    or DogmaAttributeId.FighterEcmLadar
                    or DogmaAttributeId.FighterEcmGravimetric
                    or DogmaAttributeId.FighterEcmMagnetometric,
            ((int)HullBonusSkill.Fighters, DogmaOperation.PostMultiply) => attribute == DogmaAttributeId.MaxVelocity,
            ((int)HullBonusSkill.LightFighters, DogmaOperation.PostMultiply) => IsDamageBonus(attribute),
            _ => false,
        };

    private static bool IsDamageBonus(DogmaAttributeId attribute) =>
        attribute
            is DogmaAttributeId.FighterAttackMissileDamageMultiplier
                or DogmaAttributeId.FighterTurretDamageMultiplier
                or DogmaAttributeId.FighterMissilesDamageMultiplier;

    public static FighterClass? Class(IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        attributes.GetValueOrDefault(DogmaAttributeId.LightFighterSquadron) == 1 ? FighterClass.Light
        : attributes.GetValueOrDefault(DogmaAttributeId.SupportFighterSquadron) == 1 ? FighterClass.Support
        : attributes.GetValueOrDefault(DogmaAttributeId.HeavyFighterSquadron) == 1 ? FighterClass.Heavy
        : null;

    public static FighterClass? StandupClass(IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        attributes.GetValueOrDefault(DogmaAttributeId.StandupLightFighterSquadron) == 1 ? FighterClass.Light
        : attributes.GetValueOrDefault(DogmaAttributeId.StandupSupportFighterSquadron) == 1 ? FighterClass.Support
        : attributes.GetValueOrDefault(DogmaAttributeId.StandupHeavyFighterSquadron) == 1 ? FighterClass.Heavy
        : null;

    public static int StandupBays(FighterClass kind, IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        checked(
            (int)
                attributes.GetValueOrDefault(
                    kind switch
                    {
                        FighterClass.Light => DogmaAttributeId.StandupLightFighterSlots,
                        FighterClass.Support => DogmaAttributeId.StandupSupportFighterSlots,
                        _ => DogmaAttributeId.StandupHeavyFighterSlots,
                    }
                )
        );

    public static int Bays(FighterClass kind, IReadOnlyDictionary<DogmaAttributeId, double> hull) =>
        checked(
            (int)
                hull.GetValueOrDefault(
                    kind switch
                    {
                        FighterClass.Light => DogmaAttributeId.LightFighterSlots,
                        FighterClass.Support => DogmaAttributeId.SupportFighterSlots,
                        _ => DogmaAttributeId.HeavyFighterSlots,
                    }
                )
        );
}
