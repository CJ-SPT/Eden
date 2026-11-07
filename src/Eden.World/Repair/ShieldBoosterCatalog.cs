using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Repair;

internal static class ShieldBoosterCatalog
{
    private const int AbyssalMetaGroup = 15;
    private const int OverloadShieldBoostEffect = 3201;
    private const int OverloadPhase = 5;
    private const int ExpectedEffectCount = 4;

    internal static bool Supported(CatalogDbContext catalog, int typeId, int group)
    {
        if (group != ShieldBoostDefinitions.Group)
        {
            return false;
        }
        var type = catalog.FindSource("client/types", PassiveCatalog.Key(typeId))?.Read<ClientTypes>();
        if (
            type?.Published != 1
            || type.MetaGroupID == AbyssalMetaGroup
            || InventoryDefinitions.RequiresSpecificShip(PilotFittingStore.ReadAttributes(catalog, typeId))
        )
        {
            return false;
        }
        var effects = PassiveCatalog.Effects(catalog, typeId);
        return effects.Count == ExpectedEffectCount
            && effects.Count == FittingCatalog.Effects(catalog, typeId).Count
            && effects.Any(effect =>
                effect.EffectID == (int)DogmaEffect.ShieldBoosting
                && effect.EffectCategory == (int)PassiveEffectPhase.Active
                && effect.DurationAttributeID == (int)DogmaAttributeId.Duration
                && effect.DischargeAttributeID == (int)DogmaAttributeId.CapacitorNeed
                && effect.IsWarpSafe == 1
            )
            && effects.All(effect =>
                (effect.EffectID == OverloadShieldBoostEffect && effect.EffectCategory == OverloadPhase)
                || (
                    effect.EffectID is (int)DogmaEffect.Online or (int)DogmaEffect.MediumPower or (int)DogmaEffect.ShieldBoosting
                    && (effect.ModifierInfo ?? []).Length == 0
                )
            );
    }
}
