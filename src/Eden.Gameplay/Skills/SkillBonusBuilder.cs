using System.Globalization;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Skills;

namespace Eden.Gameplay.Skills;

public sealed record SkillBonusTemplate(
    int EffectId,
    int InputId,
    int AttributeId,
    string Name,
    string Target,
    string Unit,
    DogmaOperation Operation,
    int? ScalingId,
    bool InlineScaling
)
{
    public string SearchTerms { get; init; } = "";
    public bool Multiplier => Operation is DogmaOperation.PreMultiply or DogmaOperation.PostMultiply;
    public bool Percent => Operation != DogmaOperation.Add;
    public bool CanScale => !Multiplier && (InlineScaling || ScalingId.HasValue);
    public string StatKey => $"{AttributeId}:{Percent}";
    public string StatName =>
        Name
        + (
            Percent ? " (%)"
            : string.IsNullOrWhiteSpace(Unit) ? " (amount)"
            : $" ({Unit})"
        );

    public double StoredValue(double change) => Multiplier ? 1 + change / 100 : change;

    public double DisplayValue(double stored) => Multiplier ? (stored - 1) * 100 : stored;
}

public sealed class SkillBonusValue(SkillForm form, SkillBonusTemplate template, SkillForm.AttributeInput input, int? scalingId)
{
    public SkillBonusTemplate Template { get; } = template;
    public int? ScalingId { get; set; } = scalingId;
    public SkillForm Form { get; } = form;
    public bool PerLevel => Template.InlineScaling || ScalingId.HasValue;
    public bool Decrease
    {
        get => double.IsNegative(Template.DisplayValue(input.Value));
        set => input.Value = Template.StoredValue(Math.CopySign(Amount, value ? -1 : 1));
    }
    public double Amount
    {
        get => Math.Abs(Template.DisplayValue(input.Value));
        set => input.Value = Template.StoredValue(Math.CopySign(Math.Abs(value), Decrease ? -1 : 1));
    }

    public string Preview(int level)
    {
        double change = Template.DisplayValue(input.Value) * (PerLevel ? level : 1);
        return change.ToString("+0.##;-0.##;0", CultureInfo.CurrentCulture) + (Template.Percent ? "%" : " " + Template.Unit);
    }
}

