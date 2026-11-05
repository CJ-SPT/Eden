using Eden.Gameplay.Corporations;
using Eden.Protocol.Docking;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.PilotSessions;

internal sealed record OwnerRow(long Id, string Name, int Gender, long? NameId = null, bool IsNpc = false, int TypeId = 1373) : BlueModel
{
    public override object ToBlue() => new object?[] { Id, IsNpc ? new BlueUnicode(Name) : Name, TypeId, Gender, NameId };
}

internal sealed record OwnerTable(IReadOnlyList<OwnerRow> Owners) : BlueModel
{
    public override object ToBlue() =>
        new object?[] { new object?[] { "ownerID", "ownerName", "typeID", "gender", "ownerNameID" }, new BlueList<OwnerRow>(Owners) };
}

internal sealed record CorporationTickerRow(int CorporationId, string Ticker, CorporationLogo? Logo = null) : BlueModel
{
    public override object ToBlue() =>
        new object?[] { CorporationId, Ticker, Logo?.Shape1, Logo?.Shape2, Logo?.Shape3, Logo?.Color1, Logo?.Color2, Logo?.Color3 };
}

internal sealed record CorporationTickerTable(IReadOnlyList<CorporationTickerRow> Corporations) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            // Build 3662168 CorpIcon reads logo layers from cfg.corptickernames,
            // including on the character selector before a pilot is selected.
            new object?[] { "corporationID", "tickerName", "shape1", "shape2", "shape3", "color1", "color2", "color3" },
            new BlueList<CorporationTickerRow>(Corporations),
        };
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
