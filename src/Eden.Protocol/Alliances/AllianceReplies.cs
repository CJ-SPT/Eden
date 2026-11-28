using Eden.Gameplay.Alliances;
using Eden.Protocol.Docking;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Alliances;

internal sealed record AllianceTickerSuggestions(IReadOnlyList<string> Tickers) : BlueModel
{
    public override object ToBlue() =>
        new CompatibilityRowset(["shortName"], Tickers.Select(ticker => new SuggestedAllianceTicker(ticker)).ToArray()).ToBlue();
}

internal sealed record SuggestedAllianceTicker(string Ticker) : BlueModel
{
    public override object ToBlue() => new object?[] { Ticker };
}

internal sealed record AllianceTickerRow(int Id, string Ticker) : BlueModel
{
    public override object ToBlue() => new object?[] { Id, Ticker };
}

internal sealed record AllianceTickerTable(IReadOnlyList<AllianceTickerRow> Rows) : BlueModel
{
    public override object ToBlue() => new object?[] { new object?[] { "allianceID", "shortName" }, new BlueList<AllianceTickerRow>(Rows) };
}

internal sealed record AllianceReply(Alliance Alliance, bool AllowWar = false) : BlueModel
{
    internal static BlueConstructor Descriptor() =>
        BlueRows.Descriptor([
            ("allianceID", BlueColumnType.Int32),
            ("allianceName", BlueColumnType.Unicode),
            ("shortName", BlueColumnType.String),
            ("executorCorpID", BlueColumnType.Int32),
            ("creatorCorpID", BlueColumnType.Int32),
            ("creatorCharID", BlueColumnType.Int64),
            ("warFactionID", BlueColumnType.Int32),
            ("description", BlueColumnType.Unicode),
            ("url", BlueColumnType.String),
            ("startDate", BlueColumnType.FileTime),
            ("memberCount", BlueColumnType.Int32),
            ("dictatorial", BlueColumnType.Boolean),
            ("allowWar", BlueColumnType.Boolean),
            ("deleted", BlueColumnType.Boolean),
        ]);

    public override object ToBlue() =>
        new BlueConstructor(
            "blue.DBRow",
            [
                Descriptor(),
                new object?[]
                {
                    Alliance.Id,
                    new BlueUnicode(Alliance.Details.Name),
                    Alliance.Details.Ticker,
                    Alliance.DisplayExecutorCorporationId ?? Alliance.ExecutorCorporationId,
                    Alliance.CreatorCorporationId,
                    Alliance.CreatorId,
                    null,
                    new BlueUnicode(Alliance.Details.Description),
                    Alliance.Details.Url,
                    Alliance.CreatedAt,
                    Alliance.MemberCount,
                    false,
                    AllowWar,
                    Alliance.ClosedAt.HasValue,
                },
            ]
        );
}

internal sealed record AllianceRankingsReply(IReadOnlyList<AllianceReply> Alliances) : BlueModel
{
    // all_cso_alliance caches the returned iterable inside its own rankedAlliances KeyVal.
    public override object ToBlue() => Alliances.Select(alliance => (object?)alliance.ToBlue()).ToList();
}

internal sealed record AllianceMembersReply(IReadOnlyList<AllianceMember> Members) : BlueModel
{
    internal static BlueConstructor Descriptor() =>
        BlueRows.Descriptor([
            ("corporationID", BlueColumnType.Int32),
            ("allianceID", BlueColumnType.Int32),
            ("chosenExecutorID", BlueColumnType.Int32),
            ("startDate", BlueColumnType.FileTime),
            ("deleted", BlueColumnType.Boolean),
        ]);

    public override object ToBlue() =>
        new BlueIndexedRowset(
            Descriptor(),
            "corporationID",
            Members.ToDictionary(
                m => (long)m.CorporationId,
                m =>
                    (object?)
                        new BlueConstructor(
                            "blue.DBRow",
                            [Descriptor(), new object?[] { m.CorporationId, m.AllianceId, m.ChosenExecutorId, m.JoinedAt, false }]
                        )
            )
        );
}

internal sealed record AlliancePublicMembersReply(IReadOnlyList<AllianceMember> Members) : BlueModel
{
    public override object ToBlue()
    {
        var indexed = (BlueIndexedRowset)new AllianceMembersReply(Members).ToBlue();
        return new BlueConstructor(
            "carbon.common.script.sys.crowset.CRowset",
            [AllianceMembersReply.Descriptor(), indexed.Rows.Values.ToList()]
        );
    }
}
