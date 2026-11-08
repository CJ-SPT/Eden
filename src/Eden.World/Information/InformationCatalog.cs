using Eden.Gameplay.Dogma;
using Eden.Gameplay.Information;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Information;

internal static class InformationCatalog
{
    private const int AbyssalMetaGroup = 15;
    private static readonly CatalogTypeRule s_support = new(typeof(InformationCatalog).FullName!, Validate);

    internal static bool Supported(CatalogDbContext catalog, int typeId, int group) => s_support.Supports(catalog, typeId, group);

    private static bool Validate(CatalogDbContext catalog, int typeId, int group)
    {
        if (!Enum.IsDefined((InformationGroup)group))
        {
            return false;
        }
        var type = catalog.Read(CatalogData.Client.Types, (long)typeId);
        var effects = PassiveCatalog.Effects(catalog, typeId);
        var attributes = catalog.FittingAttributes(typeId);
        var active = InformationRules.Effect(group);
        if (
            type?.Published != 1
            || type.MetaGroupID == AbyssalMetaGroup
            || effects.Count != FittingCatalog.Effects(catalog, typeId).Count
            || !FittingCatalog.Effects(catalog, typeId).Contains(DogmaEffect.Online)
            || FittingCatalog.DefaultEffect(catalog, typeId) != active
            || effects.Any(e =>
                e.EffectID != (int?)active
                && e.EffectID != (int)DogmaEffect.Online
                && (
                    e.EffectCategory is not (0 or 4)
                    || (e.ModifierInfo ?? []).Any(m => PassiveCatalog.Parse(m, e.EffectCategory!.Value) is null)
                )
            )
        )
        {
            return false;
        }
        if (active is null)
        {
            return group == (int)InformationGroup.MiningSurveyChipset
                && attributes.GetValueOrDefault(DogmaAttributeId.MiningScannerUpgrade) == 1;
        }
        var operation = effects.SingleOrDefault(e => e.EffectID == (int)active.Value);
        if (
            operation is null
            || operation.DurationAttributeID != (int)DogmaAttributeId.Duration
            || operation.DischargeAttributeID != (int)DogmaAttributeId.CapacitorNeed
            || operation.EffectCategory
                != (active == DogmaEffect.AutomaticTargeting ? (int)PassiveEffectPhase.Active : (int)PassiveEffectPhase.Targeted)
            || (operation.ModifierInfo ?? []).Length != 0
        )
        {
            return false;
        }
        try
        {
            _ = InformationRules.Define(0, typeId, active.Value, attributes);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
