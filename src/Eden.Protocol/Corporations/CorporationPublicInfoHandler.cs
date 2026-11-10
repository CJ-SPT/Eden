using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

[Injectable]
internal sealed class CorporationPublicInfoHandler(INpcCorporationDirectory npcs, IPlayerCorporations players) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "corpmgr" || method != "GetPublicInfo")
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 } || args is not [long id] || id is <= 0 or > int.MaxValue)
            {
                throw new InvalidDataException("Choose a valid corporation.");
            }
            return players.Read((int)id) is { } player
                ? new CorporationPublicInfoReply(player)
                : new CorporationPublicInfoReply(npcs.Read((int)id));
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record CorporationPublicInfoReply(
    int Id,
    string Name,
    string Ticker,
    long? CeoId,
    int? StationId,
    int? SystemId,
    long? FactionId,
    string Description,
    string Url,
    double TaxRate,
    long Shares,
    int MemberCount
) : BlueModel
{
    internal CorporationPublicInfoReply(PlayerCorporation corporation)
        : this(
            corporation.Id,
            corporation.Details.Name,
            corporation.Details.Ticker,
            corporation.CeoId,
            corporation.StationId,
            corporation.SystemId,
            null,
            corporation.Details.Description,
            corporation.Details.Url,
            corporation.Details.TaxRate,
            CorporationRules.InitialShares,
            corporation.MemberCount
        ) { }

    internal CorporationPublicInfoReply(NpcCorporationInfo corporation)
        : this(
            corporation.Id,
            corporation.Name,
            corporation.Ticker,
            corporation.CeoId,
            corporation.StationId,
            corporation.SystemId,
            corporation.FactionId,
            "",
            corporation.Url,
            corporation.TaxRate,
            corporation.Shares,
            0
        ) { }

    // Build 3662168 CorporationInfoWindow reads deleted and may assign shares to this
    // attribute-bearing reply. Its NPC description comes from the local client catalog.
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["corporationID"] = Id,
                ["corporationName"] = new BlueUnicode(Name),
                ["tickerName"] = Ticker,
                ["ceoID"] = CeoId,
                ["stationID"] = StationId,
                ["solarSystemID"] = SystemId,
                ["factionID"] = FactionId,
                ["description"] = new BlueUnicode(Description),
                ["url"] = Url,
                ["taxRate"] = TaxRate,
                ["shares"] = Shares,
                ["memberCount"] = MemberCount,
                ["allianceID"] = null,
                ["deleted"] = false,
                ["allowWar"] = false,
            }
        );
}
