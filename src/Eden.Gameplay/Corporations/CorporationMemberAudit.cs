namespace Eden.Gameplay.Corporations;

// Build 3662168 corp_ui_member_auditing.ShowJournal event types.
public enum CorporationAuditEventType
{
    Created = 12,
    Left = 14,
    Joined = 44,
}

public sealed record CorporationMemberAuditQuery(long MemberId, long From, long To, int Limit);

public sealed record CorporationMemberAuditEvent(long Id, long Time, CorporationAuditEventType Type, long PilotId, int CorporationId);

public sealed record CorporationRoleAuditEntry(
    long PilotId,
    int CorporationId,
    long Time,
    bool Grantable,
    long Before,
    long After,
    long IssuerId
);

public sealed record CorporationMemberAuditResult(
    IReadOnlyList<CorporationMemberAuditEvent> Events,
    IReadOnlyList<CorporationRoleAuditEntry> Roles
);

public interface ICorporationMemberAudit
{
    CorporationMemberAuditResult Read(long actorId, CorporationMemberAuditQuery query);
}

public static class CorporationMemberAuditRules
{
    public const int MaximumRows = 1000;

    public static void Validate(CorporationMemberAuditQuery query)
    {
        if (query.MemberId <= 0 || query.From < 0 || query.To <= query.From || query.Limit is < 1 or > MaximumRows)
        {
            throw new InvalidDataException("Choose a member, a valid date range, and between one and 1,000 audit rows.");
        }
    }

    public static IReadOnlyList<CorporationRoleAuditEntry> RoleEntries(CorporationRoleChange change)
    {
        if (change.ChangedAt <= 0 || change.IssuerId <= 0)
        {
            return [];
        }
        List<CorporationRoleAuditEntry> entries = [];
        Add(false, Roles(change.Before.Roles), Roles(change.After.Roles));
        Add(true, Grantable(change.Before.Roles), Grantable(change.After.Roles));
        return entries;

        void Add(bool grantable, long before, long after)
        {
            if (before != after)
            {
                entries.Add(new(change.PilotId, change.CorporationId, change.ChangedAt, grantable, before, after, change.IssuerId));
            }
        }
    }

    private static long Roles(CorporationRoleSet roles) => roles.Roles | roles.RolesAtHQ | roles.RolesAtBase | roles.RolesAtOther;

    private static long Grantable(CorporationRoleSet roles) =>
        roles.GrantableRoles | roles.GrantableRolesAtHQ | roles.GrantableRolesAtBase | roles.GrantableRolesAtOther;
}
