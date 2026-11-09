using Eden.Data.Catalog;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Mining;

internal static class MiningCrystalCatalog
{
    private const int AbyssalMetaGroup = 15;
    private static readonly CatalogTypeRule s_support = new(typeof(MiningCrystalCatalog).FullName!, ValidateSupport);

    internal static bool Supported(CatalogDbContext catalog, int typeId, int group) => s_support.Supports(catalog, typeId, group);

    private static bool ValidateSupport(CatalogDbContext catalog, int typeId, int group)
    {
        if (group != MiningCrystalRules.Group)
        {
            return false;
        }
        var type = catalog.Read(CatalogData.Client.Types, (long)typeId);
        var attributes = catalog.FittingAttributes(typeId);
        var effects = PassiveCatalog.Effects(catalog, typeId);
        if (
            type?.Published != 1
            || type.MetaGroupID == AbyssalMetaGroup
            || type.Volume is not > 0
            || attributes.GetValueOrDefault(DogmaAttributeId.MiningCrystalTypeList) is not > 0
            || attributes.GetValueOrDefault(DogmaAttributeId.MiningCrystalYieldMultiplier) is not > 0
            || effects.Count != FittingCatalog.Effects(catalog, typeId).Count
            || effects.Count is not (2 or 3)
        )
        {
            return false;
        }
        try
        {
            _ = CrystalRules.Use(attributes, 0, 0);
            _ = CatalogTypeLists.Read(catalog, checked((long)attributes[DogmaAttributeId.MiningCrystalTypeList]));
        }
        catch (InvalidDataException)
        {
            return false;
        }
        return effects.All(effect =>
                effect.EffectCategory == (int)PassiveEffectPhase.Fitted
                && MiningCrystalRules.Modifiers.Any(m => m.EffectId == effect.EffectID)
                && effect.ModifierInfo is { Length: > 0 }
                && effect.ModifierInfo.Value.Length == MiningCrystalRules.Modifiers.Count(m => m.EffectId == effect.EffectID)
                && effect.ModifierInfo.Value.All(source =>
                    source.Domain == "otherID"
                    && source.Func == "ItemModifier"
                    && MiningCrystalRules.Modifiers.Any(m =>
                        m.EffectId == effect.EffectID
                        && (int)m.Attribute == source.ModifiedAttributeID
                        && (int)m.Source == source.ModifyingAttributeID
                        && (int)m.Operation == source.Operation
                    )
                )
            )
            && effects.Any(effect => effect.EffectID == (int)MiningCrystalEffect.YieldAndResidue)
            && effects.Any(effect => effect.EffectID == (int)MiningCrystalEffect.Capacitor);
    }
}
