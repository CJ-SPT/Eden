using Eden.Gameplay.Dogma;

namespace Eden.Gameplay.Combat;

public sealed record WeaponModuleInputs(
    int TypeId,
    int GroupId,
    IReadOnlyDictionary<DogmaAttributeId, double> Attributes,
    IReadOnlyDictionary<CombatSkill, int> Levels,
    double HullOptimalBonus,
    IReadOnlyDictionary<DogmaAttributeId, double>? ChargeAttributes,
    long ReloadReadyAt
);

public static class WeaponRules
{
    private const double Percent = 100;

    public static IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(WeaponModuleInputs inputs)
    {
        var attributes = inputs.Attributes.ToDictionary();
        bool hybrid = CombatDefinitions.IsSupportedHybrid(inputs.GroupId, attributes);
        if (!hybrid && inputs.TypeId != (int)CombatWeaponType.CivilianGatlingRailgun)
        {
            return attributes;
        }
        foreach (
            var modifier in CombatDefinitions.SkillModifiers.Where(m =>
                (hybrid || m.RequiredSkill == CombatSkill.Gunnery) && (m.GroupId is null || m.GroupId == inputs.GroupId)
            )
        )
        {
            attributes[modifier.Attribute] *= 1 + modifier.PerLevel * inputs.Levels.GetValueOrDefault(modifier.Skill);
        }
        if (hybrid)
        {
            attributes[DogmaAttributeId.MaxRange] *= 1 + inputs.HullOptimalBonus / Percent;
            if (inputs.ChargeAttributes is { } charge)
            {
                attributes[DogmaAttributeId.MaxRange] *= charge.GetValueOrDefault(DogmaAttributeId.WeaponRangeMultiplier, 1);
                attributes[DogmaAttributeId.CapacitorNeed] *= 1 + charge.GetValueOrDefault(DogmaAttributeId.CapacitorNeedBonus) / Percent;
            }
            attributes[DogmaAttributeId.NextActivationTime] = inputs.ReloadReadyAt;
        }
        return attributes;
    }
}
