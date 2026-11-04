using Eden.Protocol.Docking;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.PilotSessions;

internal sealed record OwnerRow(long Id, string Name, int Gender, long? NameId = null, bool IsNpc = false) : BlueModel
{
    private const int CharacterTypeId = 1373;

    public override object ToBlue() => new object?[] { Id, IsNpc ? new BlueUnicode(Name) : Name, CharacterTypeId, Gender, NameId };
}

internal sealed record OwnerTable(IReadOnlyList<OwnerRow> Owners) : BlueModel
{
    public override object ToBlue() =>
        new object?[] { new object?[] { "ownerID", "ownerName", "typeID", "gender", "ownerNameID" }, new BlueList<OwnerRow>(Owners) };
}

internal sealed record CorporationTickerRow(int CorporationId, string Ticker) : BlueModel
{
    public override object ToBlue() => new object?[] { CorporationId, Ticker };
}

internal sealed record CorporationTickerTable(IReadOnlyList<CorporationTickerRow> Corporations) : BlueModel
{
    public override object ToBlue() =>
        new object?[] { new object?[] { "corporationID", "tickerName" }, new BlueList<CorporationTickerRow>(Corporations) };
}

internal sealed record SeasonalCampaignState() : BlueModel
{
    public override object ToBlue() => new object?[] { null, null, null, 0 };
}

internal sealed record AlertCountReply() : BlueModel
{
    public override object ToBlue() => new object?[] { 1, 0 };
}

internal sealed record PilotLoadSlotReply(BlueObjectReference Reference) : BlueModel
{
    public override object ToBlue() => new object?[] { Reference, new Dictionary<string, object?>() };
}
