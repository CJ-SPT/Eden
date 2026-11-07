using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Combat;

internal static class PlayerEwarCatalog
{
    private const int AbyssalMetaGroup = 15;
    private const int ExpectedEffectCount = 4;
    private const int OverloadCategory = 5;

    // Build 3662168 overload profiles are validated metadata; heat is separate.
    private enum OverloadEffect
    {
        Ecm = 3182,
        Tracking = 5754,
        Sensor = 5757,
        Painter = 5758,
        Guidance = 6384,
    }

    internal static bool Supported(CatalogDbContext catalog, int typeId, int group)
    {
        if (!PlayerEwarRules.Groups.Contains(group))
        {
            return false;
        }
        var type = catalog.FindSource("client/types", PassiveCatalog.Key(typeId))?.Read<ClientTypes>();
        var attributes = catalog.FittingAttributes(typeId);
        var active = FittingCatalog.DefaultEffect(catalog, typeId);
        if (
            type?.Published != 1
            || type.MetaGroupID == AbyssalMetaGroup
            || InventoryDefinitions.RequiresSpecificShip(attributes)
            || active is null
            || !PlayerEwarRules.Matches(group, active.Value)
        )
        {
            return false;
        }
        var effects = PassiveCatalog.Effects(catalog, typeId);
        var definition = effects.SingleOrDefault(e => e.EffectID == (int)active);
        var overload = active switch
        {
            DogmaEffect.PlayerEcm => OverloadEffect.Ecm,
            DogmaEffect.SensorDampener => OverloadEffect.Sensor,
            DogmaEffect.TrackingDisruptor => OverloadEffect.Tracking,
            DogmaEffect.GuidanceDisruptor => OverloadEffect.Guidance,
            _ => OverloadEffect.Painter,
        };
        if (
            effects.Count != ExpectedEffectCount
            || effects.Count != FittingCatalog.Effects(catalog, typeId).Count
            || definition is null
            || definition.EffectCategory != (int)PassiveEffectPhase.Targeted
            || definition.DischargeAttributeID != (int)DogmaAttributeId.CapacitorNeed
            || definition.DurationAttributeID != (int)DogmaAttributeId.Duration
            || definition.RangeAttributeID != (int)DogmaAttributeId.MaxRange
            || definition.FalloffAttributeID != (int)DogmaAttributeId.FalloffEffectiveness
            || definition.IsWarpSafe != 0
            || (definition.ModifierInfo ?? []).Length != 0
            || !effects.Any(e => e.EffectID == (int)DogmaEffect.MediumPower)
            || !effects.Any(e => e.EffectID == (int)DogmaEffect.Online)
            || !effects.Any(e =>
                e.EffectID == (int)overload
                && e.EffectCategory == OverloadCategory
                && e.ModifierInfo is { Length: > 0 }
                && e.ModifierInfo.All(m =>
                    m.Domain == "itemID"
                    && m.Func == "ItemModifier"
                    && m.Operation == (int)DogmaOperation.PostPercent
                    && m.ModifiedAttributeID is not null
                    && m.ModifyingAttributeID is not null
                )
            )
            || effects.Any(e =>
                e.EffectID != (int)active
                && e.EffectID != (int)overload
                && e.EffectID is not ((int)DogmaEffect.MediumPower or (int)DogmaEffect.Online)
            )
        )
        {
            return false;
        }
        int script = active switch
        {
            DogmaEffect.SensorDampener => PlayerEwarRules.DampeningScriptGroup,
            DogmaEffect.TrackingDisruptor => PlayerEwarRules.TrackingDisruptionScriptGroup,
            DogmaEffect.GuidanceDisruptor => PlayerEwarRules.GuidanceDisruptionScriptGroup,
            _ => 0,
        };
        if (
            script != 0
            && (
                attributes.GetValueOrDefault(DogmaAttributeId.ChargeGroup1) != script
                || attributes.GetValueOrDefault(DogmaAttributeId.ChargeSize) != SupportModuleDefinitions.ScriptChargeSize
            )
        )
        {
            return false;
        }
        try
        {
            _ = PlayerEwarRules.Define(0, typeId, active.Value, attributes);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
