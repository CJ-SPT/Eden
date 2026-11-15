namespace Eden.Gameplay.Fleets;

// Build 3662168 evefleet.const inviteScope bits.
[Flags]
public enum FleetInviteScope
{
    Closed = 0,
    Corporation = 1,
    Alliance = 2,
    Militia = 4,
    Public = 8,
    PublicOpen = 16,
}

public sealed record FleetAdvertAccess(
    double? MinimumStanding,
    double? MinimumSecurity,
    IReadOnlyList<long> Allowed,
    IReadOnlyList<long> Disallowed
);

public sealed record FleetAdvertSettings(
    string Name,
    string Description,
    FleetInviteScope Scope,
    int? Activity,
    bool Advanced,
    bool NewPlayerFriendly,
    FleetAdvertAccess Public,
    FleetAdvertAccess MemberGroups,
    bool Approval,
    bool HideInfo,
    bool UpdateOnBossChange,
    int? JoinLimit
);

public sealed record FleetAdvert(
    long FleetId,
    FleetMember Leader,
    int Members,
    long CreatedAt,
    long UpdatedAt,
    FleetAdvertSettings Settings
);
