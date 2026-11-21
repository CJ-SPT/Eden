namespace Eden.Gameplay.FactionWarfare;

public enum MilitiaOrganizationKind
{
    Corporation,
    Alliance,
}

// Build 3662168 appConst.facwarCorporation* values.
public enum MilitiaOrganizationPhase
{
    Joining = 0,
    Active = 1,
    Leaving = 2,
}

public enum MilitiaOrganizationCommand
{
    Join,
    Leave,
    WithdrawJoin,
    WithdrawLeave,
}

public sealed record MilitiaOrganizationStatus(MilitiaFaction? Faction, MilitiaOrganizationPhase? Phase = null, long? Date = null);

public interface IMilitiaOrganizations
{
    MilitiaOrganizationStatus Status(int corporationId);
    MilitiaFaction? AllianceFaction(int allianceId);
    IReadOnlyList<int> Corporations(MilitiaFaction faction);
    void Change(long actorId, MilitiaOrganizationKind kind, MilitiaOrganizationCommand command, MilitiaFaction faction);
    void Advance();
}

public static class MilitiaOrganizationRules
{
    public const double MemberStandingFloor = -5;
    public const double OrganizationStandingFloor = 0;
    public static readonly TimeSpan StandingMembershipAge = TimeSpan.FromDays(7);
    public static readonly TimeSpan WarningDuration = TimeSpan.FromDays(1);

    public static bool Inherits(MilitiaOrganizationPhase phase) =>
        phase is MilitiaOrganizationPhase.Active or MilitiaOrganizationPhase.Leaving;

    public static bool AcceptsMember(double standing, MilitiaFaction? directFaction, MilitiaFaction faction) =>
        standing >= MemberStandingFloor && (directFaction is null || directFaction == faction);

    public static double AverageStanding(IEnumerable<double> standings) => standings.DefaultIfEmpty(0).Average();
}