public static class SkillBonusBuilder
{
    public static IReadOnlyList<SkillBonusTemplate> Templates(SkillEditorChoices choices)
    {
        var attributes = SkillChoiceSearch.Index(choices.Attributes);
        var scaling = choices
            .Effects.Where(effect => effect.Available && effect.Modifiers.Count == 1)
            .Where(effect => IsScaling(effect.Modifiers[0], effect.Modifiers[0].TargetAttributeId))
            .GroupBy(effect => effect.Modifiers[0].TargetAttributeId)
            .ToDictionary(group => group.Key, group => group.Min(effect => effect.Id));
        List<SkillBonusTemplate> result = [];
        foreach (var effect in choices.Effects.Where(effect => effect.Available).OrderBy(effect => effect.Id))
        {
            var outputs = effect.Modifiers.Where(modifier => !modifier.Local).ToArray();
            if (outputs.Length != 1 || !attributes.ContainsKey(outputs[0].SourceAttributeId))
            {
                continue;
            }
            var output = outputs[0];
            if (
                !Enum.IsDefined((DogmaAttributeId)output.TargetAttributeId)
                || output.AttributeName.Contains("bonus", StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }
            var locals = effect.Modifiers.Where(modifier => modifier.Local).ToArray();
            if (
                locals.Length > 1
                || locals.Any(modifier => !IsScaling(modifier, output.SourceAttributeId))
                || string.IsNullOrWhiteSpace(output.TargetName)
                || output.Operation
                    is not (DogmaOperation.PostPercent or DogmaOperation.Add or DogmaOperation.PreMultiply or DogmaOperation.PostMultiply)
            )
            {
                continue;
            }
            bool inline = locals.Length == 1;
            if (inline && output.Operation is DogmaOperation.PreMultiply or DogmaOperation.PostMultiply)
            {
                continue;
            }
            result.Add(
                new(
                    effect.Id,
                    output.SourceAttributeId,
                    output.TargetAttributeId,
                    output.AttributeName,
                    output.TargetName,
                    output.UnitName,
                    output.Operation,
                    scaling.TryGetValue(output.SourceAttributeId, out int id) ? id : null,
                    inline
                )
                {
                    SearchTerms = attributes.GetValueOrDefault(output.TargetAttributeId)?.SearchTerms ?? "",
                }
            );
        }
        return result
            .OrderBy(template => template.StatName)
            .ThenBy(template => template.Target)
            .ThenBy(template => template.EffectId)
            .ToArray();
    }

    private static bool IsScaling(SkillEditorModifier modifier, int inputId) =>
        modifier.Local
        && modifier.SourceAttributeId == (int)DogmaAttributeId.SkillLevel
        && modifier.TargetAttributeId == inputId
        && modifier.Operation is DogmaOperation.PreMultiply or DogmaOperation.PostMultiply;

    public static IReadOnlyList<SkillBonusValue> Read(
        SkillForm form,
        SkillEditorChoices choices,
        IReadOnlyList<SkillBonusTemplate> templates
    )
    {
        var effects = SkillChoiceSearch.Index(choices.Effects);
        if (
            form.Effects.Any(effect => !effects.ContainsKey(effect.EffectId))
            || form.Effects.Select(effect => effect.EffectId).Distinct().Count() != form.Effects.Count
        )
        {
            return [];
        }
        List<SkillBonusValue> result = [];
        foreach (var template in templates.Where(template => form.Effects.Any(effect => effect.EffectId == template.EffectId)))
        {
            var inputs = form.Attributes.Where(attribute => attribute.AttributeId == template.InputId).ToArray();
            if (inputs.Length != 1)
            {
                continue;
            }
            var related = form
                .Effects.Where(effect => effect.EffectId != template.EffectId)
                .Select(effect => effects[effect.EffectId])
                .Where(effect => effect.Modifiers.Any(modifier => References(modifier, template.InputId)))
                .ToArray();
            if (
                related.Length > 1
                || related.Any(effect =>
                    !effect.Available || effect.Modifiers.Count != 1 || !IsScaling(effect.Modifiers[0], template.InputId)
                )
                || template.InlineScaling && related.Length != 0
                || template.Multiplier && related.Length != 0
            )
            {
                continue;
            }
            result.Add(new(form, template, inputs[0], related.SingleOrDefault()?.Id));
        }
        return result;
    }

    private static bool References(SkillEditorModifier modifier, int inputId) =>
        modifier.SourceAttributeId == inputId || modifier.Local && modifier.TargetAttributeId == inputId;

    public static string? Conflict(SkillForm form, SkillEditorChoices choices, SkillBonusTemplate template)
    {
        if (form.Attributes.Count(attribute => attribute.AttributeId == template.InputId) > 1)
        {
            return "This skill has duplicate stored values. Resolve them in technical rules first.";
        }
        var effects = SkillChoiceSearch.Index(choices.Effects);
        foreach (var reference in form.Effects)
        {
            if (!effects.TryGetValue(reference.EffectId, out var effect))
            {
                return "This skill contains an unrecognized rule. Review it in technical rules first.";
            }
            if (effect.Modifiers.Any(modifier => References(modifier, template.InputId)))
            {
                return "This bonus shares its strength with an existing rule. Edit that bonus or choose another target.";
            }
            if (
                effect.Modifiers.Any(modifier =>
                    !modifier.Local && modifier.TargetAttributeId == template.AttributeId && modifier.TargetName == template.Target
                )
            )
            {
                return "This skill already changes that stat on that target. Edit its existing bonus.";
            }
        }
        return null;
    }

    public static void Add(
        SkillForm form,
        SkillEditorChoices choices,
        SkillBonusTemplate template,
        double amount,
        bool decrease,
        bool perLevel
    )
    {
        if (
            !Templates(choices).Contains(template)
            || !double.IsFinite(amount)
            || amount < 0
            || perLevel && !template.CanScale
            || template.InlineScaling && !perLevel
        )
        {
            throw new InvalidOperationException("Choose a supported bonus and a finite, nonnegative amount.");
        }
        if (Conflict(form, choices, template) is { } conflict)
        {
            throw new InvalidOperationException(conflict);
        }
        var input = form.Attributes.SingleOrDefault(attribute => attribute.AttributeId == template.InputId);
        if (input is null)
        {
            input = new() { AttributeId = template.InputId };
            form.Attributes.Add(input);
        }
        input.Value = template.StoredValue(decrease ? -amount : amount);
        form.Effects.Add(new() { EffectId = template.EffectId });
        if (perLevel && !template.InlineScaling)
        {
            form.Effects.Add(new() { EffectId = template.ScalingId!.Value });
        }
    }

    public static void Remove(SkillBonusValue bonus)
    {
        bonus.Form.Effects.RemoveAll(effect => effect.EffectId == bonus.Template.EffectId || effect.EffectId == bonus.ScalingId);
    }

    public static void SetScaling(SkillBonusValue bonus, bool perLevel)
    {
        if (bonus.PerLevel == perLevel)
        {
            return;
        }
        if (!bonus.Template.CanScale || bonus.Template.InlineScaling)
        {
            throw new InvalidOperationException("This bonus has a fixed scaling rule.");
        }
        if (perLevel)
        {
            bonus.ScalingId = bonus.Template.ScalingId;
            bonus.Form.Effects.Add(new() { EffectId = bonus.ScalingId!.Value });
        }
        else
        {
            bonus.Form.Effects.RemoveAll(effect => effect.EffectId == bonus.ScalingId);
            bonus.ScalingId = null;
        }
    }
}
