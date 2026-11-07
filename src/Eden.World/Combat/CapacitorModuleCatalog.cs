using Eden.Data.Catalog;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Combat;

internal static class CapacitorModuleCatalog
{
    private const int AbyssalMetaGroup = 15;
    private const int OverloadDuration = 3002;
    private const int OverloadCategory = 5;
    private static readonly CatalogTypeRule s_support = new(typeof(CapacitorModuleCatalog).FullName!, Validate);

    internal static bool Supported(CatalogDbContext catalog, int type, int group) => s_support.Supports(catalog, type, group);

    private static bool Validate(CatalogDbContext catalog, int typeId, int group)
    {
        if (CapacitorWarfareRules.Effect(group) is not { } active)
        {
            return false;
        }
        var type = catalog.Read(CatalogData.Client.Types, (long)typeId);
        var values = catalog.FittingAttributes(typeId);
        if (
            type?.Published != 1
            || type.MetaGroupID == AbyssalMetaGroup
            || InventoryDefinitions.RequiresSpecificShip(values)
            || FittingCatalog.DefaultEffect(catalog, typeId) != active
        )
        {
            return false;
        }
        var effects = PassiveCatalog.Effects(catalog, typeId);
        var expected = new HashSet<int> { (int)DogmaEffect.HighPower, (int)DogmaEffect.Online, OverloadDuration, (int)active };
        if (effects.Count != expected.Count || !expected.SetEquals(effects.Select(e => checked((int)e.EffectID!.Value))))
        {
            return false;
        }
        foreach (var effect in effects)
        {
            if (effect.EffectID == (int)active)
            {
                bool transfer = active == DogmaEffect.RemoteCapacitorTransfer;
                if (
                    effect.EffectCategory != (int)PassiveEffectPhase.Targeted
                    || effect.DischargeAttributeID != (int)DogmaAttributeId.CapacitorNeed
                    || effect.DurationAttributeID != (int)DogmaAttributeId.Duration
                    || effect.RangeAttributeID != (int)DogmaAttributeId.MaxRange
                    || effect.FalloffAttributeID != (int)DogmaAttributeId.FalloffEffectiveness
                    || effect.ResistanceAttributeID
                        != (int)(transfer ? DogmaAttributeId.RemoteCapacitorImpedance : DogmaAttributeId.EnergyWarfareResistance)
                    || effect.IsAssistance != (transfer ? 1 : 0)
                    || effect.IsOffensive != (transfer ? 0 : 1)
                    || effect.IsWarpSafe != 0
                    || (effect.ModifierInfo ?? []).Length != 0
                )
                {
                    return false;
                }
            }
            else if (effect.EffectID == OverloadDuration)
            {
                if (effect.EffectCategory != OverloadCategory)
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
            _ = CapacitorWarfareRules.Define(0, typeId, active, values);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
