using Eden.Data.World.Alliances;
using Eden.Data.World.Sovereignty;
using Eden.Data.World.Structures;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Sovereignty;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Sovereignty;

public sealed class SovereigntyDevelopmentTests
{
    [Fact]
    public void SoleClaimBecomesCapitalAndActivityChangesLiveAdmWithoutResizingLockedWindows()
    {
        using SovereigntyCaptureFixture fixture = new();
        Claim(fixture);
        Assert.Equal(SovereigntyCaptureFixture.SystemId, fixture.Entosis.Capital(SovereigntyCaptureFixture.PilotId).CurrentSystemId);
        var original = fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Conflict!.Window;
        Assert.Equal(TimeSpan.FromHours(6).Ticks, original.EndsAt - original.StartsAt);
        Queue(fixture, SovereigntyActivityKind.Industrial, 1e9);
        var development = fixture.Entosis.Development(SovereigntyCaptureFixture.SystemId);
        Assert.Equal(5, development.Indices.Industrial);
        Assert.Equal(5.5m, development.DefenseMultiplier);
        Assert.True(development.IndustrialIncreasing);
        Assert.Equal(original, fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Conflict!.Window);
        Assert.Empty(fixture.Live.Economy.Inspect(world => world.Set<SavedSovereigntyActivityEvent>().ToArray()));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromDays(1));
        var tomorrow = fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Conflict!.Window;
        Assert.Equal(TimeSpan.FromHours(6).Ticks, tomorrow.EndsAt - tomorrow.StartsAt);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromDays(1));
        var later = fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Conflict!.Window;
        Assert.InRange(Math.Abs((later.EndsAt - later.StartsAt) - TimeSpan.FromHours(18).Ticks / 5.5), 0, 2);
        Assert.False(fixture.Entosis.Development(SovereigntyCaptureFixture.SystemId).IndustrialIncreasing);
    }

    [Fact]
    public void CapitalTransitionsRequireOwnershipWaitSevenDaysAndCanBeCancelled()
    {
        using SovereigntyCaptureFixture fixture = new();
        Claim(fixture);
        const int second = 30003990;
        AddClaim(fixture, second);
        fixture.Entosis.SetCapital(SovereigntyCaptureFixture.PilotId, second);
        var pending = fixture.Entosis.Capital(SovereigntyCaptureFixture.PilotId);
        Assert.Equal(SovereigntyCaptureFixture.SystemId, pending.CurrentSystemId);
        Assert.Equal(second, pending.NewSystemId);
        Assert.Equal(fixture.Clock.Now + TimeSpan.FromDays(7).Ticks, pending.ValidAfter);
        Assert.Throws<InvalidDataException>(() => fixture.Entosis.SetCapital(SovereigntyCaptureFixture.PilotId, 30000142));
        Assert.Equal(pending, fixture.Entosis.Capital(SovereigntyCaptureFixture.PilotId));
        fixture.Entosis.CancelCapital(SovereigntyCaptureFixture.PilotId);
        Assert.Null(fixture.Entosis.Capital(SovereigntyCaptureFixture.PilotId).NewSystemId);
        fixture.Entosis.SetCapital(SovereigntyCaptureFixture.PilotId, second);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromDays(7) - TimeSpan.FromTicks(1));
        Assert.Equal(SovereigntyCaptureFixture.SystemId, fixture.Entosis.Capital(SovereigntyCaptureFixture.PilotId).CurrentSystemId);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.Equal(second, fixture.Entosis.Capital(SovereigntyCaptureFixture.PilotId).CurrentSystemId);
        Assert.True(fixture.Entosis.Development(second).IsCapital);
        Assert.False(fixture.Entosis.Development(SovereigntyCaptureFixture.SystemId).IsCapital);
        Assert.Throws<InvalidDataException>(() => fixture.Entosis.SetCapital(SovereigntyCaptureFixture.OtherPilotId, second));
        using (var action = fixture.Live.Economy.Begin(TransactionParticipants.None))
        {
            action.Set<SavedAlliance>().Find(fixture.AllianceId)!.ExecutorCorporationId = null;
            action.Commit();
        }
        Assert.Throws<InvalidDataException>(() =>
            fixture.Entosis.SetCapital(SovereigntyCaptureFixture.PilotId, SovereigntyCaptureFixture.SystemId)
        );
        Assert.Throws<InvalidDataException>(() => fixture.Entosis.CancelCapital(SovereigntyCaptureFixture.PilotId));
        Assert.Equal(second, fixture.Entosis.Capital(SovereigntyCaptureFixture.PilotId).CurrentSystemId);
    }

    [Fact]
    public async Task PendingCreditsCheckpointTogetherAndReopenDecaysWithoutReplayingInputs()
    {
        using SovereigntyCaptureFixture fixture = new();
        Claim(fixture);
        Queue(fixture, SovereigntyActivityKind.Industrial, 1e9);
        using (var captured = Assert.IsType<WorldSnapshot>(fixture.Live.CaptureSnapshot()))
        {
            fixture.Entosis.Development(SovereigntyCaptureFixture.SystemId);
            captured.Project();
            using WorldDbContext image = new(captured.Database);
            Assert.Single(image.Set<SavedSovereigntyActivityEvent>());
            Assert.Empty(image.Set<SavedSovereigntyActivity>());
        }
        AddClaim(fixture, 30003990);
        fixture.Entosis.SetCapital(SovereigntyCaptureFixture.PilotId, 30003990);
        var pendingCapital = fixture.Entosis.Capital(SovereigntyCaptureFixture.PilotId);
        Queue(fixture, SovereigntyActivityKind.Military, 1000);
        await fixture.Live.FlushAsync();
        fixture.World.Services.Dispose();
        fixture.Clock.AdvanceWallTime(TimeSpan.FromDays(1));
        using var services = Eden.Server.ServerServices.Build(new(fixture.World.Path), fixture.Clock, liveWorld: true);
        var entosis = services.GetRequiredService<IPilotEntosis>();
        Assert.Single(
            services.GetRequiredService<LiveWorld>().Economy.Inspect(world => world.Set<SavedSovereigntyActivityEvent>().ToArray())
        );
        Assert.Equal(pendingCapital, entosis.Capital(SovereigntyCaptureFixture.PilotId));
        var development = entosis.Development(SovereigntyCaptureFixture.SystemId);
        Assert.Equal(SovereigntyDevelopmentRules.MaximumPoints - 2400000, development.IndustrialPoints, 4);
        Assert.Equal(26520000, development.MilitaryPoints, 4);
        Assert.Equal(development, entosis.Development(SovereigntyCaptureFixture.SystemId));
        Assert.Empty(
            services.GetRequiredService<LiveWorld>().Economy.Inspect(world => world.Set<SavedSovereigntyActivityEvent>().ToArray())
        );
    }

    [Fact]
    public void RejectedCommandsDiscardQueuedInputsAndWarmReadsUseNoSqlOrFits()
    {
        using SovereigntyCaptureFixture fixture = new();
        Claim(fixture);
        using (var action = fixture.Live.Economy.Begin(TransactionParticipants.None))
        {
            SovereigntyDevelopmentPersistence.Record(
                action,
                SovereigntyCaptureFixture.SystemId,
                SovereigntyActivityKind.Military,
                100,
                fixture.Clock.Now
            );
        }
        Assert.Equal(0, fixture.Entosis.Development(SovereigntyCaptureFixture.SystemId).MilitaryPoints);
        Queue(fixture, SovereigntyActivityKind.Military, 1);
        Assert.Equal(50000, fixture.Entosis.Development(SovereigntyCaptureFixture.SystemId).MilitaryPoints);
        using var metrics = SessionPerformance.Begin("warm sovereignty development", _ => { }, force: true)!;
        for (int read = 0; read < 4; read++)
        {
            fixture.Entosis.Development(SovereigntyCaptureFixture.SystemId);
            fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId);
            fixture.Entosis.Capital(SovereigntyCaptureFixture.PilotId);
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void LostPendingCapitalIsCancelledAndObservedDecayCannotRunBackward()
    {
        using SovereigntyCaptureFixture fixture = new();
        Claim(fixture);
        AddClaim(fixture, 30003990);
        fixture.Entosis.SetCapital(SovereigntyCaptureFixture.PilotId, 30003990);
        using (var action = fixture.Live.Economy.Begin(TransactionParticipants.None))
        {
            action.Remove(action.Set<SavedSovereigntyClaim>().Find(30003990)!);
            action.Commit();
        }
        var capital = fixture.Entosis.Capital(SovereigntyCaptureFixture.PilotId);
        Assert.Equal(SovereigntyCaptureFixture.SystemId, capital.CurrentSystemId);
        Assert.Null(capital.NewSystemId);
        Assert.Null(capital.ValidAfter);
        Queue(fixture, SovereigntyActivityKind.Industrial, 1e9);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromDays(1));
        var decayed = fixture.Entosis.Development(SovereigntyCaptureFixture.SystemId);
        fixture.Clock.AdvanceWallTime(-TimeSpan.FromHours(12));
        Assert.Equal(decayed, fixture.Entosis.Development(SovereigntyCaptureFixture.SystemId));
    }

    [Fact]
    public async Task MultipleCapitalChangesRetainEarlierBonusesForDelayedConstellationsAndCheckpoints()
    {
        using SovereigntyCaptureFixture fixture = new();
        Claim(fixture);
        AddClaim(fixture, 30003990);
        long before = fixture.Clock.Now + TimeSpan.FromDays(6).Ticks;
        fixture.Entosis.SetCapital(SovereigntyCaptureFixture.PilotId, 30003990);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromDays(8));
        fixture.Entosis.Capital(SovereigntyCaptureFixture.PilotId);
        long middle = fixture.Clock.Now;
        fixture.Clock.AdvanceWallTime(TimeSpan.FromDays(1));
        using (var action = fixture.Live.Economy.Begin(TransactionParticipants.None))
        {
            action.Remove(action.Set<SavedSovereigntyClaim>().Find(30003990)!);
            SovereigntyDevelopmentPersistence.ReconcileCapital(action, fixture.AllianceId, fixture.Clock.Now);
            action.Commit();
        }
        void AssertTimeline(LiveWorld live) =>
            live.Economy.Inspect(world =>
            {
                Assert.Equal(
                    SovereigntyCaptureFixture.SystemId,
                    SovereigntyDevelopmentPersistence.CapitalAt(world, fixture.AllianceId, before)
                );
                Assert.Equal(30003990, SovereigntyDevelopmentPersistence.CapitalAt(world, fixture.AllianceId, middle));
                Assert.Equal(
                    SovereigntyCaptureFixture.SystemId,
                    SovereigntyDevelopmentPersistence.CapitalAt(world, fixture.AllianceId, fixture.Clock.Now)
                );
                return true;
            });
        AssertTimeline(fixture.Live);
        await fixture.Live.FlushAsync();
        fixture.World.Services.Dispose();
        using var reopened = Eden.Server.ServerServices.Build(new(fixture.World.Path), fixture.Clock, liveWorld: true);
        AssertTimeline(reopened.GetRequiredService<LiveWorld>());
    }

    internal static void Claim(SovereigntyCaptureFixture fixture)
    {
        long module = fixture.Fit();
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 20);
        SovereigntyCampaignTests.Claim(fixture, module);
    }

    internal static void Queue(SovereigntyCaptureFixture fixture, SovereigntyActivityKind kind, double amount)
    {
        using var action = fixture.Live.Economy.Begin(TransactionParticipants.None);
        SovereigntyDevelopmentPersistence.Record(action, SovereigntyCaptureFixture.SystemId, kind, amount, fixture.Clock.Now);
        action.Commit();
    }

    internal static void AddClaim(SovereigntyCaptureFixture fixture, int system)
    {
        using (var action = fixture.Live.Economy.Begin(TransactionParticipants.None))
        {
            long hub = InventoryPersistence.NextId(action);
            action.Add(
                new SavedStructure
                {
                    Id = hub,
                    TypeId = EntosisRules.HubTypeId,
                    CorporationId = fixture.CorporationId,
                    SystemId = system,
                    State = StructureState.ShieldVulnerable,
                }
            );
            action.Add(
                new SavedSovereigntyHub
                {
                    SystemId = system,
                    HubId = hub,
                    CaptureDuration = TimeSpan.FromSeconds(595).Ticks,
                    UpdatedAt = fixture.Clock.Now,
                }
            );
            action.Add(
                new SavedSovereigntyClaim
                {
                    SystemId = system,
                    HubId = hub,
                    AllianceId = fixture.AllianceId,
                    CorporationId = fixture.CorporationId,
                    ClaimedAt = fixture.Clock.Now,
                }
            );
            action.Commit();
        }
        fixture.Entosis.Advance(system, fixture.Clock.Now);
    }
}
