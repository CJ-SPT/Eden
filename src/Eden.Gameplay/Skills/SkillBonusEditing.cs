using Eden.Gameplay.Dogma;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Skills;

namespace Eden.Gameplay.Skills;

public static class SkillBonusEditing
{
    // Build 3662168 Mining/Astrogeology typedogma: 389 scales attribute 434 by
    // skillLevel; 391 applies it as a percentage to modules requiring Mining.
    public const int MiningScalingEffect = 389;
    public const int MiningYieldEffect = 391;
    public const int MiningBonusAttribute = 434;
    public const double ExampleMiningPercent = 25;

    public static bool SupportsMining(SkillEditorChoices choices) =>
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

    public static IEnumerable<SkillEditorOption> SelectedEffects(SkillForm form, SkillEditorChoices choices) =>
        form
            .Effects.Select(effect => SkillChoiceSearch.Index(choices.Effects).GetValueOrDefault(effect.EffectId))
            .OfType<SkillEditorOption>();

    public static bool CanConfigureMining(SkillForm form, SkillEditorChoices choices) =>
        SupportsMining(choices)
        && form.Attributes.Count(attribute => attribute.AttributeId == MiningBonusAttribute) <= 1
        && form.Effects.Count(effect => effect.EffectId == MiningScalingEffect) <= 1
        && form.Effects.Count(effect => effect.EffectId == MiningYieldEffect) <= 1
        && form.Effects.All(effect =>
            SkillChoiceSearch.Index(choices.Effects).GetValueOrDefault(effect.EffectId) is { } option
            && (
                option.Id == MiningScalingEffect
                || IsMiningApplication(option)
                || option.Modifiers.All(modifier =>
                    modifier.SourceAttributeId != MiningBonusAttribute && modifier.TargetAttributeId != MiningBonusAttribute
                )
            )
        );

    public static bool HasMiningBonus(SkillForm form, SkillEditorChoices choices) =>
        CanConfigureMining(form, choices)
        && form.Attributes.Any(attribute => attribute.AttributeId == MiningBonusAttribute)
        && form.Effects.Any(effect => effect.EffectId == MiningScalingEffect)
        && form.Effects.Any(effect => effect.EffectId == MiningYieldEffect)
        && SelectedEffects(form, choices).All(effect => effect.Id == MiningYieldEffect || !IsMiningApplication(effect));

    public static bool HasMiningRules(SkillForm form, SkillEditorChoices choices) =>
        SelectedEffects(form, choices).Any(effect => effect.Id == MiningScalingEffect || IsMiningApplication(effect));

    public static void RemoveMining(SkillForm form, SkillEditorChoices choices)
    {
        if (!HasMiningBonus(form, choices))
        {
            throw new InvalidOperationException("These mining rules require advanced editing.");
        }
        form.Effects.RemoveAll(effect => effect.EffectId is MiningScalingEffect or MiningYieldEffect);
    }

    public static bool IsMiningApplication(SkillEditorOption effect) =>
        effect.Modifiers.Count == 1
        && effect.Modifiers[0]
            is {
                Local: false,
                SourceAttributeId: MiningBonusAttribute,
                TargetAttributeId: (int)DogmaAttributeId.MiningAmount,
                Operation: DogmaOperation.PostPercent,
                RequiredSkillId: (int)MiningSkill.Mining,
            };

    public static IEnumerable<int> InputIds(SkillEditorOption effect) =>
        effect
            .Modifiers.Select(modifier =>
                modifier.Local && modifier.SourceAttributeId == (int)DogmaAttributeId.SkillLevel
                    ? modifier.TargetAttributeId
                    : modifier.SourceAttributeId
            )
            .Where(id => id != (int)DogmaAttributeId.SkillLevel)
            .Distinct();

    public static void EnsureInputs(SkillForm form, SkillEditorChoices choices)
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

    public static void AddMining(SkillForm form, SkillEditorChoices choices)
    {
        if (!CanConfigureMining(form, choices))
        {
            throw new InvalidOperationException("The installed catalog does not support the ore mining yield recipe.");
        }
        // Module-online versions (notably 1882) and equivalent application rules
        // would otherwise stack with 391 after converting a flat bonus to per-level.
        var replacements = SelectedEffects(form, choices)
            .Where(effect => effect.Id != MiningYieldEffect && IsMiningApplication(effect))
            .Select(effect => effect.Id)
            .ToHashSet();
        form.Effects.RemoveAll(effect => replacements.Contains(effect.EffectId));
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
