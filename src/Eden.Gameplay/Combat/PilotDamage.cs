using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Insurance;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Combat;

// Motion comes from the authoritative space coordinator, never from RPC arguments.
public sealed record PilotWeaponTarget(long ShipId, SpacePosition Velocity);

public sealed record PilotCombatPresence(long ShipId, SpacePosition Position, SpacePosition Velocity);

public interface IPilotCombatPresence
{
    PilotCombatPresence? Find(int systemId, long shipId);

    PilotCombatPresence? FindOnline(int systemId, long shipId) => Find(systemId, shipId);
}

public sealed record PilotDamage(
    long PilotId,
    long AttackerId,
    long AttackerShipId,
    ShipHealth Health,
    DamageVector Damage,
    double Applied,
    double Quality,
    int WeaponType,
    bool Banked,
    DockedPilot? Capsule,
    InsurancePayout? Insurance
)
{
    public IReadOnlyList<InventoryChange> InventoryChanges { get; init; } = [];
}

public sealed record PilotLossResult(InsurancePayout? Insurance, IReadOnlyList<InventoryChange> Changes);

public interface IPilotDamageEvents
{
    void Publish(PilotDamage damage);
}
