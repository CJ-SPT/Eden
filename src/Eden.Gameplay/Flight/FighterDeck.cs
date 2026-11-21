using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;

namespace Eden.Gameplay.Flight;

public enum FighterClass
{
    Light,
    Support,
    Heavy,
}

public sealed record FighterTube(int Flag, long SquadronId, int TypeId, int Count);

public sealed record FighterInSpace(int Flag, long SquadronId, int TypeId, int Count, DroneActivity Activity);

public sealed record FighterDeckSnapshot(IReadOnlyList<FighterTube> Tubes, IReadOnlyList<FighterInSpace> Squadrons);

public sealed record FighterLaunch(FighterInSpace Squadron, InventoryChange Inventory);

public sealed record FighterTubeChange(int Flag, FighterTube? Tube, IReadOnlyList<InventoryChange> Inventory);

public interface IPilotFighterDeck
{
    FighterDeckSnapshot Read(long pilotId, long shipId);
    IReadOnlyList<FighterLaunch> Launch(long pilotId, long shipId, IReadOnlyList<int> flags, long now);
    FighterTubeChange Load(long pilotId, long shipId, long stackId, int flag);
    FighterTubeChange Unload(long pilotId, long shipId, int flag);
}

public interface IPilotFighterOrders
{
    void OrderFighters(long pilotId, IReadOnlyList<long> squadrons, DroneOrder order, long? target, IReadOnlySet<long> locks, long now);
}

public static class FighterDeckRules
{
    // Build 3662168 inventory flags, from the client inventory constants.
    public const int FirstTube = 159;
    public const int MaximumTubes = 5;
    public const int Caiman = 45647;
    public const int Komodo = 45649;

    public static bool IsTube(int flag) => flag is >= FirstTube and < FirstTube + MaximumTubes;

    public static bool Supports(int group, int typeId) =>
        group is (int)HullClass.Carrier or (int)HullClass.Supercarrier
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
