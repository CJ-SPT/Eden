using Eden.Protocol.Docking;

namespace Eden.Protocol.Combat;

public sealed record NpcWeaponContact(long TargetId, int TurretTypeId, long StartedAt, double Duration);
public sealed record NpcShot(long NpcId, int TypeId, NpcWeaponContact Weapon, DamageVector Damage, double Applied,
    double Quality, ShipHealth Health, DockedPilot? Capsule);

public interface INpcCombat
{
    IReadOnlyList<long> Aggressors(long shipId);
    NpcShot? Fire(long pilotId, long shipId, long npcId, SpacePosition velocity, long now, double roll);
    void Disengage(long shipId, long? npcId = null);
}
