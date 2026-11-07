using Eden.Data.Catalog;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Mining;

internal static class MiningLaserCatalog
{
    private const int AbyssalMetaGroup = 15;
    private const int LaserEffectCount = 4;
    private const int StripMinerEffectCount = 3;

    internal static bool Supported(CatalogDbContext catalog, int typeId, int group)
    {
        var type = catalog.FindSource("client/types", PassiveCatalog.Key(typeId))?.Read<ClientTypes>();
        var attributes = PilotFittingStore.ReadAttributes(catalog, typeId);
        if (
            !InventoryDefinitions.IsOreMiningGroup(group)
            || (group == InventoryDefinitions.StripMinerGroup && !MiningDefinitions.IsOrdinaryStripMiner(attributes))
            || type?.Published != 1
            || type.MetaGroupID == AbyssalMetaGroup
            || (group != InventoryDefinitions.StripMinerGroup && InventoryDefinitions.RequiresSpecificShip(attributes))
            // Deep-core lasers need Mercoxit cloud hazards before being enabled.
            || attributes.GetValueOrDefault(DogmaAttributeId.MiningDamageCloudChance) != 0
        )
        {
            return false;
        }
        var effects = PassiveCatalog.Effects(catalog, typeId);
        return effects.Count == (group == InventoryDefinitions.StripMinerGroup ? StripMinerEffectCount : LaserEffectCount)
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
