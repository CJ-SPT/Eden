using Eden.Gameplay.Dogma;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Skills;

namespace Eden.Server.Administration;

internal static class SkillBonusEditing
{
    // Build 3662168 Mining/Astrogeology typedogma: 389 scales attribute 434 by
    // skillLevel; 391 applies it as a percentage to modules requiring Mining.
    internal const int MiningScalingEffect = 389;
    internal const int MiningYieldEffect = 391;
    internal const int MiningBonusAttribute = 434;
    internal const double ExampleMiningPercent = 25;

    internal static bool SupportsMining(SkillEditorChoices choices) =>
        SkillChoiceSearch.Index(choices.Attributes).ContainsKey(MiningBonusAttribute)
        && SkillChoiceSearch.Index(choices.Effects).GetValueOrDefault(MiningScalingEffect) is { Available: true } scaling
        && scaling.Modifiers.Count == 1
        && scaling.Modifiers[0]
            is {
                Local: true,
                SourceAttributeId: (int)DogmaAttributeId.SkillLevel,
                TargetAttributeId: MiningBonusAttribute,
                Operation: DogmaOperation.PreMultiply,
            }
        && SkillChoiceSearch.Index(choices.Effects).GetValueOrDefault(MiningYieldEffect) is { Available: true } application
        && application.Modifiers.Count == 1
        && application.Modifiers[0]
            is {
                Local: false,
                SourceAttributeId: MiningBonusAttribute,
                TargetAttributeId: (int)DogmaAttributeId.MiningAmount,
                Operation: DogmaOperation.PostPercent,
                RequiredSkillId: (int)MiningSkill.Mining,
            };

    internal static IEnumerable<SkillEditorOption> SelectedEffects(SkillForm form, SkillEditorChoices choices) =>
        form
            .Effects.Select(effect => SkillChoiceSearch.Index(choices.Effects).GetValueOrDefault(effect.EffectId))
            .OfType<SkillEditorOption>();

    internal static IEnumerable<int> InputIds(SkillEditorOption effect) =>
        effect
            .Modifiers.Select(modifier =>
                modifier.Local && modifier.SourceAttributeId == (int)DogmaAttributeId.SkillLevel
                    ? modifier.TargetAttributeId
                    : modifier.SourceAttributeId
            )
            .Where(id => id != (int)DogmaAttributeId.SkillLevel)
            .Distinct();

    internal static void EnsureInputs(SkillForm form, SkillEditorChoices choices)
    {
        foreach (var effect in SelectedEffects(form, choices))
        {
            foreach (int id in InputIds(effect))
            {
                if (
                    form.Attributes.All(attribute => attribute.AttributeId != id)
                    && SkillChoiceSearch.Index(choices.Attributes).GetValueOrDefault(id) is { } option
                )
                {
                    form.Attributes.Add(new() { AttributeId = id, Value = option.DefaultValue });
                }
            }
        }
    }

    internal static void AddMining(SkillForm form, SkillEditorChoices choices)
    {
        if (!SupportsMining(choices))
        {
            throw new InvalidOperationException("The installed catalog does not support the ore mining yield recipe.");
        }
        foreach (int id in new[] { MiningScalingEffect, MiningYieldEffect })
        {
            if (form.Effects.All(effect => effect.EffectId != id))
            {
                form.Effects.Add(new() { EffectId = id });
            }
        }
        if (form.Attributes.All(attribute => attribute.AttributeId != MiningBonusAttribute))
        {
            form.Attributes.Add(new() { AttributeId = MiningBonusAttribute, Value = ExampleMiningPercent });
        }
    }
}
