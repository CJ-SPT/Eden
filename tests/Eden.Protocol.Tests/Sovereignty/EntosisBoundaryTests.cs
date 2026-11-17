using Eden.Data.World.Alliances;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Targeting;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Sovereignty;

public sealed class EntosisBoundaryTests
{
    [Fact]
    public void UnpublishedQaLinkCannotBeFitted()
    {
        using SovereigntyCaptureFixture fixture = new();
        Assert.Throws<InvalidDataException>(() => fixture.Fit(34826));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LostLockOrMembershipStopsScoringAndDuplicateCallbacksCannotRepay(bool membership)
    {
        using SovereigntyCaptureFixture fixture = new();
        long module = fixture.Fit();
        long fuel = fixture.Grant(SovereigntyCaptureFixture.Strontium, 10);
        long hub = fixture.Deploy();
        fixture.Lock(hub);
        fixture.Entosis.Activate(SovereigntyCaptureFixture.PilotId, fixture.Ship(), module, hub, 10, fixture.Clock.Now);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
        fixture.Entosis.Renew(SovereigntyCaptureFixture.PilotId, module, fixture.Clock.Now);
        Assert.Throws<InvalidDataException>(() => fixture.Entosis.Renew(SovereigntyCaptureFixture.PilotId, module, fixture.Clock.Now));
        Assert.Equal(8, fixture.Quantity(fuel));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(30));
        long score = fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Capture.ProgressTicks;
        if (membership)
        {
            using var action = fixture.Live.Economy.Begin(TransactionParticipants.None);
            action.Remove(action.Set<SavedAllianceMember>().Find(fixture.CorporationId)!);
            action.Commit();
        }
        else
        {
            fixture.Locks.Remove(fixture.Ship(), hub);
        }
        fixture.Entosis.Advance(SovereigntyCaptureFixture.SystemId, fixture.Clock.Now);
        Assert.True(fixture.Entosis.TravelLocked(fixture.Ship(), fixture.Clock.Now));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
        Assert.Equal(score, fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Capture.ProgressTicks);
        Assert.Null(fixture.Entosis.Renew(SovereigntyCaptureFixture.PilotId, module, fixture.Clock.Now));
        Assert.Equal(8, fixture.Quantity(fuel));
        Assert.Null(fixture.Claims.Read(SovereigntyCaptureFixture.SystemId));
    }

