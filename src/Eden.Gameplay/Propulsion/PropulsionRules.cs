using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

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
            foreach (
                var bonus in bonuses.Where(b =>
                    b.Group == group
                    || b.RequiredSkill is int required
                        && InventoryDefinitions.RequiredSkills.Any(pair => basis.GetValueOrDefault(pair.Skill) == required)
                )
            )
            {
                attributes[bonus.Attribute] =
                    attributes.GetValueOrDefault(bonus.Attribute) * (1 + bonus.Percent / PropulsionDefinitions.Percent);
            }
        }
        return attributes;
    }
}
