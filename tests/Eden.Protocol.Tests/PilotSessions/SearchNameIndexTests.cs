using Eden.Gameplay.Characters;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class SearchNameIndexTests
{
    [Theory]
    [InlineData(CharacterSearchMatch.PartialTerms, "eden pil", new long[] { 1, 2, 3 })]
    [InlineData(CharacterSearchMatch.PartialTerms, "pi", new long[] { 4 })]
    [InlineData(CharacterSearchMatch.ExactTerms, "pilot eden", new long[] { 1, 2 })]
    [InlineData(CharacterSearchMatch.ExactPhrase, "eden pilot", new long[] { 1 })]
    [InlineData(CharacterSearchMatch.ExactPhrase, "pilot eden", new long[] { 2 })]
    [InlineData(CharacterSearchMatch.ExactName, " Eden Pilot ", new long[] { 1 })]
    [InlineData(CharacterSearchMatch.ExactName, "eden  pilot", new long[0])]
    [InlineData(CharacterSearchMatch.ExactPhrase, "学習", new long[] { 5 })]
    [InlineData(CharacterSearchMatch.PartialTerms, "éto", new long[] { 6 })]
    [InlineData(CharacterSearchMatch.ExactTerms, "eden\tpilot", new long[] { 1, 2 })]
    public void IndexedQueriesPreserveNativeTermsPhrasesUnicodeAndDisplayOrder(CharacterSearchMatch match, string query, long[] expected)
    {
        SearchNameIndex index = new([
            new(SearchCategory.Character, 3, "Pilot Edenic"),
            new(SearchCategory.Character, 2, "Pilot Eden"),
            new(SearchCategory.Character, 1, "Eden Pilot"),
            new(SearchCategory.Character, 4, "Pi"),
            new(SearchCategory.Character, 5, "Ada 学習 Pilot"),
            new(SearchCategory.Character, 6, "Agent Étoile", true),
            new(SearchCategory.Character, 1, "Eden Pilot"),
        ]);
        var results = index.Select(new(query, match));
        Assert.Equal(expected, results.Select(entry => entry.Id));
        Assert.DoesNotContain(index.Select(new(query, match), hideNpc: true), entry => entry.IsNpc);
        Assert.Equal("Eden Pilot", index.Find(1)!.Name);
        Assert.Null(index.Find(long.MaxValue));
    }

    [Fact]
    public void CommonNamesAreSortedAndCappedAfterDeduplicationAndNpcFiltering()
    {
        var entries = Enumerable.Range(1, 700).Select(id => new SearchEntry(SearchCategory.Character, id, $"Pilot {id:D4}", id <= 100));
        SearchNameIndex index = new(entries.Reverse().Concat(entries));
        var found = index.Select(new("pilot", CharacterSearchMatch.PartialTerms), hideNpc: true);
        Assert.Equal(Enumerable.Range(101, CharacterSearchRules.MaximumResults).Select(id => (long)id), found.Select(entry => entry.Id));
        Assert.Empty(index.Select(new("absent", CharacterSearchMatch.ExactName)));
    }
}
