using Eden.Gameplay.Characters;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Characters;

[Injectable]
internal sealed class PlayerOwnerLookupHandler(ILocalCharacterSearch search, ICharacterDirectory directory) : ISelectedPilotCallHandler
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
            var ids = search.Search(BlueMarshal.Text(query), (CharacterSearchMatch)mode);
            return new BlueList<PlayerOwnerLookupRow>(
                directory.Read(ids).Where(p => !p.IsNpc).Select(p => new PlayerOwnerLookupRow(p)).ToArray()
            );
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record PlayerOwnerLookupRow(CharacterIdentity Character) : BlueModel
{
    // Build 3662168 inventorycommon.const and the existing owner-table adapter.
    private const int CharacterGroup = 1;
    private const int CharacterType = 1373;

    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["ownerID"] = Character.Id,
                ["ownerName"] = new BlueUnicode(Character.Name),
                ["typeID"] = CharacterType,
                ["groupID"] = CharacterGroup,
            }
        );
}
