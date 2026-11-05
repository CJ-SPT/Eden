using Eden.Gameplay.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

internal sealed partial class CorporationHandler
{
    private PilotCallResult CallRoles(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords
    )
    {
        if (
            service != "corpRegistry"
            || method is not ("GetTitles" or "UpdateTitle" or "UpdateTitles" or "DeleteTitle" or "UpdateMember" or "UpdateMembers")
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected corporation role arguments.");
            }
            long actor = context.Pilot.PilotId;
            switch (method, args)
            {
                case ("GetTitles", []):
                    return new CorporationTitlesReply(roles.Titles(actor));
                case ("UpdateTitle", { Length: 10 }):
                    roles.UpdateTitles(actor, [ParseTitle(args)]);
                    break;
                case ("UpdateTitles", [var rows]):
                    roles.UpdateTitles(actor, RoleRows(rows, TitleColumns).Select(ParseTitle).ToArray());
                    break;
                case ("DeleteTitle", [long title]):
                    roles.DeleteTitle(actor, title);
                    break;
                case ("UpdateMember", { Length: 15 }):
                    roles.UpdateMembers(actor, [ParseMember(args)]);
                    break;
                case ("UpdateMembers", [var rows]):
                    roles.UpdateMembers(actor, RoleRows(rows, MemberColumns).Select(ParseMember).ToArray());
                    break;
                default:
                    throw new InvalidDataException("Invalid corporation role request.");
            }
            Advance(context);
            return PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static readonly string[] TitleColumns =
    [
        "titleID",
        "titleName",
        "roles",
        "grantableRoles",
        "rolesAtHQ",
        "grantableRolesAtHQ",
        "rolesAtBase",
        "grantableRolesAtBase",
        "rolesAtOther",
        "grantableRolesAtOther",
    ];
    private static readonly string[] MemberColumns =
    [
        "characterID",
        "title",
        "divisionID",
        "squadronID",
        "roles",
        "grantableRoles",
        "rolesAtHQ",
        "grantableRolesAtHQ",
        "rolesAtBase",
        "grantableRolesAtBase",
        "rolesAtOther",
        "grantableRolesAtOther",
        "baseID",
        "titleMask",
    ];

    private static IReadOnlyList<object?[]> RoleRows(object? value, string[] expected)
    {
        if (value is BlueInstance { Name: "eve.common.script.sys.rowset.Rowset" or "util.Rowset" } rowset)
        {
            var fields = BlueRead.Fields(rowset.State);
            if (fields.GetValueOrDefault("header") is not List<object?> header || !header.Select(BlueMarshal.Text).SequenceEqual(expected))
            {
                throw new InvalidDataException("Unexpected corporation role row columns.");
            }
            value = fields.GetValueOrDefault("lines");
        }
        if (value is not List<object?> rows || rows.Count > CorporationRoleRules.MaximumBatch)
        {
            throw new InvalidDataException("Expected corporation role rows within the batch limit.");
        }
        return rows.Select(row =>
                row switch
                {
                    List<object?> list when list.Count == expected.Length => list.ToArray(),
                    object?[] tuple when tuple.Length == expected.Length => tuple,
                    _ => throw new InvalidDataException("Invalid corporation role row."),
                }
            )
            .ToArray();
    }

    private static CorporationTitle ParseTitle(object?[] row)
    {
        var fields = BlueRead.Tuple(row, 10);
        long Number(int index) =>
            fields[index] is long number && number >= 0 ? number : throw new InvalidDataException("Choose a valid corporation role mask.");
        return new(Number(0), Text(fields[1]), new(Number(2), Number(3), Number(4), Number(5), Number(6), Number(7), Number(8), Number(9)));
    }

    private static CorporationMemberUpdate ParseMember(object?[] fields)
    {
        if (fields.Length is not (14 or 15) || fields[0] is not long pilot || pilot <= 0)
        {
            throw new InvalidDataException("Choose a corporation member.");
        }
        long? Number(int index) =>
            fields[index] switch
            {
                null => null,
                long number when number >= 0 => number,
                _ => throw new InvalidDataException("Choose a valid corporation role mask."),
            };
        return new(
            pilot,
            fields[1] is null ? null : Text(fields[1]),
            OptionalId(fields[2]),
            OptionalId(fields[3]),
            new(Number(4), Number(5), Number(6), Number(7), Number(8), Number(9), Number(10), Number(11)),
            OptionalId(fields[12]),
            Number(13),
            fields.Length == 14 || fields[14] is null ? null : Flag(fields[14])
        );
    }
}

internal sealed record CorporationTitlesReply(IReadOnlyList<CorporationTitle> Titles) : BlueModel
{
    public override object ToBlue() => Titles.ToDictionary(t => t.Id, t => (object?)new CorporationTitleReply(t));
}

internal sealed record CorporationTitleReply(CorporationTitle Title) : BlueModel
{
    internal Dictionary<string, object?> Fields()
    {
        var fields = CorporationRoleFields.Values(new(Title.Roles));
        fields.Remove("title");
        fields.Remove("titleMask");
        fields.Remove("baseID");
        fields.Remove("divisionID");
        fields.Remove("squadronID");
        fields.Remove("blockRoles");
        fields.Remove("rolesAtAll");
        fields.Add("titleID", Title.Id);
        fields.Add("titleName", new BlueUnicode(Title.Name));
        return fields;
    }

