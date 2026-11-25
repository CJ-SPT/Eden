using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Characters;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

[Injectable]
internal sealed class CorporationCombatReportHandler(ICorporationCombatReports reports) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "corpRegistry" || method is not ("GetRecentKills" or "GetRecentLosses"))
        {
            return PilotCallResult.Unsupported;
        }
        if (
            keywords is { Count: > 0 }
            || args is not [long count, null or long]
            || count is < 1 or > InteractionHistoryRules.MaximumCombatPageSize
            || args[1] is long and < 0
        )
        {
            return new PilotCallResult.Failure("Choose a valid corporation combat history page.");
        }
        try
        {
            long? beforeId = args[1] is long id && id > 0 ? id : null;
            return new CorporationCombatReportReply(
                reports.Recent(context.Pilot.PilotId, method == "GetRecentLosses", (int)count, beforeId)
            );
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record CorporationCombatReportReply(IReadOnlyList<ShipCombatReport> Reports) : BlueModel
{
    private static readonly (string Name, BlueColumnType Type)[] s_columns =
    [
        ("killID", BlueColumnType.Int64),
        ("solarSystemID", BlueColumnType.Int32),
        ("victimCharacterID", BlueColumnType.Int64),
        ("victimCorporationID", BlueColumnType.Int32),
        ("victimAllianceID", BlueColumnType.Int32),
        ("victimFactionID", BlueColumnType.Int32),
        ("victimShipTypeID", BlueColumnType.Int32),
        ("finalCharacterID", BlueColumnType.Int64),
        ("finalCorporationID", BlueColumnType.Int32),
        ("finalAllianceID", BlueColumnType.Int32),
        ("finalFactionID", BlueColumnType.Int32),
        ("finalShipTypeID", BlueColumnType.Int32),
        ("finalWeaponTypeID", BlueColumnType.Int32),
        ("killBlob", BlueColumnType.Unicode),
        ("killTime", BlueColumnType.FileTime),
        ("victimDamageTaken", BlueColumnType.Float64),
        ("finalSecurityStatus", BlueColumnType.Float64),
        ("finalDamageDone", BlueColumnType.Float64),
        ("moonID", BlueColumnType.Int32),
        ("warID", BlueColumnType.Int64),
        ("iskLost", BlueColumnType.Currency),
        ("bountyClaimed", BlueColumnType.Currency),
        ("loyaltyPoints", BlueColumnType.Int32),
        ("iskDestroyed", BlueColumnType.Currency),
        ("killRightSupplied", BlueColumnType.Int64),
    ];

    public override object ToBlue()
    {
        byte[] payload = BlueMarshal.Encode(Rows());
        return new BlueInstance(
            "carbon.common.script.net.objectCaching.CachedMethodCallResult",
            new object?[]
            {
                new Dictionary<string, object?> { ["versionCheck"] = "always", ["sessionInfo"] = "corpid" },
                payload,
                new object?[] { DateTime.UnixEpoch.ToFileTimeUtc(), CachedMethodPayload.Checksum(payload) },
            }
        );
    }

    internal BlueConstructor Rows()
    {
        var header = BlueRows.Descriptor(s_columns);
        var rows = Reports
            .Select(report =>
            {
                var fields = new ShipCombatReportRow(report).Fields();
                fields["killBlob"] = new BlueUnicode(BlueRead.Text(fields["killBlob"]));
                return (object?)new BlueConstructor("blue.DBRow", [header, s_columns.Select(column => fields[column.Name]).ToArray()]);
            })
            .ToList();
        return new("carbon.common.script.sys.crowset.CRowset", [header, rows]);
    }
}
