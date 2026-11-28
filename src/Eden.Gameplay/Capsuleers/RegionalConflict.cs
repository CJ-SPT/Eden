namespace Eden.Gameplay.Capsuleers;

public enum RegionalObjective
{
    Raid,
    Intercept,
    Escort,
    Defend,
    Screen,
}

public enum RegionalReportKind
{
    CombatShip,
    Freight,
    Mining,
    Attack,
    Loss,
}

public enum RegionalOperationStatus
{
    Active,
    Completed,
    Aborted,
    Expired,
}

public sealed record RegionalGroup(int Id, string Name, int HomeSystemId, int HomeStationId)
{
    public int Pressure { get; init; }
    public long Losses { get; init; }
    public long Kills { get; init; }
    public long NextSequence { get; init; } = 1;
    public long NextLaunchAt { get; init; }
    public long NextOffensiveAt { get; init; }
}

public sealed record RegionalRelationship(int LowerGroupId, int HigherGroupId, int Tension, long LastIncidentAt);

public sealed record RegionalReport(
    int ObserverGroupId,
    int? OtherGroupId,
    long PilotId,
    long ShipId,
    int SystemId,
    long GridId,
    RegionalReportKind Kind,
    long SeenAt,
    long ExpiresAt
)
{
    public double ObservedStrength { get; init; }
}

public sealed record RegionalOperationKey(int RegionId, int GroupId, long Sequence);

public sealed record RegionalOperation(
    RegionalOperationKey Key,
    RegionalObjective Objective,
    int? OpponentGroupId,
    int DestinationStationId,
    int TargetSystemId,
    long TargetGridId,
    int[] Systems,
    long TargetPilotId,
    long IssuedAt,
    long ExpiresAt
)
{
    public int? ScreenClaimSystemId { get; init; }
    public RegionalOperationStatus Status { get; init; }
    public long GangId { get; init; }
    public long GangSequence { get; init; }
    public bool ReachedObjective { get; init; }
    public string Reason { get; init; } = "Patrolling a contested route.";
}

public sealed record RegionalConflictRegion(int RegionId, long Revision)
{
    public RegionalGroup[] Groups { get; init; } = [];
    public RegionalRelationship[] Relationships { get; init; } = [];
    public RegionalReport[] Reports { get; init; } = [];
    public RegionalOperation[] Operations { get; init; } = [];
    public long[] RecentLosses { get; init; } = [];
    public long DecayAt { get; init; }
}

public sealed record RegionalRoute(int GroupId, int StationId, int SystemId, int[] Systems, long GateId, long BeltId)
{
    public bool FriendlyAllianceSpace { get; init; }
    public RegionalScreenObjective? Screen { get; init; }
}

public sealed record RegionalScreenObjective(
    int ClaimSystemId,
    long GridId,
    bool HostileLink,
    double ClaimProgress,
    double TargetProgress,
    bool Covered = false
);

public sealed record RegionalEscortCandidate(long PilotId, long ShipId, int StationId, int[] Systems, long GateId);

public sealed record RegionalEscort(RegionalOperation Operation, long ShipId, RoamingGangMember[] Guards);

public interface IRegionalConflictDiagnostics
{
    IReadOnlyList<RegionalConflictRegion> ReadConflicts();
}

public static class RegionalConflictRules
{
    public const int MaximumGroups = 64;
    public const int MaximumRelationships = 128;
    public const int MaximumReports = 64;
    public const int MaximumOperations = 192;
    public const int RetainedOperations = 32;
    public const int RetainedLosses = 128;
    public const int RivalTension = 50;
    public const int MaximumPressure = 100;
    public const int AttackPressure = 10;
    public const int LossPressure = 20;
    public const int DecayPerMinute = 2;
    public static readonly long ReportTicks = TimeSpan.FromMinutes(3).Ticks;
    public static readonly long ReportingInterval = TimeSpan.FromSeconds(30).Ticks;
    public static readonly long LaunchInterval = TimeSpan.FromMinutes(2).Ticks;
    public static readonly long LossRecovery = TimeSpan.FromMinutes(5).Ticks;

