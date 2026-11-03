using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Server.Catalog;

namespace Eden.Server.Persistence;

internal static class HybridChargeCatalog
{
    private const int AbyssalMetaGroup = 15;

    internal static bool Supported(CatalogDbContext catalog, int typeId, int group)
    {
        if (!AmmunitionDefinitions.HybridGroup(group))
        {
            return false;
        }
        var type = catalog.Set<SourceRecord>().Find("client/types", PassiveCatalog.Key(typeId))?.Read<ClientTypes>();
        var attributes = PilotFittingStore.ReadAttributes(catalog, typeId);
        if (
            !AmmunitionDefinitions.HybridGroup(group)
            || type?.Published != 1
            || type.MetaGroupID == AbyssalMetaGroup
            || !AmmunitionDefinitions.ValidSize(attributes.GetValueOrDefault(DogmaAttributeId.ChargeSize))
        )
        {
            return false;
        }
        var effects = PassiveCatalog.Effects(catalog, typeId);
        return effects.Count >= 2
            && effects.Count == FittingCatalog.Effects(catalog, typeId).Count
            && effects.Any(e => e.EffectID == (int)HybridChargeEffect.Range)
            && effects.All(e =>
            {
                var expected = AmmunitionDefinitions.HybridModifiers.SingleOrDefault(m => (int)m.Effect == e.EffectID);
                return expected is not null
                    && e.EffectCategory == (int)PassiveEffectPhase.Fitted
                    && e.ModifierInfo is [{ } modifier]
                    && modifier.Func == "ItemModifier"
                    && modifier.Domain == "otherID"
                    && modifier.ModifiedAttributeID == (int)expected.Attribute
                    && modifier.ModifyingAttributeID == (int)expected.Source
                    && modifier.Operation == (int)expected.Operation;
            });
    }
}
