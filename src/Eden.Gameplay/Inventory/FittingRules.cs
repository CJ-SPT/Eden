using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Targeting;

namespace Eden.Gameplay.Inventory;

public sealed record FittedResourceUsage(int FlagId, bool Online, double Cpu, double Power, bool Turret, bool Launcher);

public sealed record RookieResistanceModifier(DogmaAttributeId Bonus, DogmaAttributeId Resonance);

public static class FittingRules
{
    // Corvette roles are exempt graph modifiers, before any final resistance override.
    public static IReadOnlyList<RookieResistanceModifier> RookieResistanceModifiers { get; } =
    [
        new(DogmaAttributeId.RookieShieldResistanceBonus, DogmaAttributeId.ShieldEmResonance),
        new(DogmaAttributeId.RookieShieldResistanceBonus, DogmaAttributeId.ShieldExplosiveResonance),
        new(DogmaAttributeId.RookieShieldResistanceBonus, DogmaAttributeId.ShieldKineticResonance),
        new(DogmaAttributeId.RookieShieldResistanceBonus, DogmaAttributeId.ShieldThermalResonance),
        new(DogmaAttributeId.RookieArmorResistanceBonus, DogmaAttributeId.ArmorEmResonance),
        new(DogmaAttributeId.RookieArmorResistanceBonus, DogmaAttributeId.ArmorExplosiveResonance),
        new(DogmaAttributeId.RookieArmorResistanceBonus, DogmaAttributeId.ArmorKineticResonance),
        new(DogmaAttributeId.RookieArmorResistanceBonus, DogmaAttributeId.ArmorThermalResonance),
    ];

    public static Dictionary<DogmaAttributeId, double> Apply(
        IReadOnlyDictionary<DogmaAttributeId, double> basis,
        IReadOnlyDictionary<int, int> levels,
        IReadOnlyList<FittedResourceUsage> modules
    )
    {
        var attributes = basis.ToDictionary();
        // These skills each grant five percent per trained level to their hull attribute.
        foreach (
            var (skill, output) in new[]
            {
                ((int)FittingSkill.CpuManagement, DogmaAttributeId.CpuOutput),
                ((int)FittingSkill.PowerGridManagement, DogmaAttributeId.PowerOutput),
                ((int)FittingSkill.CapacitorManagement, DogmaAttributeId.CapacitorCapacity),
                ((int)TargetingSkill.LongRangeTargeting, DogmaAttributeId.MaxTargetRange),
                ((int)TargetingSkill.SignatureAnalysis, DogmaAttributeId.ScanResolution),
                ((int)DefenseSkill.Mechanics, DogmaAttributeId.StructureHitPoints),
                ((int)DefenseSkill.HullUpgrades, DogmaAttributeId.ArmorHitPoints),
                ((int)DefenseSkill.ShieldManagement, DogmaAttributeId.ShieldCapacity),
            }
        )
        {
            attributes[output] =
                attributes.GetValueOrDefault(output)
                * (1 + InventoryDefinitions.OutputBonusPerSkillLevel * levels.GetValueOrDefault(skill));
        }
        attributes[DogmaAttributeId.CapacitorRechargeTime] =
            attributes.GetValueOrDefault(DogmaAttributeId.CapacitorRechargeTime)
            * (1 - InventoryDefinitions.OutputBonusPerSkillLevel * levels.GetValueOrDefault((int)FittingSkill.CapacitorSystemsOperation));
        attributes[DogmaAttributeId.ShieldRechargeTime] =
            attributes.GetValueOrDefault(DogmaAttributeId.ShieldRechargeTime)
            * (1 - InventoryDefinitions.OutputBonusPerSkillLevel * levels.GetValueOrDefault((int)DefenseSkill.ShieldOperation));
        int targetLimit =
            TargetingDefinitions.BasePilotTargetLimit
            + levels.GetValueOrDefault((int)TargetingSkill.TargetManagement)
            + levels.GetValueOrDefault((int)TargetingSkill.AdvancedTargetManagement);
        attributes[DogmaAttributeId.MaxLockedTargets] = Math.Min(
            attributes.GetValueOrDefault(DogmaAttributeId.MaxLockedTargets),
            targetLimit
        );
        attributes[DogmaAttributeId.CpuLoad] = modules.Where(m => m.Online).Sum(m => m.Cpu);
        attributes[DogmaAttributeId.PowerLoad] = modules.Where(m => m.Online).Sum(m => m.Power);
        attributes[DogmaAttributeId.TurretSlotsLeft] = Math.Max(
            0,
            attributes.GetValueOrDefault(DogmaAttributeId.TurretSlotsLeft) - modules.Count(m => m.Turret)
        );
        attributes[DogmaAttributeId.LauncherSlotsLeft] = Math.Max(
            0,
            attributes.GetValueOrDefault(DogmaAttributeId.LauncherSlotsLeft) - modules.Count(m => m.Launcher)
        );
        return attributes;
    }
}
