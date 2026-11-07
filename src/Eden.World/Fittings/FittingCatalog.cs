using System.Globalization;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Combat.Weapons;
using Eden.World.Mining;
using Eden.World.Propulsion;
using Eden.World.Repair;

namespace Eden.World.Fittings;

internal static class FittingCatalog
{
    internal static ModuleFittingDefinition? Definition(CatalogDbContext catalog, int typeId)
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
        var attributes = catalog
            .Set<TypeAttribute>()
            .Where(a => a.ItemTypeId == typeId)
            .ToDictionary(a => (DogmaAttributeId)a.AttributeId, a => a.Value);
        return ModuleFittingRules.Define(
            typeId,
            type.GroupId,
            attributes,
            Effects(catalog, typeId),
            PassiveCatalog.Supported(catalog, typeId, type.GroupId)
        );
    }

    internal static HashSet<DogmaEffect> Effects(CatalogDbContext catalog, int typeId) =>
        catalog
            .FindSource("client/typedogma", typeId.ToString(CultureInfo.InvariantCulture))
            ?.Read<ClientTypedogma>()
            .DogmaEffects?.Select(e => (DogmaEffect)checked((int)e.EffectID!.Value))
            .ToHashSet()
        ?? [];

    internal static DogmaEffect? DefaultEffect(CatalogDbContext catalog, int typeId)
    {
        var effects =
            catalog
                .FindSource("client/typedogma", PassiveCatalog.Key(typeId))
                ?.Read<ClientTypedogma>()
                .DogmaEffects?.Where(e => e.IsDefault == 1)
                .ToArray()
            ?? [];
        return effects is [{ EffectID: long effect }] ? (DogmaEffect)checked((int)effect) : null;
    }
}
