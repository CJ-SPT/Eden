using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Targeting;

namespace Eden.Gameplay.Inventory;

public sealed record FittedResourceUsage(int FlagId, bool Online, double Cpu, double Power);

public static class FittingRules
{
    public static Dictionary<DogmaAttributeId, double> Apply(
        IReadOnlyDictionary<DogmaAttributeId, double> basis,
        IReadOnlyDictionary<int, int> levels,
        IReadOnlyList<FittedResourceUsage> modules
    )
    {
        var attributes = basis.ToDictionary();
        // Catalog corvette role effects multiply the corresponding resonances.
        // A -8 percent bonus means 0.92 times damage, not eight resistance points.
        foreach (
            var (bonus, resonances) in new[]
            {
                (
                    DogmaAttributeId.RookieShieldResistanceBonus,
                    new[]
                    {
                        DogmaAttributeId.ShieldEmResonance,
                        DogmaAttributeId.ShieldExplosiveResonance,
                        DogmaAttributeId.ShieldKineticResonance,
                        DogmaAttributeId.ShieldThermalResonance,
                    }
                ),
                (
                    DogmaAttributeId.RookieArmorResistanceBonus,
                    new[]
                    {
                        DogmaAttributeId.ArmorEmResonance,
                        DogmaAttributeId.ArmorExplosiveResonance,
                        DogmaAttributeId.ArmorKineticResonance,
                        DogmaAttributeId.ArmorThermalResonance,
                    }
                ),
            }
        )
        {
            foreach (var resonance in resonances)
            {
                attributes[resonance] = attributes.GetValueOrDefault(resonance, 1) * (1 + attributes.GetValueOrDefault(bonus) / 100);
            }
        }
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
            attributes.GetValueOrDefault(DogmaAttributeId.TurretSlotsLeft) - modules.Count(m => InventoryDefinitions.IsHighSlot(m.FlagId))
        );
        return attributes;
    }
}
