using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;

namespace Eden.Gameplay.Sovereignty;

public sealed record SovereigntyHub(
    int SystemId,
    long HubId,
    int TypeId,
    int CorporationId,
    SovereigntyClaim? Claim,
    CaptureProgress Capture,
    long CaptureDuration
)
{
    public SovereigntyConflict? Conflict { get; init; }
    public SovereigntyDevelopment? Development { get; init; }
}

public sealed record CaptureProgress(
    int? ControllingAllianceId,
    long ProgressTicks,
    long UpdatedAt,
    int? ScoringAllianceId = null,
    int Rate = 0,
    long? CompletedAt = null,
    bool Blocked = false
);

public sealed record EntosisContribution(int AllianceId, long From, long Until);

public sealed record EntosisCycle(
    long ModuleId,
    int TypeId,
    long PilotId,
    long ShipId,
    long HubId,
    long StartedAt,
    long EndsAt,
    long WarmupUntil,
    double SpeedLimit,
    bool Capital,
    long Repeats,
    bool Stopping
);

public sealed record EntosisActivation(EntosisCycle Cycle, double Capacitor, IReadOnlyList<InventoryChange> Changes);

public interface IPilotEntosis
{
    bool Available { get; }
    long ViewRevision { get; }
    bool IsHubItem(long pilotId, long itemId);
    StructureResult Deploy(long pilotId, long itemId);
    SovereigntyHub? Hub(int systemId);
    IReadOnlyList<SovereigntyClaim> Claims();
    IReadOnlyList<SovereigntyClaim> ClaimsForAlliance(int allianceId);
    SovereigntyDevelopment Development(int systemId);
    IReadOnlyList<SovereigntyDevelopment> AllDevelopment();
    SovereigntyCapital Capital(long pilotId);
    void SetCapital(long pilotId, int systemId);
    void CancelCapital(long pilotId);
    AlliancePrimeTime PrimeTime(long pilotId);
    void SetPrimeHour(long pilotId, int hour);
    IReadOnlyList<SovereigntyHub> AllianceHubs(long pilotId);
    IReadOnlyList<StaticSpaceObject> CommandNodes(int systemId);
    EntosisActivation Activate(long pilotId, long shipId, long moduleId, long hubId, long repeats, long now);
    EntosisActivation? Renew(long pilotId, long moduleId, long now);
    IReadOnlyList<EntosisCycle> Cycles(long pilotId);
    void Advance(int systemId, long now);
    void AdvanceAll();
    void Stop(long pilotId, long moduleId, long now);
    void Disconnect(long pilotId, long now);
    double? SpeedLimit(long shipId, long now);
    bool TravelLocked(long shipId, long now);
}

public static class EntosisRules
{
    private readonly record struct ContributionChange(long At, int AllianceId, int Count);

    public const int ModuleGroup = 1313;
    public const int HubTypeId = 32458;
    public const int NeutralCorporationId = 1000132;
    public const int InterceptorGroup = 831;
    public const int SunGroup = 6;
    public const int InfomorphPsychologySkill = 24242;
    public const DogmaEffect Effect = (DogmaEffect)6063;

    public static SpacePosition Placement(SpacePosition star, SpacePosition ship, double starDistance)
    {
        if (!Finite(star) || !Finite(ship) || !double.IsFinite(starDistance) || starDistance <= 0)
        {
            throw new InvalidDataException("Invalid sovereignty hub placement.");
        }
        var direction = ship - star;
        if (!double.IsFinite(direction.Length))
        {
            throw new InvalidDataException("The sovereignty hub coordinates exceed the supported placement range.");
        }
        return star + (direction.Length > 0 ? direction * (starDistance / direction.Length) : new(0, starDistance, 0));
    }

    private static bool Finite(SpacePosition position) =>
        double.IsFinite(position.X) && double.IsFinite(position.Y) && double.IsFinite(position.Z);

    public static CaptureProgress Advance(
        CaptureProgress progress,
        IReadOnlyList<EntosisContribution> contributions,
        long now,
        long duration
    )
    {
        if (duration <= 0 || progress.ProgressTicks < 0 || progress.ProgressTicks > duration || now < 0 || progress.UpdatedAt < 0)
        {
            throw new InvalidDataException("Invalid Entosis capture progress.");
        }
        if (contributions.Any(link => link.AllianceId <= 0 || link.From < 0 || link.Until < link.From))
        {
            throw new InvalidDataException("Invalid Entosis contribution interval.");
        }
        if (now < progress.UpdatedAt || progress.CompletedAt is not null)
        {
            return progress;
        }
        long score = progress.ProgressTicks;
        int? controller = progress.ControllingAllianceId;
        Dictionary<int, int> active = [];
        foreach (var link in contributions.Where(link => link.From <= progress.UpdatedAt && link.Until > progress.UpdatedAt))
        {
            active[link.AllianceId] = active.GetValueOrDefault(link.AllianceId) + 1;
        }
        var changes = contributions
            .Where(link => link.From < link.Until)
            .SelectMany(link => new ContributionChange[] { new(link.From, link.AllianceId, 1), new(link.Until, link.AllianceId, -1) })
            .Where(change => change.At > progress.UpdatedAt && change.At <= now)
            .OrderBy(change => change.At)
            .ThenBy(change => change.Count)
            .Append(new(now, 0, 0))
            .ToArray();
        long from = progress.UpdatedAt;
        foreach (var change in changes)
        {
            long until = change.At;
            if (until > from && active.Count == 1)
            {
                int team = active.First().Key;
                long elapsed = until - from;
                if (controller != team && score > 0)
                {
                    long reversal = Math.Min(elapsed, score);
                    score -= reversal;
                    elapsed -= reversal;
                    from += reversal;
                }
                if (score == 0 || controller == team)
                {
                    controller = team;
                    long remaining = duration - score;
                    if (elapsed >= remaining)
                    {
                        return new(controller, duration, from + remaining, CompletedAt: from + remaining);
                    }
                    score += elapsed;
                }
            }
            from = until;
            if (change.Count != 0)
            {
                int count = active.GetValueOrDefault(change.AllianceId) + change.Count;
                if (count == 0)
                {
                    active.Remove(change.AllianceId);
                }
                else
                {
                    active[change.AllianceId] = count;
                }
            }
        }
        int? scoring = active.Count == 1 ? active.First().Key : null;
        int rate =
            scoring is null ? 0
            : controller is null || score == 0 || controller == scoring ? 1
            : -1;
        return new(controller, score, now, scoring, rate, Blocked: active.Count > 1);
    }
}
