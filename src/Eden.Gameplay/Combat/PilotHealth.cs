using Eden.Gameplay.Dogma;

namespace Eden.Gameplay.Combat;

public sealed record ShipHealth(long ShipId, HullDefense Defense, HullDamage Remaining)
{
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
