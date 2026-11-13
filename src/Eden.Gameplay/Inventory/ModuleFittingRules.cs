using Eden.Gameplay.Combat;
using Eden.Gameplay.Defenses;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Information;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Ships;

namespace Eden.Gameplay.Inventory;

public enum ModuleRack
{
    Low,
    Medium,
    High,
    Rig,
}

public sealed record ModuleFittingDefinition(ModuleRack Rack, bool Turret, bool Launcher, int MaximumGroup, int MaximumType)
{
    public int FirstFlag =>
        (int)(
            Rack switch
            {
                ModuleRack.Low => InventoryFlag.LowSlot0,
                ModuleRack.Medium => InventoryFlag.MediumSlot0,
                ModuleRack.Rig => InventoryFlag.RigSlot0,
                _ => InventoryFlag.HighSlot0,
            }
        );

    public DogmaAttributeId SlotAttribute =>
        Rack switch
        {
            ModuleRack.Low => DogmaAttributeId.LowSlots,
            ModuleRack.Medium => DogmaAttributeId.MediumSlots,
            ModuleRack.Rig => DogmaAttributeId.RigSlotsLeft,
            _ => DogmaAttributeId.HighSlots,
        };
}

public static class ModuleFittingRules
{
    public static ModuleFittingDefinition? Define(
        int typeId,
        int groupId,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        IReadOnlySet<DogmaEffect> effects,
        bool passive = false
    )
    {
        bool supported =
            groupId == CommandBurstRules.Group && effects.Any(CommandBurstRules.IsBurst)
            || groupId == SmartbombRules.Group && effects.Contains(DogmaEffect.Smartbomb)
            || groupId == SpatialModuleRules.CloakGroup && effects.Any(SpatialModuleRules.IsCloak)
            || groupId == SpatialModuleRules.MicroJumpGroup && effects.Contains(DogmaEffect.MicroJumpDrive)
            || groupId is InventoryDefinitions.GasScoopGroup or InventoryDefinitions.GasHarvesterGroup
                && effects.Contains(DogmaEffect.MiningClouds)
            || groupId == HackingRules.AnalyzerGroup && effects.Contains(DogmaEffect.Hacking)
            || groupId == ProbeScanRules.LauncherGroup && effects.Contains(DogmaEffect.UseMissiles)
            || Enum.IsDefined((InformationGroup)groupId)
                && (InformationRules.Effect(groupId) is { } information ? effects.Contains(information) : true)
            || groupId == AssaultDamageControlRules.Group && effects.Contains(DogmaEffect.AssaultDamageControl)
            || typeId == BreacherRules.CounterType && effects.Contains(DogmaEffect.BreachControl)
            || groupId == SignatureSuppressionRules.Group && effects.Contains(DogmaEffect.SignatureSuppression)
            || AncillaryRepairRules.Kind(groupId) is { } ancillary
                && effects.Contains(AncillaryRepairRules.Effect(ancillary))
                && !InventoryDefinitions.RequiresSpecificShip(attributes)
            || RemoteAssistanceRules.Effect(groupId) is { } assistance
                && effects.Contains(assistance)
                && !InventoryDefinitions.RequiresSpecificShip(attributes)
            || CapacitorWarfareRules.Effect(groupId) is { } capacitor
                && effects.Contains(capacitor)
                && !InventoryDefinitions.RequiresSpecificShip(attributes)
            || RemoteRepairRules.Effect(groupId) is { } remote
                && effects.Contains(remote)
                && (groupId == RemoteRepairRules.MutadaptiveArmorGroup || !InventoryDefinitions.RequiresSpecificShip(attributes))
            || PlayerEwarRules.Groups.Contains(groupId)
                && !InventoryDefinitions.RequiresSpecificShip(attributes)
                && effects.Any(e => PlayerEwarRules.Matches(groupId, e))
            || groupId
                is TackleDefinitions.WebifierGroup
                    or TackleDefinitions.WarpDisruptionGroup
                    or TackleDefinitions.WarpCoreStabilizerGroup
                && !InventoryDefinitions.RequiresSpecificShip(attributes)
                && effects.Any(e => TackleDefinitions.Targeted(e) || e == DogmaEffect.WarpCoreStabilizer)
            || SupportModuleDefinitions.Effect(groupId) is { } support
                && effects.Contains(support)
                && !InventoryDefinitions.RequiresSpecificShip(attributes)
            || MissileDefinitions.SupportedLauncher(groupId, attributes) && effects.Contains(DogmaEffect.UseMissiles)
            || groupId switch
            {
                InventoryDefinitions.MiningLaserGroup or InventoryDefinitions.StripMinerGroup or InventoryDefinitions.ModulatedMinerGroup =>
                    effects.Contains(DogmaEffect.MiningLaser)
                        && (
                            groupId == InventoryDefinitions.ModulatedMinerGroup
                            || MiningDefinitions.Family(groupId, attributes) == MiningFamily.Ice
                            || (
                                groupId == InventoryDefinitions.StripMinerGroup
                                    ? MiningDefinitions.IsOrdinaryStripMiner(attributes)
                                    : !InventoryDefinitions.RequiresSpecificShip(attributes)
                            )
                        )
                        && attributes.GetValueOrDefault(DogmaAttributeId.MiningDamageCloudChance) == 0,
                ShieldBoostDefinitions.Group => effects.Contains(DogmaEffect.ShieldBoosting),
                CapacitorBoosterDefinitions.Group => CapacitorBoosterDefinitions.IsSupported(groupId, attributes)
                    && effects.Contains(DogmaEffect.CapacitorBoosting),
                HullRepairDefinitions.Group => LocalRepairDefinitions.IsSupported(attributes)
                    && effects.Contains(DogmaEffect.StructureRepair),
                ArmorRepairDefinitions.Group => ArmorRepairDefinitions.IsSupported(attributes) && effects.Contains(DogmaEffect.ArmorRepair),
                HardenerDefinitions.ShieldGroup => effects.Contains(DogmaEffect.ActiveShieldResistance),
                HardenerDefinitions.ArmorGroup => effects.Contains(DogmaEffect.ActiveArmorResistance),
                PropulsionDefinitions.AfterburnerGroup => (
                    effects.Contains(DogmaEffect.Afterburner)
                    && !effects.Contains(DogmaEffect.Microwarpdrive)
                    && !InventoryDefinitions.RequiresSpecificShip(attributes)
                )
                    || (
                        effects.Contains(DogmaEffect.Microwarpdrive)
                        && !effects.Contains(DogmaEffect.Afterburner)
                        && PropulsionDefinitions.IsSupportedMicrowarpdrive(attributes)
                    ),
                CombatDefinitions.EnergyWeaponGroup => (
                    typeId == (int)CombatWeaponType.CivilianGatlingRailgun || CombatDefinitions.IsSupportedAmmoTurret(groupId, attributes)
                ) && effects.Contains(DogmaEffect.LaserFired),
                CombatDefinitions.HybridWeaponGroup or CombatDefinitions.ProjectileWeaponGroup => CombatDefinitions.IsSupportedAmmoTurret(
                    groupId,
                    attributes
                ) && effects.Contains(DogmaEffect.ProjectileFired),
                CombatDefinitions.DisintegratorGroup => CombatDefinitions.IsSupportedAmmoTurret(groupId, attributes)
                    && effects.Contains(DogmaEffect.Disintegrator),
                CombatDefinitions.VortonGroup => CombatDefinitions.IsSupportedAmmoTurret(groupId, attributes)
                    && effects.Contains(DogmaEffect.Vorton),
                _ => typeId == (int)CombatWeaponType.CivilianGatlingRailgun && effects.Contains(DogmaEffect.LaserFired),
            };
        if ((!supported && !passive) || !effects.Contains(DogmaEffect.Online))
        {
            return null;
        }
        ModuleRack? rack =
            effects.Contains(DogmaEffect.LowPower) ? ModuleRack.Low
            : effects.Contains(DogmaEffect.MediumPower) ? ModuleRack.Medium
            : effects.Contains(DogmaEffect.HighPower) ? ModuleRack.High
            : null;
        return rack is null
            ? null
            : new(
                rack.Value,
                effects.Contains(DogmaEffect.TurretFitted),
                effects.Contains(DogmaEffect.LauncherFitted),
                checked((int)attributes.GetValueOrDefault(DogmaAttributeId.MaxGroupFitted)),
                checked((int)attributes.GetValueOrDefault(DogmaAttributeId.MaxTypeFitted))
            );
    }
}
