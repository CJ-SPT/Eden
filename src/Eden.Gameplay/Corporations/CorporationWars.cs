using Eden.Gameplay.Structures;

namespace Eden.Gameplay.Corporations;

public sealed record CorporationWar(
    long Id,
    int AttackerId,
    int DefenderId,
    long? HeadquartersId,
    long DeclaredAt,
    long StartsAt,
    long? EndingAt,
    long? EndsAt,
    long? RetractedAt,
    int? RetractedBy,
    long? BillId,
    bool Mutual,
    CorporationWarEndReason? ReasonEnded,
    long? InheritedFromWarId = null
)
{
    public bool CanBeRetracted => Mutual && EndingAt is null;
}

public enum CorporationWarEndReason
{
    AttackerIneligible = 1,
    DefenderIneligible = 2,
    HeadquartersLost = 3,
    Retracted = 6,
    BillUnpaid = 10,
    MutualEnded = 11,
    AllianceDeparted = 12,
}

public sealed record CorporationWarHeadquarters(
    int TypeId,
    long StructureId,
    StructureUpkeepState UpkeepState,
    IReadOnlyList<long> Wars,
    int OwnerId,
    int SolarSystemId,
    string Name
);

public sealed record CorporationWarInvitation(int FromOwnerId, int ToOwnerId, long SentAt);

public sealed record CorporationWarInvoice(long Id, long WarId, int DebtorId, int DefenderId, long DueAt, long AmountCents);

public sealed record CorporationWarPeaceTreaty(int OtherOwnerId, long ExpiresAt, long WarId, long HeadquartersId, bool Outgoing);

public interface ICorporationWars
{
    int Organization(long pilotId);
    IReadOnlyList<CorporationWarPeaceTreaty> PeaceTreaties(long pilotId);
    bool IsEligible(int ownerId);
    IReadOnlyList<CorporationWar> Wars(int ownerId);
    IReadOnlyList<CorporationWar> PublicWars(long maxWarId);
    CorporationWar? War(long warId);
    IReadOnlyList<CorporationWar> WarsForStructure(long structureId);
    IReadOnlyDictionary<long, IReadOnlyList<long>> HeadquartersWars(IReadOnlyList<long> structureIds);
    IReadOnlyList<CorporationWarHeadquarters> Headquarters(long pilotId, int ownerId);
    CorporationWar Declare(long pilotId, int targetOrganizationId, long headquartersId, bool allianceRegistry);
    void RetractMutual(long pilotId, long warId);
    IReadOnlyList<CorporationWarInvitation> Invitations(long pilotId);
    void SendInvitation(long pilotId, int toOwnerId);
    void WithdrawInvitation(long pilotId, int toOwnerId);
    void RespondInvitation(long pilotId, int fromOwnerId, bool accept);
    bool InvitationsBlocked(long pilotId);
    void SetInvitationsBlocked(long pilotId, bool blocked);
}

public interface ICorporationWarMaintenance
{
    void Advance(long now);
}

public static class CorporationWarRules
{
    // CCP increased the war fee to 500 million ISK on September 22, 2026, before build 3662168.
    public const long WeeklyFeeCents = 500_000_000 * 100L;
    public const int WarBillType = 4;
    public const int MaximumOutstandingInvitations = 5;
    public const long Warmup = TimeSpan.TicksPerDay;
    public const long Cooldown = TimeSpan.TicksPerDay;
    public const long BillingPeriod = 7 * TimeSpan.TicksPerDay;
    public const long HeadquartersLossTruce = 14 * TimeSpan.TicksPerDay;
    public const long InvitationLifetime = 30 * TimeSpan.TicksPerDay;
}
