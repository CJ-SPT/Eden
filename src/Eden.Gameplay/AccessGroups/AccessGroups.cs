namespace Eden.Gameplay.AccessGroups;

// Build 3662168 ownergroupConst. Excluded is not a higher administrative rank.
public enum AccessGroupRole
{
    None = -1,
    Member = 0,
    Manager = 1,
    Admin = 2,
    Excluded = 3,
}

public enum AccessGroupChange
{
    Created = 1,
    MemberAdded = 2,
    MemberRoleChanged = 3,
    MemberRemoved = 4,
    NameChanged = 5,
    DescriptionChanged = 6,
    Deleted = 7,
    ChangedForListener = 8,
}

public sealed record AccessGroupLog(
    long Id,
    long GroupId,
    long ActorId,
    long Time,
    AccessGroupChange Change,
    long? OwnerId,
    AccessGroupRole? OldRole,
    AccessGroupRole? NewRole,
    string? OldText,
    string? NewText
);

public sealed record AccessGroupMember(long OwnerId, AccessGroupRole Role);

public sealed record AccessGroup(
    long Id,
    long CreatorId,
    string Name,
    string Description,
    AccessGroupRole Role,
    IReadOnlyList<long> Admins
);

public sealed record AccessGroupContents(AccessGroup Group, IReadOnlyList<AccessGroupMember> Members);

public interface IPilotAccessGroups
{
    long Revision { get; }
    IReadOnlyList<AccessGroup> Read(long pilotId);
    IReadOnlyList<AccessGroupContents> ReadContents(long pilotId);
    AccessGroup? Find(long pilotId, long groupId);
    IReadOnlyList<AccessGroupMember> Members(long pilotId, long groupId);
    IReadOnlyList<AccessGroupLog> Logs(long pilotId, long groupId);
    AccessGroup Create(long pilotId, string name, string description);
    void Delete(long pilotId, long groupId);
    void Update(long pilotId, long groupId, string? name, string? description);
    void ChangeMembers(long pilotId, long groupId, IReadOnlyList<AccessGroupMember> members, bool remove = false);
}

public static class AccessGroupRules
{
    public const int MaximumBatch = 10000;
    public const int MaximumLogEntries = 1000;
    public const int MaximumManagedGroups = 1000;
    public const int MaximumMembers = 10000;

    public static bool CanManage(AccessGroupRole role) => role is AccessGroupRole.Admin or AccessGroupRole.Manager;

    public static bool GrantsAccess(AccessGroupRole role) =>
        role is AccessGroupRole.Member or AccessGroupRole.Manager or AccessGroupRole.Admin;

    public static AccessGroupRole Resolve(IReadOnlyList<AccessGroupMember> members, long pilotId, long corporationId)
    {
        // Character exceptions take precedence over corporation membership and Everyone.
        foreach (long subject in new[] { pilotId, corporationId, 0 })
        {
            if (members.FirstOrDefault(m => m.OwnerId == subject) is { } member)
            {
                return member.Role;
            }
        }
        return AccessGroupRole.None;
    }

    public static void CheckChange(
        AccessGroupRole actor,
        long actorId,
        AccessGroupMember changed,
        AccessGroupRole previous,
        bool character,
        bool remove
    )
    {
        if (
            !CanManage(actor)
            || changed.OwnerId < 0
            || !remove && (changed.Role < AccessGroupRole.Member || changed.Role > AccessGroupRole.Excluded)
        )
        {
            throw new InvalidDataException("Choose valid access list members and roles.");
        }
        if (!remove && (!character && CanManage(changed.Role) || changed.OwnerId == 0 && changed.Role != AccessGroupRole.Member))
        {
            throw new InvalidDataException("Only characters may manage an access list; Everyone may only be a member.");
        }
        if (actor == AccessGroupRole.Manager && (changed.OwnerId != actorId && CanManage(previous) || !remove && CanManage(changed.Role)))
        {
            throw new InvalidDataException("Managers cannot change administrators or other managers, or promote members.");
        }
    }
}
