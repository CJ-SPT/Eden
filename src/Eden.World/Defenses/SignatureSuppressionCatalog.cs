using Eden.Data.Catalog;
using Eden.Gameplay.Defenses;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Defenses;

internal static class SignatureSuppressionCatalog
{
    private const int AbyssalMetaGroup = 15;

    private static readonly CatalogTypeRule s_support = new(typeof(SignatureSuppressionCatalog).FullName!, Validate);

    internal static bool Supported(CatalogDbContext catalog, int type, int group) => s_support.Supports(catalog, type, group);

    private static bool Validate(CatalogDbContext catalog, int type, int group)
    {
        var source = catalog.Read(CatalogData.Client.Types, (long)type);
        var values = catalog.FittingAttributes(type);
        if (
            group != SignatureSuppressionRules.Group
            || source?.Published != 1
            || source.MetaGroupID == AbyssalMetaGroup
            || catalog.FittingType(type)?.CategoryId != (int)InventoryCategory.Module
            || FittingCatalog.DefaultEffect(catalog, type) != DogmaEffect.SignatureSuppression
            || values.GetValueOrDefault(DogmaAttributeId.MaxGroupFitted) != 1
            || values.GetValueOrDefault(DogmaAttributeId.MaxGroupActive) != 1
            || values.GetValueOrDefault(DogmaAttributeId.DisallowRepeatingActivation) != 1
            || values.GetValueOrDefault(DogmaAttributeId.CanFitShipGroup1) != (int)HullClass.Marauder
            || values.GetValueOrDefault(DogmaAttributeId.CanFitShipGroup2) != (int)HullClass.Battleship
            || values.GetValueOrDefault(DogmaAttributeId.CanFitShipGroup3) != (int)HullClass.BlackOps
            || InventoryDefinitions
                .ShipRestrictions.Where(a =>
                    a is not (DogmaAttributeId.CanFitShipGroup1 or DogmaAttributeId.CanFitShipGroup2 or DogmaAttributeId.CanFitShipGroup3)
                )
                .Any(a => values.GetValueOrDefault(a) != 0)
        )
        {
            return false;
        }
        var effects = PassiveCatalog.Effects(catalog, type);
        HashSet<int> expected =
        [
            (int)DogmaEffect.MediumPower,
            (int)DogmaEffect.Online,
            (int)DogmaEffect.SignatureSuppressionOnline,
            (int)DogmaEffect.SignatureSuppression,
        ];
        if (effects.Count != expected.Count || !expected.SetEquals(effects.Select(e => checked((int)e.EffectID!.Value))))
        {
            return false;
        }
        foreach (var effect in effects)
        {
            if (effect.EffectID is (int)DogmaEffect.MediumPower or (int)DogmaEffect.Online)
            {
                int expectedPhase =
                    effect.EffectID == (int)DogmaEffect.MediumPower ? (int)PassiveEffectPhase.Fitted : (int)PassiveEffectPhase.Active;
                if (effect.EffectCategory != expectedPhase || (effect.ModifierInfo ?? []).Length != 0)
                {
                    return false;
                }
                continue;
            }
            bool active = effect.EffectID == (int)DogmaEffect.SignatureSuppression;
            if (
                effect.EffectCategory != (int)(active ? PassiveEffectPhase.Active : PassiveEffectPhase.Online)
                || effect.IsWarpSafe != 0
                || effect.IsOffensive != 0
                || effect.IsAssistance != 0
                || (effect.ModifierInfo ?? []) is not [var modifier]
                || modifier.Domain != "shipID"
                || modifier.Func != "ItemModifier"
                || modifier.Operation != (int)DogmaOperation.PostMultiply
                || modifier.ModifiedAttributeID != (int)DogmaAttributeId.SignatureRadius
                || modifier.ModifyingAttributeID
                    != (int)(
                        active
                            ? DogmaAttributeId.SignatureSuppressionActiveMultiplier
                            : DogmaAttributeId.SignatureSuppressionPassiveMultiplier
                    )
                || PassiveCatalog.Parse(modifier, effect.EffectCategory!.Value) is null
                || active
                    && (
                        effect.DurationAttributeID != (int)DogmaAttributeId.SignatureSuppressionDuration
                        || effect.DischargeAttributeID != (int)DogmaAttributeId.CapacitorNeed
                    )
            )
            {
                return false;
            }
        }
        try
        {
            _ = SignatureSuppressionRules.Define(0, type, values);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
