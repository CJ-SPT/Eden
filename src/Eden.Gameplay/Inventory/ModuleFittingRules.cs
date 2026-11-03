using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;

namespace Eden.Gameplay.Inventory;

public enum ModuleRack
{
    Low,
    Medium,
    High,
}

public sealed record ModuleFittingDefinition(ModuleRack Rack, bool Turret, bool Launcher, int MaximumGroup, int MaximumType)
{
    public int FirstFlag =>
        (int)(
            Rack switch
            {
                ModuleRack.Low => InventoryFlag.LowSlot0,
                ModuleRack.Medium => InventoryFlag.MediumSlot0,
                _ => InventoryFlag.HighSlot0,
            }
        );

    public DogmaAttributeId SlotAttribute =>
        Rack switch
        {
            ModuleRack.Low => DogmaAttributeId.LowSlots,
            ModuleRack.Medium => DogmaAttributeId.MediumSlots,
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
        bool supported = groupId switch
        {
            InventoryDefinitions.MiningLaserGroup => effects.Contains(DogmaEffect.MiningLaser),
            ShieldBoostDefinitions.Group => effects.Contains(DogmaEffect.ShieldBoosting),
            ArmorRepairDefinitions.Group => ArmorRepairDefinitions.IsSupported(attributes) && effects.Contains(DogmaEffect.ArmorRepair),
            HardenerDefinitions.ShieldGroup => effects.Contains(DogmaEffect.ActiveShieldResistance),
            HardenerDefinitions.ArmorGroup => effects.Contains(DogmaEffect.ActiveArmorResistance),
            PropulsionDefinitions.AfterburnerGroup => effects.Contains(DogmaEffect.Afterburner)
                && !effects.Contains(DogmaEffect.Microwarpdrive),
            CombatDefinitions.HybridWeaponGroup => CombatDefinitions.IsSupportedHybrid(groupId, attributes)
                && effects.Contains(DogmaEffect.ProjectileFired),
            _ => typeId == (int)CombatWeaponType.CivilianGatlingRailgun && effects.Contains(DogmaEffect.CivilianWeapon),
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
