namespace Eden.Gameplay.Characters;

public enum CharacterSearchMatch
{
    PartialTerms = 0,
    ExactTerms = 1,
    ExactPhrase = 2,
    ExactName = 3,
}

public interface ILocalCharacterSearch
{
    IReadOnlyList<long> Search(string query, CharacterSearchMatch match);
}

public static class CharacterSearchRules
{
    // Pinned eve/common/script/search/const.pyj and searchUtil.IsMatch.
    public const int ResultType = 2;
    public const int MaximumQuery = 100;
    public const int MaximumResults = 500;
    private const int MinimumWildcard = 3;

    public static void Check(string query, CharacterSearchMatch match)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > MaximumQuery || query.Contains('\0') || !Enum.IsDefined(match))
        {
            throw new InvalidDataException("Choose a character search of 1 to 100 characters and a valid matching mode.");
        }
    }

    public static bool Matches(string query, string name, CharacterSearchMatch match)
    {
        Check(query, match);
        query = query.Trim().ToLowerInvariant();
        name = name.Trim().ToLowerInvariant();
        string[] terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string[] candidate = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (match == CharacterSearchMatch.PartialTerms && terms.All(t => t.Length < MinimumWildcard))
        {
            match = CharacterSearchMatch.ExactTerms;
        }
        return match switch
        {
            CharacterSearchMatch.PartialTerms => terms.All(term => candidate.Any(word => word.StartsWith(term, StringComparison.Ordinal))),
            CharacterSearchMatch.ExactTerms => terms.All(term => candidate.Contains(term)),
            CharacterSearchMatch.ExactPhrase => Enumerable
                .Range(0, Math.Max(0, candidate.Length - terms.Length + 1))
                .Any(start => candidate.Skip(start).Take(terms.Length).SequenceEqual(terms)),
            CharacterSearchMatch.ExactName => query == name,
            _ => false,
        };
    }
}
