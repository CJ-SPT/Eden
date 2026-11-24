using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Capsuleers;

public enum PvpRole
{
    Scout,
    Tackle,
    Damage,
    Kiter,
    Brawler,
    ElectronicWarfare,
    Logistics,
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
    public GangRallyRoute? Rally { get; init; }
}

public sealed record GangRallyRoute(int StationId, int[] Systems);

public sealed record RoamingGangRecruit(RoamingGangMember Member, double Speed, bool HasTackle);

public sealed record GangBackup(long ShipId, double ArrivalSeconds);

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
    public long RallyUntil { get; init; }
}

public static class RoamingGangRules
{
    public const int MinimumMembers = 3;
    public const int MaximumMembers = 6;
    public const int MaximumJumps = 3;
    public const int MaximumRallyJumps = 2;
    public static readonly long FormationTicks = TimeSpan.FromSeconds(30).Ticks;
    public static readonly long RallyTicks = TimeSpan.FromMinutes(4).Ticks;
    public static readonly long ScoutLeadTicks = TimeSpan.FromSeconds(8).Ticks;
    public static readonly long ReportTicks = TimeSpan.FromSeconds(12).Ticks;
    public static readonly long SortieTicks = TimeSpan.FromMinutes(10).Ticks;
    public static readonly long RecoveryTicks = TimeSpan.FromMinutes(2).Ticks;
    public static readonly long RendezvousTicks = TimeSpan.FromMinutes(2).Ticks;

    // Eden small-gang policy: approximate the range at which incoming damage support can help.
    public const double ScoutBackupMeters = 30_000;
    private const double MaximumScoutHoldSeconds = 24;
    private const double BackupArrivalMargin = 2;

    public static double ScoutHoldSeconds(double shieldAndArmorFraction, double opposition, double scoutThreat, double capacitorSeconds) =>
        Math.Min(
            Math.Clamp(shieldAndArmorFraction, 0, 1) * MaximumScoutHoldSeconds / Math.Max(1, opposition / scoutThreat),
            capacitorSeconds
        );

    public static RoamingGangMember[] SelectRoster(IReadOnlyList<RoamingGangRecruit> available, int size)
    {
        if (
            available.Count < MinimumMembers
            || !available.Any(recruit => recruit.HasTackle)
            || !available.Any(recruit => recruit.Member.DamageCapable)
        )
        {
            return [];
        }
        var ranked = available
            .OrderBy(recruit => recruit.Member.Role is PvpRole.ElectronicWarfare or PvpRole.Logistics)
            .ThenByDescending(recruit => recruit.Speed)
            .ThenBy(recruit => recruit.Member.PilotId)
            .ToArray();
        var tacklers = ranked.Where(recruit => recruit.HasTackle).ToArray();
        foreach (var scout in ranked)
        {
            foreach (var tackler in tacklers.Where(recruit => recruit != scout))
            {
                // Keep the initiating pilot and reserve a damage dealer outside the scout role.
                var required = new HashSet<RoamingGangRecruit> { available[0], scout, tackler };
                if (!required.Any(recruit => recruit != scout && recruit.Member.DamageCapable))
                {
                    var damage = available.FirstOrDefault(recruit => recruit != scout && recruit.Member.DamageCapable);
                    if (damage is null)
                    {
                        continue;
                    }
                    required.Add(damage);
                }
                if (required.Count > size)
                {
                    continue;
                }
                foreach (var recruit in available)
                {
                    if (required.Count == size)
                    {
                        break;
                    }
                    required.Add(recruit);
                }
                return available
                    .Where(required.Contains)
                    .Select(recruit =>
                        recruit.Member with
                        {
                            Role =
                                recruit == scout ? PvpRole.Scout
                                : recruit == tackler ? PvpRole.Tackle
                                : recruit.Member.Role == PvpRole.Tackle ? PvpRole.Brawler
                                : recruit.Member.Role,
                        }
                    )
                    .ToArray();
            }
        }
        return [];
    }

    public static bool CanScoutTackle(
        RoamingGang gang,
        RoamingGangMember scout,
        SpacePosition position,
        double radius,
        double speed,
        double tackleRange,
        PilotSighting target,
        IReadOnlyList<PilotSighting> visible,
        double opposition,
        IReadOnlyList<GangBackup>? inbound = null,
        double holdSeconds = 0
    )
    {
        if (
            gang.Phase != RoamingGangPhase.Hunting
            || tackleRange <= 0
            || PvpTactics.PursuitDistance(target.Position - position, target.Velocity, speed) > tackleRange + radius + target.Radius
        )
        {
            return false;
        }
        double backup = 0;
        foreach (var member in gang.Members)
        {
            if (member.Status != RoamingGangMemberStatus.Active || member.ShipId == scout.ShipId || !member.DamageCapable)
            {
                continue;
            }
            var contact = visible.FirstOrDefault(ship => ship.ShipId == member.ShipId && ship.PilotId == member.PilotId);
            if (
                contact is not null && contact.Position.DistanceTo(target.Position) <= ScoutBackupMeters
                || inbound?.Any(arrival => arrival.ShipId == member.ShipId && arrival.ArrivalSeconds + BackupArrivalMargin < holdSeconds)
                    == true
            )
            {
                backup += PvpTactics.Threat(contact?.Hull ?? member.Hull);
            }
        }
        return backup > 0 && opposition <= backup + PvpTactics.Threat(scout.Hull);
    }

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
