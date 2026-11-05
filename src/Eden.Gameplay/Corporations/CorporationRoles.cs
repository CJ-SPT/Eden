namespace Eden.Gameplay.Corporations;

public sealed record CorporationRoleSet(
    long Roles = 0,
    long GrantableRoles = 0,
    long RolesAtHQ = 0,
    long GrantableRolesAtHQ = 0,
    long RolesAtBase = 0,
    long GrantableRolesAtBase = 0,
    long RolesAtOther = 0,
    long GrantableRolesAtOther = 0
)
{
    public CorporationRoleSet Union(CorporationRoleSet other) =>
        new(
            Roles | other.Roles,
            GrantableRoles | other.GrantableRoles,
            RolesAtHQ | other.RolesAtHQ,
            GrantableRolesAtHQ | other.GrantableRolesAtHQ,
            RolesAtBase | other.RolesAtBase,
            GrantableRolesAtBase | other.GrantableRolesAtBase,
            RolesAtOther | other.RolesAtOther,
            GrantableRolesAtOther | other.GrantableRolesAtOther
        );

    public bool HasAuthority => this != new CorporationRoleSet();

    public bool Has(CorporationRole role) => (Roles & (long)(CorporationRole.Director | role)) != 0;

    public long At(int? stationId, int headquartersId, int? baseId) =>
        Roles
        | (
            stationId switch
            {
                null => 0,
                var station when station == headquartersId => RolesAtHQ,
                var station when station == baseId => RolesAtBase,
                _ => RolesAtOther,
            }
        );
}

public sealed record CorporationRolePatch(
    long? Roles,
    long? GrantableRoles,
    long? RolesAtHQ,
    long? GrantableRolesAtHQ,
    long? RolesAtBase,
    long? GrantableRolesAtBase,
    long? RolesAtOther,
    long? GrantableRolesAtOther
)
{
    public CorporationRoleSet Apply(CorporationRoleSet previous) =>
        new(
            Roles ?? previous.Roles,
            GrantableRoles ?? previous.GrantableRoles,
            RolesAtHQ ?? previous.RolesAtHQ,
            GrantableRolesAtHQ ?? previous.GrantableRolesAtHQ,
            RolesAtBase ?? previous.RolesAtBase,
            GrantableRolesAtBase ?? previous.GrantableRolesAtBase,
            RolesAtOther ?? previous.RolesAtOther,
            GrantableRolesAtOther ?? previous.GrantableRolesAtOther
        );
}

public sealed record CorporationMemberAccess(
    CorporationRoleSet Roles,
    string Title = "",
    long TitleMask = 0,
    int? BaseId = null,
    bool BlockRoles = false,
    long? RolesRemovedAt = null,
    int? DivisionId = null,
    int? SquadronId = null
);

public sealed record CorporationMemberUpdate(
    long PilotId,
    string? Title,
    int? DivisionId,
    int? SquadronId,
    CorporationRolePatch Roles,
    int? BaseId,
    long? TitleMask,
    bool? BlockRoles = null
);

public sealed record CorporationTitle(long Id, string Name, CorporationRoleSet Roles);

public sealed record CorporationRoleChange(int CorporationId, long PilotId, CorporationMemberAccess Before, CorporationMemberAccess After);

public sealed record CorporationTitleChange(int CorporationId, long TitleId, CorporationTitle? Before, CorporationTitle? After);

public interface ICorporationRoles
{
    IReadOnlyList<CorporationTitle> Titles(long memberId);
    void UpdateMembers(long actorId, IReadOnlyList<CorporationMemberUpdate> updates);
    void UpdateTitles(long actorId, IReadOnlyList<CorporationTitle> titles);
    void DeleteTitle(long actorId, long titleId);
}

public static class CorporationRoleRules
{
    public const int TitleCount = 16;
    public const long AllTitles = (1L << TitleCount) - 1;
    public const int MaximumBatch = 1000;
    public static readonly TimeSpan RemovalDelay = TimeSpan.FromHours(24);
    public static readonly long KnownRoles = Enum.GetValues<CorporationRole>().Aggregate(0L, (mask, role) => mask | (long)role);

    // Seven adjacent bits in each pinned hangar-query, hangar-take and container-take family.
    public static readonly long LocationalRoles =
        (long)(CorporationRole.HangarCanTake1 | CorporationRole.HangarCanQuery1 | CorporationRole.ContainerCanTake1) * ((1L << 7) - 1);

    public static void Validate(CorporationRoleSet roles)
    {
        long[] masks =
        [
            roles.Roles,
            roles.GrantableRoles,
            roles.RolesAtHQ,
            roles.GrantableRolesAtHQ,
            roles.RolesAtBase,
            roles.GrantableRolesAtBase,
            roles.RolesAtOther,
            roles.GrantableRolesAtOther,
        ];
        if (
            masks.Any(mask => mask < 0 || (mask & ~KnownRoles) != 0)
            || (roles.GrantableRoles & CorporationRules.DirectorRole) != 0
            || (
                (
                    roles.RolesAtHQ
                    | roles.GrantableRolesAtHQ
                    | roles.RolesAtBase
                    | roles.GrantableRolesAtBase
                    | roles.RolesAtOther
                    | roles.GrantableRolesAtOther
                ) & ~LocationalRoles
            ) != 0
        )
        {
            throw new InvalidDataException("Choose known corporation roles; Director cannot be grantable.");
        }
    }

    public static void Validate(CorporationTitle title)
    {
        Validate(title.Roles);
        if (
            title.Id <= 0
            || title.Id > AllTitles
            || (title.Id & (title.Id - 1)) != 0
            || title.Name.Length > CorporationDivisions.MaximumNameLength
            || title.Name.Any(char.IsControl)
            || (title.Roles.Roles & CorporationRules.DirectorRole) != 0
        )
        {
            throw new InvalidDataException("Choose one of 16 titles, a valid name and non-Director roles.");
        }
    }

    public static void Authorize(bool ceo, CorporationRoleSet actor, CorporationRoleSet before, CorporationRoleSet after)
    {
        Validate(after);
        if (ceo)
        {
            return;
        }
        if (((before.Roles | after.Roles) & CorporationRules.DirectorRole) != 0)
        {
            throw new InvalidDataException("Only the CEO can change a Director's roles.");
        }
        if (actor.Has(CorporationRole.Director))
        {
            return;
        }
        if (
            before.GrantableRoles != after.GrantableRoles
            || before.GrantableRolesAtHQ != after.GrantableRolesAtHQ
            || before.GrantableRolesAtBase != after.GrantableRolesAtBase
            || before.GrantableRolesAtOther != after.GrantableRolesAtOther
            || ((before.Roles ^ after.Roles) & ~actor.GrantableRoles) != 0
            || ((before.RolesAtHQ ^ after.RolesAtHQ) & ~actor.GrantableRolesAtHQ) != 0
            || ((before.RolesAtBase ^ after.RolesAtBase) & ~actor.GrantableRolesAtBase) != 0
            || ((before.RolesAtOther ^ after.RolesAtOther) & ~actor.GrantableRolesAtOther) != 0
        )
        {
            throw new InvalidDataException("These changes exceed your grantable corporation roles.");
        }
    }
}
