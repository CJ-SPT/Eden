namespace Eden.Gameplay.Sovereignty;

public enum SovereigntyPhase
{
    Secure,
    Reinforced,
    Campaign,
}

public sealed record VulnerabilityWindow(long StartsAt, long EndsAt);

public sealed record AlliancePrimeTime(int CurrentHour, int? NewHour, long? ValidAfter, int? PreviousHour = null);

public sealed record TerritoryProgress(
    double Value,
    long UpdatedAt,
    int? ScoringTeam = null,
    double Rate = 0,
    bool Blocked = false,
    long? CompletedAt = null
);

public sealed record SovereigntyConflict(
    int SystemId,
    long HubId,
    int ConstellationId,
    int DefenderAllianceId,
    SovereigntyPhase Phase,
    VulnerabilityWindow Window,
    long? CampaignAt,
    int DefenderPoints,
    TerritoryProgress Capture
)
{
    public decimal DefenseMultiplier { get; init; } = 1m;
    public decimal WindowMultiplier { get; init; } = 1m;
    public decimal CampaignMultiplier { get; init; } = 1m;
}

public sealed record SovereigntyCommandNode(
    long Id,
    int SystemId,
    int SourceSystemId,
    long HubId,
    int CorporationId,
    int AllianceId,
    Flight.SpacePosition Position,
    TerritoryProgress Capture,
    long Duration
);

public static class SovereigntyWarfareRules
{
    private const ulong DeterministicSeedMultiplier = 11400714819323198485UL;

    private readonly record struct CaptureChange(long At, int Team, int Count);

    // Pinned entosisConst.CHANGE_PRIMETIME_DELAY is 48 hours, unlike the current UniWiki's 96.
    public static readonly long PrimeChangeDelay = TimeSpan.FromHours(48).Ticks;
    public static readonly long TransitionOverlap = TimeSpan.TicksPerDay;
    public static readonly long ExtraNodeInterval = TimeSpan.FromMinutes(30).Ticks;
    public static readonly long BaseVulnerabilityDuration = TimeSpan.FromHours(18).Ticks;

    // Eden chooses reproducible off-grid locations; these are placement policy, not CCP constants.
    public const double NodeOrbit = 5_000_000_000;
    public const double NodeSeparation = 1_000_000;
    public const long NodePositionSlots = 1_000_000;
    public const int DefaultPrimeHour = 18;
    public const int InitialNodes = 5;
    public const int MaximumSystemNodes = 5;
    public const int MaximumCampaignNodes = 10;
    public const int MaximumConstellationNodes = 20;
    public const int InitialDefenderPoints = 60;
    public const int NodePoints = 7;
    public const int CommandNodeTypeId = 34832;
    public const int CommandNodeGroupId = 1316;
    public const int Attackers = 0;
    public const int HubDefenseEvent = 2;

    public static AlliancePrimeTime Prime(AlliancePrimeTime saved, long now) =>
        saved.ValidAfter is long at && saved.NewHour is int hour && now >= at
            ? new(hour, null, at, now < at + TransitionOverlap ? saved.CurrentHour : null)
            : saved;

    public static VulnerabilityWindow Window(int hour, long now, decimal multiplier = 1m)
    {
        if (hour is < 0 or > 23 || now < 0 || multiplier is < 1 or > SovereigntyRules.MaximumDefenseMultiplier)
        {
            throw new InvalidDataException("Invalid vulnerability schedule.");
        }
        long length = (long)(BaseVulnerabilityDuration / multiplier);
        long middle = now / TimeSpan.TicksPerDay * TimeSpan.TicksPerDay + hour * TimeSpan.TicksPerHour;
        if (now >= middle + length / 2)
        {
            middle += TimeSpan.TicksPerDay;
        }
        else if (now < middle - length / 2 && now < middle - TimeSpan.TicksPerDay + length / 2)
        {
            middle -= TimeSpan.TicksPerDay;
        }
        return new(middle - length / 2, middle + length / 2);
    }

    public static VulnerabilityWindow Window(AlliancePrimeTime prime, long now)
    {
        var current = Window(prime.CurrentHour, now);
        if (prime.PreviousHour is int previous)
        {
            var old = Window(previous, now);
            if (now >= old.StartsAt && now < old.EndsAt)
            {
                return now >= current.StartsAt ? new(Math.Min(old.StartsAt, current.StartsAt), Math.Max(old.EndsAt, current.EndsAt)) : old;
            }
        }
        return current;
    }

