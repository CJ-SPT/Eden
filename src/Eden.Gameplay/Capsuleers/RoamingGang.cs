namespace Eden.Gameplay.Capsuleers;

public enum PvpRole
{
    Scout,
    Tackle,
    Damage,
    Kiter,
    Brawler,
    ElectronicWarfare,
}

public enum RoamingGangPhase
{
    Forming,
    Hunting,
    Returning,
    Complete,
}

public enum RoamingGangMemberStatus
{
    Active,
    Returned,
    Lost,
}

public sealed record RoamingGangMember(long PilotId, long ShipId, PvpRole Role)
{
    public RoamingGangMemberStatus Status { get; init; }
    public Eden.Gameplay.Ships.HullClass Hull { get; init; }
    public bool DamageCapable { get; init; } = true;
}

public sealed record GangSighting(long ShipId, int SystemId, long GridId, long SeenAt, long ExpiresAt);

// One immutable command revision is shared by the members' resident checkpoints.
public sealed record RoamingGang(
    long Id,
    long Sequence,
    long Revision,
    int HomeStationId,
    int DestinationStationId,
    int[] Systems,
    long LeaderId,
    RoamingGangMember[] Members,
    long DepartAt,
    long ReturnAt
)
{
    public RoamingGangPhase Phase { get; init; }
    public GangSighting? Sighting { get; init; }
    public string Reason { get; init; } = "Forming a hunting patrol.";
    public RegionalOperation? Regional { get; init; }
}

public static class RoamingGangRules
{
    public const int MinimumMembers = 3;
    public const int MaximumMembers = 6;
    public const int MaximumJumps = 3;
    public static readonly long FormationTicks = TimeSpan.FromSeconds(30).Ticks;
    public static readonly long ScoutLeadTicks = TimeSpan.FromSeconds(8).Ticks;
    public static readonly long ReportTicks = TimeSpan.FromSeconds(12).Ticks;
    public static readonly long SortieTicks = TimeSpan.FromMinutes(10).Ticks;
    public static readonly long RecoveryTicks = TimeSpan.FromMinutes(2).Ticks;
    public static readonly long RendezvousTicks = TimeSpan.FromMinutes(2).Ticks;

    public static RoamingGang Reconcile(RoamingGang gang, IReadOnlySet<long> survivingShips, long elapsed)
    {
        var members = gang
            .Members.Select(member =>
                member.Status == RoamingGangMemberStatus.Active && !survivingShips.Contains(member.ShipId)
                    ? member with
                    {
                        Status = RoamingGangMemberStatus.Lost,
                    }
                    : member
            )
            .ToArray();
        var active = members.Where(member => member.Status == RoamingGangMemberStatus.Active).ToArray();
        long leader = active.Any(member => member.PilotId == gang.LeaderId)
            ? gang.LeaderId
            : active
                .OrderBy(member => member.Role == PvpRole.Scout)
                .ThenBy(member => member.PilotId)
                .Select(member => member.PilotId)
                .FirstOrDefault();
        var phase =
            active.Length == 0 ? RoamingGangPhase.Complete
            : gang.Phase != RoamingGangPhase.Complete
            && (
                elapsed >= gang.ReturnAt
                || active.Length < MinimumMembers
                || !active.Any(member => member.Role != PvpRole.Scout && member.DamageCapable)
            )
                ? RoamingGangPhase.Returning
            : gang.Phase;
        if (members.SequenceEqual(gang.Members) && leader == gang.LeaderId && phase == gang.Phase)
        {
            return gang;
        }
        return gang with
        {
            Revision = checked(gang.Revision + 1),
            Members = members,
            LeaderId = leader,
            Phase = phase,
            Reason = phase == RoamingGangPhase.Returning ? "Returning after losses or the sortie deadline." : gang.Reason,
        };
    }

    public static long Departure(RoamingGang gang, PvpRole role) => checked(gang.DepartAt + (role == PvpRole.Scout ? 0 : ScoutLeadTicks));

    public static long Reported(RoamingGang gang, int system, long grid, long elapsed) =>
        gang.Sighting is { } report && report.SystemId == system && report.GridId == grid && elapsed < report.ExpiresAt ? report.ShipId : 0;

    public static RoamingGang Report(RoamingGang gang, long ship, int system, long grid, long elapsed) =>
        gang with
        {
            Revision = checked(gang.Revision + 1),
            Sighting = new(ship, system, grid, elapsed, checked(elapsed + ReportTicks)),
        };
}
