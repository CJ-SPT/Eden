using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Server.Catalog;

namespace Eden.Server.Persistence;

internal static class HardenerCatalog
{
    private const int TechOne = 1;
    private const int TechOneMetaGroup = 1;
    private const int OverloadPhase = 5;

    internal static bool Supported(CatalogDbContext catalog, int typeId, int group)
    {
        if (HardenerDefinitions.Effect(group) is not { } active)
        {
            return false;
        }
        var type = catalog.Set<SourceRecord>().Find("client/types", PassiveCatalog.Key(typeId))?.Read<ClientTypes>();
        if (
            type?.Published != 1
            || type.MetaGroupID != TechOneMetaGroup
            || catalog.Set<TypeAttribute>().Find(typeId, (int)DogmaAttributeId.TechLevel)?.Value != TechOne
        )
        {
            return false;
        }
        var effects = PassiveCatalog.Effects(catalog, typeId);
        return effects.Count == FittingCatalog.Effects(catalog, typeId).Count
            && effects.Any(e => e.EffectID == (int)active && e.EffectCategory == (int)PassiveEffectPhase.Active)
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
                    && e.ModifierInfo.All(m => PassiveCatalog.Parse(m, (int)PassiveEffectPhase.Active) is not null)
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
