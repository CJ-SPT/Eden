using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Configuration;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class RegionalConflictTests
{
    private static RegionalConflictRegion Region() =>
        new(100, 1)
        {
            Groups = [new(1, "Home patrol", 300, 600), new(2, "Rival patrol", 301, 601)],
            Relationships = [new(1, 2, RegionalConflictRules.RivalTension, 0)],
            DecayAt = TimeSpan.TicksPerMinute,
        };

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void OperationLimitsRejectUnboundedEscalation(int limit) =>
        Assert.Throws<InvalidDataException>(() => CapsuleerPopulationRules.Validate(new() { RegionalOperationsPerGroup = limit }));

    [Fact]
    public void ReportsExpireAndEscortPlanningNeverInventsFreight()
    {
        var region = Region();
        RegionalRoute[] routes = [new(2, 601, 301, [300, 301], 501, 401)];
        RegionalEscortCandidate[] owned = [new(900, 901, 601, [300, 301], 501)];
        var escort = RegionalConflictRules.Plan(region, 1, routes, owned, 0, 1)!;
        Assert.Equal(RegionalObjective.Escort, escort.Objective);
        Assert.Equal(900, escort.TargetPilotId);
        var report = new RegionalReport(1, 2, 902, 903, 301, 501, RegionalReportKind.Freight, 0, RegionalConflictRules.ReportTicks);
        region = RegionalConflictRules.Report(region, report);
        var intercept = RegionalConflictRules.Plan(region, 1, routes, [], 0, 1)!;
        Assert.Equal(RegionalObjective.Intercept, intercept.Objective);
        Assert.Equal(report.PilotId, intercept.TargetPilotId);
        var expired = RegionalConflictRules.Advance(region, report.ExpiresAt);
        Assert.Empty(expired.Reports);
        Assert.Equal(RegionalObjective.Raid, RegionalConflictRules.Plan(expired, 1, routes, [], report.ExpiresAt, 1)!.Objective);
        Assert.Single(region.Reports);
    }

    [Fact]
    public void WitnessedAttacksTakePriorityAndLossesDelayOffensiveOperations()
    {
        var region = Region();
        RegionalRoute[] routes = [new(1, 600, 300, [300], 500, 400), new(2, 601, 301, [300, 301], 501, 401)];
        var raid = Assert.IsType<RegionalOperation>(RegionalConflictRules.Plan(region, 1, routes, [], 0, 1));
        Assert.Equal(RegionalObjective.Raid, raid.Objective);
        Assert.Equal(2, raid.OpponentGroupId);
        Assert.Equal(401, raid.TargetGridId);
        var report = new RegionalReport(1, null, 900, 901, 300, 400, RegionalReportKind.Attack, 0, RegionalConflictRules.ReportTicks);
        region = RegionalConflictRules.Report(region, report);
        Assert.Same(region, RegionalConflictRules.Report(region, report));
        var defense = Assert.IsType<RegionalOperation>(RegionalConflictRules.Plan(region, 1, routes, [], 0, 1));
        Assert.Equal(RegionalObjective.Defend, defense.Objective);
        Assert.Equal(900, defense.TargetPilotId);
        region = RegionalConflictRules.Loss(region, 1, 2, 1000, 0);
        Assert.Same(region, RegionalConflictRules.Loss(region, 1, 2, 1000, 0));
        Assert.Equal(1, region.Groups[0].Losses);
        Assert.Equal(1, region.Groups[1].Kills);
        Assert.Equal(RegionalObjective.Defend, RegionalConflictRules.Plan(region, 1, routes, [], 0, 1)!.Objective);
        var quiet = region with { Reports = [] };
        Assert.Null(RegionalConflictRules.Plan(quiet, 1, routes, [], 0, 1));
        Assert.NotNull(RegionalConflictRules.Plan(quiet, 1, routes, [], RegionalConflictRules.LossRecovery, 1));
    }

    [Fact]
    public void ActiveOperationLimitsAndReceiptsSurviveQuietAdvancement()
    {
        var region = Region();
        RegionalRoute[] routes = [new(2, 601, 301, [300, 301], 501, 401)];
        var order = RegionalConflictRules.Plan(region, 1, routes, [], 0, 1)!;
        var started = RegionalConflictRules.Launch(region, order, 99, 1);
        Assert.Null(RegionalConflictRules.Plan(started, 1, routes, [], RegionalConflictRules.LaunchInterval, 1));
        Assert.Throws<InvalidDataException>(() => RegionalConflictRules.Launch(started, order, 99, 1));
        var ended = RegionalConflictRules.Advance(started, order.ExpiresAt);
        Assert.Equal(RegionalOperationStatus.Expired, Assert.Single(ended.Operations).Status);
        var next = Assert.IsType<RegionalOperation>(RegionalConflictRules.Plan(ended, 1, routes, [], order.ExpiresAt, 1));
        Assert.Equal(2, next.Key.Sequence);
        Assert.Equal(RegionalOperationStatus.Active, Assert.Single(started.Operations).Status);
        Assert.Equal(1, region.Groups[0].NextSequence);
    }

    [Fact]
    public void OutmatchedGangInterceptsExposedFreightAndPassesWhenItsSightingExpires()
    {
        var region = Region();
        RegionalRoute[] routes = [new(2, 601, 301, [300, 301], 501, 401)];
        long oldReportTime = TimeSpan.TicksPerMinute;
        long freshReportTime = 2 * TimeSpan.TicksPerMinute;
        var reports = new RegionalReport[]
        {
            new(1, 2, 900, 1000, 301, 501, RegionalReportKind.Freight, freshReportTime, 5 * TimeSpan.TicksPerMinute),
            new(1, 2, 901, 1001, 301, 502, RegionalReportKind.Freight, oldReportTime, 4 * TimeSpan.TicksPerMinute),
            new(1, 2, 902, 1002, 301, 501, RegionalReportKind.CombatShip, freshReportTime, 5 * TimeSpan.TicksPerMinute)
            {
                ObservedStrength = 8,
            },
            new(1, 2, 903, 1003, 301, 401, RegionalReportKind.CombatShip, freshReportTime, 5 * TimeSpan.TicksPerMinute)
            {
                ObservedStrength = 8,
            },
        };
        foreach (var report in reports.OrderBy(report => report.SeenAt))
        {
            region = RegionalConflictRules.Report(region, report);
        }

        var intercept = Assert.IsType<RegionalOperation>(RegionalConflictRules.Plan(region, 1, routes, [], freshReportTime, 1, force: 4));
        Assert.Equal((RegionalObjective.Intercept, 901L, 502L), (intercept.Objective, intercept.TargetPilotId, intercept.TargetGridId));
        Assert.Null(RegionalConflictRules.Plan(region, 1, routes, [], reports[1].ExpiresAt, 1, force: 4));
        Assert.Equal(
            900,
            RegionalConflictRules
                .Plan(region with { Reports = [reports[0], reports[2]] }, 1, routes, [], freshReportTime, 1, force: 10)
                ?.TargetPilotId
        );
    }

    [Fact]
    public void CorporationsStaggerTheirOffensiveShiftsAndStillScreenAContestedHub()
    {
        int[] corporations = Enumerable.Range(98000001, 5).ToArray();
        long[] utcHours = Enumerable.Range(0, 24).Select(hour => hour * TimeSpan.TicksPerHour).ToArray();
        foreach (int corporation in corporations)
        {
            Assert.Equal(6, utcHours.Count(utc => RegionalConflictRules.OffensiveShiftOpen(1, corporation, utc)));
            Assert.All(
                utcHours,
                utc =>
                    Assert.Equal(
                        RegionalConflictRules.OffensiveShiftOpen(1, corporation, utc),
                        RegionalConflictRules.OffensiveShiftOpen(1, corporation, utc + TimeSpan.TicksPerDay)
                    )
            );
        }
        Assert.True(utcHours.Count(utc => corporations.All(corp => RegionalConflictRules.OffensiveShiftOpen(1, corp, utc))) >= 2);
        Assert.Contains(
            utcHours,
            utc =>
                corporations.Any(corp => RegionalConflictRules.OffensiveShiftOpen(1, corp, utc))
                && corporations.Any(corp => !RegionalConflictRules.OffensiveShiftOpen(1, corp, utc))
        );
        var region = Region();
        RegionalRoute home = new(1, 600, 300, [300], 500, 400);
        RegionalRoute rival = new(2, 601, 301, [300, 301], 501, 401);
        Assert.Null(RegionalConflictRules.Plan(region, 1, [home, rival], [], 0, 1, offensiveShift: false));
        var screen = Assert.IsType<RegionalOperation>(
            RegionalConflictRules.Plan(region, 1, [home with { ContestedAllianceHub = true }, rival], [], 0, 1, offensiveShift: false)
        );
        Assert.Equal((RegionalObjective.Screen, 300, 500L), (screen.Objective, screen.TargetSystemId, screen.TargetGridId));
        var started = RegionalConflictRules.Launch(region, screen, 99, 1);
        var returned = RegionalConflictRules.Finish(started, screen.Key, true);
        Assert.Null(
            RegionalConflictRules.Plan(
                returned,
                1,
                [home with { ContestedAllianceHub = true }, rival],
                [],
                RegionalConflictRules.LaunchInterval,
                1,
                offensiveShift: false
            )
        );
    }
}
