using Eden.Data.Catalog;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Repair;

internal static class CapacitorBoosterCatalog
{
    private const int AbyssalMetaGroup = 15;
    private const int OverloadCapacitorBoosterEffect = 3002;
    private const int OverloadPhase = 5;
    private const int ExpectedEffectCount = 4;

    internal static bool Supported(CatalogDbContext catalog, int typeId, int group)
    {
        if (group != CapacitorBoosterDefinitions.Group)
        {
            return false;
        }
        var type = catalog.FindSource("client/types", PassiveCatalog.Key(typeId))?.Read<ClientTypes>();
        if (
            type?.Published != 1
            || type.MetaGroupID == AbyssalMetaGroup
            || !CapacitorBoosterDefinitions.IsSupported(group, PilotFittingStore.ReadAttributes(catalog, typeId))
        )
        {
            return false;
        }
        var effects = PassiveCatalog.Effects(catalog, typeId);
        return effects.Count == ExpectedEffectCount
            && effects.Count == FittingCatalog.Effects(catalog, typeId).Count
            && effects.Any(effect =>
                effect.EffectID == (int)DogmaEffect.CapacitorBoosting
                && effect.EffectCategory == (int)PassiveEffectPhase.Active
                && effect.DurationAttributeID == (int)DogmaAttributeId.Duration
                && effect.IsWarpSafe == 1
            )
            && effects.All(effect =>
                (effect.EffectID == OverloadCapacitorBoosterEffect && effect.EffectCategory == OverloadPhase)
                || (
                    effect.EffectID is (int)DogmaEffect.Online or (int)DogmaEffect.MediumPower or (int)DogmaEffect.CapacitorBoosting
                    && (effect.ModifierInfo ?? []).Length == 0
                )
            );
    }
}
