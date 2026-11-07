using Eden.Data.Catalog;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Repair;

internal static class HardenerCatalog
{
    private const int AbyssalMetaGroup = 15;
    private const int OverloadPhase = 5;
    private const int ExpectedEffectCount = 4;

    private static readonly CatalogTypeRule s_support = new(typeof(HardenerCatalog).FullName!, ValidateSupport);

    internal static bool Supported(CatalogDbContext catalog, int typeId, int group) => s_support.Supports(catalog, typeId, group);

    private static bool ValidateSupport(CatalogDbContext catalog, int typeId, int group)
    {
        if (HardenerDefinitions.Effect(group) is not { } active)
        {
            return false;
        }
        var type = catalog.Read(CatalogData.Client.Types, (long)typeId);
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
            && effects.Any(e =>
                e.EffectID == (int)active
                && e.EffectCategory == (int)PassiveEffectPhase.Active
                && e.DurationAttributeID == (int)DogmaAttributeId.Duration
                && e.DischargeAttributeID == (int)DogmaAttributeId.CapacitorNeed
                && e.IsWarpSafe == 1
            )
            && effects.All(e =>
                e.EffectID == (int)DogmaEffect.Online
                || e.EffectCategory == OverloadPhase
                || (
                    e.EffectCategory is (int)PassiveEffectPhase.Fitted or (int)PassiveEffectPhase.Online
                    && (e.ModifierInfo ?? []).Length == 0
                )
                || (
                    e.EffectID == (int)active
                    && e.ModifierInfo is { Length: 4 }
                    && e.ModifierInfo.Value.All(m => PassiveCatalog.Parse(m, (int)PassiveEffectPhase.Active) is not null)
                )
            );
    }

    internal static IReadOnlyList<PassiveModifier> Modifiers(CatalogDbContext catalog, int typeId) =>
        PassiveCatalog
            .Effects(catalog, typeId)
            .Where(e => e.EffectID is (int)DogmaEffect.ActiveShieldResistance or (int)DogmaEffect.ActiveArmorResistance)
            .SelectMany(e =>
                (e.ModifierInfo ?? []).Select(m =>
                    PassiveCatalog.Parse(m, (int)PassiveEffectPhase.Active)
                    ?? throw new InvalidDataException($"Unsupported active resistance modifier on type {typeId}.")
                )
            )
            .ToArray();
}
