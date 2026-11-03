using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Server.Catalog;

namespace Eden.Server.Persistence;

internal static class MiningLaserCatalog
{
    private const int AbyssalMetaGroup = 15;
    private const int ExpectedEffectCount = 4;

    internal static bool Supported(CatalogDbContext catalog, int typeId, int group)
    {
        var type = catalog.Set<SourceRecord>().Find("client/types", PassiveCatalog.Key(typeId))?.Read<ClientTypes>();
        var attributes = PilotFittingStore.ReadAttributes(catalog, typeId);
        if (
            group != InventoryDefinitions.MiningLaserGroup
            || type?.Published != 1
            || type.MetaGroupID == AbyssalMetaGroup
            || InventoryDefinitions.RequiresSpecificShip(attributes)
            // Deep-core lasers need Mercoxit cloud hazards before being enabled.
            || attributes.GetValueOrDefault(DogmaAttributeId.MiningDamageCloudChance) != 0
        )
        {
            return false;
        }
        var effects = PassiveCatalog.Effects(catalog, typeId);
        return effects.Count == ExpectedEffectCount
            && effects.Count == FittingCatalog.Effects(catalog, typeId).Count
            && effects.Any(effect =>
                effect.EffectID == (int)DogmaEffect.MiningLaser
                && effect.EffectCategory == (int)PassiveEffectPhase.Targeted
                && effect.DischargeAttributeID == (int)DogmaAttributeId.CapacitorNeed
                && effect.DurationAttributeID == (int)DogmaAttributeId.Duration
                && effect.RangeAttributeID == (int)DogmaAttributeId.MaxRange
                && effect.IsWarpSafe == 0
            )
            && effects.All(effect =>
                effect.EffectID
                    is (int)DogmaEffect.Online
                        or (int)DogmaEffect.HighPower
                        or (int)DogmaEffect.TurretFitted
                        or (int)DogmaEffect.MiningLaser
                && (effect.ModifierInfo ?? []).Length == 0
            );
    }
}
