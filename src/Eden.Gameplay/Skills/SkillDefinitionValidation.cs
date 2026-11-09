using System.Collections.Immutable;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Skills;

public static class SkillDefinitionValidation
{
    private const int SkillLevelEffect = 132;
    private static readonly int[] Races = [1, 2, 4, 8];

    public static SkillDefinition Normalize(SkillDefinition skill)
    {
        if (
            (!skill.Prerequisites.IsDefault && skill.Prerequisites.Any(value => value is null))
            || (!skill.AlphaLimits.IsDefault && skill.AlphaLimits.Any(value => value is null))
            || (!skill.Attributes.IsDefault && skill.Attributes.Any(value => value is null))
            || (!skill.Effects.IsDefault && skill.Effects.Any(value => value is null))
        )
        {
            throw new InvalidDataException("Skill collections cannot contain empty entries.");
        }
        return skill with
        {
            Name = skill.Name?.Trim() ?? "",
            Description = skill.Description ?? "",
            Prerequisites = skill.Prerequisites.IsDefault ? [] : skill.Prerequisites.OrderBy(value => value.TypeId).ToImmutableArray(),
            AlphaLimits = skill.AlphaLimits.IsDefault ? [] : skill.AlphaLimits.OrderBy(value => value.RaceId).ToImmutableArray(),
            Attributes = skill.Attributes.IsDefault ? [] : skill.Attributes.OrderBy(value => value.AttributeId).ToImmutableArray(),
            Effects = skill.Effects.IsDefault ? [] : skill.Effects.OrderBy(value => value.EffectId).ToImmutableArray(),
        };
    }

    public static void Validate(
        SkillDefinition skill,
        IReadOnlyDictionary<int, SkillDefinition> definitions,
        IReadOnlyDictionary<int, SkillDefinition> originals,
        SkillEditorChoices choices
    )
    {
        if (skill.Name.Length is < 1 or > 200 || skill.Description.Length > 16000 || skill.Name.Any(char.IsControl))
        {
            throw new InvalidDataException("Provide a name of 1–200 characters and a description of at most 16,000 characters.");
        }
        if (!choices.Groups.Any(group => group.Id == skill.GroupId) || !double.IsFinite(skill.Rank) || skill.Rank is <= 0 or > 1000)
        {
            throw new InvalidDataException("Choose an existing skill group and a positive training multiplier up to 1,000.");
        }
        if (!IsTrainingAttribute(skill.PrimaryAttribute) || !IsTrainingAttribute(skill.SecondaryAttribute))
        {
            throw new InvalidDataException("Choose valid primary and secondary character attributes.");
        }
        if (!double.IsFinite(skill.BasePrice) || skill.BasePrice < 0 || skill.BasePrice > 1e15)
        {
            throw new InvalidDataException("Skillbook price must be finite, nonnegative and no more than 1,000 trillion ISK.");
        }
        if (
            skill.IconId is int icon && !choices.Icons.Any(value => value.Id == icon)
            || skill.MarketGroupId is int market && !choices.MarketGroups.Any(value => value.Id == market)
        )
        {
            throw new InvalidDataException("Choose an existing icon and market group, or leave them empty.");
        }
        if (
            skill.Prerequisites.Length > 6
            || skill.Prerequisites.DistinctBy(value => value.TypeId).Count() != skill.Prerequisites.Length
            || skill.Prerequisites.Any(value => value.Level is < 1 or > 5 || !definitions.ContainsKey(value.TypeId))
        )
        {
            throw new InvalidDataException("Choose at most six distinct existing skills at levels I through V.");
        }
        if (
            skill.AlphaLimits.Length != Races.Length
            || skill.AlphaLimits.Select(value => value.RaceId).Order().SequenceEqual(Races) == false
            || skill.AlphaLimits.Any(value => value.Level is < 0 or > 5)
        )
        {
            throw new InvalidDataException("Provide an Alpha level from zero through five for every race.");
        }
        if (
            skill.Attributes.Length > 200
            || skill.Attributes.DistinctBy(value => value.AttributeId).Count() != skill.Attributes.Length
            || skill.Attributes.Any(value =>
                ManagedAttribute(value.AttributeId)
                || !double.IsFinite(value.Value)
                || !choices.Attributes.Any(option => option.Id == value.AttributeId)
            )
        )
        {
            throw new InvalidDataException(
                "Additional attributes must be distinct, known and finite. Edit training and prerequisites in their own fields."
            );
        }
        if (
            skill.Effects.Length > 100
            || skill.Effects.DistinctBy(value => value.EffectId).Count() != skill.Effects.Length
            || skill.Effects.Count(value => value.IsDefault) > 1
            || skill.Effects.Any(value => !choices.Effects.Any(option => option.Id == value.EffectId))
        )
        {
            throw new InvalidDataException("Effects must be distinct catalog effects, with at most one default effect.");
        }
        HashSet<int> visiting = [];
        bool originalSkill = originals.TryGetValue(skill.TypeId, out var original);
        bool changedBonuses =
            !originalSkill
            || !skill.Attributes.OrderBy(value => value.AttributeId).SequenceEqual(original!.Attributes.OrderBy(value => value.AttributeId))
            || !skill.Effects.OrderBy(value => value.EffectId).SequenceEqual(original!.Effects.OrderBy(value => value.EffectId));
        if (changedBonuses)
        {
            if (originalSkill && !SkillEditingRules.CatalogBonuses(skill.TypeId))
            {
                throw new InvalidDataException(
                    "This original skill uses a dedicated gameplay calculation. Its bonus attributes and effects must be retained."
                );
            }
            foreach (var reference in skill.Effects.Where(effect => effect.EffectId != SkillLevelEffect))
            {
                if (!choices.Effects.Any(value => value.Id == reference.EffectId && value.Available))
                {
                    throw new InvalidDataException(
                        $"Effect {reference.EffectId} cannot be added to a skill. Choose a supported passive effect; "
                            + "online, active and targeted module effects are not skill bonuses."
                    );
                }
            }
        }
        HashSet<int> visited = [];
        void Visit(int id)
        {
            if (visited.Contains(id))
            {
                return;
            }
            if (!visiting.Add(id))
            {
                throw new InvalidDataException("Skill prerequisites cannot contain a cycle.");
            }
            foreach (var required in definitions[id].Prerequisites)
            {
                Visit(required.TypeId);
            }
            visiting.Remove(id);
            visited.Add(id);
        }
        Visit(skill.TypeId);
    }

    private static bool IsTrainingAttribute(int id) => id is >= 164 and <= 168;

    public static bool ManagedAttribute(int id) =>
        id is (int)DogmaAttributeId.PrimaryAttribute or (int)DogmaAttributeId.SecondaryAttribute or (int)DogmaAttributeId.SkillTimeConstant
        || InventoryDefinitions.RequiredSkills.Any(pair => (int)pair.Skill == id || (int)pair.Level == id);
}
