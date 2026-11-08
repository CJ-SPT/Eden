using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Defenses;

public sealed record AssaultDamageControl(
    long ItemId,
    int TypeId,
    double DurationMilliseconds,
    double CapacitorNeed,
    double ReactivationMilliseconds,
    double ActiveResonance
);

public sealed record AssaultDamageControlSchedule(long StartedAt, long ExpiresAt, long ReadyAt)
{
    public bool Active(long now) => now >= StartedAt && now < ExpiresAt;
}

public sealed record AssaultResistanceBinding(DogmaAttributeId ShipAttribute, DogmaAttributeId ModuleAttribute);

public static class AssaultDamageControlRules
{
    public const int Group = (int)PassiveModuleGroup.DamageControl;
    public const int HeraldType = 97793;
    public const int PassiveEffect = 2302;

    public static IReadOnlyList<AssaultResistanceBinding> Resistances { get; } =
        Array.AsReadOnly<AssaultResistanceBinding>([
            new(DogmaAttributeId.ArmorEmResonance, DogmaAttributeId.ArmorEmResonance),
            new(DogmaAttributeId.ArmorExplosiveResonance, DogmaAttributeId.ArmorExplosiveResonance),
            new(DogmaAttributeId.ArmorKineticResonance, DogmaAttributeId.ArmorKineticResonance),
            new(DogmaAttributeId.ArmorThermalResonance, DogmaAttributeId.ArmorThermalResonance),
            new(DogmaAttributeId.ShieldEmResonance, DogmaAttributeId.ShieldEmResonance),
            new(DogmaAttributeId.ShieldExplosiveResonance, DogmaAttributeId.ShieldExplosiveResonance),
            new(DogmaAttributeId.ShieldKineticResonance, DogmaAttributeId.ShieldKineticResonance),
            new(DogmaAttributeId.ShieldThermalResonance, DogmaAttributeId.ShieldThermalResonance),
            new(DogmaAttributeId.StructureEmResonance, DogmaAttributeId.DamageControlHullEmResonance),
            new(DogmaAttributeId.StructureExplosiveResonance, DogmaAttributeId.DamageControlHullExplosiveResonance),
            new(DogmaAttributeId.StructureKineticResonance, DogmaAttributeId.DamageControlHullKineticResonance),
            new(DogmaAttributeId.StructureThermalResonance, DogmaAttributeId.DamageControlHullThermalResonance),
        ]);

    public static AssaultDamageControl Define(long item, int type, IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        AssaultDamageControl module = new(
            item,
            type,
            attributes.GetValueOrDefault(DogmaAttributeId.Duration),
            attributes.GetValueOrDefault(DogmaAttributeId.CapacitorNeed),
            attributes.GetValueOrDefault(DogmaAttributeId.ModuleReactivationDelay),
            attributes.GetValueOrDefault(DogmaAttributeId.AssaultResistanceMultiplier)
        );
        if (Resistances.Any(r => !ValidResonance(attributes.GetValueOrDefault(r.ModuleAttribute))))
        {
            throw new InvalidDataException("Invalid assault damage control passive resistances.");
        }
        Validate(module);
        return module;
    }

    private static bool ValidResonance(double value) => double.IsFinite(value) && value is > 0 and <= 1;

    private static void Validate(AssaultDamageControl module)
    {
        if (
            !double.IsFinite(module.DurationMilliseconds)
            || module.DurationMilliseconds <= 0
            || !double.IsFinite(module.CapacitorNeed)
            || module.CapacitorNeed < 0
            || !double.IsFinite(module.ReactivationMilliseconds)
            || module.ReactivationMilliseconds < 0
            || !ValidResonance(module.ActiveResonance)
        )
        {
            throw new InvalidDataException("Invalid assault damage control profile.");
        }
    }

    public static AssaultDamageControlSchedule Schedule(AssaultDamageControl module, long now)
    {
        Validate(module);
        if (now < 0)
        {
            throw new InvalidDataException("Invalid assault damage control start time.");
        }
        long expires = checked(now + checked((long)Math.Ceiling(module.DurationMilliseconds * TimeSpan.TicksPerMillisecond)));
        long ready = checked(expires + checked((long)Math.Ceiling(module.ReactivationMilliseconds * TimeSpan.TicksPerMillisecond)));
        return new(now, expires, ready);
    }

    public static IReadOnlyList<DogmaModifier> Modifiers(AssaultDamageControl module, long ship, bool online, bool active)
    {
        Validate(module);
        if (!online)
        {
            return [];
        }
        List<DogmaModifier> result = [];
        foreach (var binding in Resistances)
        {
            result.Add(
                new(
                    ship,
                    binding.ShipAttribute,
                    DogmaOperation.PreMultiply,
                    new DogmaValue.Attribute(module.ItemId, binding.ModuleAttribute)
                )
            );
            if (active)
            {
                // Native effect 7012 assigns each module resonance to attribute
                // 2746. The online effect then projects that value onto the ship.
                result.Add(
                    new(
                        module.ItemId,
                        binding.ModuleAttribute,
                        DogmaOperation.PostAssignment,
                        new DogmaValue.Attribute(module.ItemId, DogmaAttributeId.AssaultResistanceMultiplier)
                    )
                );
            }
        }
        return result.AsReadOnly();
    }
}
