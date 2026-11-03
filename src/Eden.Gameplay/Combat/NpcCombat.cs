using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Combat;

public sealed record NpcWeaponContact(long TargetId, int TurretTypeId, long StartedAt, double Duration);

public sealed record NpcTargetCandidate(long PilotId, long ShipId);

public sealed record NpcShot(
    long NpcId,
    int TypeId,
    NpcWeaponContact Weapon,
    DamageVector Damage,
    double Applied,
    double Quality,
    ShipHealth Health,
    DockedPilot? Capsule
);

public interface INpcCombat
{
    void Acquire(IReadOnlyList<NpcTargetCandidate> candidates, long now);
    IReadOnlyList<long> Aggressors(long shipId);
    NpcShot? Fire(long pilotId, long shipId, long npcId, SpacePosition velocity, long now, double roll, int destinyStamp);
    void Disengage(long shipId, long? npcId = null);
}
