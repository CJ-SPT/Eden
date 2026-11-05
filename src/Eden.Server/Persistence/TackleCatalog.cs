using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Server.Catalog;

namespace Eden.Server.Persistence;

internal static class TackleCatalog
{
    private const int AbyssalMetaGroup = 15;
    private const int RangeOverloadEffect = 3174;
    private const int CoreScanResolutionEffect = 2645;
    private const int CoreTargetRangeEffect = 2646;
    private const int CoreDroneBandwidthEffect = 8123;
    private const int MicroJumpDriveEffect = 6441;
    private const int MicroJumpFieldEffect = 6442;
    private static readonly HashSet<int> CoreEffects =
    [
        (int)DogmaEffect.LowPower,
        (int)DogmaEffect.Online,
        CoreScanResolutionEffect,
        CoreTargetRangeEffect,
        (int)DogmaEffect.WarpCoreStabilizer,
        CoreDroneBandwidthEffect,
    ];

    internal static bool Supported(CatalogDbContext catalog, int typeId, int group)
    {
        if (
            group
            is not (TackleDefinitions.WebifierGroup or TackleDefinitions.WarpDisruptionGroup or TackleDefinitions.WarpCoreStabilizerGroup)
        )
        {
            return false;
        }
        var type = catalog.FindSource("client/types", PassiveCatalog.Key(typeId))?.Read<ClientTypes>();
        var attributes = PilotFittingStore.ReadAttributes(catalog, typeId);
        if (type?.Published != 1 || type.MetaGroupID == AbyssalMetaGroup || InventoryDefinitions.RequiresSpecificShip(attributes))
        {
            return false;
        }
        var active = FittingCatalog.DefaultEffect(catalog, typeId);
        if (
            active is null
            || (
                group == TackleDefinitions.WebifierGroup ? active != DogmaEffect.StasisWebifier
                : group == TackleDefinitions.WarpCoreStabilizerGroup ? active != DogmaEffect.WarpCoreStabilizer
                : active is not (DogmaEffect.WarpDisruptor or DogmaEffect.WarpScrambler)
            )
        )
        {
            return false;
        }
        var effects = PassiveCatalog.Effects(catalog, typeId);
        var activeDefinition = effects.SingleOrDefault(e => e.EffectID == (int)active);
        if (
            effects.Count != FittingCatalog.Effects(catalog, typeId).Count
            || activeDefinition is null
            || activeDefinition.DurationAttributeID != (int)DogmaAttributeId.Duration
            || activeDefinition.DischargeAttributeID != (int)DogmaAttributeId.CapacitorNeed
            || activeDefinition.IsWarpSafe != 0
        )
        {
            return false;
        }
        try
        {
            _ = TackleDefinitions.Define(0, typeId, active.Value, attributes);
        }
        catch (InvalidDataException)
        {
            return false;
        }
        if (active == DogmaEffect.WarpCoreStabilizer)
        {
            return activeDefinition.EffectCategory == (int)PassiveEffectPhase.Active
                && effects.Count == CoreEffects.Count
                && CoreEffects.SetEquals(effects.Select(e => checked((int)e.EffectID!.Value)))
                && effects.All(e =>
                    e.EffectID is (int)DogmaEffect.LowPower or (int)DogmaEffect.Online
                    || (e.ModifierInfo ?? []).Length > 0
                        && e.ModifierInfo!.All(m => PassiveCatalog.Parse(m, e.EffectCategory!.Value) is not null)
                );
        }
        if (
            activeDefinition.EffectCategory != (int)PassiveEffectPhase.Targeted
            || activeDefinition.RangeAttributeID != (int)DogmaAttributeId.MaxRange
            || !effects.Any(e => e.EffectID == (int)DogmaEffect.MediumPower)
            || !effects.Any(e => e.EffectID == (int)DogmaEffect.Online)
            || effects.Any(e =>
                e.EffectID != (int)active
                && e.EffectID is not ((int)DogmaEffect.MediumPower or (int)DogmaEffect.Online or RangeOverloadEffect)
            )
        )
        {
            return false;
        }
        if (active == DogmaEffect.StasisWebifier)
        {
            return (activeDefinition.ModifierInfo ?? []).Length == 0
                && activeDefinition.FalloffAttributeID == (int)DogmaAttributeId.FalloffEffectiveness
                && attributes.GetValueOrDefault(DogmaAttributeId.FalloffEffectiveness) == 0
                && attributes.GetValueOrDefault(DogmaAttributeId.RemoteResistanceId) == (int)DogmaAttributeId.StasisWebifierResistance;
        }
        var modifiers = activeDefinition.ModifierInfo ?? [];
        return active == DogmaEffect.WarpDisruptor
            ? modifiers
                is [
                    {
                        Domain: "targetID",
                        Func: "ItemModifier",
                        Operation: (int)DogmaOperation.Add,
                        ModifiedAttributeID: (int)DogmaAttributeId.WarpScrambleStatus,
                        ModifyingAttributeID: (int)DogmaAttributeId.WarpScrambleStrength
                    },
                ]
            : modifiers.Length == 5
                && modifiers.Count(m => m.Func == "EffectStopper" && m.EffectID is MicroJumpDriveEffect or MicroJumpFieldEffect) == 2
                && modifiers.Count(m =>
                    m.Domain == "targetID"
                    && m.Func == "ItemModifier"
                    && m.ModifiedAttributeID == (int)DogmaAttributeId.WarpScrambleStatus
                    && m.ModifyingAttributeID == (int)DogmaAttributeId.WarpScrambleStrength
                    && m.Operation == (int)DogmaOperation.Add
                ) == 1
                && modifiers.Count(m =>
                    m.Domain == "targetID"
                    && m.Func == "LocationRequiredSkillModifier"
                    && m.SkillTypeID == (int)NavigationSkill.HighSpeedManeuvering
                    && m.ModifiedAttributeID == (int)DogmaAttributeId.ActivationBlocked
                    && m.ModifyingAttributeID == (int)DogmaAttributeId.ActivationBlockStrength
                    && m.Operation == (int)DogmaOperation.Add
                ) == 1
                && modifiers.Count(m =>
                    m.Domain == "targetID"
                    && m.Func == "ItemModifier"
                    && m.ModifiedAttributeID == (int)DogmaAttributeId.MaxVelocityMultiplier
                    && m.ModifyingAttributeID == (int)DogmaAttributeId.MaxVelocityMultiplier
                    && m.Operation == (int)DogmaOperation.PostAssignment
                ) == 1;
    }

    internal static IReadOnlyList<PassiveModifier> CoreModifiers(CatalogDbContext catalog, int typeId) =>
        PassiveCatalog
            .Effects(catalog, typeId)
            .Where(e => e.EffectID is not ((int)DogmaEffect.LowPower or (int)DogmaEffect.Online))
            .SelectMany(e =>
                (e.ModifierInfo ?? []).Select(m =>
                    PassiveCatalog.Parse(m, e.EffectCategory!.Value) ?? throw new InvalidDataException("Unsupported warp core modifier.")
                )
            )
            .ToArray();
}
