using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Propulsion;

internal static class SpatialModuleCatalog
{
    private const int AbyssalMetaGroup = 15;
    private static readonly CatalogTypeRule s_support = new(typeof(SpatialModuleCatalog).FullName!, Validate);

    internal static bool Supported(CatalogDbContext catalog, int type, int group) => s_support.Supports(catalog, type, group);

    internal static IReadOnlyList<PassiveModifier> Modifiers(CatalogDbContext catalog, int type) =>
        PassiveCatalog
            .Effects(catalog, type)
            .SelectMany(e => (e.ModifierInfo ?? []).Select(m => PassiveCatalog.Parse(m, e.EffectCategory!.Value)!))
            .ToArray();

    private static bool Validate(CatalogDbContext catalog, int type, int group)
    {
        if (group is not (SpatialModuleRules.CloakGroup or SpatialModuleRules.MicroJumpGroup))
        {
            return false;
        }
        var source = catalog.Read(CatalogData.Client.Types, (long)type);
        var effect = FittingCatalog.DefaultEffect(catalog, type);
        if (source?.Published != 1 || source.MetaGroupID == AbyssalMetaGroup || effect is null)
        {
            return false;
        }
        if (group == SpatialModuleRules.CloakGroup ? !SpatialModuleRules.IsCloak(effect.Value) : effect != DogmaEffect.MicroJumpDrive)
        {
            return false;
        }
        HashSet<int> allowed =
        [
            (int)DogmaEffect.HighPower,
            (int)DogmaEffect.MediumPower,
            (int)DogmaEffect.Online,
            (int)DogmaEffect.CloakingScanResolution,
            (int)DogmaEffect.CovertCloakCpu,
            (int)effect,
        ];
        var effects = PassiveCatalog.Effects(catalog, type);
        if (
            effects.Any(e =>
                !allowed.Contains((int)e.EffectID!.Value)
                || (e.ModifierInfo ?? []).Any(m => PassiveCatalog.Parse(m, e.EffectCategory!.Value) is null)
            )
        )
        {
            return false;
        }
        try
        {
            _ = SpatialModuleRules.Define(0, type, effect.Value, catalog.FittingAttributes(type));
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
