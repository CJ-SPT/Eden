using Eden.Data.Catalog;
using Eden.Gameplay.Defenses;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Defenses;

internal static class AssaultDamageControlCatalog
{
    private const int AbyssalMetaGroup = 15;
    private static readonly CatalogTypeRule s_support = new(typeof(AssaultDamageControlCatalog).FullName!, Validate);
    private static readonly CatalogDefinition<bool> s_consumers = new("assault-damage-control-consumers", ReadConsumers);

    internal static bool Supported(CatalogDbContext catalog, int type, int group) => s_support.Supports(catalog, type, group);

    internal static bool ConsumersAdmitted(CatalogDbContext catalog) => catalog.Definition(s_consumers, 0);

    private static bool ReadConsumers(CatalogDbContext catalog, int key)
    {
        var hulls = catalog
            .Records(CatalogData.Client.Types)
            .Where(pair =>
                pair.Value.Published == 1
                && (
                    pair.Value.GroupID == (int)HullClass.AssaultFrigate
                    || pair.Value.GroupID == (int)HullClass.HeavyAssaultCruiser
                    || pair.Key == AssaultDamageControlRules.HeraldType
                )
            )
            .Select(pair => catalog.FittingType(pair.Key))
            .ToArray();
        return hulls.Length > 0 && hulls.All(t => t is not null && HullCatalog.CanAssemble(catalog, t, (int)InventoryCategory.Ship));
    }

    private static bool Validate(CatalogDbContext catalog, int type, int group)
    {
        var source = catalog.Read(CatalogData.Client.Types, (long)type);
        var attributes = catalog.FittingAttributes(type);
        if (
            group != AssaultDamageControlRules.Group
            || catalog.FittingType(type)?.CategoryId != (int)InventoryCategory.Module
            || source?.Published != 1
            || source.MetaGroupID == AbyssalMetaGroup
            || FittingCatalog.DefaultEffect(catalog, type) != DogmaEffect.AssaultDamageControl
            || attributes.GetValueOrDefault(DogmaAttributeId.MaxGroupFitted) != 1
            || attributes.GetValueOrDefault(DogmaAttributeId.DisallowRepeatingActivation) != 1
            || attributes.GetValueOrDefault(DogmaAttributeId.CanFitShipGroup1) != (int)HullClass.HeavyAssaultCruiser
            || attributes.GetValueOrDefault(DogmaAttributeId.CanFitShipGroup2) != (int)HullClass.AssaultFrigate
            || attributes.GetValueOrDefault(DogmaAttributeId.CanFitShipType1) != AssaultDamageControlRules.HeraldType
            || InventoryDefinitions
                .ShipRestrictions.Where(a =>
                    a is not (DogmaAttributeId.CanFitShipGroup1 or DogmaAttributeId.CanFitShipGroup2 or DogmaAttributeId.CanFitShipType1)
                )
                .Any(a => attributes.GetValueOrDefault(a) != 0)
        )
        {
            return false;
        }
        var effects = PassiveCatalog.Effects(catalog, type);
        HashSet<int> expected =
        [
            (int)DogmaEffect.LowPower,
            (int)DogmaEffect.Online,
            AssaultDamageControlRules.PassiveEffect,
            (int)DogmaEffect.AssaultDamageControl,
        ];
        if (effects.Count != expected.Count || !expected.SetEquals(effects.Select(e => checked((int)e.EffectID!.Value))))
        {
            return false;
        }
        foreach (var effect in effects)
        {
            if (effect.EffectID is (int)DogmaEffect.LowPower or (int)DogmaEffect.Online)
            {
                int phase = effect.EffectID == (int)DogmaEffect.LowPower ? (int)PassiveEffectPhase.Fitted : (int)PassiveEffectPhase.Active;
                if (effect.EffectCategory != phase || (effect.ModifierInfo ?? []).Length != 0)
                {
                    return false;
                }
                continue;
            }
            bool active = effect.EffectID == (int)DogmaEffect.AssaultDamageControl;
            var modifiers = effect.ModifierInfo ?? [];
            if (
                effect.EffectCategory != (int)(active ? PassiveEffectPhase.Active : PassiveEffectPhase.Online)
                || effect.IsWarpSafe != 0
                || effect.IsOffensive != 0
                || effect.IsAssistance != 0
                || modifiers.Length != AssaultDamageControlRules.Resistances.Count
                || modifiers.Select(m => m.ModifiedAttributeID).Distinct().Count() != modifiers.Length
                || active
                    && (
                        effect.DurationAttributeID != (int)DogmaAttributeId.Duration
                        || effect.DischargeAttributeID != (int)DogmaAttributeId.CapacitorNeed
                    )
            )
            {
                return false;
            }
            foreach (var binding in AssaultDamageControlRules.Resistances)
            {
                long target = (int)(active ? binding.ModuleAttribute : binding.ShipAttribute);
                long from = (int)(active ? DogmaAttributeId.AssaultResistanceMultiplier : binding.ModuleAttribute);
                if (
                    !modifiers.Any(m =>
                        m.ModifiedAttributeID == target
                        && m.ModifyingAttributeID == from
                        && m.Func == "ItemModifier"
                        && m.Domain == (active ? "itemID" : "shipID")
                        && m.Operation == (int)(active ? DogmaOperation.PostAssignment : DogmaOperation.PreMultiply)
                    )
                )
                {
                    return false;
                }
            }
        }
        try
        {
            _ = AssaultDamageControlRules.Define(0, type, attributes);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
