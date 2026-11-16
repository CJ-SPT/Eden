using Eden.Gameplay.Alliances;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Alliances;

[Injectable]
internal sealed class AllianceMembershipHandler(IAllianceMembership membership) : ISelectedPilotCallHandler
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
            !(service == "corpRegistry" && method is "ApplyToJoinAlliance" or "GetAllianceApplications" or "DeleteAllianceApplication")
            && !(
                service == "allianceRegistry"
                && method is "GetApplications" or "UpdateApplication" or "DeclareExecutorSupport" or "DeleteMember"
            )
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected alliance membership arguments.");
            }
            long actor = context.Pilot.PilotId;
            switch (method, args)
            {
                case ("ApplyToJoinAlliance", [long id, var text]) when id is > 0 and <= int.MaxValue:
                    membership.Apply(actor, (int)id, AllianceHandler.Text(text));
                    return PilotCallResult.None;
                case ("GetAllianceApplications", []):
                    return new AllianceApplicationsReply(membership.Applications(actor, false), true);
                case ("GetApplications", []):
                    return new AllianceApplicationsReply(membership.Applications(actor, true), false);
                case ("DeleteAllianceApplication", [long id]) when id is > 0 and <= int.MaxValue:
                    membership.Withdraw(actor, (int)id);
                    return PilotCallResult.None;
                case ("UpdateApplication", [long corporation, var text, long status])
                    when corporation is > 0 and <= int.MaxValue
                        && status is (long)AllianceApplicationStatus.Accepted or (long)AllianceApplicationStatus.Rejected:
                    membership.Decide(actor, (int)corporation, AllianceHandler.Text(text), (AllianceApplicationStatus)status);
                    return PilotCallResult.None;
                case ("DeclareExecutorSupport", [long corporation]) when corporation is > 0 and <= int.MaxValue:
                    membership.DeclareSupport(actor, (int)corporation);
                    return PilotCallResult.None;
                case ("DeleteMember", [long corporation]) when corporation is > 0 and <= int.MaxValue:
                    membership.Remove(actor, (int)corporation);
                    return PilotCallResult.None;
                default:
                    throw new InvalidDataException("Invalid alliance membership request.");
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record AllianceApplicationRow(AllianceApplication Application) : BlueModel
{
    internal static BlueConstructor Descriptor() =>
        BlueRows.Descriptor([
            ("allianceID", BlueColumnType.Int32),
            ("corporationID", BlueColumnType.Int32),
            ("applicationText", BlueColumnType.Unicode),
            ("state", BlueColumnType.Int32),
            ("applicationDateTime", BlueColumnType.FileTime),
        ]);

    public override object ToBlue() =>
        new BlueConstructor(
            "blue.DBRow",
            [
                Descriptor(),
                new object?[]
                {
                    Application.AllianceId,
                    Application.CorporationId,
                    new BlueUnicode(Application.Text),
                    (int)Application.Status,
                    Application.AppliedAt,
                },
            ]
        );
}

internal sealed record AllianceApplicationsReply(IReadOnlyList<AllianceApplication> Applications, bool ForCorporation) : BlueModel
{
    public override object ToBlue() =>
        new BlueIndexedRowset(
            AllianceApplicationRow.Descriptor(),
            ForCorporation ? "allianceID" : "corporationID",
            Applications.ToDictionary(
                row => (long)(ForCorporation ? row.AllianceId : row.CorporationId),
                row => (object?)new AllianceApplicationRow(row)
            )
        );
}

internal sealed record AllianceHistoryReply(IReadOnlyList<AllianceHistory> History) : BlueModel
{
    public override object ToBlue() =>
        History
            .Select(row =>
                (object?)
                    new BlueInstance(
                        "utillib.KeyVal",
                        new Dictionary<string, object?> { ["allianceID"] = row.AllianceId, ["startDate"] = row.StartedAt }
                    )
            )
            .ToList();
}
