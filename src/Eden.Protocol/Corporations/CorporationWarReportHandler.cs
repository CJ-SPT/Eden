using System.Globalization;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Characters;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

[Injectable]
internal sealed class CorporationWarReportHandler(ICorporationWars wars, ICorporationWarReports reports) : ISelectedPilotCallHandler
{
    internal const string BoundPrefix = "@warstats:";

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        bool bound = service.StartsWith(BoundPrefix, StringComparison.Ordinal);
        if (!bound && (service != "warStatisticMgr" || method != "GetKillMail"))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            long warId = 0;
            if (
                keywords is { Count: > 0 }
                || bound
                    && (
                        !long.TryParse(service.AsSpan(BoundPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out warId)
                        || warId <= 0
                    )
            )
            {
                throw new InvalidDataException("Choose a valid war report.");
            }
            switch (method, args)
            {
                case ("GetBaseInfo", []) when bound:
                    return wars.War(warId) is { } war ? new CorporationWarBaseInfoReply(war, reports.Read(warId)) : PilotCallResult.None;
                case ("GetKillsByGroup", []) when bound:
                    return new CorporationWarGroupedLossesReply(reports.Read(warId).KillsByGroup);
                case ("GetKills", [var owner, var group]) when bound:
                    return new BlueList<ShipCombatReportRow>(reports.Kills(warId, Owner(owner), Group(group)).Select(Row).ToArray());
                case ("GetKillMail", [long killId, var hash]) when killId > 0:
                    string value = BlueRead.Text(hash);
                    return value.Length == 40 && reports.Kill(bound ? warId : null, killId, value) is { } kill
                        ? Row(kill)
                        : PilotCallResult.None;
                default:
                    throw new InvalidDataException("Choose valid war report arguments.");
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static int? Owner(object? value) =>
        value switch
        {
            null or 0L => null,
            long owner when owner is > 0 and <= int.MaxValue => (int)owner,
            _ => throw new InvalidDataException("Choose a valid war participant."),
        };

    private static WarReportGroup? Group(object? value) =>
        value switch
        {
            null => null,
            long id when id is >= 0 and <= int.MaxValue && Enum.IsDefined((WarReportGroup)id) => (WarReportGroup)id,
            _ => throw new InvalidDataException("Choose a valid war report ship class."),
        };

    private static ShipCombatReportRow Row(CorporationWarKill kill) =>
        new(kill.Report, kill.WarId, (double)kill.IskLost, (double)kill.IskDestroyed);
}

internal sealed record CorporationWarBaseInfoReply(CorporationWar War, CorporationWarStatistics Statistics) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            new CorporationWarReply(War),
            new BlueMap<int, int>(Statistics.ShipsKilled),
            new BlueMap<int, double>(Statistics.IskKilled.ToDictionary(stat => stat.Key, stat => (double)stat.Value)),
            CorporationWarReply.AllyEntries(War.Allies),
        };
}

internal sealed record CorporationWarGroupedLossesReply(IReadOnlyDictionary<int, WarReportGroupLosses> Groups) : BlueModel
{
    public override object ToBlue() =>
        new BlueMap<int, CorporationWarGroupLossReply>(
            Groups.ToDictionary(group => group.Key, group => new CorporationWarGroupLossReply(group.Value))
        );
}

internal sealed record CorporationWarGroupLossReply(WarReportGroupLosses Losses) : BlueModel
{
    public override object ToBlue() =>
        new Dictionary<string, object?>
        {
            ["attackerShipLoss"] = Losses.AttackerShipLoss,
            ["attackerIskLoss"] = (double)Losses.AttackerIskLoss,
            ["defenderShipLoss"] = Losses.DefenderShipLoss,
            ["defenderIskLoss"] = (double)Losses.DefenderIskLoss,
        };
}
