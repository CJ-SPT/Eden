namespace Eden.Gameplay.Corporations;

// Build 3662168 appConst.crpApplication* values.
public enum CorporationApplicationStatus
{
    Applied = 0,
    RenegotiatedByApplicant = 1,
    Joined = 2,
    RejectedByApplicant = 3,
    RejectedByCorporation = 4,
    RenegotiatedByCorporation = 5,
    Offered = 6,
    Withdrawn = 7,
    Invited = 8,
}

public sealed record CorporationApplication(
    long Id,
    long PilotId,
    int CorporationId,
    string Text,
    CorporationApplicationStatus Status,
    long AppliedAt,
    long UpdatedAt,
    string Response
);

public sealed record CorporationApplicationUpdate(
    long Id,
    long PilotId,
    int CorporationId,
    string Text,
    CorporationApplicationStatus Status,
    string Response,
    long? AppliedAt
);

public sealed record CorporationMemberChange(
    long PilotId,
    int PreviousCorporationId,
    int CorporationId,
    int PreviousMemberCount,
    int MemberCount,
    long Time
);

public sealed record CorporationChange(
    long Id,
    CorporationApplication? Application,
    CorporationMemberChange? Membership,
    CorporationAdministrationChange? Administration = null,
    CorporationRoleChange? Roles = null,
    CorporationTitleChange? Title = null,
    CorporationShareChange? Shares = null
);

public sealed record CorporationLeavePermission(bool Allowed, string Reason, string ErrorName = "CustomInfo");

public interface ICorporationMembership
{
    IReadOnlyList<CorporationApplication> Applications(long pilotId, bool forCorporation);
    CorporationApplication Apply(long pilotId, int corporationId, string text);
    CorporationApplication Update(long actorId, CorporationApplicationUpdate update);
    CorporationLeavePermission CanRemove(long actorId, long pilotId);
    void Remove(long actorId, long pilotId);
    long Head(long pilotId);
    IReadOnlyList<CorporationChange> Changes(long pilotId, long after);
}

public static class CorporationMembershipRules
{
    public const int MaximumApplicationLength = 1000;
    public const int MemberPageSize = 50;
    public const int ChangePageSize = 100;
    public const int MaximumMemberLookup = 1000;

    public static bool Closed(CorporationApplicationStatus status) =>
        status
            is CorporationApplicationStatus.Joined
                or CorporationApplicationStatus.RejectedByApplicant
                or CorporationApplicationStatus.RejectedByCorporation
                or CorporationApplicationStatus.Withdrawn;

    public static void CheckText(string text)
    {
        if (text.Length > MaximumApplicationLength || text.Contains('\0'))
        {
            throw new InvalidDataException("Corporation applications and responses allow at most 1,000 characters and no nulls.");
        }
    }

    public static void CheckTransition(CorporationApplicationStatus before, CorporationApplicationStatus after, bool applicant, bool ceo)
    {
        bool valid =
            !Closed(before)
            && (
                applicant
                    && after switch
                    {
                        CorporationApplicationStatus.Joined or CorporationApplicationStatus.RejectedByApplicant => before
                            is CorporationApplicationStatus.Offered
                                or CorporationApplicationStatus.Invited,
                        CorporationApplicationStatus.Withdrawn => true,
                        CorporationApplicationStatus.RenegotiatedByApplicant => before
                            is CorporationApplicationStatus.Applied
                                or CorporationApplicationStatus.RenegotiatedByCorporation
                                or CorporationApplicationStatus.RenegotiatedByApplicant,
                        _ => false,
                    }
                || ceo
                    && after switch
                    {
                        CorporationApplicationStatus.Offered or CorporationApplicationStatus.RenegotiatedByCorporation => before
                            is CorporationApplicationStatus.Applied
                                or CorporationApplicationStatus.RenegotiatedByApplicant,
                        CorporationApplicationStatus.RejectedByCorporation => true,
                        _ => false,
                    }
            );
        if (!valid)
        {
            throw new InvalidDataException("That corporation application action is not available to you in its current state.");
        }
    }

    public static void CheckJoin(bool alreadyMember, bool isCeo, int members, int limit)
    {
        if (alreadyMember || isCeo || members >= limit)
        {
            throw new InvalidDataException("You cannot join: already a member, currently a CEO, or the corporation is full.");
        }
    }
}