    // A six-hour UTC deployment window with up to two hours of variation around a constellation's common start.
    private const int OffensiveShiftHours = 6;
    private const int OffensiveShiftOffsets = 5;
    private const double UnknownCombatShipStrength = 1;

    public static bool OffensiveShiftOpen(int constellation, int corporation, long utcTicks)
    {
        int startHour =
            (int)(TravelerRoutineRules.Mix((uint)constellation) % 24)
            + (int)(TravelerRoutineRules.Mix((uint)corporation) % OffensiveShiftOffsets)
            - OffensiveShiftOffsets / 2;
        long sinceStart = utcTicks % TimeSpan.TicksPerDay - startHour * TimeSpan.TicksPerHour;
        return (sinceStart + TimeSpan.TicksPerDay * 2) % TimeSpan.TicksPerDay < OffensiveShiftHours * TimeSpan.TicksPerHour;
    }

    public static bool Rivals(RegionalConflictRegion region, int group, int other) =>
        group != other
        && region.Relationships.Any(relation =>
            relation.LowerGroupId == Math.Min(group, other)
            && relation.HigherGroupId == Math.Max(group, other)
            && relation.Tension >= RivalTension
        );

    public static RegionalConflictRegion Discover(RegionalConflictRegion region, RegionalGroup group)
    {
        if (region.Groups.Any(existing => existing.Id == group.Id) || region.Groups.Length >= MaximumGroups)
        {
            return region;
        }
        return region with { Revision = checked(region.Revision + 1), Groups = [.. region.Groups, group] };
    }

    public static RegionalConflictRegion Compete(RegionalConflictRegion region, int group, int rival)
    {
        if (
            group == rival
            || Rivals(region, group, rival)
            || region.Relationships.Length >= MaximumRelationships
            || !region.Groups.Any(candidate => candidate.Id == group)
            || !region.Groups.Any(candidate => candidate.Id == rival)
        )
        {
            return region;
        }
        var relation = new RegionalRelationship(Math.Min(group, rival), Math.Max(group, rival), RivalTension, 0);
        return region with { Revision = checked(region.Revision + 1), Relationships = [.. region.Relationships, relation] };
    }

    public static RegionalConflictRegion Advance(RegionalConflictRegion region, long elapsed)
    {
        if (elapsed < region.DecayAt)
        {
            return region;
        }
        long steps = 1 + Math.Max(0, elapsed - region.DecayAt) / TimeSpan.TicksPerMinute;
        int decay = (int)Math.Min(MaximumPressure, steps * DecayPerMinute);
        return region with
        {
            Revision = checked(region.Revision + 1),
            DecayAt = checked(elapsed + TimeSpan.TicksPerMinute),
            Groups = region.Groups.Select(group => group with { Pressure = Math.Max(0, group.Pressure - decay) }).ToArray(),
            Relationships = region
                .Relationships.Select(relation => relation with { Tension = Math.Max(RivalTension, relation.Tension - decay) })
                .ToArray(),
            Reports = region.Reports.Where(report => report.ExpiresAt > elapsed).ToArray(),
            Operations = region
                .Operations.Select(operation =>
                    operation.Status == RegionalOperationStatus.Active && operation.ExpiresAt <= elapsed
                        ? operation with
                        {
                            Status = RegionalOperationStatus.Expired,
                        }
                        : operation
                )
                .OrderByDescending(operation => operation.Status == RegionalOperationStatus.Active)
                .ThenByDescending(operation => operation.IssuedAt)
                .Take(
                    Math.Min(
                        MaximumOperations,
                        region.Operations.Count(operation => operation.Status == RegionalOperationStatus.Active) + RetainedOperations
                    )
                )
                .ToArray(),
        };
    }

