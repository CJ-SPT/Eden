using Eden.Gameplay.Corporations;
using Eden.Protocol.Characters;
using Eden.Protocol.Docking;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

internal sealed record CorporationMedalCatalogueReply(IReadOnlyList<CorporationMedalDetails> Entries) : BlueModel
{
    private static readonly (string Name, BlueColumnType Type)[] s_medalColumns =
    [
        ("medalID", BlueColumnType.Int64),
        ("ownerID", BlueColumnType.Int32),
        ("title", BlueColumnType.Unicode),
        ("description", BlueColumnType.Unicode),
        ("creatorID", BlueColumnType.Int64),
        ("date", BlueColumnType.FileTime),
        ("noRecepients", BlueColumnType.Int32),
    ];

    private static readonly (string Name, BlueColumnType Type)[] s_graphicColumns =
    [
        ("medalID", BlueColumnType.Int64),
        ("part", BlueColumnType.Int32),
        ("layer", BlueColumnType.Int32),
        ("graphic", BlueColumnType.String),
        ("color", BlueColumnType.Int32),
    ];

    public override object ToBlue() =>
        new object?[]
        {
            Rowset(
                s_medalColumns,
                Entries.Select(entry =>
                    new object?[]
                    {
                        entry.Medal.Id,
                        entry.Medal.CorporationId,
                        new BlueUnicode(entry.Medal.Design.Title),
                        new BlueUnicode(entry.Medal.Design.Description),
                        entry.Medal.CreatorId,
                        entry.Medal.CreatedAt,
                        entry.RecipientCount,
                    }
                )
            ),
            Rowset(
                s_graphicColumns,
                Entries.SelectMany(entry =>
                    entry
                        .Medal.Design.Graphics.GroupBy(graphic => graphic.Part)
                        .SelectMany(group =>
                            group.Select(
                                (graphic, layer) => new object?[] { entry.Medal.Id, graphic.Part, layer, graphic.Graphic, graphic.Color }
                            )
                        )
                )
            ),
        };

    private static BlueConstructor Rowset(IReadOnlyList<(string Name, BlueColumnType Type)> columns, IEnumerable<object?[]> values)
    {
        var header = BlueRows.Descriptor(columns);
        var rows = values.Select(row => (object?)new BlueConstructor("blue.DBRow", [header, row])).ToList();
        return new("carbon.common.script.sys.crowset.CRowset", [header, rows]);
    }
}

internal sealed record CorporationMedalRecipientsReply(IReadOnlyList<CorporationMedalAward> Awards) : BlueModel
{
    public override object ToBlue() =>
        new CompatibilityRowset(
            ["medalID", "recepientID", "issuerID", "reason", "date"],
            Awards.Select(award => new CorporationMedalRecipientRow(award)).ToArray()
        );
}

internal sealed record CorporationMedalRecipientRow(CorporationMedalAward Award) : BlueModel
{
    public override object ToBlue() =>
        new object?[] { Award.MedalId, Award.RecipientId, Award.IssuerId, new BlueUnicode(Award.Reason), Award.IssuedAt };
}

internal sealed record CorporationMedalDetailsReply(CorporationMedalDetails? Details) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["info"] = Details is { } details ? new List<object?> { new CorporationMedalInfoRow(details) } : new List<object?>(),
                ["graphics"] = new CorporationMedalGraphicsReply(Details is { } detail ? [detail.Medal] : []),
            }
        );
}

internal sealed record CorporationMedalInfoRow(CorporationMedalDetails Details) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["medalID"] = Details.Medal.Id,
                ["ownerID"] = Details.Medal.CorporationId,
                ["creatorID"] = Details.Medal.CreatorId,
                ["title"] = new BlueUnicode(Details.Medal.Design.Title),
                ["description"] = new BlueUnicode(Details.Medal.Design.Description),
                ["createDateTime"] = Details.Medal.CreatedAt,
                ["numberOfRecipients"] = Details.RecipientCount,
            }
        );
}

internal sealed record CorporationMedalGraphicsReply(IReadOnlyList<CorporationMedal> Medals) : BlueModel
{
    public override object ToBlue() =>
        new CompatibilityRowset(
            ["medalID", "part", "graphic", "color"],
            Medals.SelectMany(medal => medal.Design.Graphics.Select(graphic => new CorporationMedalGraphicRow(medal.Id, graphic))).ToArray()
        );
}

internal sealed record CorporationMedalGraphicRow(long MedalId, CorporationMedalGraphic Graphic) : BlueModel
{
    public override object ToBlue() => new object?[] { MedalId, Graphic.Part, Graphic.Graphic, Graphic.Color };
}

internal sealed record ReceivedMedalsReply(IReadOnlyList<ReceivedCorporationMedal> Medals) : BlueModel
{
    internal static BlueModel Create(IReadOnlyList<ReceivedCorporationMedal> medals) =>
        medals.Count > 0 ? new ReceivedMedalsReply(medals) : new PublicCharacterMedals();

    public override object ToBlue() =>
        new object?[]
        {
            new CompatibilityRowset(
                ["medalID", "issuerID", "ownerID", "status", "reason", "date", "isDeleted", "title", "description"],
                Medals.Select(entry => new ReceivedMedalRow(entry)).ToArray()
            ),
            new CorporationMedalGraphicsReply(Medals.Select(entry => entry.Medal).DistinctBy(medal => medal.Id).ToArray()),
        };
}

internal sealed record ReceivedMedalRow(ReceivedCorporationMedal Entry) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            Entry.Medal.Id,
            Entry.Award.IssuerId,
            Entry.Medal.CorporationId,
            (int)Entry.Award.Status,
            new BlueUnicode(Entry.Award.Reason),
            Entry.Award.IssuedAt,
            Entry.Award.Status == CorporationMedalStatus.Deleted ? 1 : 0,
            new BlueUnicode(Entry.Medal.Design.Title),
            new BlueUnicode(Entry.Medal.Design.Description),
        };
}