    [Theory]
    [InlineData(34593, 300, 50, 20000, 4000)]
    [InlineData(34595, 120, 500, 50000, 4000)]
    [InlineData(40307, 300, 50, 25000, 4200)]
    [InlineData(40308, 300, 50, 25000, 4000)]
    [InlineData(40309, 300, 20, 25000, 4000)]
    [InlineData(40310, 300, 50, 30000, 4000)]
    public void PublishedEntosisVariantsUseTheirCatalogCostsAndPaidDeadlines(
        int type,
        int seconds,
        double capacitor,
        int range,
        double speed
    )
    {
        using SovereigntyCaptureFixture fixture = new();
        long module = fixture.Fit(type);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 2);
        long hub = fixture.Deploy();
        fixture.Position(new(2_000_000_000 + range, 0, 0));
        fixture.Lock(hub);
        double before = fixture.Capacitor();
        var result = fixture.Entosis.Activate(SovereigntyCaptureFixture.PilotId, fixture.Ship(), module, hub, 0, fixture.Clock.Now);
        Assert.Equal(before - capacitor, result.Capacitor, 6);
        Assert.Equal(TimeSpan.FromSeconds(seconds).Ticks, result.Cycle.EndsAt - result.Cycle.StartedAt);
        Assert.Equal(speed, fixture.Entosis.SpeedLimit(fixture.Ship(), fixture.Clock.Now));
        fixture.Entosis.Stop(SovereigntyCaptureFixture.PilotId, module, fixture.Clock.Now);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(seconds));
        Assert.Equal(speed, fixture.Entosis.SpeedLimit(fixture.Ship(), fixture.Clock.Now));
        Assert.False(fixture.Entosis.TravelLocked(fixture.Ship(), fixture.Clock.Now));
        Assert.Null(fixture.Entosis.Renew(SovereigntyCaptureFixture.PilotId, module, fixture.Clock.Now));
        Assert.Null(fixture.Claims.Read(SovereigntyCaptureFixture.SystemId));
    }

    [Fact]
    public void CapitalDurationAndAssistanceRestrictionsUseThePaidCycle()
    {
        using SovereigntyCaptureFixture fixture = new(hull: 19720);
        long module = fixture.Fit(SovereigntyCaptureFixture.LinkOne);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 2);
        long hub = fixture.Deploy();
        fixture.Lock(hub);
        var cycle = fixture.Entosis.Activate(SovereigntyCaptureFixture.PilotId, fixture.Ship(), module, hub, 0, fixture.Clock.Now).Cycle;
        Assert.True(cycle.Capital);
        Assert.Equal(TimeSpan.FromMinutes(25).Ticks, cycle.EndsAt - cycle.StartedAt);
        fixture.Position(new(2_000_070_000, 0, 0), SovereigntyCaptureFixture.OtherPilotId);
        fixture.Live.Economy.Inspect(world =>
        {
            using Eden.World.Catalog.CatalogDbContext catalog = new(new WorldDatabase(fixture.World.Path).CatalogPath);
            // Repair drones share this admission check with other assistance consumers.
            Assert.Throws<InvalidDataException>(() =>
                Eden.World.Combat.AssistancePersistence.Check(
                    world,
                    catalog,
                    world.Pilots.Find(SovereigntyCaptureFixture.OtherPilotId)!,
                    world.Pilots.Find(SovereigntyCaptureFixture.PilotId)!,
                    fixture.Clock.Now
                )
            );
            Assert.Throws<InvalidDataException>(() =>
                Eden.World.Sovereignty.EntosisPersistence.RequireAssistance(world, fixture.Ship(), fixture.Clock.Now)
            );
            Eden.World.Sovereignty.EntosisPersistence.RequireAssistance(world, fixture.Ship(), cycle.EndsAt);
            return true;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoLockAndOutOfRangeActivationsLeaveFuelCapacitorAndClaimsUnchanged(bool range)
    {
        using SovereigntyCaptureFixture fixture = new();
        long module = fixture.Fit();
        long fuel = fixture.Grant(SovereigntyCaptureFixture.Strontium, 2);
        long hub = fixture.Deploy();
        if (range)
        {
            fixture.Lock(hub);
            fixture.Position(new(2_001_000_000, 0, 0));
        }
        double before = fixture.Capacitor();
        Assert.Throws<InvalidDataException>(() =>
            fixture.Entosis.Activate(SovereigntyCaptureFixture.PilotId, fixture.Ship(), module, hub, 10, fixture.Clock.Now)
        );
        Assert.Equal(2, fixture.Quantity(fuel));
        Assert.Equal(before, fixture.Capacitor());
        Assert.Empty(fixture.Entosis.Cycles(SovereigntyCaptureFixture.PilotId));
        Assert.Null(fixture.Claims.Read(SovereigntyCaptureFixture.SystemId));
    }

    [Fact]
    public void CompetingAlliancesPauseUntilTheOpponentsPaidCycleEnds()
    {
        using SovereigntyCaptureFixture fixture = new();
        long first = fixture.Fit();
        long second = fixture.Fit(pilot: SovereigntyCaptureFixture.OtherPilotId);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 10);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 10, pilot: SovereigntyCaptureFixture.OtherPilotId);
        long hub = fixture.Deploy();
        fixture.Position(new(2_000_000_000, 0, 0), SovereigntyCaptureFixture.OtherPilotId);
        fixture.Lock(hub);
        fixture.Lock(hub, SovereigntyCaptureFixture.OtherPilotId);
        fixture.Entosis.Activate(SovereigntyCaptureFixture.PilotId, fixture.Ship(), first, hub, 10, fixture.Clock.Now);
        fixture.Entosis.Activate(
            SovereigntyCaptureFixture.OtherPilotId,
            fixture.Ship(SovereigntyCaptureFixture.OtherPilotId),
            second,
            hub,
            10,
            fixture.Clock.Now
        );
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
        fixture.Entosis.Renew(SovereigntyCaptureFixture.PilotId, first, fixture.Clock.Now);
        fixture.Entosis.Renew(SovereigntyCaptureFixture.OtherPilotId, second, fixture.Clock.Now);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(30));
        var contested = fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!;
        Assert.True(contested.Capture.Blocked);
        Assert.Equal(0, contested.Capture.ProgressTicks);
        fixture.Entosis.Stop(SovereigntyCaptureFixture.OtherPilotId, second, fixture.Clock.Now);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(90));
        fixture.Entosis.Renew(SovereigntyCaptureFixture.PilotId, first, fixture.Clock.Now);
        Assert.Null(fixture.Entosis.Renew(SovereigntyCaptureFixture.OtherPilotId, second, fixture.Clock.Now));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(30));
        var scoring = fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!;
        Assert.False(scoring.Capture.Blocked);
        Assert.Equal(fixture.AllianceId, scoring.Capture.ControllingAllianceId);
        Assert.Equal(TimeSpan.FromSeconds(30).Ticks, scoring.Capture.ProgressTicks);
    }

    [Fact]
    public void DisconnectStopsScoringWithoutRefundingOrReleasingThePaidReservation()
    {
        using SovereigntyCaptureFixture fixture = new();
        long module = fixture.Fit();
        long fuel = fixture.Grant(SovereigntyCaptureFixture.Strontium, 10);
        long hub = fixture.Deploy();
        fixture.Lock(hub);
        fixture.Entosis.Activate(SovereigntyCaptureFixture.PilotId, fixture.Ship(), module, hub, 10, fixture.Clock.Now);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
        var renewal = fixture.Entosis.Renew(SovereigntyCaptureFixture.PilotId, module, fixture.Clock.Now)!;
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(30));
        fixture.Entosis.Disconnect(SovereigntyCaptureFixture.PilotId, fixture.Clock.Now);
        Assert.NotNull(fixture.Entosis.SpeedLimit(fixture.Ship(), fixture.Clock.Now));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromHours(1));
        Assert.Equal(TimeSpan.FromSeconds(30).Ticks, fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Capture.ProgressTicks);
        Assert.Equal(8, fixture.Quantity(fuel));
        Assert.Null(fixture.Entosis.Renew(SovereigntyCaptureFixture.PilotId, module, fixture.Clock.Now));
        fixture.Lock(hub);
        var restarted = fixture.Entosis.Activate(SovereigntyCaptureFixture.PilotId, fixture.Ship(), module, hub, 0, fixture.Clock.Now);
        Assert.Equal(restarted.Cycle.EndsAt, restarted.Cycle.WarmupUntil);
    }

    [Fact]
    public void WarmReadsAndRepeatedAdvancementUseNoSqlConnectionsOrFittingRebuilds()
    {
        using SovereigntyCaptureFixture fixture = new();
        long module = fixture.Fit();
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 10);
        long hub = fixture.Deploy();
        fixture.Lock(hub);
        fixture.Entosis.Activate(SovereigntyCaptureFixture.PilotId, fixture.Ship(), module, hub, 10, fixture.Clock.Now);
        _ = fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId);
        using var metrics = SessionPerformance.Begin("warm Entosis frame", _ => { }, force: true)!;
        for (int frame = 0; frame < 8; frame++)
        {
            fixture.Clock.AdvanceWallTime(TimeSpan.FromMilliseconds(100));
            fixture.Entosis.Advance(SovereigntyCaptureFixture.SystemId, fixture.Clock.Now);
            Assert.Equal(hub, fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.HubId);
            Assert.NotNull(fixture.Entosis.SpeedLimit(fixture.Ship(), fixture.Clock.Now));
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }
}
