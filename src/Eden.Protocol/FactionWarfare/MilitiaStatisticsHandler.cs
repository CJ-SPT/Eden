using Eden.Gameplay.FactionWarfare;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.FactionWarfare;

[Injectable]
internal sealed class MilitiaStatisticsHandler(IMilitiaProgression progression) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (
            service != "facWarMgr"
            || method
                is not (
                    "GetStats_Character"
                    or "GetStats_Personal"
                    or "GetStats_Corp"
                    or "GetStats_Alliance"
                    or "GetStats_Militia"
                    or "GetStats_FactionInfo"
                    or "GetStats_CorpPilots"
                    or "GetStats_TopAndAllKillsAndVPs"
                    or "CheckForRankChange"
                )
        )
        {
            return PilotCallResult.Unsupported;
        }
        if (args.Length != 0 || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("This militia statistics request takes no arguments.");
        }

        long pilot = context.Pilot.PilotId;
        return method switch
        {
            "CheckForRankChange" => progression.Rank(pilot) is { } rank ? rank.Rank : PilotCallResult.None,
            "GetStats_CorpPilots" => progression.CorporationPilots(pilot),
            "GetStats_FactionInfo" => new MilitiaFactionStatisticsReply(progression.Factions()),
            "GetStats_Militia" => new MilitiaStatisticsTable(progression.Factions()),
            "GetStats_TopAndAllKillsAndVPs" => new MilitiaLeaderboardReply(progression.Leaderboards()),
            "GetStats_Corp" => new MilitiaStatisticValues(progression.Statistics(pilot, MilitiaStatisticOwner.Corporation)),
            "GetStats_Alliance" => new MilitiaStatisticValues(progression.Statistics(pilot, MilitiaStatisticOwner.Alliance)),
            _ => new MilitiaStatisticValues(progression.Statistics(pilot, MilitiaStatisticOwner.Pilot)),
        };
    }
}

internal sealed record MilitiaRankReply(MilitiaRank Rank) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["characterID"] = Rank.CharacterId,
                ["factionID"] = (int)Rank.Faction,
                ["currentRank"] = Rank.Rank,
            }
        );
}

internal sealed record MilitiaRankOverview(IReadOnlyList<MilitiaRank> Ranks) : BlueModel
{
    public override object ToBlue() => new BlueList<MilitiaRankReply>(Ranks.Select(rank => new MilitiaRankReply(rank)).ToArray());
}

internal sealed record MilitiaStatisticValues(MilitiaStatistics Statistics) : BlueModel
{
    internal static IReadOnlyList<string> Columns { get; } = ["killsY", "killsLW", "killsTotal", "vpY", "vpLW", "vpTotal"];

    internal long[] Values() =>
        [
            Statistics.Yesterday.Kills,
            Statistics.LastWeek.Kills,
            Statistics.Total.Kills,
            Statistics.Yesterday.VictoryPoints,
            Statistics.LastWeek.VictoryPoints,
            Statistics.Total.VictoryPoints,
        ];

    public override object ToBlue() => Columns.Zip(Values()).ToDictionary(pair => pair.First, pair => (object?)pair.Second);
}

internal sealed record MilitiaFactionStatisticsReply(IReadOnlyList<MilitiaFactionStatistics> Factions) : BlueModel
{
    public override object ToBlue() =>
        Factions.ToDictionary(
            row => (long)row.Faction,
            row =>
                (object?)
                    new BlueInstance(
                        "utillib.KeyVal",
                        new Dictionary<string, object?>
                        {
                            ["totalMembersCount"] = row.TotalMembersCount,
                            ["systemsCount"] = row.SystemsCount,
                            ["militiaMembersCount"] = row.MilitiaMembersCount,
                            ["corporationsCount"] = row.CorporationsCount,
                            ["topCorporation"] = row.TopCorporation,
                            ["topMemberCount"] = row.TopMemberCount,
                        }
                    )
        );
}

internal sealed record MilitiaStatisticsTable(IReadOnlyList<MilitiaFactionStatistics> Factions) : BlueModel
{
    public override object ToBlue()
    {
        var owners = Factions.Select(row => (long)row.Faction).ToArray();
        var values = Factions.Select(row => new MilitiaStatisticValues(row.Activity).Values()).ToArray();
        var data = MilitiaStatisticValues
            .Columns.Select((column, index) => (column, index))
            .ToDictionary(
                row => row.column,
                row =>
                    (object?)
                        owners
                            .Select((id, i) => (id, Value: values[i][row.index]))
                            .ToDictionary(metric => metric.id, metric => (object?)metric.Value)
            );
        return new Dictionary<string, object?> { ["header"] = new BlueList<long>(owners), ["data"] = data };
    }
}

internal sealed record MilitiaLeaderboardReply(IReadOnlyList<MilitiaLeaderboard> Boards) : BlueModel
{
    // Build 3662168 appConst groupCharacter, groupCorporation and groupAlliance.
    private const long CharacterGroup = 1;
    private const long CorporationGroup = 2;
    private const long AllianceGroup = 32;

    public override object ToBlue() => new object?[] { Map(board => board.Top), Map(board => board.All) };

    private Dictionary<long, object?> Map(Func<MilitiaLeaderboard, MilitiaStatistics> select) =>
        Boards.ToDictionary(board => Index(board.Owner), board => (object?)new MilitiaStatisticValues(select(board)));

    private static long Index(MilitiaStatisticOwner owner) =>
        owner switch
        {
            MilitiaStatisticOwner.Pilot => CharacterGroup,
            MilitiaStatisticOwner.Corporation => CorporationGroup,
            MilitiaStatisticOwner.Alliance => AllianceGroup,
            _ => throw new ArgumentOutOfRangeException(nameof(owner)),
        };
}
