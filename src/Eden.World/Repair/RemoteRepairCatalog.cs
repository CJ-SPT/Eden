using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Repair;

internal static class RemoteRepairCatalog
{
    private const int AbyssalMetaGroup = 15;
    private const int ShieldOverload = 3201;
    private const int ArmorOverload = 3200;
    private const int OverloadCategory = 5;
    private const int MediumFittingAdjustment = 6953;

    internal static bool Supported(CatalogDbContext catalog, int typeId, int group)
    {
        if (RemoteRepairRules.Effect(group) is not { } active)
        {
            return false;
        }
        var type = catalog.FindSource("client/types", PassiveCatalog.Key(typeId))?.Read<ClientTypes>();
        var attributes = catalog.FittingAttributes(typeId);
        if (
            type?.Published != 1
            || type.MetaGroupID == AbyssalMetaGroup
            || InventoryDefinitions.RequiresSpecificShip(attributes)
            || FittingCatalog.DefaultEffect(catalog, typeId) != active
        )
        {
            return false;
        }
        var effects = PassiveCatalog.Effects(catalog, typeId);
        int overload = group == RemoteRepairRules.ShieldGroup ? ShieldOverload : ArmorOverload;
        var expected = new HashSet<int> { (int)DogmaEffect.HighPower, (int)DogmaEffect.Online, (int)active, overload };
        if (effects.Any(e => e.EffectID == MediumFittingAdjustment))
        {
            expected.Add(MediumFittingAdjustment);
        }
        if (effects.Count != expected.Count || !expected.SetEquals(effects.Select(e => checked((int)e.EffectID!.Value))))
        {
            return false;
        }
        foreach (var effect in effects)
        {
            if (effect.EffectID == (int)active)
            {
                if (
                    effect.EffectCategory != (int)PassiveEffectPhase.Targeted
                    || effect.DurationAttributeID != (int)DogmaAttributeId.Duration
                    || effect.DischargeAttributeID != (int)DogmaAttributeId.CapacitorNeed
                    || effect.RangeAttributeID != (int)DogmaAttributeId.MaxRange
                    || effect.FalloffAttributeID != (int)DogmaAttributeId.FalloffEffectiveness
                    || effect.ResistanceAttributeID != (int)DogmaAttributeId.RemoteRepairImpedance
                    || effect.IsAssistance != 1
                    || effect.IsWarpSafe != 0
                    || (effect.ModifierInfo ?? []).Length != 0
                )
                {
                    return false;
                }
            }
            else if (effect.EffectID == overload)
            {
                if (effect.EffectCategory != OverloadCategory)
                {
                    return false;
                }
            }
            else if (effect.EffectID == MediumFittingAdjustment)
            {
                var modifiers = effect.ModifierInfo ?? [];
                if (
                    effect.EffectCategory != (int)PassiveEffectPhase.Fitted
                    || modifiers.Length != 2
                    || !modifiers.All(m =>
                        m.Domain == "itemID"
                        && m.Func == "ItemModifier"
                        && m.Operation == (int)DogmaOperation.PostMultiply
                        && m.ModifyingAttributeID == (int)DogmaAttributeId.MediumRemoteRepairFittingMultiplier
                    )
                    || !modifiers
                        .Select(m => m.ModifiedAttributeID)
                        .Order()
                        .SequenceEqual(new long?[] { (int)DogmaAttributeId.Power, (int)DogmaAttributeId.Cpu }.Order())
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
            _ = RemoteRepairRules.Define(0, typeId, active, attributes);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    internal static IReadOnlyList<PassiveModifier> Modifiers(CatalogDbContext catalog, int typeId) =>
        PassiveCatalog.Modifiers(catalog, typeId);
}
