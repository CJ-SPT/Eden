using Eden.Gameplay.Dogma;

namespace Eden.Gameplay.Exploration;

public readonly record struct IncursionSystemEffect(
    long TaleId,
    int SystemId,
    IncursionScene SceneType,
    long EndsAt,
    double Influence,
    int? SourceTypeId,
    double WeaponDamageMultiplier,
    double ResonanceMultiplier
)
{
    public bool JamsStandardCynos => true;
    public decimal BountyMultiplier => 0.5m;
}

public interface IIncursionSystemEffects
{
    IncursionSystemEffect? Read(int systemId, long now);
}

public static class IncursionSystemEffectRules
{
    private static readonly DogmaAttributeId[] s_resonanceAttributes =
    [
        DogmaAttributeId.ShieldEmResonance,
        DogmaAttributeId.ShieldExplosiveResonance,
        DogmaAttributeId.ShieldKineticResonance,
        DogmaAttributeId.ShieldThermalResonance,
        DogmaAttributeId.ArmorEmResonance,
        DogmaAttributeId.ArmorExplosiveResonance,
        DogmaAttributeId.ArmorKineticResonance,
        DogmaAttributeId.ArmorThermalResonance,
    ];

    public static IncursionSystemEffect Create(IncursionFocus focus, IncursionSystem system)
    {
        // Build 3662168 OffensiveDefensiveReduction (effect 4728). Attribute 1686 reduces damage.
        (int? TypeId, double Penalty) profile = system.SceneType switch
        {
            IncursionScene.Vanguard => (3069, 0.1),
            IncursionScene.Assault => (3493, 0.25),
            IncursionScene.Headquarters => (3494, 0.5),
            _ => (null, 0),
        };
        double influence = Math.Clamp(focus.Influence, 0, 1);
        double penalty = influence * profile.Penalty;
        return new(focus.TaleId, system.SystemId, system.SceneType, focus.EndsAt, influence, profile.TypeId, 1 - penalty, 1 + penalty);
    }

    public static void ApplyResonance(Dictionary<DogmaAttributeId, double> attributes, double multiplier)
    {
        // The resolved shield and armor attributes receive one exempt post-multiplier.
        foreach (var attribute in s_resonanceAttributes)
        {
            attributes[attribute] = attributes.GetValueOrDefault(attribute, 1) * multiplier;
        }
    }
}
