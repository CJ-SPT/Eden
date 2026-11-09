using System.Globalization;
using System.Runtime.CompilerServices;
using Eden.Gameplay.Skills;

namespace Eden.Server.Administration;

internal static class SkillChoiceSearch
{
    internal const int ResultLimit = 30;
    private static readonly ConditionalWeakTable<IReadOnlyList<SkillEditorOption>, Dictionary<int, SkillEditorOption>> Indexes = new();

    internal static IReadOnlyDictionary<int, SkillEditorOption> Index(IReadOnlyList<SkillEditorOption> options) =>
        Indexes.GetValue(options, values => values.ToDictionary(value => value.Id));

    internal static IReadOnlyList<SkillEditorOption> Find(IReadOnlyList<SkillEditorOption> options, string search, int? selected)
    {
        string term = search.Trim();
        return options
            .Where(option =>
                option.Available
                && option.Id != selected
                && (
                    option.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || option.Description.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || option.SearchTerms.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || option.Id.ToString(CultureInfo.InvariantCulture) == term
                )
            )
            .Take(ResultLimit)
            .ToArray();
    }
}