    public static bool NeedsReport(RegionalConflictRegion region, RegionalReport report) =>
        !region.Reports.Any(previous =>
            previous.ObserverGroupId == report.ObserverGroupId
            && previous.ShipId == report.ShipId
            && previous.Kind == report.Kind
            && previous.SystemId == report.SystemId
            && previous.GridId == report.GridId
            && report.SeenAt < previous.SeenAt + ReportingInterval
        );

    public static RegionalConflictRegion Report(RegionalConflictRegion region, RegionalReport report)
    {
        if (!NeedsReport(region, report))
        {
            return region;
        }
        region = Advance(region, report.SeenAt);
        bool danger = report.Kind is RegionalReportKind.Attack or RegionalReportKind.Loss;
        return region with
        {
            Revision = checked(region.Revision + 1),
            Reports = region
                .Reports.Where(previous =>
                    previous.ObserverGroupId != report.ObserverGroupId || previous.ShipId != report.ShipId || previous.Kind != report.Kind
                )
                .Prepend(report)
                .Take(MaximumReports)
                .ToArray(),
            Groups = region
                .Groups.Select(group =>
                    danger && group.Id == report.ObserverGroupId
                        ? group with
                        {
                            Pressure = Math.Min(MaximumPressure, group.Pressure + AttackPressure),
                        }
                        : group
                )
                .ToArray(),
            Relationships = region
                .Relationships.Select(relation =>
                    danger
                    && report.OtherGroupId is { } other
                    && relation.LowerGroupId == Math.Min(report.ObserverGroupId, other)
                    && relation.HigherGroupId == Math.Max(report.ObserverGroupId, other)
                        ? relation with
                        {
                            Tension = Math.Min(MaximumPressure, relation.Tension + AttackPressure),
                            LastIncidentAt = report.SeenAt,
                        }
                        : relation
                )
                .ToArray(),
        };
    }

    public static RegionalConflictRegion Loss(RegionalConflictRegion region, int? victimGroup, int? attackerGroup, long ship, long elapsed)
    {
        if (region.RecentLosses.Contains(ship))
        {
            return region;
        }
        region = Advance(region, elapsed);
        return region with
        {
            Revision = checked(region.Revision + 1),
            RecentLosses = region.RecentLosses.Prepend(ship).Take(RetainedLosses).ToArray(),
            Groups = region
                .Groups.Select(group =>
                    group.Id == victimGroup
                        ? group with
                        {
                            Losses = checked(group.Losses + 1),
                            Pressure = Math.Min(MaximumPressure, group.Pressure + LossPressure),
                            NextOffensiveAt = Math.Max(group.NextOffensiveAt, checked(elapsed + LossRecovery)),
                        }
                    : attackerGroup == group.Id ? group with { Kills = checked(group.Kills + 1) }
                    : group
                )
                .ToArray(),
        };
    }

