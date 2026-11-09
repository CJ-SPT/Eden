using System.Globalization;
using System.Runtime.CompilerServices;
using Eden.Gameplay.Skills;

namespace Eden.Server.Administration;

internal static class SkillChoiceSearch
{
    internal const int ResultLimit = 30;
    private static readonly ConditionalWeakTable<IReadOnlyList<SkillEditorOption>, Dictionary<int, SkillEditorOption>> Indexes = new();

    private sealed record SearchEntry(SkillEditorOption Option, string Name, string Text);

    private static readonly ConditionalWeakTable<IReadOnlyList<SkillEditorOption>, SearchEntry[]> SearchIndexes = new();

    internal static IReadOnlyDictionary<int, SkillEditorOption> Index(IReadOnlyList<SkillEditorOption> options) =>
        Indexes.GetValue(options, values => values.ToDictionary(value => value.Id));

    internal static IReadOnlyList<SkillEditorOption> Find(
        IReadOnlyList<SkillEditorOption> options,
        string search,
        int? selected,
        int? resultLimit = ResultLimit
    )
    {
        string query = Normalize(search);
        if (query.Length == 0)
        {
            return options.Where(option => option.Available && option.Id != selected).Take(resultLimit ?? int.MaxValue).ToArray();
        }
        string[] words = query.Split(' ');
        var index = SearchIndexes.GetValue(
            options,
            values =>
                values
                    .Select(option => new SearchEntry(
                        option,
                        Normalize(option.Name),
                        Normalize(
                            $"{option.Name} {option.Description} {option.SearchTerms} {option.Id.ToString(CultureInfo.InvariantCulture)}"
                        )
                    ))
                    .ToArray()
        );
        return index
            .Where(entry =>
                entry.Option.Available
                && entry.Option.Id != selected
                && words.All(word => entry.Text.Contains(word, StringComparison.Ordinal))
            )
            .OrderBy(entry => Rank(entry, query, words))
            .Take(resultLimit ?? int.MaxValue)
            .Select(entry => entry.Option)
            .ToArray();
    }

    private static int Rank(SearchEntry entry, string query, string[] words) =>
        entry.Option.Id.ToString(CultureInfo.InvariantCulture) == query ? 0
        : entry.Name == query ? 1
        : entry.Name.StartsWith(query, StringComparison.Ordinal) ? 2
        : entry.Name.Contains(query, StringComparison.Ordinal) ? 3
        : words.All(word => entry.Name.Contains(word, StringComparison.Ordinal)) ? 4
        : 5;

    private static string Normalize(string value) =>
        string.Join(
            ' ',
            new string(value.Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ').ToArray()).Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries
            )
        );
}
