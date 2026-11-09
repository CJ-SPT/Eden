using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using Eden.Gameplay.Skills;

namespace Eden.Gameplay.Skills;

public sealed class SkillForm
{
    public int TypeId { get; set; }

    [Required, StringLength(200)]
    public string Name { get; set; } = "New skill";

    [StringLength(16000)]
    public string Description { get; set; } = "";
    public int GroupId { get; set; }
    public int? IconId { get; set; }
    public int? MarketGroupId { get; set; }
    public bool Published { get; set; } = true;
    public double BasePrice { get; set; }
    public double Rank { get; set; } = 1;
    public int PrimaryAttribute { get; set; } = 165;
    public int SecondaryAttribute { get; set; } = 166;
    public List<PrerequisiteInput> Prerequisites { get; set; } = [];
    public List<AlphaInput> AlphaLimits { get; set; } = [];
    public List<AttributeInput> Attributes { get; set; } = [];
    public List<EffectInput> Effects { get; set; } = [];

    public sealed class PrerequisiteInput
    {
        public int TypeId { get; set; }
        public int Level { get; set; } = 1;
    }

    public sealed class AlphaInput
    {
        public int RaceId { get; set; }
        public int Level { get; set; }
    }

    public sealed class AttributeInput
    {
        public int AttributeId { get; set; }
        public double Value { get; set; }
    }

    public sealed class EffectInput
    {
        public int EffectId { get; set; }
        public bool IsDefault { get; set; }
    }

    public SkillDefinition Build() =>
        new()
        {
            TypeId = TypeId,
            Name = Name,
            Description = Description,
            GroupId = GroupId,
            IconId = IconId,
            MarketGroupId = MarketGroupId,
            Published = Published,
            BasePrice = BasePrice,
            Rank = Rank,
            PrimaryAttribute = PrimaryAttribute,
            SecondaryAttribute = SecondaryAttribute,
            Prerequisites = Prerequisites.Select(value => new SkillPrerequisite(value.TypeId, value.Level)).ToImmutableArray(),
            AlphaLimits = AlphaLimits.Select(value => new SkillAlphaLimit(value.RaceId, value.Level)).ToImmutableArray(),
            Attributes = Attributes.Select(value => new SkillAttributeValue(value.AttributeId, value.Value)).ToImmutableArray(),
            Effects = Effects.Select(value => new SkillEffectReference(value.EffectId, value.IsDefault)).ToImmutableArray(),
        };

    public static SkillForm From(SkillDefinition skill) =>
        new()
        {
            TypeId = skill.TypeId,
            Name = skill.Name,
            Description = skill.Description,
            GroupId = skill.GroupId,
            IconId = skill.IconId,
            MarketGroupId = skill.MarketGroupId,
            Published = skill.Published,
            BasePrice = skill.BasePrice,
            Rank = skill.Rank,
            PrimaryAttribute = skill.PrimaryAttribute,
            SecondaryAttribute = skill.SecondaryAttribute,
            Prerequisites = skill
                .Prerequisites.Select(value => new PrerequisiteInput { TypeId = value.TypeId, Level = value.Level })
                .ToList(),
            AlphaLimits = skill.AlphaLimits.Select(value => new AlphaInput { RaceId = value.RaceId, Level = value.Level }).ToList(),
            Attributes = skill
                .Attributes.Select(value => new AttributeInput { AttributeId = value.AttributeId, Value = value.Value })
                .ToList(),
            Effects = skill.Effects.Select(value => new EffectInput { EffectId = value.EffectId, IsDefault = value.IsDefault }).ToList(),
        };
}
