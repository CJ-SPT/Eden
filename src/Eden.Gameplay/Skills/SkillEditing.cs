using System.Collections.Immutable;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Skills;

public sealed record SkillAttributeValue(int AttributeId, double Value);

public sealed record SkillEffectReference(int EffectId, bool IsDefault);

public sealed record SkillPrerequisite(int TypeId, int Level);

public sealed record SkillAlphaLimit(int RaceId, int Level);

public sealed record SkillDefinition
{
    public int TypeId { get; init; }
    public string Name { get; init; } = "New skill";
    public string Description { get; init; } = "";
    public int GroupId { get; init; }
    public int? IconId { get; init; }
    public int? MarketGroupId { get; init; }
    public bool Published { get; init; } = true;
    public double BasePrice { get; init; }
    public double Rank { get; init; } = 1;
    public int PrimaryAttribute { get; init; } = (int)DogmaAttributeId.Intelligence;
    public int SecondaryAttribute { get; init; } = (int)DogmaAttributeId.Memory;
    public ImmutableArray<SkillPrerequisite> Prerequisites { get; init; } = [];
    public ImmutableArray<SkillAlphaLimit> AlphaLimits { get; init; } = [];
    public ImmutableArray<SkillAttributeValue> Attributes { get; init; } = [];
    public ImmutableArray<SkillEffectReference> Effects { get; init; } = [];
}

public sealed record SkillEditorEntry(SkillDefinition Definition, SkillDefinition? Original, bool UserCreated, bool Deleted, long Revision)
{
    public bool CanEditBonuses => UserCreated || SkillEditingRules.CatalogBonuses(Definition.TypeId);
    public bool IsModified => !UserCreated && Original is not null && !SkillEditingRules.SameDefinition(Definition, Original);
}

public static class SkillEditingRules
{
    // Reserved above the pinned build's item types, within its signed 32-bit type-ID range.
    public const int FirstCustomTypeId = 1_500_000_000;

    // Other existing bonuses have feature-specific owners; retaining them prevents
    // changing client dogma while leaving an authoritative typed calculation unchanged.
    public static bool CatalogBonuses(int typeId) => PassiveModules.Skills.Contains(typeId);

    public static bool SameDefinition(SkillDefinition left, SkillDefinition right) =>
        left with
        {
            Prerequisites = [],
            AlphaLimits = [],
            Attributes = [],
            Effects = [],
        } == right with { Prerequisites = [], AlphaLimits = [], Attributes = [], Effects = [] }
        && left.Prerequisites.OrderBy(value => value.TypeId).SequenceEqual(right.Prerequisites.OrderBy(value => value.TypeId))
        && left.AlphaLimits.OrderBy(value => value.RaceId).SequenceEqual(right.AlphaLimits.OrderBy(value => value.RaceId))
        && left.Attributes.OrderBy(value => value.AttributeId).SequenceEqual(right.Attributes.OrderBy(value => value.AttributeId))
        && left.Effects.OrderBy(value => value.EffectId).SequenceEqual(right.Effects.OrderBy(value => value.EffectId));
}

public sealed record SkillEditorOption(int Id, string Name, string Description = "", bool Available = true, string SearchTerms = "");

public sealed record SkillEditorChoices(
    IReadOnlyList<SkillEditorOption> Groups,
    IReadOnlyList<SkillEditorOption> Attributes,
    IReadOnlyList<SkillEditorOption> Effects,
    IReadOnlyList<SkillEditorOption> Icons,
    IReadOnlyList<SkillEditorOption> MarketGroups
);

public sealed record SkillEditorSnapshot(long Generation, string CatalogFingerprint, ImmutableArray<SkillEditorEntry> Skills);

public sealed record PublishedSkillDefinition(SkillDefinition Definition, bool UserCreated, bool Deleted, bool PriceChanged = false);

public sealed record SkillEditorRevision(
    long Id,
    int TypeId,
    string Author,
    DateTimeOffset CreatedAt,
    string Operation,
    SkillDefinition Definition
);

public interface ISkillEditor
{
    SkillEditorSnapshot Read();
    IReadOnlyList<SkillEditorOption> Groups();
    IReadOnlyList<SkillEditorOption> Attributes();
    IReadOnlyList<SkillEditorOption> Effects();
    SkillEditorChoices Choices();
    SkillEditorEntry Save(long generation, SkillDefinition definition, string author);
    SkillEditorEntry Create(long generation, SkillDefinition definition, string author);
    SkillEditorEntry Restore(long generation, int typeId, long? revision, string author);
    SkillEditorEntry Delete(long generation, int typeId, string author);
    IReadOnlyList<SkillEditorRevision> History(int typeId);
}
