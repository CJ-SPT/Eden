using Eden.Gameplay.Dogma;

namespace Eden.Gameplay.Propulsion;

public static class PropulsionRules
{
    public static double NavigationSpeed(double speed, double percentBonus) => speed * (1 + percentBonus / PropulsionDefinitions.Percent);

    public static IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(
        int group,
        IReadOnlyDictionary<DogmaAttributeId, double> basis,
        IReadOnlyList<NavigationBonus> bonuses
    )
    {
        var attributes = basis.ToDictionary();
        if (group == PropulsionDefinitions.AfterburnerGroup)
        {
            foreach (var bonus in bonuses.Where(b => b.RequiredSkill.HasValue || b.Group == group))
            {
                attributes[bonus.Attribute] =
                    attributes.GetValueOrDefault(bonus.Attribute) * (1 + bonus.Percent / PropulsionDefinitions.Percent);
            }
        }
        return attributes;
    }
}
