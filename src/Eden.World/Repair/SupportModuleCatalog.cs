using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Repair;

internal static class SupportModuleCatalog
{
    private const int AbyssalMetaGroup = 15;
    private const int OverloadPhase = 5;
    private const int ExpectedEffectCount = 4;

    internal static bool Supported(CatalogDbContext catalog, int typeId, int group)
    {
        if (SupportModuleDefinitions.Effect(group) is not { } active)
        {
            return false;
        }
        var type = catalog.FindSource("client/types", PassiveCatalog.Key(typeId))?.Read<ClientTypes>();
        var attributes = PilotFittingStore.ReadAttributes(catalog, typeId);
        if (
            type?.Published != 1
            || type.MetaGroupID == AbyssalMetaGroup
            || InventoryDefinitions.RequiresSpecificShip(attributes)
            || FittingCatalog.DefaultEffect(catalog, typeId) != active
            || attributes.GetValueOrDefault(DogmaAttributeId.ChargeGroup1) != SupportModuleDefinitions.ScriptGroup(group)
            || attributes.GetValueOrDefault(DogmaAttributeId.ChargeSize) != SupportModuleDefinitions.ScriptChargeSize
        )
        {
            return false;
        }
        var effects = PassiveCatalog.Effects(catalog, typeId);
        int modifierCount =
            group == SupportModuleDefinitions.SensorBoosterGroup ? 6
            : group == SupportModuleDefinitions.TrackingComputerGroup ? 3
            : 4;
        return effects.Count == ExpectedEffectCount
            && effects.Count == FittingCatalog.Effects(catalog, typeId).Count
            && effects.Any(e => e.EffectID == (int)DogmaEffect.MediumPower)
            && effects.Any(e => e.EffectID == (int)DogmaEffect.Online)
            && effects.Any(e =>
                e.EffectID == (int)active
                && e.EffectCategory == (int)PassiveEffectPhase.Active
                && e.DurationAttributeID == (int)DogmaAttributeId.Duration
                && e.DischargeAttributeID == (int)DogmaAttributeId.CapacitorNeed
                && e.IsWarpSafe == 1
                && e.ModifierInfo?.Length == modifierCount
            )
            && effects.All(e =>
                e.EffectID is (int)DogmaEffect.MediumPower or (int)DogmaEffect.Online
                || e.EffectCategory == OverloadPhase
                || e.EffectID == (int)active
                    && (e.ModifierInfo ?? []).All(m => PassiveCatalog.Parse(m, (int)PassiveEffectPhase.Active) is not null)
            );
    }

    internal static IReadOnlyList<PassiveModifier> Modifiers(CatalogDbContext catalog, int typeId, int group) =>
        PassiveCatalog
            .Effects(catalog, typeId)
            .Where(e => e.EffectID == (int?)SupportModuleDefinitions.Effect(group))
            .SelectMany(e =>
                (e.ModifierInfo ?? []).Select(m =>
                    PassiveCatalog.Parse(m, (int)PassiveEffectPhase.Active)
                    ?? throw new InvalidDataException("Unsupported support module modifier.")
                )
            )
            .ToArray();
}