    public static RegionalOperation? Plan(
        RegionalConflictRegion region,
        int groupId,
        IReadOnlyList<RegionalRoute> routes,
        IReadOnlyList<RegionalEscortCandidate> escorts,
        long elapsed,
        int maximumActive,
        double force = double.PositiveInfinity,
        bool offensiveShift = true,
        bool screensOnly = false
    )
    {
        var group = region.Groups.FirstOrDefault(candidate => candidate.Id == groupId);
        if (
            group is null
            || elapsed < group.NextLaunchAt
            || region.Operations.Count(operation =>
                operation.Key.GroupId == groupId && operation.Status == RegionalOperationStatus.Active && operation.ExpiresAt > elapsed
            ) >= maximumActive
        )
        {
            return null;
        }
        var key = new RegionalOperationKey(region.RegionId, groupId, group.NextSequence);
        RegionalOperation Create(RegionalObjective objective, RegionalRoute route, long target, string reason, long grid = 0) =>
            new(
                key,
                objective,
                route.GroupId == groupId ? null : route.GroupId,
                route.StationId,
                route.SystemId,
                grid != 0 ? grid
                    : objective == RegionalObjective.Raid && route.BeltId != 0 ? route.BeltId
                    : route.GateId,
                route.Systems,
                target,
                elapsed,
                checked(elapsed + RoamingGangRules.SortieTicks)
            )
            {
                Reason = reason,
            };
        RegionalOperation? PlanScreen()
        {
            var route = routes
                .Where(candidate =>
                    candidate.Screen is { Covered: false } screen
                    && !region.Operations.Any(operation =>
                        operation.Objective == RegionalObjective.Screen
                        && operation.TargetGridId == screen.GridId
                        && operation.ExpiresAt > elapsed
                    )
                )
                .OrderByDescending(candidate => candidate.Screen!.HostileLink)
                .ThenBy(candidate => candidate.Systems.Length)
                .ThenBy(candidate => candidate.Screen!.ClaimProgress)
                .ThenBy(candidate => candidate.Screen!.TargetProgress)
                .ThenBy(candidate => candidate.Screen!.GridId)
                .FirstOrDefault();
            return route?.Screen is { } objective
                ? Create(RegionalObjective.Screen, route, 0, "Screening a contested sovereignty objective.", objective.GridId) with
                {
                    OpponentGroupId = null,
                    ScreenClaimSystemId = objective.ClaimSystemId,
                }
                : null;
        }
        if (screensOnly)
        {
            return PlanScreen();
        }
        foreach (
            var report in region
                .Reports.Where(report =>
                    report.ObserverGroupId == groupId
                    && report.ExpiresAt > elapsed
                    && (
                        report.Kind is RegionalReportKind.Attack or RegionalReportKind.Loss
                        || report.Kind == RegionalReportKind.CombatShip
                            && report.OtherGroupId is { } other
                            && Rivals(region, groupId, other)
                            && routes.Any(route => route.Screen is null && route.SystemId == report.SystemId && route.GroupId == groupId)
                    )
                    && !routes.Any(route => route.Screen?.GridId == report.GridId)
                )
                .OrderByDescending(report => report.Kind is RegionalReportKind.Attack or RegionalReportKind.Loss)
                .ThenByDescending(report => report.SeenAt)
        )
        {
            var route = routes.FirstOrDefault(route => route.Screen is null && route.SystemId == report.SystemId);
            if (route is not null && report.GridId != 0)
            {
                return Create(
                    RegionalObjective.Defend,
                    route,
                    report.PilotId,
                    report.Kind == RegionalReportKind.CombatShip
                        ? "Responding to a rival combat ship sighted in home territory."
                        : "Responding to a witnessed attack on local pilots.",
                    report.GridId
                );
            }
        }
        var escort = escorts.OrderBy(candidate => candidate.PilotId).FirstOrDefault();
        if (escort is not null)
        {
            return new(
                key,
                RegionalObjective.Escort,
                null,
                escort.StationId,
                escort.Systems[^1],
                escort.GateId,
                escort.Systems,
                escort.PilotId,
                elapsed,
                checked(elapsed + RoamingGangRules.SortieTicks)
            )
            {
                Reason = "Escorting an owned freight shipment along its actual route.",
            };
        }
        if (PlanScreen() is { } screen)
        {
            return screen;
        }
        if (!offensiveShift || elapsed < group.NextOffensiveAt)
        {
            return null;
        }
        var rivals = routes
            .Where(route => route.Screen is null && !route.FriendlyAllianceSpace && Rivals(region, groupId, route.GroupId))
            .OrderBy(route => route.Systems.Length)
            .ThenBy(route => TravelerRoutineRules.Mix((ulong)route.StationId ^ (ulong)group.NextSequence))
            .ToArray();
        var reports = region
            .Reports.Where(report => report.ObserverGroupId == groupId && report.SeenAt <= elapsed && report.ExpiresAt > elapsed)
            .ToArray();
        var opposition = reports
            .Where(report => report.Kind == RegionalReportKind.CombatShip)
            .DistinctBy(report => report.ShipId)
            .GroupBy(report => (report.SystemId, report.GridId))
            .ToDictionary(
                grid => grid.Key,
                grid => grid.Sum(report => report.ObservedStrength > 0 ? report.ObservedStrength : UnknownCombatShipStrength)
            );
        var freight = reports
            .Where(report => report.Kind == RegionalReportKind.Freight && report.GridId != 0)
            .Select(report =>
                (
                    Report: report,
                    Route: rivals.FirstOrDefault(route => route.SystemId == report.SystemId && route.GroupId == report.OtherGroupId)
                )
            )
            .Where(pair => pair.Route is not null)
            .Select(pair => (pair.Report, pair.Route, Opposition: opposition.GetValueOrDefault((pair.Report.SystemId, pair.Report.GridId))))
            .Where(pair => pair.Opposition <= force)
            .OrderBy(pair => pair.Opposition)
            .ThenBy(pair => pair.Route!.Systems.Length)
            .ThenByDescending(pair => pair.Report.SeenAt)
            .FirstOrDefault();
        if (freight.Route is not null)
        {
            return Create(
                RegionalObjective.Intercept,
                freight.Route,
                freight.Report.PilotId,
                "Intercepting recently observed rival freight traffic.",
                freight.Report.GridId
            );
        }
        var objective = group.NextSequence % 2 == 0 ? RegionalObjective.Intercept : RegionalObjective.Raid;
        var rival = rivals
            .Select(route => (Route: route, Grid: objective == RegionalObjective.Raid && route.BeltId != 0 ? route.BeltId : route.GateId))
            .FirstOrDefault(pair => pair.Grid != 0 && opposition.GetValueOrDefault((pair.Route.SystemId, pair.Grid)) <= force);
        return rival.Route is null
            ? null
            : Create(objective, rival.Route, 0, "Patrolling a rival's extraction sites and transport corridor.", rival.Grid);
    }

