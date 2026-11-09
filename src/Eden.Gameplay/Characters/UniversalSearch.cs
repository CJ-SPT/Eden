namespace Eden.Gameplay.Characters;

// Build 3662168 eve/common/script/search/const.pyj ResultType.
public enum SearchCategory
{
    Agent = 1,
    Character = 2,
    Corporation = 3,
    Alliance = 4,
    Faction = 5,
    Constellation = 6,
    SolarSystem = 7,
    Region = 8,
    Station = 9,
    ItemType = 10,
    Wormhole = 11,
    Structure = 12,
    StructureWithInlinedData = 13,
}

public sealed record SearchRequest(
    string Query,
    IReadOnlyList<SearchCategory> Categories,
    CharacterSearchMatch Match = CharacterSearchMatch.PartialTerms,
    bool HideNpc = false,
    bool OnlyAlternateName = false
);

public sealed record SearchEntry(SearchCategory Category, long Id, string Name, bool IsNpc = false);

public sealed record SearchGroup(SearchCategory Category, IReadOnlyList<long> Ids);

public interface IUniversalSearch
{
    IReadOnlyList<SearchGroup> Search(SearchRequest request);
    IReadOnlyList<SearchEntry> Owners(IReadOnlyList<long> ids);
}

public static class UniversalSearchRules
{
    public static void Check(SearchRequest request)
    {
        CharacterSearchRules.Check(request.Query, request.Match);
        if (
            request.Categories.Count is < 1 or > (int)SearchCategory.StructureWithInlinedData
            || request.Categories.Any(category => !Enum.IsDefined(category))
            || request.Categories.Distinct().Count() != request.Categories.Count
        )
        {
            throw new InvalidDataException("Choose distinct, known search categories.");
        }
    }

    public static IReadOnlyList<long> Select(SearchRequest request, IEnumerable<SearchEntry> entries) =>
        request.OnlyAlternateName
            ? []
            : entries
                .Where(entry =>
                    (!request.HideNpc || !entry.IsNpc) && CharacterSearchRules.Matches(request.Query, entry.Name, request.Match)
                )
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Id)
                .DistinctBy(entry => entry.Id)
                .Take(CharacterSearchRules.MaximumResults)
                .Select(entry => entry.Id)
                .ToArray();
}
