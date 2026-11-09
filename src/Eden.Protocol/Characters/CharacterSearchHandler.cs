using Eden.Gameplay.Characters;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Characters;

[Injectable]
internal sealed class CharacterSearchHandler(IUniversalSearch search) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "search" || method is not ("Query" or "QuickQuery"))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (
                args is not [var query, List<object?> groups]
                || query is not (string or byte[])
                || groups.Any(group =>
                    group is not (long and >= (long)SearchCategory.Agent and <= (long)SearchCategory.StructureWithInlinedData)
                )
            )
            {
                throw new InvalidDataException("Choose a search string and known search categories.");
            }
            var match = CharacterSearchMatch.PartialTerms;
            bool onlyAlternate = false;
            bool hideNpc = false;
            foreach (var (key, value) in keywords ?? [])
            {
                switch (key)
                {
                    case "exact" when value is long mode && mode is >= 0 and <= 3:
                        match = (CharacterSearchMatch)mode;
                        break;
                    case "hideNPC" when value is bool or (long and (0 or 1)):
                        hideNpc = value is true or 1L;
                        break;
                    case "onlyAltName" when value is bool or (long and (0 or 1)):
                        onlyAlternate = value is true or 1L;
                        break;
                    default:
                        throw new InvalidDataException("Invalid character search options.");
                }
            }
            string text = BlueMarshal.Text(query);
            var result = search.Search(
                new(text, groups.Cast<long>().Select(group => (SearchCategory)group).ToArray(), match, hideNpc, onlyAlternate)
            );
            if (method == "QuickQuery")
            {
                return new BlueList<long>(result.SelectMany(group => group.Ids).Distinct().ToArray());
            }
            return new BlueMap<SearchCategory, BlueList<long>>(
                result.Where(group => group.Ids.Count > 0).ToDictionary(group => group.Category, group => new BlueList<long>(group.Ids))
            );
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
