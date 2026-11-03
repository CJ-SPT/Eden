using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Combat;

public sealed record WeaponModuleInputs(
    int TypeId,
    int GroupId,
    IReadOnlyDictionary<DogmaAttributeId, double> Attributes,
    IReadOnlyDictionary<CombatSkill, int> Levels,
    double HullOptimalBonus,
    IReadOnlyDictionary<DogmaAttributeId, double>? ChargeAttributes,
    long ReloadReadyAt,
    double HullProjectileDamageBonus = 0
);

public static class WeaponRules
{
    private const double Percent = 100;

    public static IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(WeaponModuleInputs inputs)
    {
        var attributes = inputs.Attributes.ToDictionary();
        bool ammoFed = CombatDefinitions.IsSupportedAmmoTurret(inputs.GroupId, attributes);
        if (!ammoFed && inputs.TypeId != (int)CombatWeaponType.CivilianGatlingRailgun)
        {
            return attributes;
        }
        foreach (
            var modifier in CombatDefinitions.SkillModifiers.Where(m =>
                (
                    m.GroupId is int group
                        ? group == inputs.GroupId
                        : InventoryDefinitions.RequiredSkills.Any(pair => attributes.GetValueOrDefault(pair.Skill) == (int)m.RequiredSkill)
                )
            )
        )
        {
            attributes[modifier.Attribute] =
                attributes.GetValueOrDefault(modifier.Attribute)
                * (1 + modifier.PerLevel * inputs.Levels.GetValueOrDefault(modifier.Skill));
        }
        if (ammoFed)
        {
            if (
                InventoryDefinitions.RequiredSkills.Any(pair =>
                    attributes.GetValueOrDefault(pair.Skill) == (int)CombatSkill.SmallHybridTurret
                )
            )
            {
                attributes[DogmaAttributeId.MaxRange] *= 1 + inputs.HullOptimalBonus / Percent;
            }
            if (inputs.ChargeAttributes is { } charge)
            {
                foreach (var modifier in AmmunitionDefinitions.TurretModifiers)
                {
                    if (charge.TryGetValue(modifier.Source, out double value))
                    {
                        double factor = modifier.Operation == DogmaOperation.PostPercent ? 1 + value / Percent : value;
                        attributes[modifier.Attribute] = attributes.GetValueOrDefault(modifier.Attribute, modifier.DefaultValue) * factor;
                    }
                }
            }
            attributes[DogmaAttributeId.NextActivationTime] = inputs.ReloadReadyAt;
            if (inputs.GroupId == CombatDefinitions.ProjectileWeaponGroup)
            {
                if (
                    InventoryDefinitions.RequiredSkills.Any(pair =>
                        attributes.GetValueOrDefault(pair.Skill) == (int)CombatSkill.SmallProjectileTurret
                    )
                )
                {
                    attributes[DogmaAttributeId.DamageMultiplier] *= 1 + inputs.HullProjectileDamageBonus / Percent;
                }
                attributes[DogmaAttributeId.BarrageDamageMultiplier] =
                    attributes.GetValueOrDefault(DogmaAttributeId.DamageMultiplier)
                    * (1 + attributes.GetValueOrDefault(DogmaAttributeId.BarrageDamageBonus) / Percent);
                attributes[DogmaAttributeId.BarrageFalloff] =
                    attributes.GetValueOrDefault(DogmaAttributeId.AccuracyFalloff)
                    * (1 + attributes.GetValueOrDefault(DogmaAttributeId.BarrageFalloffBonus) / Percent);
            }
        }
        return attributes;
    }
}