    public static bool Open(VulnerabilityWindow window, long now) => now >= window.StartsAt && now < window.EndsAt;

    public static long CampaignDeadline(long reinforcedAt, int primeHour, long hubId, decimal multiplier = 1m)
    {
        var window = Window(primeHour, checked(reinforcedAt + 2 * TimeSpan.TicksPerDay), multiplier);
        // Stable jitter is selected once and persisted; replay/restart cannot reroll it.
        ulong seed = unchecked((ulong)hubId * DeterministicSeedMultiplier + (ulong)reinforcedAt);
        return checked(window.StartsAt + (long)(seed % (ulong)(window.EndsAt - window.StartsAt)));
    }

    public static int SelectNodeSystem(long hubId, int sequence, IReadOnlyList<int> available)
    {
        if (hubId <= 0 || sequence <= 0 || available.Count == 0)
        {
            throw new InvalidDataException("Choose a valid command-node location.");
        }
        ulong seed = unchecked((ulong)hubId * DeterministicSeedMultiplier + (ulong)sequence);
        return available[(int)(seed % (ulong)available.Count)];
    }

    public static Flight.SpacePosition NodePosition(long id, IReadOnlyList<Flight.SpacePosition> occupied)
    {
        double distance = NodeOrbit + id % NodePositionSlots * NodeSeparation;
        while (occupied.Any(point => Math.Abs(point.X - distance) < NodeSeparation))
        {
            distance += NodeSeparation;
        }
        return new(distance, distance / 2, -distance / 4);
    }

    public static TerritoryProgress Advance(
        TerritoryProgress state,
        IReadOnlyList<EntosisContribution> links,
        long now,
        long duration,
        double regeneration,
        int defender,
        bool node,
        long admissionEndsAt = long.MaxValue,
        decimal attackMultiplier = 1m
    )
    {
        double lower = node ? -1 : 0;
        if (
            !double.IsFinite(state.Value)
            || state.Value < lower
            || state.Value > 1
            || duration <= 0
            || attackMultiplier is < 1 or > SovereigntyRules.MaximumDefenseMultiplier
            || !double.IsFinite(regeneration)
            || regeneration < 0
            || now < 0
            || state.UpdatedAt < 0
            || links.Any(link => link.From < 0 || link.Until < link.From || link.AllianceId < 0)
        )
        {
            throw new InvalidDataException("Invalid territorial capture state.");
        }
        if (now < state.UpdatedAt)
        {
            return state;
        }
        int defending = 0,
            attacking = 0;
        foreach (var link in links.Where(link => link.From <= state.UpdatedAt && link.Until > state.UpdatedAt))
        {
            if (link.AllianceId == defender)
            {
                defending++;
            }
            else
            {
                attacking++;
            }
        }
        var events = links
            .Where(link => link.From < link.Until)
            .SelectMany(link => new CaptureChange[] { new(link.From, link.AllianceId, 1), new(link.Until, link.AllianceId, -1) })
            .Where(change => change.At > state.UpdatedAt && change.At <= now)
            .Append(new(now, defender, 0))
            .OrderBy(change => change.At)
            .ThenBy(change => change.Count)
            .ToArray();
        double score = state.Value;
        long from = state.UpdatedAt;
        double Rate() =>
            defending > 0 && attacking > 0 ? 0
            : attacking > 0 && (node || score < 1 || from < admissionEndsAt) ? -1 / (double)attackMultiplier
            : defending > 0 ? 1
            : regeneration;
        foreach (var change in events)
        {
            double rate = Rate();
            if (change.At > from && rate != 0)
            {
                double distance = rate > 0 ? 1 - score : score - lower;
                long elapsed = change.At - from;
                long required = checked((long)Math.Ceiling(Math.Max(0, distance) * duration / Math.Abs(rate)));
                if ((node || rate < 0) && required <= elapsed)
                {
                    return new(rate > 0 ? 1 : lower, from + required, rate > 0 ? defender : Attackers, rate, CompletedAt: from + required);
                }
                score = Math.Clamp(score + elapsed / (double)duration * rate, lower, 1);
            }
            from = change.At;
            if (change.Team == defender)
            {
                defending += change.Count;
            }
            else
            {
                attacking += change.Count;
            }
        }
        double finalRate = Rate();
        if (score == 1 && finalRate > 0)
        {
            finalRate = 0;
        }
        return new(
            score,
            now,
            defending > 0 && attacking == 0 ? defender
                : attacking > 0 && defending == 0 ? Attackers
                : null,
            finalRate,
            defending > 0 && attacking > 0
        );
    }
}
