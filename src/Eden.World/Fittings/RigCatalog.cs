using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;

namespace Eden.World.Fittings;

internal static class RigCatalog
{
    internal static bool Supported(CatalogDbContext catalog, int typeId, int groupId)
    {
        if (!RigRules.Groups.Contains(groupId))
        {
            return false;
        }
        var type = catalog.FindSource("client/types", PassiveCatalog.Key(typeId))?.Read<ClientTypes>();
        var attributes = catalog.FittingAttributes(typeId);
        if (
            type?.Published != 1
            || type.TechLevel is not (1 or 2)
            || type.MetaGroupID is not (null or 1 or 2)
            || attributes.GetValueOrDefault(DogmaAttributeId.RigSize) is not (1 or 2 or 3 or 4)
            || attributes.GetValueOrDefault(DogmaAttributeId.CalibrationCost) <= 0
        )
        {
            return false;
        }
        var effects = PassiveCatalog.Effects(catalog, typeId);
        return FittingCatalog.Effects(catalog, typeId).Contains(DogmaEffect.RigSlot)
            && effects.Count == FittingCatalog.Effects(catalog, typeId).Count
            && effects.All(e =>
                e.EffectCategory == (int)PassiveEffectPhase.Fitted
                && (e.ModifierInfo ?? []).All(m => PassiveCatalog.Parse(m, e.EffectCategory.Value, rig: true) is not null)
            );
    }

    internal static IReadOnlyList<PassiveModifier> Modifiers(CatalogDbContext catalog, int typeId) =>
        PassiveCatalog
            .Effects(catalog, typeId)
            .SelectMany(e =>
                (e.ModifierInfo ?? []).Select(m =>
                    PassiveCatalog.Parse(m, e.EffectCategory!.Value, rig: true)
                    ?? throw new InvalidDataException($"Unsupported rig modifier on type {typeId}.")
                )
            )
            .ToArray();
}
