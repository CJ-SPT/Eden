using Eden.Data.Catalog;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Repair;

internal static class ArmorRepairCatalog
{
    private const int AbyssalMetaGroup = 15;
    private const int OverloadArmorRepairEffect = 3200;
    private const int OverloadPhase = 5;
    private const int ExpectedEffectCount = 4;

    private static readonly CatalogTypeRule s_support = new(typeof(ArmorRepairCatalog).FullName!, ValidateSupport);

    internal static bool Supported(CatalogDbContext catalog, int typeId, int group) => s_support.Supports(catalog, typeId, group);

    private static bool ValidateSupport(CatalogDbContext catalog, int typeId, int group)
    {
        if (group != ArmorRepairDefinitions.Group)
        {
            return false;
        }
        var type = catalog.Read(CatalogData.Client.Types, (long)typeId);
        if (
            type?.Published != 1
            || type.MetaGroupID == AbyssalMetaGroup
            || !ArmorRepairDefinitions.IsSupported(PilotFittingStore.ReadAttributes(catalog, typeId))
        )
        {
            return false;
        }
        var effects = PassiveCatalog.Effects(catalog, typeId);
        return effects.Count == ExpectedEffectCount
            && effects.Count == FittingCatalog.Effects(catalog, typeId).Count
            && effects.Any(effect =>
                effect.EffectID == (int)DogmaEffect.ArmorRepair
                && effect.EffectCategory == (int)PassiveEffectPhase.Active
                && effect.DurationAttributeID == (int)DogmaAttributeId.Duration
                && effect.DischargeAttributeID == (int)DogmaAttributeId.CapacitorNeed
                && effect.IsWarpSafe == 1
            )
            && effects.All(effect =>
                (effect.EffectID == OverloadArmorRepairEffect && effect.EffectCategory == OverloadPhase)
                || (
                    effect.EffectID is (int)DogmaEffect.Online or (int)DogmaEffect.LowPower or (int)DogmaEffect.ArmorRepair
                    && (effect.ModifierInfo ?? []).Length == 0
                )
            );
    }
}
