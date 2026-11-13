using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Combat;

public sealed record MissileFiringState(SpacePosition Velocity, float MaximumSpeed, SpacePosition RearDirection, int Stamp);

public enum MissileTargetKind
{
    Npc,
    Pilot,
}

public sealed record MissileAggressor(long Id, SpacePosition Position);

public sealed record MissileLauncher(
    long ItemId,
    int TypeId,
    int ChargeTypeId,
    double CycleMilliseconds,
    bool Automatic,
    bool Repeats = true
);

public sealed record MissileLaunch(MissileLauncher Launcher, MissileContact Projectile, Magazine Magazine);

public sealed record MissileImpact(
    long ProjectileId,
    long PilotId,
    long SourceShipId,
    long TargetId,
    int LauncherTypeId,
    int ChargeTypeId,
    ShipHealth Health,
    DamageVector Damage,
    double Applied,
    bool Destroyed,
    CombatReward? Reward
);

public interface IPilotMissiles
{
    MissileLaunch Launch(
        long pilotId,
        long moduleId,
        long? targetId,
        IReadOnlySet<long> lockedTargets,
        MissileFiringState motion,
        long now
    );
    IReadOnlyList<MissileLaunch> LaunchBank(
        long pilotId,
        long moduleId,
        long? targetId,
        IReadOnlySet<long> lockedTargets,
        MissileFiringState motion,
        long now
    ) => [Launch(pilotId, moduleId, targetId, lockedTargets, motion, now)];
    void Advance(int systemId, long now);
    IReadOnlyList<MissileContact> List(int systemId);
    bool Impacted(long projectileId);
    IReadOnlyList<MissileImpact> TakeImpacts(long pilotId);
    void Cancel(long pilotId, long shipId);
}
