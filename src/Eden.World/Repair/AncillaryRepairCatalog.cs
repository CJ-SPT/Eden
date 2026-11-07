using Eden.Data.Catalog;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Inventory;

namespace Eden.World.Repair;

internal static class AncillaryRepairCatalog
{
    private const int AbyssalMetaGroup = 15;
    private const int ShieldOverload = 3201;
    private const int ArmorOverload = 3200;
    private const int OverloadPhase = 5;

    private static readonly CatalogTypeRule s_modules = new(typeof(AncillaryRepairCatalog).FullName! + ".module", Validate);
    private static readonly CatalogTypeRule s_paste = new(typeof(AncillaryRepairCatalog).FullName! + ".paste", ValidatePaste);

    internal static bool Supported(CatalogDbContext catalog, int type, int group) => s_modules.Supports(catalog, type, group);

    internal static bool PasteSupported(CatalogDbContext catalog, int type, int group) => s_paste.Supports(catalog, type, group);

    private static bool ValidatePaste(CatalogDbContext catalog, int type, int group)
    {
        var definition = catalog.Read(CatalogData.Client.Types, (long)type);
        return group == AncillaryRepairRules.PasteGroup
            && definition?.Published == 1
            && definition.MetaGroupID != AbyssalMetaGroup
            && catalog.FittingType(type)?.CategoryId == (int)InventoryCategory.Charge
            && FittingCatalog.Effects(catalog, type).Count == 0
            && double.IsFinite(PilotInventoryStore.Volume(catalog, type))
            && PilotInventoryStore.Volume(catalog, type) > 0;
    }

    private static bool Validate(CatalogDbContext catalog, int type, int group)
    {
        if (AncillaryRepairRules.Kind(group) is not { } kind)
        {
            return false;
        }
        var source = catalog.Read(CatalogData.Client.Types, (long)type);
        var values = catalog.FittingAttributes(type);
        var active = AncillaryRepairRules.Effect(kind);
        int fitting = kind == AncillaryRepairKind.Shield ? (int)DogmaEffect.MediumPower : (int)DogmaEffect.LowPower;
        int overload = kind == AncillaryRepairKind.Shield ? ShieldOverload : ArmorOverload;
        if (
            source?.Published != 1
            || source.MetaGroupID == AbyssalMetaGroup
            || InventoryDefinitions.RequiresSpecificShip(values)
            || FittingCatalog.DefaultEffect(catalog, type) != active
            || !(catalog.FittingType(type)?.Capacity > 0)
        )
        {
            return false;
        }
        var effects = PassiveCatalog.Effects(catalog, type);
        HashSet<int> expected = [fitting, (int)DogmaEffect.Online, (int)active, overload];
        if (effects.Count != expected.Count || !expected.SetEquals(effects.Select(e => checked((int)e.EffectID!.Value))))
        {
            return false;
        }
        foreach (var effect in effects)
        {
            if (effect.EffectID == (int)active)
            {
                if (
                    effect.EffectCategory != (int)PassiveEffectPhase.Active
                    || effect.DurationAttributeID != (int)DogmaAttributeId.Duration
                    || effect.DischargeAttributeID != (int)DogmaAttributeId.CapacitorNeed
                    || effect.IsWarpSafe != 1
                    || (effect.ModifierInfo ?? []).Length != 0
                )
                {
                    return false;
                }
            }
            else if (effect.EffectID == overload)
            {
                if (effect.EffectCategory != OverloadPhase)
                {
                    return false;
                }
            }
            else if ((effect.ModifierInfo ?? []).Length != 0)
            {
                return false;
            }
        }
        try
        {
            _ = AncillaryRepairRules.Define(kind, values);
            return values.GetValueOrDefault(DogmaAttributeId.ChargeGroup2)
                == (
                    kind == AncillaryRepairKind.Shield
                        ? Eden.Gameplay.Ships.CapacitorBoosterDefinitions.ChargeGroup
                        : AncillaryRepairRules.PasteGroup
                );
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
