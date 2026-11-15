using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

[Injectable]
internal sealed class CorporationMemberAuditHandler(ICorporationMemberAudit audit) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "corpmgr" || method != "AuditMember")
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            // Build 3662168 CorpAuditing.ShowJournal sends member, inclusive start, exclusive end and page size.
            if (
                keywords is { Count: > 0 }
                || args is not [long member, long from, long to, long limit]
                || limit is < 1 or > CorporationMemberAuditRules.MaximumRows
            )
            {
                throw new InvalidDataException("Invalid corporation member audit request.");
            }
            return new CorporationMemberAuditReply(audit.Read(context.Pilot.PilotId, new(member, from, to, (int)limit)));
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record CorporationMemberAuditReply(CorporationMemberAuditResult Result) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            Rows(
                BlueRows.Descriptor([
                    ("eventID", BlueColumnType.Int64),
                    ("eventDateTime", BlueColumnType.FileTime),
                    ("eventTypeID", BlueColumnType.Int16),
                    ("characterID", BlueColumnType.Int32),
                    ("corporationID", BlueColumnType.Int32),
                ]),
                Result.Events.Select(entry =>
                    new object?[] { entry.Id, entry.Time, (int)entry.Type, checked((int)entry.PilotId), entry.CorporationId }
                )
            ),
            Rows(
                BlueRows.Descriptor([
                    ("charID", BlueColumnType.Int32),
                    ("corporationID", BlueColumnType.Int32),
                    ("changeTime", BlueColumnType.FileTime),
                    ("grantable", BlueColumnType.Boolean),
                    ("oldRoles", BlueColumnType.Int64),
                    ("newRoles", BlueColumnType.Int64),
                    ("issuerID", BlueColumnType.Int32),
                ]),
                Result.Roles.Select(entry =>
                    new object?[]
                    {
                        checked((int)entry.PilotId),
                        entry.CorporationId,
                        entry.Time,
                        entry.Grantable,
                        entry.Before,
                        entry.After,
                        checked((int)entry.IssuerId),
                    }
                )
            ),
        };

    private static BlueConstructor Rows(BlueConstructor header, IEnumerable<object?[]> rows) =>
        new(
            "carbon.common.script.sys.crowset.CRowset",
            [header, rows.Select(values => (object?)new BlueConstructor("blue.DBRow", [header, values])).ToList()]
        );
}
