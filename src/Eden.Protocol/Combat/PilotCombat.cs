using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Docking;

namespace Eden.Protocol.Combat;

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
    int EffectId = (int)DogmaEffect.CivilianWeapon
);

public sealed record CombatReward(
    long Id,
    long PilotId,
    long NpcId,
    int TypeId,
    int SystemId,
    long Time,
    long AmountCents,
    long BalanceCents
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

public interface IPilotCombat
{
    IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(long pilotId, InventoryItem module);
    CombatShot Fire(long pilotId, long moduleId, long targetId, SpacePosition velocity, long now, double roll, string? effectName = null);
    IReadOnlyList<CombatReward> Rewards(long pilotId);
}
