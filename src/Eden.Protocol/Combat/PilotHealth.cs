using Eden.Protocol.Docking;

namespace Eden.Protocol.Combat;

public sealed record ShipHealth(long ShipId, HullDefense Defense, HullDamage Remaining)
{
    // damageStateValue.pyj accepts (shield fraction, tau in milliseconds), then
    // armor and structure fractions. dogma.const.dgmTauConstant is 10,000 ticks/ms.
    public object?[] DamageState()
    {
        var fractions = Remaining.Fractions(Defense);
        return
        [
            new object?[] { fractions.Shield, ChargedResource.TimeConstant(Defense.ShieldRechargeMilliseconds) },
            fractions.Armor,
            fractions.Structure,
        ];
    }

    public static HullDefense DefenseFrom(IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        double Value(DogmaAttributeId id, double fallback = 0) => attributes.GetValueOrDefault(id, fallback);
        return new(
            Value(DogmaAttributeId.ShieldCapacity),
            Value(DogmaAttributeId.ArmorHitPoints),
            Value(DogmaAttributeId.StructureHitPoints),
            Value(DogmaAttributeId.ShieldRechargeTime),
            new(
                Value(DogmaAttributeId.ShieldEmResonance, 1),
                Value(DogmaAttributeId.ShieldExplosiveResonance, 1),
                Value(DogmaAttributeId.ShieldKineticResonance, 1),
                Value(DogmaAttributeId.ShieldThermalResonance, 1)
            ),
            new(
                Value(DogmaAttributeId.ArmorEmResonance, 1),
                Value(DogmaAttributeId.ArmorExplosiveResonance, 1),
                Value(DogmaAttributeId.ArmorKineticResonance, 1),
                Value(DogmaAttributeId.ArmorThermalResonance, 1)
            ),
            new(
                Value(DogmaAttributeId.StructureEmResonance, 1),
                Value(DogmaAttributeId.StructureExplosiveResonance, 1),
                Value(DogmaAttributeId.StructureKineticResonance, 1),
                Value(DogmaAttributeId.StructureThermalResonance, 1)
            )
        );
    }
}

public interface IPilotHealth
{
    ShipHealth Read(long pilotId, long now);
    bool WasDestroyed(long shipId);
}

public enum DefenseSkill
{
    Mechanics = 3392,
    HullUpgrades = 3394,
    ShieldOperation = 3416,
    ShieldManagement = 3419,
}
