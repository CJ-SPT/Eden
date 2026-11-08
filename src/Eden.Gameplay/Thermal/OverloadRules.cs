using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Thermal;

public sealed record OverloadBonus(DogmaAttributeId Attribute, DogmaAttributeId SourceAttribute);

public sealed record OverloadProfile(
    int TypeId,
    int EffectId,
    ModuleRack Rack,
    double HitPoints,
    double HeatDamage,
    double HeatAbsorption,
    int RequiredThermodynamics,
    IReadOnlyList<OverloadBonus> Bonuses
);

public static class OverloadRules
{
    public const int ThermodynamicsSkill = 28164;
    public const int EffectCategory = 5;
    private const int MaximumSkillLevel = 5;
    private const double Percent = 100;

    public static OverloadProfile Define(
        int type,
        int effect,
        ModuleRack rack,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        IReadOnlyList<OverloadBonus> bonuses
    )
    {
        double hp = attributes.GetValueOrDefault(DogmaAttributeId.StructureHitPoints);
        double damage = attributes.GetValueOrDefault(DogmaAttributeId.HeatDamage);
        double absorption = attributes.GetValueOrDefault(DogmaAttributeId.HeatAbsorption);
        double required = attributes.GetValueOrDefault(DogmaAttributeId.RequiredThermodynamics);
        if (
            rack is not (ModuleRack.Low or ModuleRack.Medium or ModuleRack.High)
            || type <= 0
            || effect <= 0
            || !double.IsFinite(hp)
            || hp <= 0
            || !double.IsFinite(damage)
            || damage < 0
            || !double.IsFinite(absorption)
            || absorption < 0
            || !double.IsFinite(required)
            || required is < 0 or > MaximumSkillLevel
            || required != Math.Truncate(required)
            || bonuses.Count == 0
            || bonuses.Select(b => b.Attribute).Distinct().Count() != bonuses.Count
            || bonuses.Any(b =>
                !Enum.IsDefined(b.Attribute)
                || !Enum.IsDefined(b.SourceAttribute)
                || !attributes.TryGetValue(b.SourceAttribute, out double value)
                || !double.IsFinite(value)
                || value < -Percent
            )
        )
        {
            throw new InvalidDataException("Invalid overload profile.");
        }
        return new(type, effect, rack, hp, damage, absorption, checked((int)required), Array.AsReadOnly(bonuses.ToArray()));
    }

    public static IReadOnlyList<DogmaModifier> Modifiers(OverloadProfile profile, long item, bool overloaded) =>
        overloaded
            ? profile
                .Bonuses.Select(b => new DogmaModifier(
                    item,
                    b.Attribute,
                    DogmaOperation.PostPercent,
                    new DogmaValue.Attribute(item, b.SourceAttribute)
                ))
                .ToArray()
            : [];

    public static double ThermodynamicsDamage(double damage, int level, double bonusPerLevel)
    {
        if (
            !double.IsFinite(damage)
            || damage < 0
            || level is < 0 or > MaximumSkillLevel
            || !double.IsFinite(bonusPerLevel)
            || bonusPerLevel > 0
        )
        {
            throw new InvalidDataException("Invalid Thermodynamics damage inputs.");
        }
        double multiplier = 1 + bonusPerLevel * level / Percent;
        double amount = damage * multiplier;
        if (!double.IsFinite(multiplier) || multiplier < 0 || !double.IsFinite(amount) || amount < 0)
        {
            throw new InvalidDataException("Invalid effective heat damage.");
        }
        return amount;
    }
}