    public static RegionalConflictRegion Launch(RegionalConflictRegion region, RegionalOperation operation, long gang, long sequence)
    {
        var group = region.Groups.FirstOrDefault(candidate => candidate.Id == operation.Key.GroupId);
        if (
            operation.Key.RegionId != region.RegionId
            || group?.NextSequence != operation.Key.Sequence
            || region.Operations.Any(existing => existing.Key == operation.Key)
            || region.Operations.Count(existing => existing.Status == RegionalOperationStatus.Active) >= MaximumOperations
        )
        {
            throw new InvalidDataException("The regional operation has already launched.");
        }
        return region with
        {
            Revision = checked(region.Revision + 1),
            Operations = region
                .Operations.Prepend(operation with { GangId = gang, GangSequence = sequence })
                .OrderByDescending(existing => existing.Status == RegionalOperationStatus.Active)
                .ThenByDescending(existing => existing.IssuedAt)
                .Take(MaximumOperations)
                .ToArray(),
            Groups = region
                .Groups.Select(group =>
                    group.Id == operation.Key.GroupId
                        ? group with
                        {
                            NextSequence = checked(group.NextSequence + 1),
                            NextLaunchAt = checked(operation.IssuedAt + LaunchInterval),
                        }
                        : group
                )
                .ToArray(),
        };
    }

    public static RegionalConflictRegion Finish(
        RegionalConflictRegion region,
        RegionalOperationKey key,
        bool success,
        string? reason = null
    ) =>
        region with
        {
            Revision = checked(region.Revision + 1),
            Operations = region
                .Operations.Select(operation =>
                    operation.Key == key && operation.Status == RegionalOperationStatus.Active
                        ? operation with
                        {
                            Status = success ? RegionalOperationStatus.Completed : RegionalOperationStatus.Aborted,
                            Reason = reason ?? operation.Reason,
                        }
                        : operation
                )
                .ToArray(),
        };
}
