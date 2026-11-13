using System.Collections.Frozen;

namespace Eden.Gameplay.Characters;

// Immutable, ordinal word indexes preserve the pinned client's matching rules and final display-name ordering.
public sealed class SearchNameIndex
{
    private sealed record IndexedName(SearchEntry Entry, CharacterSearchName Name);

    private readonly IndexedName[] _entries;
    private readonly FrozenDictionary<long, SearchEntry> _identities;
    private readonly FrozenDictionary<string, int[]> _names;
    private readonly FrozenDictionary<string, int[]> _words;
    private readonly string[] _vocabulary;
    public long EstimatedBytes { get; }

    public SearchNameIndex(IEnumerable<SearchEntry> entries)
    {
        _entries = entries
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Id)
            .Select(entry => new IndexedName(entry, new(entry.Name)))
            .ToArray();
        _identities = _entries.GroupBy(entry => entry.Entry.Id).ToFrozenDictionary(group => group.Key, group => group.First().Entry);
        _names = Enumerable
            .Range(0, _entries.Length)
            .GroupBy(index => _entries[index].Name.Normalized)
            .ToFrozenDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        _words = Enumerable
            .Range(0, _entries.Length)
            .SelectMany(index => _entries[index].Name.Words.Distinct().Select(word => (Word: word, Index: index)))
            .GroupBy(pair => pair.Word)
            .ToFrozenDictionary(group => group.Key, group => group.Select(pair => pair.Index).ToArray(), StringComparer.Ordinal);
        _vocabulary = _words.Keys.Order(StringComparer.Ordinal).ToArray();
        EstimatedBytes =
            1024
            + _entries.Sum(entry => 128L + entry.Entry.Name.Length * 4L)
            + _words.Sum(pair => 64L + pair.Key.Length * 2L + pair.Value.Length * sizeof(int));
    }

    public SearchEntry? Find(long id) => _identities.GetValueOrDefault(id);

    public IReadOnlyList<SearchEntry> Select(CharacterSearchQuery query, bool hideNpc = false)
    {
        IEnumerable<int> candidates;
        string first = query.Name.Words[0];
        if (query.Match == CharacterSearchMatch.ExactName)
        {
            candidates = _names.GetValueOrDefault(query.Name.Normalized) ?? [];
        }
        else if (query.Match == CharacterSearchMatch.PartialTerms)
        {
            int start = Array.BinarySearch(_vocabulary, first, StringComparer.Ordinal);
            start = start < 0 ? ~start : start;
            HashSet<int> matching = [];
            for (int word = start; word < _vocabulary.Length && _vocabulary[word].StartsWith(first, StringComparison.Ordinal); word++)
            {
                matching.UnionWith(_words[_vocabulary[word]]);
            }
            candidates = matching.Order();
        }
        else
        {
            candidates = _words.GetValueOrDefault(first) ?? [];
        }
        List<SearchEntry> selected = [];
        HashSet<long> seen = [];
        foreach (int index in candidates)
        {
            var entry = _entries[index];
            if ((!hideNpc || !entry.Entry.IsNpc) && query.Matches(entry.Name) && seen.Add(entry.Entry.Id))
            {
                selected.Add(entry.Entry);
                if (selected.Count == CharacterSearchRules.MaximumResults)
                {
                    break;
                }
            }
        }
        return selected;
    }
}
