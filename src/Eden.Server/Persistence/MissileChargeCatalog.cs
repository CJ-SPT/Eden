using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Server.Catalog;

namespace Eden.Server.Persistence;

internal static class MissileChargeCatalog
{
    private const int AbyssalMetaGroup = 15;

    internal static bool Supported(CatalogDbContext catalog, int typeId, int group)
    {
        if (!MissileDefinitions.ChargeGroups.Contains(group))
        {
            return false;
        }
        var type = catalog.Set<SourceRecord>().Find("client/types", PassiveCatalog.Key(typeId))?.Read<ClientTypes>();
        var attributes = PilotFittingStore.ReadAttributes(catalog, typeId);
        bool automatic =
            group
            is (int)MissileChargeGroup.AutoTargetingLight
                or (int)MissileChargeGroup.AutoTargetingHeavy
                or (int)MissileChargeGroup.AutoTargetingCruise;
        var effectId = automatic ? DogmaEffect.AutoTargetingMissile : DogmaEffect.MissileLaunching;
        if (
            type?.Published != 1
            || type.MetaGroupID == AbyssalMetaGroup
            || InventoryDefinitions.RequiresSpecificShip(attributes)
            || FittingCatalog.DefaultEffect(catalog, typeId) != effectId
        )
        {
            return false;
        }
        var effects = PassiveCatalog.Effects(catalog, typeId);
        if (
            effects.Count != FittingCatalog.Effects(catalog, typeId).Count
            || effects is not [{ } effect]
            || effect.EffectID != (int)effectId
            || effect.IsWarpSafe != 0
            || effect.EffectCategory != (int)(automatic ? PassiveEffectPhase.Active : PassiveEffectPhase.Targeted)
            || (effect.ModifierInfo ?? []).Length != 0
        )
        {
            return false;
        }
        try
        {
            _ = MissileRules.Warhead(attributes);
            return new[] { DogmaAttributeId.MaxVelocity, DogmaAttributeId.MissileFlightTime }.All(a =>
                double.IsFinite(attributes.GetValueOrDefault(a)) && attributes.GetValueOrDefault(a) > 0
            );
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
