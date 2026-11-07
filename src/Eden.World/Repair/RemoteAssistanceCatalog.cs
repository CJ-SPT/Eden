using Eden.Data.Catalog;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Repair;

internal static class RemoteAssistanceCatalog
{
    private const int AbyssalMetaGroup = 15;
    private const int TrackingOverload = 5754;
    private const int SensorOverload = 5757;
    private const int OverloadCategory = 5;

    private static readonly CatalogTypeRule s_support = new(typeof(RemoteAssistanceCatalog).FullName!, Validate);

    internal static bool Supported(CatalogDbContext catalog, int typeId, int group) => s_support.Supports(catalog, typeId, group);

    private static bool Validate(CatalogDbContext catalog, int typeId, int group)
    {
        if (RemoteAssistanceRules.Effect(group) is not { } active)
        {
            return false;
        }
        var type = catalog.Read(CatalogData.Client.Types, (long)typeId);
        var attributes = catalog.FittingAttributes(typeId);
        if (
            type?.Published != 1
            || type.MetaGroupID == AbyssalMetaGroup
            || InventoryDefinitions.RequiresSpecificShip(attributes)
            || FittingCatalog.DefaultEffect(catalog, typeId) != active
            || attributes.GetValueOrDefault(DogmaAttributeId.ChargeGroup1) != RemoteAssistanceRules.ScriptGroup(group)
            || attributes.GetValueOrDefault(DogmaAttributeId.ChargeSize) != SupportModuleDefinitions.ScriptChargeSize
            || attributes.GetValueOrDefault(DogmaAttributeId.DisallowAgainstEwImmuneTarget) != 1
        )
        {
            return false;
        }
        int overload = group == RemoteAssistanceRules.TrackingGroup ? TrackingOverload : SensorOverload;
        var effects = PassiveCatalog.Effects(catalog, typeId);
        HashSet<int> expected = [(int)DogmaEffect.MediumPower, (int)DogmaEffect.Online, (int)active, overload];
        if (effects.Count != expected.Count || !expected.SetEquals(effects.Select(e => checked((int)e.EffectID!.Value))))
        {
            return false;
        }
        foreach (var effect in effects)
        {
            if (effect.EffectID == (int)active)
            {
                bool resistance =
                    group == RemoteAssistanceRules.TrackingGroup
                        ? effect.ResistanceAttributeID == (int)DogmaAttributeId.RemoteAssistanceImpedance
                        : effect.ResistanceAttributeID is null
                            && attributes.GetValueOrDefault(DogmaAttributeId.RemoteResistanceId)
                                == (int)DogmaAttributeId.RemoteAssistanceImpedance;
                if (
                    effect.EffectCategory != (int)PassiveEffectPhase.Targeted
                    || effect.DurationAttributeID != (int)DogmaAttributeId.Duration
                    || effect.DischargeAttributeID != (int)DogmaAttributeId.CapacitorNeed
                    || effect.RangeAttributeID != (int)DogmaAttributeId.MaxRange
                    || effect.FalloffAttributeID != (int)DogmaAttributeId.FalloffEffectiveness
                    || effect.IsAssistance != 1
                    || effect.IsOffensive != 0
                    || effect.IsWarpSafe != 0
                    || !resistance
                    || (effect.ModifierInfo ?? []).Length != 0
                )
                {
                    return false;
                }
            }
            else if (effect.EffectID == overload)
            {
                if (
                    effect.EffectCategory != OverloadCategory
                    || effect.ModifierInfo is not { Length: > 0 }
                    || !effect.ModifierInfo.Value.All(m =>
                        m.Domain == "itemID"
                        && m.Func == "ItemModifier"
                        && m.Operation == (int)DogmaOperation.PostPercent
                        && m.ModifiedAttributeID is not null
                        && m.ModifyingAttributeID is not null
                    )
                )
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
            _ = RemoteAssistanceRules.Define(0, typeId, active, attributes);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
