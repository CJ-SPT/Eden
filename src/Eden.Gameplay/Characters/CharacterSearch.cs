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
    internal const int MinimumWildcard = 3;

    public static void Check(string query, CharacterSearchMatch match)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > MaximumQuery || query.Contains('\0') || !Enum.IsDefined(match))
        {
            throw new InvalidDataException("Choose a character search of 1 to 100 characters and a valid matching mode.");
        }
    }

    public static bool Matches(string query, string name, CharacterSearchMatch match)
    {
        return new CharacterSearchQuery(query, match).Matches(new(name));
    }
}
