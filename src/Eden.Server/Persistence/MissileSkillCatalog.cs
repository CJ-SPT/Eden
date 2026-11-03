using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Server.Catalog;

namespace Eden.Server.Persistence;

internal static class MissileSkillCatalog
{
    private enum LegacyEffect
    {
        EmDamage = 660,
        ExplosiveDamage = 661,
        ThermalDamage = 662,
        KineticDamage = 668,
        SpecializationRate = 1851,
    }

    internal static IReadOnlyList<PassiveModifier>? LegacyModifiers(int skill, ClientDogmaeffects effect)
    {
        if (!MissileDefinitions.Skills.Contains(skill) || !Enum.IsDefined(typeof(LegacyEffect), checked((int)effect.EffectID!.Value)))
        {
            return null;
        }
        var kind = (LegacyEffect)effect.EffectID.Value;
        var attribute = kind switch
        {
            LegacyEffect.EmDamage => DogmaAttributeId.EmDamage,
            LegacyEffect.ExplosiveDamage => DogmaAttributeId.ExplosiveDamage,
            LegacyEffect.ThermalDamage => DogmaAttributeId.ThermalDamage,
            LegacyEffect.KineticDamage => DogmaAttributeId.KineticDamage,
            _ => DogmaAttributeId.WeaponCycleTime,
        };
        bool rate = kind == LegacyEffect.SpecializationRate;
        return
        [
            new(
                rate ? ModifierDomain.Ship : ModifierDomain.Character,
                rate ? ModifierSelector.RequiredSkill : ModifierSelector.OwnerRequiredSkill,
                skill,
                attribute,
                rate ? DogmaAttributeId.MissileRateBonus : DogmaAttributeId.SkillDamageBonus,
                DogmaOperation.PostPercent,
                PassiveEffectPhase.Fitted
            ),
        ];
    }
}
