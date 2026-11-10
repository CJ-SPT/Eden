using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;

namespace Eden.Protocol.Tests.Skills;

public sealed class SkillRequirementRulesTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(5, true)]
    public void BatchedSkillEligibilityPreservesRequiredLevels(int level, bool expected)
    {
        const int Skill = 3436;
        var first = InventoryDefinitions.RequiredSkills[0];
        var second = InventoryDefinitions.RequiredSkills[1];
        Dictionary<DogmaAttributeId, double> attributes = new()
        {
            [first.Skill] = Skill,
            [first.Level] = 3,
            [second.Skill] = Skill + 1,
            [second.Level] = 1,
        };
        Dictionary<int, int> levels = new() { [Skill] = level, [Skill + 1] = 1 };
        Assert.Equal(expected, SkillRequirementRules.Meets(attributes, levels));
        levels.Remove(Skill + 1);
        Assert.False(SkillRequirementRules.Meets(attributes, levels));
        Assert.True(SkillRequirementRules.Meets(new Dictionary<DogmaAttributeId, double>(), levels));
    }
}
