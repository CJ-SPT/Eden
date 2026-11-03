using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Server.Catalog;

namespace Eden.Server.Persistence;

internal static class TurretWeaponCatalog
{
    private const int AbyssalMetaGroup = 15;
    private const int OverloadRate = 3001;
    private const int OverloadDamage = 3025;
    private const int OverloadPhase = 5;
    private const int HullResistanceOverride = 5994;
    private const int ShieldArmorResistanceOverride = 5995;
    private const int BarrageDamageDerivation = 253;
    private const int BarrageFalloffDerivation = 254;
    private const int LegacyBarrage = 263;

    internal static bool Supported(CatalogDbContext catalog, int typeId, int group)
    {
        if (group is not (CombatDefinitions.HybridWeaponGroup or CombatDefinitions.ProjectileWeaponGroup))
        {
            return false;
        }
        var type = catalog.Set<SourceRecord>().Find("client/types", PassiveCatalog.Key(typeId))?.Read<ClientTypes>();
        if (
            type?.Published != 1
            || type.MetaGroupID == AbyssalMetaGroup
            || !CombatDefinitions.IsSupportedAmmoTurret(group, PilotFittingStore.ReadAttributes(catalog, typeId))
            || FittingCatalog.DefaultEffect(catalog, typeId) != DogmaEffect.ProjectileFired
        )
        {
            return false;
        }
        var effects = PassiveCatalog.Effects(catalog, typeId);
        var ids = effects.Select(e => e.EffectID!.Value).ToHashSet();
        if (
            effects.Count != FittingCatalog.Effects(catalog, typeId).Count
            || !new long[]
            {
                (int)DogmaEffect.Online,
                (int)DogmaEffect.HighPower,
                (int)DogmaEffect.TurretFitted,
                (int)DogmaEffect.ProjectileFired,
            }.All(ids.Contains)
            || ids.Contains(OverloadRate) == ids.Contains(OverloadDamage)
            || ids.Contains(HullResistanceOverride) != ids.Contains(ShieldArmorResistanceOverride)
            || (
                group == CombatDefinitions.ProjectileWeaponGroup
                && !new long[] { BarrageDamageDerivation, BarrageFalloffDerivation, LegacyBarrage }.All(ids.Contains)
            )
        )
        {
            return false;
        }
        return effects.Any(e =>
                e.EffectID == (int)DogmaEffect.ProjectileFired
                && e.EffectCategory == (int)PassiveEffectPhase.Targeted
                && e.RangeAttributeID == (int)DogmaAttributeId.MaxRange
                && e.DurationAttributeID == (int)DogmaAttributeId.WeaponCycleTime
                && e.DischargeAttributeID == (int)DogmaAttributeId.CapacitorNeed
                && e.TrackingSpeedAttributeID == (int)DogmaAttributeId.TrackingSpeed
                && e.FalloffAttributeID == (int)DogmaAttributeId.AccuracyFalloff
                && e.IsWarpSafe == 0
            )
            && effects.All(e =>
                e.EffectID switch
                {
                    (int)DogmaEffect.Online
                    or (int)DogmaEffect.HighPower
                    or (int)DogmaEffect.TurretFitted
                    or (int)DogmaEffect.ProjectileFired => (e.ModifierInfo ?? []).Length == 0,
                    OverloadRate or OverloadDamage => e.EffectCategory == OverloadPhase,
                    HullResistanceOverride or ShieldArmorResistanceOverride => ValidOverride(e),
                    BarrageDamageDerivation => group == CombatDefinitions.ProjectileWeaponGroup
                        && ValidDerivation(
                            e,
                            DogmaAttributeId.BarrageDamageMultiplier,
                            DogmaAttributeId.DamageMultiplier,
                            DogmaAttributeId.BarrageDamageBonus
                        ),
                    BarrageFalloffDerivation => group == CombatDefinitions.ProjectileWeaponGroup
                        && ValidDerivation(
                            e,
                            DogmaAttributeId.BarrageFalloff,
                            DogmaAttributeId.AccuracyFalloff,
                            DogmaAttributeId.BarrageFalloffBonus
                        ),
                    LegacyBarrage => group == CombatDefinitions.ProjectileWeaponGroup
                        && e.EffectCategory == (int)PassiveEffectPhase.Targeted
                        && e.DurationAttributeID == (int)DogmaAttributeId.WeaponCycleTime
                        && e.FalloffAttributeID == (int)DogmaAttributeId.BarrageFalloff,
                    _ => false,
                }
            );
    }

    private static bool ValidDerivation(ClientDogmaeffects effect, DogmaAttributeId target, DogmaAttributeId basis, DogmaAttributeId bonus)
    {
        var modifiers = effect.ModifierInfo ?? [];
        return effect.EffectCategory == (int)PassiveEffectPhase.Fitted
            && modifiers.Length == 2
            && modifiers.All(m => m.Domain == "itemID" && m.Func == "ItemModifier" && m.ModifiedAttributeID == (int)target)
            && modifiers.Any(m => m.Operation == (int)DogmaOperation.PreAssignment && m.ModifyingAttributeID == (int)basis)
            && modifiers.Any(m => m.Operation == (int)DogmaOperation.PostPercent && m.ModifyingAttributeID == (int)bonus);
    }

    private static bool ValidOverride(ClientDogmaeffects effect)
    {
        var attributes =
            effect.EffectID == HullResistanceOverride
                ? new[]
                {
                    DogmaAttributeId.StructureEmResonance,
                    DogmaAttributeId.StructureExplosiveResonance,
                    DogmaAttributeId.StructureKineticResonance,
                    DogmaAttributeId.StructureThermalResonance,
                }
                : new[]
                {
                    DogmaAttributeId.ShieldEmResonance,
                    DogmaAttributeId.ShieldExplosiveResonance,
                    DogmaAttributeId.ShieldKineticResonance,
                    DogmaAttributeId.ShieldThermalResonance,
                    DogmaAttributeId.ArmorEmResonance,
                    DogmaAttributeId.ArmorExplosiveResonance,
                    DogmaAttributeId.ArmorKineticResonance,
                    DogmaAttributeId.ArmorThermalResonance,
                };
        var modifiers = effect.ModifierInfo ?? [];
        var source = effect.EffectID == HullResistanceOverride ? DogmaAttributeId.HullResistanceKiller : DogmaAttributeId.ResistanceKiller;
        return effect.EffectCategory == (int)PassiveEffectPhase.Fitted
            && modifiers.Length == attributes.Length
            && attributes.All(a => modifiers.Count(m => m.ModifiedAttributeID == (int)a) == 1)
            && modifiers.All(m =>
                m.Domain == "shipID"
                && m.Func == "ItemModifier"
                && m.Operation == (int)DogmaOperation.PostAssignment
                && m.ModifyingAttributeID == (int)source
            );
    }

    internal static IReadOnlyList<PassiveModifier> Modifiers(CatalogDbContext catalog, int typeId) =>
        PassiveCatalog
            .Effects(catalog, typeId)
            .Where(e => e.EffectID is HullResistanceOverride or ShieldArmorResistanceOverride)
            .SelectMany(e =>
                e.ModifierInfo!.Select(m => new PassiveModifier(
                    ModifierDomain.Ship,
                    ModifierSelector.Item,
                    null,
                    (DogmaAttributeId)m.ModifiedAttributeID!.Value,
                    (DogmaAttributeId)m.ModifyingAttributeID!.Value,
                    DogmaOperation.PostAssignment,
                    PassiveEffectPhase.Fitted
                ))
            )
            .ToArray();
}
