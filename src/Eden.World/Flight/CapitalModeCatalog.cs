using System.Collections.Immutable;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Flight;

internal static class CapitalModeCatalog
{
    private static readonly CatalogDefinition<ImmutableArray<PassiveModifier>> s_modifiers = new("capital-mode", Load);

    internal static bool Supported(CatalogDbContext catalog, int type) =>
        CapitalModeRules.Effect(type) is { } effect
        && catalog.Read(CatalogData.Client.Types, (long)type)?.Published == 1
        && FittingCatalog.DefaultEffect(catalog, type) == effect
        && catalog.FittingAttributes(type).GetValueOrDefault(DogmaAttributeId.Duration) == CapitalModeRules.DurationMilliseconds(effect)
        && !Modifiers(catalog, type).IsDefaultOrEmpty;

    internal static ImmutableArray<PassiveModifier> Modifiers(CatalogDbContext catalog, int type) => catalog.Definition(s_modifiers, type);

    internal static bool SharesStackingPenalty(int type, PassiveModifier modifier) =>
        CapitalModeRules.Effect(type) == DogmaEffect.Bastion
        && (
            // Bastion's pre-multipliers share the damage control resonance buckets.
            modifier.Operation == DogmaOperation.PreMultiply
            || modifier.Attribute
                is DogmaAttributeId.MaxRange
                    or DogmaAttributeId.AccuracyFalloff
                    or DogmaAttributeId.ShieldBonus
                    or DogmaAttributeId.ArmorRepairAmount
            || ModifiesMissileVelocity(modifier)
        );

    internal static bool ModifiesMissileVelocity(PassiveModifier modifier) =>
        modifier is { Attribute: DogmaAttributeId.MaxVelocity, Selector: ModifierSelector.RequiredSkill };

    private static ImmutableArray<PassiveModifier> Load(CatalogDbContext catalog, int type)
    {
        var effect = CapitalModeRules.Effect(type);
        var source = PassiveCatalog.Effects(catalog, type).SingleOrDefault(e => e.EffectID == (int?)effect);
        if (source?.ModifierInfo is not { } modifiers || source.EffectCategory is not long phase)
        {
            return [];
        }
        // Capital modes use the same assignment and owner selectors as supported rigs.
        var parsed = modifiers.Select(m => PassiveCatalog.Parse(m, phase, rig: true)).ToArray();
        return parsed.Any(m => m is null) ? [] : parsed.Select(m => m!).ToImmutableArray();
    }
}
