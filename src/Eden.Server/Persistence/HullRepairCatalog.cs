using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Server.Catalog;

namespace Eden.Server.Persistence;

internal static class HullRepairCatalog
{
    private const int AbyssalMetaGroup = 15;
    private const int OverloadDurationEffect = 3002;
    private const int OverloadPhase = 5;
    private const int ExpectedEffectCount = 4;

    internal static bool Supported(CatalogDbContext catalog, int typeId, int group)
    {
        if (group != HullRepairDefinitions.Group)
        {
            return false;
        }
        var type = catalog.FindSource("client/types", PassiveCatalog.Key(typeId))?.Read<ClientTypes>();
        if (
            type?.Published != 1
            || type.MetaGroupID == AbyssalMetaGroup
            || !LocalRepairDefinitions.IsSupported(PilotFittingStore.ReadAttributes(catalog, typeId))
            || FittingCatalog.DefaultEffect(catalog, typeId) != DogmaEffect.StructureRepair
        )
        {
            return false;
        }
        try
        {
            _ = LocalRepairRules.Define(0, typeId, PilotFittingStore.ReadAttributes(catalog, typeId), RepairLayer.Hull);
        }
        catch (InvalidDataException)
        {
            return false;
        }
        var effects = PassiveCatalog.Effects(catalog, typeId);
        return effects.Count == ExpectedEffectCount
            && effects.Count == FittingCatalog.Effects(catalog, typeId).Count
            && effects.Any(effect =>
                effect.EffectID == (int)DogmaEffect.StructureRepair
                && effect.EffectCategory == (int)PassiveEffectPhase.Active
                && effect.DurationAttributeID == (int)DogmaAttributeId.Duration
                && effect.DischargeAttributeID == (int)DogmaAttributeId.CapacitorNeed
                && effect.IsWarpSafe == 1
            )
            && effects.Any(effect => effect.EffectID == OverloadDurationEffect && effect.EffectCategory == OverloadPhase)
            && effects.All(effect =>
                (
                    effect.EffectID is (int)DogmaEffect.Online or (int)DogmaEffect.MediumPower or (int)DogmaEffect.StructureRepair
                    && (effect.ModifierInfo ?? []).Length == 0
                )
                || (
                    effect.EffectID == OverloadDurationEffect
                    && effect.ModifierInfo is [{ } modifier]
                    && modifier.Domain == "itemID"
                    && modifier.Func == "ItemModifier"
                    && modifier.ModifiedAttributeID == (int)DogmaAttributeId.Duration
                    && modifier.ModifyingAttributeID == (int)DogmaAttributeId.OverloadDurationBonus
                    && modifier.Operation == (int)DogmaOperation.PostPercent
                )
            );
    }
}
