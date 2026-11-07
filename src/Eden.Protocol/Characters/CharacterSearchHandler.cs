using Eden.Gameplay.Characters;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Characters;

[Injectable]
internal sealed class CharacterSearchHandler(ILocalCharacterSearch characters) : ISelectedPilotCallHandler
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
                || groups.Count != 1
                || groups[0] is not (long and CharacterSearchRules.ResultType)
            )
            {
                throw new InvalidDataException("Only local character-category searches are available.");
            }
            var match = CharacterSearchMatch.PartialTerms;
            bool onlyAlternate = false;
            foreach (var (key, value) in keywords ?? [])
            {
                switch (key)
                {
                    case "exact" when value is long mode && mode is >= 0 and <= 3:
                        match = (CharacterSearchMatch)mode;
                        break;
                    case "hideNPC" when value is bool or (long and (0 or 1)):
                        // Local player characters are never NPCs.
                        break;
                    case "onlyAltName" when value is bool or (long and (0 or 1)):
                        onlyAlternate = value is true or 1L;
                        break;
                    default:
                        throw new InvalidDataException("Invalid character search options.");
                }
            }
            string text = BlueMarshal.Text(query);
            CharacterSearchRules.Check(text, match);
            // Local characters have no alternate localized name.
            var result = onlyAlternate ? Array.Empty<long>() : characters.Search(text, match);
            if (method == "QuickQuery")
            {
                return new BlueList<long>(result);
            }
            return result.Count == 0
                ? BlueEmpty.Dictionary
                : new BlueMap<int, BlueList<long>>(new Dictionary<int, BlueList<long>> { [CharacterSearchRules.ResultType] = new(result) });
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