    public override object ToBlue()
    {
        var fields = Fields();
        // Native Row.__setstate__ unpacks (header, line); title events reuse its header.
        return new BlueInstance("util.Row", new object?[] { new BlueList<string>(fields.Keys.ToArray()), fields.Values.ToList() });
    }
}

internal sealed record CorporationRolesChanged(CorporationRoleChange Change) : PilotNotification("OnCorporationMemberChanged")
{
    public override object?[] Arguments =>
        [Change.CorporationId, Change.PilotId, CorporationRoleFields.Changes(Change.Before, Change.After)];
}

internal sealed record CorporationTitleChanged(CorporationTitleChange Change) : PilotNotification("OnTitleChanged")
{
    public override object?[] Arguments
    {
        get
        {
            var before = Change.Before is null ? null : new CorporationTitleReply(Change.Before).Fields();
            var after = Change.After is null ? null : new CorporationTitleReply(Change.After).Fields();
            var keys = (before ?? after!).Keys;
            return
            [
                Change.CorporationId,
                Change.TitleId,
                keys.Where(key => before is null || after is null || !Equals(before[key], after[key]))
                    .ToDictionary(
                        key => key,
                        key => (object?)new object?[] { before?.GetValueOrDefault(key), after?.GetValueOrDefault(key) }
                    ),
            ];
        }
    }
}

internal static class CorporationRoleFields
{
    internal static Dictionary<string, object?> Values(CorporationMemberAccess access)
    {
        var roles = access.Roles;
        return new()
        {
            ["title"] = new BlueUnicode(access.Title),
            ["titleMask"] = access.TitleMask,
            ["roles"] = roles.Roles,
            ["rolesAtAll"] = roles.Roles,
            ["grantableRoles"] = roles.GrantableRoles,
            ["rolesAtHQ"] = roles.RolesAtHQ,
            ["grantableRolesAtHQ"] = roles.GrantableRolesAtHQ,
            ["rolesAtBase"] = roles.RolesAtBase,
            ["grantableRolesAtBase"] = roles.GrantableRolesAtBase,
            ["rolesAtOther"] = roles.RolesAtOther,
            ["grantableRolesAtOther"] = roles.GrantableRolesAtOther,
            ["baseID"] = access.BaseId,
            ["divisionID"] = access.DivisionId,
            ["squadronID"] = access.SquadronId,
            ["blockRoles"] = access.BlockRoles,
        };
    }

    internal static Dictionary<string, object?> Changes(CorporationMemberAccess before, CorporationMemberAccess after)
    {
        var previous = Values(before);
        return Values(after)
            .Where(p => !Equals(previous[p.Key], p.Value))
            .ToDictionary(p => p.Key, p => (object?)new object?[] { previous[p.Key], p.Value });
    }
}
