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
        && catalog.FittingAttributes(type).GetValueOrDefault(DogmaAttributeId.Duration) == CapitalModeRules.CycleMilliseconds
        && !Modifiers(catalog, type).IsDefaultOrEmpty;

    internal static ImmutableArray<PassiveModifier> Modifiers(CatalogDbContext catalog, int type) => catalog.Definition(s_modifiers, type);

    private static ImmutableArray<PassiveModifier> Load(CatalogDbContext catalog, int type)
    {
        var effect = CapitalModeRules.Effect(type);
        var source = PassiveCatalog.Effects(catalog, type).SingleOrDefault(e => e.EffectID == (int?)effect);
        if (source?.ModifierInfo is not { } modifiers || source.EffectCategory is not long phase)
        {
            return [];
        }
        // These two activation effects use the same assignment and owner selectors as supported rigs.
        var parsed = modifiers.Select(m => PassiveCatalog.Parse(m, phase, rig: true)).ToArray();
        return parsed.Any(m => m is null) ? [] : parsed.Select(m => m!).ToImmutableArray();
    }
}
