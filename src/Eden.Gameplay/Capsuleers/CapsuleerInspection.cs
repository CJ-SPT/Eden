using Eden.Gameplay.Industry;

namespace Eden.Gameplay.Capsuleers;

public enum CapsuleerDirectory
{
    Bots,
    Corporations,
    Alliances,
}

public sealed record CapsuleerDirectoryEntry(long Id, string Name, string? Ticker = null, CapsuleerCareer? Career = null);

public sealed record CapsuleerDirectoryPage(IReadOnlyList<CapsuleerDirectoryEntry> Entries, int Page, int PageSize, int Total);

public sealed record CapsuleerOrganizationLink(int Id, string Name, string Ticker, bool Inspectable);

public sealed record CapsuleerSkillInspection(int TypeId, int Level, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt);

public sealed record CapsuleerWorkInspection(string Activity, string? Goal = null, int? StationId = null, int? SystemId = null);

public sealed record CapsuleerPilotInspection(
    long Id,
    string Name,
    CapsuleerCareer FounderCareer,
    CapsuleerCareer ActiveCareer,
    int OriginSystemId,
    int SystemId,
    long? DockedLocationId,
    int ShipTypeId,
    string ShipName,
    long BalanceCents,
    bool Suspended,
    bool SimulationActive,
    CapsuleerWorkInspection Work,
    string? WaitingReason,
    CapsuleerAction? NextAction,
    TimeSpan? ActiveTimeUntilNextAction,
    CapsuleerCareerPlan? CareerPlan,
    int? PvpGoalTypeId,
    long FreeSkillPoints,
    IReadOnlyList<CapsuleerSkillInspection> Skills,
    CapsuleerOrganizationLink? Corporation,
    CapsuleerOrganizationLink? Alliance,
    int? WorkBaseId,
    long? CorporationApplicationId,
    int? ProspectiveCorporationId
);

public sealed record CapsuleerMemberInspection(long Id, string Name, CapsuleerCareer? Career, bool Suspended);

public sealed record CapsuleerIndustryInspection(long Id, int TypeId, IndustryActivity Activity, IndustryJobStatus Status, int Runs);

public sealed record CapsuleerSovereigntyInspection(
    long PilotId,
    string PilotName,
    int CorporationId,
    int SystemId,
    SovereigntyDuty Duty,
    bool Returning
);

public sealed record CapsuleerCorporationInspection(
    CapsuleerOrganizationLink Corporation,
    bool BotLed,
    long CeoId,
    int HeadquartersSystemId,
    int HeadquartersStationId,
    int OriginSystemId,
    long BalanceCents,
    int MemberLimit,
    int PendingApplications,
    int RelocatingPilots,
    CapsuleerOrganizationLink? Alliance,
    IReadOnlyList<CapsuleerMemberInspection> Members,
    IReadOnlyList<CapsuleerIndustryInspection> IndustryJobs,
    IReadOnlyList<CapsuleerSovereigntyInspection> Expeditions
);

public sealed record CapsuleerAllianceInspection(
    CapsuleerOrganizationLink Alliance,
    bool BotFounded,
    int? ExecutorCorporationId,
    int CreatorCorporationId,
    IReadOnlyList<CapsuleerOrganizationLink> Corporations,
    IReadOnlyList<int> ClaimedSystems,
    IReadOnlyList<CapsuleerSovereigntyInspection> Expeditions,
    DateTimeOffset? BillDue,
    long? BillCents
);

public interface ICapsuleerInspection
{
    CapsuleerDirectoryPage Browse(CapsuleerDirectory directory, string? search = null, int page = 0);
    CapsuleerPilotInspection? Pilot(long id);
    CapsuleerCorporationInspection? Corporation(int id);
    CapsuleerAllianceInspection? Alliance(int id);
}
