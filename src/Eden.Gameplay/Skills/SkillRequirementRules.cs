using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Skills;

public static class SkillRequirementRules
{
    public static bool Meets(IReadOnlyDictionary<DogmaAttributeId, double> attributes, IReadOnlyDictionary<int, int> levels) =>
        !InventoryDefinitions.RequiredSkills.Any(pair =>
        {
            int skill = checked((int)attributes.GetValueOrDefault(pair.Skill));
            return skill != 0 && levels.GetValueOrDefault(skill) < attributes.GetValueOrDefault(pair.Level);
        });
}
