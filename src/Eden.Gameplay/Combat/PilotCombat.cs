using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Combat;

public enum WeaponActivation
{
    Laser,
    AmmoFed,
    Disintegrator,
    Vorton,
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
    int EffectId = (int)DogmaEffect.LaserFired,
    DisintegratorSpool? Spool = null,
    VortonProfile? Vorton = null
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
    Magazine? Magazine = null,
    int SpoolCycles = 0
);

public sealed record CombatVolley(CombatShot Shot, IReadOnlyList<CombatWeapon> Weapons, IReadOnlyList<Magazine> Magazines)
{
    public IReadOnlyList<CombatShot> SecondaryHits { get; init; } = [];
}

public sealed record AdvancedWeaponCycle(
    WeaponActivation Activation,
    int PaidCycles = 0,
    int? PreviousCharge = null,
    IReadOnlyList<SmartbombShip>? Ships = null,
    IReadOnlyList<long>? Npcs = null,
    IReadOnlyList<long>? Drones = null
);

public interface IPilotCombat
{
    CombatVolley FireAdvanced(
        long pilotId,
        long moduleId,
        long targetId,
        PilotWeaponTarget? player,
        SpacePosition velocity,
        long now,
        double roll,
        AdvancedWeaponCycle cycle
    ) => throw new InvalidDataException("Advanced weapons are unavailable.");

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
