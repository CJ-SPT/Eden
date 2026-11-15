using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

[Injectable]
internal sealed class CorporationMemberQueryHandler(ICorporationMemberQueries queries, SharedStationGuests presence, TimeProvider clock)
    : ISelectedPilotCallHandler
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
            service != "corpRegistry"
            || method
                is not (
                    "GetMemberIDsByQuery"
                    or "GetMemberIDsWithMoreThanAvgShares"
                    or "GetMemberTrackingInfo"
                    or "GetMemberTrackingInfoSimple"
                )
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected member query arguments.");
            }
            if (method == "GetMemberIDsWithMoreThanAvgShares" && args.Length == 0)
            {
                return new BlueList<long>(queries.AboveAverageShareholders(context.Pilot.PilotId));
            }
            if (method is "GetMemberTrackingInfo" or "GetMemberTrackingInfoSimple" && args.Length == 0)
            {
                bool full = method == "GetMemberTrackingInfo";
                long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
                return new BlueList<CorporationMemberTrackingReply>(
                    queries
                        .Tracking(context.Pilot.PilotId, full)
                        .Select(row => new CorporationMemberTrackingReply(row, full, presence.IsOnline(row.Member.PilotId), now))
                        .ToArray()
                );
            }
            if (
                method == "GetMemberIDsByQuery"
                && args is [List<object?> terms, bool implied, bool titles]
                && terms.Count <= CorporationMemberQueryRules.MaximumCriteria
            )
            {
                return new BlueList<long>(queries.Query(context.Pilot.PilotId, new(terms.Select(Parse).ToArray(), implied, titles)));
            }
            throw new InvalidDataException("Invalid member query request.");
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static CorporationMemberCriterion Parse(object? value)
    {
        if (value is not List<object?> term || term.Count is not (3 or 4))
        {
            throw new InvalidDataException("Invalid member query condition.");
        }
        int offset = term.Count - 3;
        var join =
            offset == 0 ? (CorporationQueryJoin?)null
            : term[0] is long code && code is 1 or 2 ? (CorporationQueryJoin)code
            : throw new InvalidDataException("Invalid member query join.");
        CorporationQueryField field = BlueMarshal.Text(term[offset]) switch
        {
            "roles" => CorporationQueryField.Roles,
            "rolesAtHQ" => CorporationQueryField.RolesAtHQ,
            "rolesAtBase" => CorporationQueryField.RolesAtBase,
            "rolesAtOther" => CorporationQueryField.RolesAtOther,
            "grantableRoles" => CorporationQueryField.GrantableRoles,
            "grantableRolesAtHQ" => CorporationQueryField.GrantableRolesAtHQ,
            "grantableRolesAtBase" => CorporationQueryField.GrantableRolesAtBase,
            "grantableRolesAtOther" => CorporationQueryField.GrantableRolesAtOther,
            "baseID" => CorporationQueryField.Base,
            "startDateTime" => CorporationQueryField.Joined,
            "characterID" => CorporationQueryField.Name,
            "titleMask" => CorporationQueryField.Titles,
            _ => throw new InvalidDataException("Unknown member query field."),
        };
        var operation =
            term[offset + 1] is long op && op is >= 1 and <= 13
                ? (CorporationQueryOperator)op
                : throw new InvalidDataException("Unknown member query operator.");
        object? expected = term[offset + 2];
        if (field == CorporationQueryField.Name && expected is string or byte[] or BlueUnicode)
        {
            return new(join, field, operation, Text: BlueMarshal.Text(expected));
        }
        if (field != CorporationQueryField.Name && expected is null or long)
        {
            return new(join, field, operation, (long?)expected);
        }
        throw new InvalidDataException("Invalid member query value.");
    }
}

internal sealed record CorporationMemberTrackingReply(CorporationMemberTracking Tracking, bool Full, bool Online, long Now) : BlueModel
{
    public override object ToBlue()
    {
        var member = Tracking.Member;
        var access = member.Access ?? new(new());
        long? last = Math.Max(Tracking.Activity.LoggedOnAt ?? 0, Tracking.Activity.LoggedOffAt ?? 0);
        // Native GetLastLoggedOnText takes hours; a negative value means currently online.
        double? hours =
            Online ? -1
            : last > 0 ? Math.Max(0, (Now - last.Value) / (double)TimeSpan.TicksPerHour)
            : null;
        return new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["characterID"] = member.PilotId,
                ["corporationID"] = member.CorporationId,
                ["roles"] = access.Roles.Roles,
                ["grantableRoles"] = access.Roles.GrantableRoles,
                ["title"] = new BlueUnicode(access.Title),
                ["baseID"] = access.BaseId,
                ["startDateTime"] = member.JoinedAt,
                ["lastOnline"] = hours,
                ["logonDateTime"] = Full ? Tracking.Activity.LoggedOnAt : null,
                ["logoffDateTime"] = Full ? Tracking.Activity.LoggedOffAt : null,
                ["locationID"] = Tracking.LocationId,
                ["shipTypeID"] = Tracking.ShipTypeId,
                ["factionID"] = null,
            }
        );
    }
}
