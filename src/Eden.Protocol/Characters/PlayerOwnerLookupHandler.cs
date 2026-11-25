using Eden.Gameplay.Alliances;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Characters;

[Injectable]
internal sealed class PlayerOwnerLookupHandler(ILocalCharacterSearch search, ICharacterDirectory directory, IUniversalSearch organizations)
    : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "lookupSvc" || method != "LookupPCOwners")
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 } || args is not [var query, long mode] || query is not (string or byte[]) || mode is < 0 or > 3)
            {
                throw new InvalidDataException("Invalid player-owner lookup arguments.");
            }
            string name = BlueMarshal.Text(query);
            var match = (CharacterSearchMatch)mode;
            var ids = search.Search(name, match);
            return new BlueList<PlayerOwnerLookupRow>(
                directory
                    .Read(ids)
                    .Where(p => !p.IsNpc)
                    .Select(p => new SearchEntry(SearchCategory.Character, p.Id, p.Name))
                    .Concat(organizations.PlayerOrganizations(new(name, match)))
                    .OrderBy(owner => owner.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(owner => owner.Id)
                    .Take(CharacterSearchRules.MaximumResults)
                    .Select(owner => new PlayerOwnerLookupRow(owner))
                    .ToArray()
            );
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record PlayerOwnerLookupRow(SearchEntry Owner) : BlueModel
{
    // Build 3662168 inventorycommon.const and the existing owner-table adapter.
    private const int CharacterGroup = 1;
    private const int CharacterType = 1373;
    private const int CorporationGroup = 2;
    private const int AllianceGroup = 32;

    public override object ToBlue()
    {
        var (type, group) = Owner.Category switch
        {
            SearchCategory.Corporation => (CorporationRules.CorporationType, CorporationGroup),
            SearchCategory.Alliance => (AllianceRules.AllianceTypeId, AllianceGroup),
            _ => (CharacterType, CharacterGroup),
        };
        return new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["ownerID"] = Owner.Id,
                ["ownerName"] = new BlueUnicode(Owner.Name),
                ["typeID"] = type,
                ["groupID"] = group,
            }
        );
    }
}
