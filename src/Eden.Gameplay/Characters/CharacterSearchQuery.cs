using System.Collections.Immutable;

namespace Eden.Gameplay.Characters;

public sealed class CharacterSearchName
{
    public string Normalized { get; }
    public ImmutableArray<string> Words { get; }

    public CharacterSearchName(string value)
    {
        Normalized = value.Trim().ToLowerInvariant();
        Words = Normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToImmutableArray();
    }
}

public sealed class CharacterSearchQuery
{
    public CharacterSearchName Name { get; }
    public CharacterSearchMatch Match { get; }

    public CharacterSearchQuery(string query, CharacterSearchMatch match)
    {
        CharacterSearchRules.Check(query, match);
        Name = new(query);
        Match =
            match == CharacterSearchMatch.PartialTerms && Name.Words.All(word => word.Length < CharacterSearchRules.MinimumWildcard)
                ? CharacterSearchMatch.ExactTerms
                : match;
    }

    public bool Matches(CharacterSearchName candidate)
    {
        if (Match == CharacterSearchMatch.ExactName)
        {
            return Name.Normalized == candidate.Normalized;
        }
        if (Match == CharacterSearchMatch.ExactPhrase)
        {
            for (int start = 0; start <= candidate.Words.Length - Name.Words.Length; start++)
            {
                if (candidate.Words.AsSpan(start, Name.Words.Length).SequenceEqual(Name.Words.AsSpan()))
                {
                    return true;
                }
            }
            return false;
        }
        foreach (string term in Name.Words)
        {
            bool found = false;
            foreach (string word in candidate.Words)
            {
                if (Match == CharacterSearchMatch.PartialTerms ? word.StartsWith(term, StringComparison.Ordinal) : word == term)
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                return false;
            }
        }
        return true;
    }
}
