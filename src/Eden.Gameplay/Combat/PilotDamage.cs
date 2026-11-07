using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Insurance;

namespace Eden.Gameplay.Combat;

// Motion comes from the authoritative space coordinator, never from RPC arguments.
public sealed record PilotWeaponTarget(long ShipId, SpacePosition Velocity);

public sealed record PilotDamage(
    long PilotId,
    long AttackerId,
    long AttackerShipId,
    CombatShot Shot,
    bool Banked,
    DockedPilot? Capsule,
    InsurancePayout? Insurance
);

public interface IPilotDamageEvents
{
    void Publish(PilotDamage damage);
}
