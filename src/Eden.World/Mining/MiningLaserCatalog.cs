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
    private const int ModulatedMinerEffectCount = 4;

    private static readonly CatalogTypeRule s_support = new(typeof(MiningLaserCatalog).FullName!, ValidateSupport);

    internal static bool Supported(CatalogDbContext catalog, int typeId, int group) => s_support.Supports(catalog, typeId, group);

    private static bool ValidateSupport(CatalogDbContext catalog, int typeId, int group)
    {
        var type = catalog.Read(CatalogData.Client.Types, (long)typeId);
        var attributes = PilotFittingStore.ReadAttributes(catalog, typeId);
        if (
            !InventoryDefinitions.IsMiningGroup(group)
            || (group == InventoryDefinitions.StripMinerGroup && !MiningDefinitions.IsOrdinaryStripMiner(attributes))
            || type?.Published != 1
            || type.MetaGroupID == AbyssalMetaGroup
            || (
                group
                    is not (
                        InventoryDefinitions.StripMinerGroup
                        or InventoryDefinitions.ModulatedMinerGroup
                        or InventoryDefinitions.GasHarvesterGroup
                    )
                && InventoryDefinitions.RequiresSpecificShip(attributes)
            )
            // Deep-core lasers need Mercoxit cloud hazards before being enabled.
            || attributes.GetValueOrDefault(DogmaAttributeId.MiningDamageCloudChance) != 0
        )
        {
            return false;
        }
        var effects = PassiveCatalog.Effects(catalog, typeId);
        var family = MiningDefinitions.Family(group, attributes);
        var miningEffect = family == MiningFamily.Gas ? DogmaEffect.MiningClouds : DogmaEffect.MiningLaser;
        int expected = group == InventoryDefinitions.StripMinerGroup ? StripMinerEffectCount : LaserEffectCount;
        if (group == InventoryDefinitions.GasHarvesterGroup)
        {
            expected = StripMinerEffectCount;
        }
        if (group == InventoryDefinitions.ModulatedMinerGroup)
        {
            expected = ModulatedMinerEffectCount + (effects.Any(e => e.EffectID == (int)DogmaEffect.TurretFitted) ? 1 : 0);
        }
        return effects.Count == expected
            && effects.Count == FittingCatalog.Effects(catalog, typeId).Count
            && effects.Any(effect =>
                effect.EffectID == (int)miningEffect
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
                        or (int)DogmaEffect.MiningClouds
                        or (int)DogmaEffect.MiningCrystalInformation
                && (
                    effect.EffectID == (int)DogmaEffect.MiningCrystalInformation
                        ? group == InventoryDefinitions.ModulatedMinerGroup
                            && effect.EffectCategory == (int)PassiveEffectPhase.Fitted
                            && effect.ModifierInfo is { Length: 1 }
                            && effect.ModifierInfo.Value[0] is { Domain: "itemID", Func: "ItemModifier" } info
                            && info.ModifiedAttributeID == (int)DogmaAttributeId.MiningSpecialtyAmount
                            && info.ModifyingAttributeID == (int)DogmaAttributeId.MiningAmount
                            && info.Operation == (int)DogmaOperation.PreAssignment
                        : (effect.ModifierInfo ?? []).Length == 0
                )
            );
    }
}
