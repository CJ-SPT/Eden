using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Combat;

public enum WeaponActivation
{
    Laser,
    AmmoFed,
}

public sealed record CombatWeapon(
    long ItemId,
    int TypeId,
    int ChargeTypeId,
    DamageVector Damage,
    double CycleMilliseconds,
    double Optimal,
    double Falloff,
    double Tracking,
    double SignatureResolution,
    double CapacitorNeed,
    int EffectId = (int)DogmaEffect.LaserFired
);

public sealed record CombatReward(
    long Id,
    long PilotId,
    long NpcId,
    int TypeId,
    int SystemId,
    long Time,
    long AmountCents,
    long BalanceCents,
    long TaxCents = 0
);

public sealed record CombatShot(
    CombatWeapon Weapon,
    double Applied,
    double Quality,
    ShipHealth Health,
    double Capacitor,
    bool Destroyed,
    CombatReward? Reward,
    Magazine? Magazine = null
);

public sealed record CombatVolley(CombatShot Shot, IReadOnlyList<CombatWeapon> Weapons, IReadOnlyList<Magazine> Magazines);

public interface IPilotCombat
{
    CombatVolley FirePlayerBank(
        long pilotId,
        long moduleId,
        PilotWeaponTarget target,
        SpacePosition velocity,
        long now,
        double roll,
        WeaponActivation? activation = null
    ) => throw new InvalidDataException("Pilot turret combat is unavailable.");

    IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(long pilotId, InventoryItem module);
    CombatShot Fire(
        long pilotId,
        long moduleId,
        long targetId,
        SpacePosition velocity,
        long now,
        double roll,
        WeaponActivation? activation = null,
        double? crystalRoll = null
    );
    CombatVolley FireBank(
        long pilotId,
        long moduleId,
        long targetId,
        SpacePosition velocity,
        long now,
        double roll,
        WeaponActivation? activation = null
    )
    {
        var shot = Fire(pilotId, moduleId, targetId, velocity, now, roll, activation);
        return new(shot, [shot.Weapon], shot.Magazine is { } magazine ? [magazine] : []);
    }
    IReadOnlyList<CombatReward> Rewards(long pilotId);
}
