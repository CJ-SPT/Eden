using System.Collections.Immutable;
using System.Globalization;
using Eden.Data.Catalog;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Weapons;
using Eden.World.Mining;
using Eden.World.Propulsion;
using Eden.World.Repair;

namespace Eden.World.Fittings;

internal static class FittingCatalog
{
    private static readonly CatalogDefinition<ModuleFittingDefinition?> s_definition = new("module-fitting", LoadDefinition, 256);
    private static readonly CatalogDefinition<ImmutableHashSet<DogmaEffect>> s_effects = new("module-effects", LoadEffects);
    private static readonly CatalogDefinition<DogmaEffect?> s_defaultEffect = new("default-effect", LoadDefaultEffect, 256);

    internal static ModuleFittingDefinition? Definition(CatalogDbContext catalog, int typeId) => catalog.Definition(s_definition, typeId);

    private static ModuleFittingDefinition? LoadDefinition(CatalogDbContext catalog, int typeId)
    {
        var type = catalog.FittingType(typeId);
        if (type is null)
        {
            return null;
        }
        if (RigRules.Groups.Contains(type.GroupId))
        {
            var rigAttributes = catalog.FittingAttributes(typeId);
            return RigCatalog.Supported(catalog, typeId, type.GroupId)
                ? new(
                    ModuleRack.Rig,
                    false,
                    false,
                    checked((int)rigAttributes.GetValueOrDefault(DogmaAttributeId.MaxGroupFitted)),
                    checked((int)rigAttributes.GetValueOrDefault(DogmaAttributeId.MaxTypeFitted))
                )
                : null;
        }
        if (
            type.GroupId
                is TackleDefinitions.WebifierGroup
                    or TackleDefinitions.WarpDisruptionGroup
                    or TackleDefinitions.WarpCoreStabilizerGroup
            && !TackleCatalog.Supported(catalog, typeId, type.GroupId)
        )
        {
            return null;
        }
        if (SupportModuleDefinitions.Effect(type.GroupId) is not null && !SupportModuleCatalog.Supported(catalog, typeId, type.GroupId))
        {
            return null;
        }
        if (
            (
                type.GroupId
                    is CombatDefinitions.EnergyWeaponGroup
                        or CombatDefinitions.HybridWeaponGroup
                        or CombatDefinitions.ProjectileWeaponGroup
                || MissileDefinitions.LauncherGroups.Contains(type.GroupId)
            )
            && typeId != (int)CombatWeaponType.CivilianGatlingRailgun
            && !WeaponCatalog.Supported(catalog, typeId, type.GroupId)
        )
        {
            return null;
        }
        if (InventoryDefinitions.IsOreMiningGroup(type.GroupId) && !MiningLaserCatalog.Supported(catalog, typeId, type.GroupId))
        {
            return null;
        }
        if (type.GroupId == ShieldBoostDefinitions.Group && !ShieldBoosterCatalog.Supported(catalog, typeId, type.GroupId))
        {
            return null;
        }
        if (type.GroupId == HullRepairDefinitions.Group && !HullRepairCatalog.Supported(catalog, typeId, type.GroupId))
        {
            return null;
        }
        if (type.GroupId == ArmorRepairDefinitions.Group && !ArmorRepairCatalog.Supported(catalog, typeId, type.GroupId))
        {
            return null;
        }
        if (HardenerDefinitions.Effect(type.GroupId) is not null && !HardenerCatalog.Supported(catalog, typeId, type.GroupId))
        {
            return null;
        }
        if (
            type.GroupId == PropulsionDefinitions.AfterburnerGroup
            && Effects(catalog, typeId).Contains(DogmaEffect.Afterburner)
            && !AfterburnerCatalog.Supported(catalog, typeId, type.GroupId)
        )
        {
            return null;
        }
        if (
            type.GroupId == PropulsionDefinitions.AfterburnerGroup
            && Effects(catalog, typeId).Contains(DogmaEffect.Microwarpdrive)
            && !MicrowarpdriveCatalog.Supported(catalog, typeId, type.GroupId)
        )
        {
            return null;
        }
        if (type.GroupId == CapacitorBoosterDefinitions.Group && !CapacitorBoosterCatalog.Supported(catalog, typeId, type.GroupId))
        {
            return null;
        }
        if (PlayerEwarRules.Groups.Contains(type.GroupId) && !PlayerEwarCatalog.Supported(catalog, typeId, type.GroupId))
        {
            return null;
        }
        if (RemoteRepairRules.Effect(type.GroupId) is not null && !RemoteRepairCatalog.Supported(catalog, typeId, type.GroupId))
        {
            return null;
        }
        var attributes = catalog.FittingAttributes(typeId);
        return ModuleFittingRules.Define(
            typeId,
            type.GroupId,
            attributes,
            Effects(catalog, typeId),
            PassiveCatalog.Supported(catalog, typeId, type.GroupId)
        );
    }

    internal static ImmutableHashSet<DogmaEffect> Effects(CatalogDbContext catalog, int typeId) => catalog.Definition(s_effects, typeId);

    private static ImmutableHashSet<DogmaEffect> LoadEffects(CatalogDbContext catalog, int typeId) =>
        catalog
            .Read(CatalogData.Client.Typedogma, (long)typeId)
            ?.DogmaEffects?.Select(e => (DogmaEffect)checked((int)e.EffectID!.Value))
            .ToImmutableHashSet()
        ?? ImmutableHashSet<DogmaEffect>.Empty;

    internal static DogmaEffect? DefaultEffect(CatalogDbContext catalog, int typeId) => catalog.Definition(s_defaultEffect, typeId);

    private static DogmaEffect? LoadDefaultEffect(CatalogDbContext catalog, int typeId)
    {
        var effects = catalog.Read(CatalogData.Client.Typedogma, (long)typeId)?.DogmaEffects?.Where(e => e.IsDefault == 1).ToArray() ?? [];
        return effects is [{ EffectID: long effect }] ? (DogmaEffect)checked((int)effect) : null;
    }
}
