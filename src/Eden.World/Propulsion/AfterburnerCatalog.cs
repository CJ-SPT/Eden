using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Propulsion;

internal static class AfterburnerCatalog
{
    private const int AbyssalMetaGroup = 15;
    private const int OverloadSpeedEffect = 3175;
    private const int OverloadPhase = 5;
    private const int ExpectedEffectCount = 4;

    internal static bool Supported(CatalogDbContext catalog, int typeId, int group)
    {
        if (group != PropulsionDefinitions.AfterburnerGroup)
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
                effect.EffectID == (int)DogmaEffect.Afterburner
                && effect.EffectCategory == (int)PassiveEffectPhase.Active
                && effect.DischargeAttributeID == (int)DogmaAttributeId.CapacitorNeed
                && effect.DurationAttributeID == (int)DogmaAttributeId.Duration
                && effect.IsWarpSafe == 0
            )
            && effects.All(effect =>
                (effect.EffectID == OverloadSpeedEffect && effect.EffectCategory == OverloadPhase)
                || (
                    effect.EffectID is (int)DogmaEffect.Online or (int)DogmaEffect.MediumPower or (int)DogmaEffect.Afterburner
                    && (effect.ModifierInfo ?? []).Length == 0
                )
            );
    }
}
