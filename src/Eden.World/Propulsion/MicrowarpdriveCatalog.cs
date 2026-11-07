using Eden.Data.Catalog;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Propulsion;

internal static class MicrowarpdriveCatalog
{
    private const int OverloadSpeedEffect = 3175;

    private static readonly CatalogTypeRule s_support = new(typeof(MicrowarpdriveCatalog).FullName!, ValidateSupport);

    internal static bool Supported(CatalogDbContext catalog, int typeId, int group) => s_support.Supports(catalog, typeId, group);

    private static bool ValidateSupport(CatalogDbContext catalog, int typeId, int group)
    {
        if (
            group != PropulsionDefinitions.AfterburnerGroup
            || !FittingCatalog.Effects(catalog, typeId).Contains(DogmaEffect.Microwarpdrive)
        )
        {
            return false;
        }
        var type = catalog.Read(CatalogData.Client.Types, (long)typeId);
        if (
            type is not { Published: 1 }
            || !PropulsionDefinitions.IsSupportedMicrowarpdrive(PilotFittingStore.ReadAttributes(catalog, typeId))
        )
        {
            return false;
        }
        var effects = PassiveCatalog.Effects(catalog, typeId);
        return effects.Count == FittingCatalog.Effects(catalog, typeId).Count
            && effects.Any(e => e.EffectID == (int)DogmaEffect.CapacitorCapacityMultiply)
            && effects.All(e =>
                (
                    e.EffectID is (int)DogmaEffect.Online or (int)DogmaEffect.MediumPower or (int)DogmaEffect.Microwarpdrive
                    && (e.ModifierInfo ?? []).Length == 0
                )
                || e.EffectID == OverloadSpeedEffect
                || (
                    e.EffectID == (int)DogmaEffect.CapacitorCapacityMultiply
                    && e.EffectCategory == (int)PassiveEffectPhase.Online
                    && e.ModifierInfo is { Length: 1 }
                    && PassiveCatalog.Parse(e.ModifierInfo.Value[0], e.EffectCategory.Value)
                        is {
                            Domain: ModifierDomain.Ship,
                            Selector: ModifierSelector.Item,
                            Attribute: DogmaAttributeId.CapacitorCapacity,
                            SourceAttribute: DogmaAttributeId.CapacitorCapacityMultiplier,
                            Operation: DogmaOperation.PostMultiply
                        }
                )
            );
    }

    // Pinned propulsionModules.Microwarpdrive extends the afterburner mass
    // rule with this live post-percent signature modifier (effect 6730).
    internal static PassiveModifier SignatureModifier =>
        new(
            ModifierDomain.Ship,
            ModifierSelector.Item,
            null,
            DogmaAttributeId.SignatureRadius,
            DogmaAttributeId.SignatureRadiusBonus,
            DogmaOperation.PostPercent,
            PassiveEffectPhase.Active
        );
}
