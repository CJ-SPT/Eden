using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Targeting;

namespace Eden.Gameplay.Inventory;

public sealed record FittedResourceUsage(
    int FlagId,
    bool Online,
    double Cpu,
    double Power,
    bool Turret,
    bool Launcher,
    double Calibration = 0
);

public sealed record RookieResistanceModifier(DogmaAttributeId Bonus, DogmaAttributeId Resonance);

public sealed record ShipAttributeSkillBonus(int SkillId, DogmaAttributeId Attribute, double PerLevel)
{
    public double Fraction(IReadOnlyDictionary<int, int> levels) => PerLevel * levels.GetValueOrDefault(SkillId);
}

public static class FittingRules
{
    private const double StandardSkillBonus = InventoryDefinitions.OutputBonusPerSkillLevel;

    public static IReadOnlyList<ShipAttributeSkillBonus> ShipAttributeSkills { get; } =
    [
        new((int)FittingSkill.CpuManagement, DogmaAttributeId.CpuOutput, StandardSkillBonus),
        new((int)FittingSkill.PowerGridManagement, DogmaAttributeId.PowerOutput, StandardSkillBonus),
        new((int)FittingSkill.CapacitorManagement, DogmaAttributeId.CapacitorCapacity, StandardSkillBonus),
        new((int)FittingSkill.CapacitorSystemsOperation, DogmaAttributeId.CapacitorRechargeTime, -StandardSkillBonus),
        new((int)TargetingSkill.LongRangeTargeting, DogmaAttributeId.MaxTargetRange, StandardSkillBonus),
        new((int)TargetingSkill.SignatureAnalysis, DogmaAttributeId.ScanResolution, StandardSkillBonus),
        new((int)DefenseSkill.Mechanics, DogmaAttributeId.StructureHitPoints, StandardSkillBonus),
        new((int)DefenseSkill.HullUpgrades, DogmaAttributeId.ArmorHitPoints, StandardSkillBonus),
        new((int)DefenseSkill.ShieldManagement, DogmaAttributeId.ShieldCapacity, StandardSkillBonus),
        new((int)DefenseSkill.ShieldOperation, DogmaAttributeId.ShieldRechargeTime, -StandardSkillBonus),
    ];

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
        foreach (var bonus in ShipAttributeSkills)
        {
            attributes[bonus.Attribute] = attributes.GetValueOrDefault(bonus.Attribute) * (1 + bonus.Fraction(levels));
        }
        if (attributes.TryGetValue(DogmaAttributeId.ShieldUniformity, out double uniformity))
        {
            const double uniformityPerLevel = 0.05;
            attributes[DogmaAttributeId.ShieldUniformity] = Math.Min(
                1,
                uniformity + uniformityPerLevel * levels.GetValueOrDefault((int)DefenseSkill.TacticalShieldManipulation)
            );
        }
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
        attributes[DogmaAttributeId.CalibrationLoad] = modules.Sum(m => m.Calibration);
        attributes[DogmaAttributeId.RigSlotsLeft] = Math.Max(
            0,
            attributes.GetValueOrDefault(DogmaAttributeId.RigSlotsLeft) - modules.Count(m => InventoryDefinitions.IsRigSlot(m.FlagId))
        );
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
