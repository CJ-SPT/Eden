using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Sovereignty;
using Eden.Server;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Sovereignty;

public sealed class SovereigntyAdmIntegrationTests
{
    [Fact]
    public void ActivityChangesOnlySubsequentPaidCaptureTime()
    {
        using SovereigntyCaptureFixture fixture = new();
        SovereigntyDevelopmentTests.Claim(fixture);
        long attacker = fixture.Fit(pilot: SovereigntyCaptureFixture.OtherPilotId);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 50, pilot: SovereigntyCaptureFixture.OtherPilotId);
        long hub = fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.HubId;
        StartAttack(fixture, attacker, hub);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(60));
        SovereigntyDevelopmentTests.Queue(fixture, SovereigntyActivityKind.Industrial, 1e9);
        var first = fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!;
        Assert.Equal(1 - 60d / (595 * 3), first.Conflict!.Capture.Value, 10);
        Assert.Equal(5.5m, first.Conflict.DefenseMultiplier);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(60));
        var second = fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!;
        Assert.Equal(1 - 60d / (595 * 3) - 60d / (595 * 5.5), second.Conflict!.Capture.Value, 10);
        Assert.Equal(first.Conflict.Window, second.Conflict.Window);
    }

    [Fact]
    public async Task CampaignKeepsSourceAdmAcrossCapitalTransitionNodeLocationAndRestart()
    {
        using SovereigntyCaptureFixture fixture = new();
        SovereigntyDevelopmentTests.Claim(fixture);
        const int second = 30003990;
        SovereigntyDevelopmentTests.AddClaim(fixture, second);
        fixture.Entosis.SetCapital(SovereigntyCaptureFixture.PilotId, second);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromDays(6));
        long attacker = fixture.Fit(pilot: SovereigntyCaptureFixture.OtherPilotId);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 100, pilot: SovereigntyCaptureFixture.OtherPilotId);
        long hub = fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.HubId;
        SovereigntyCampaignTests.Reinforce(fixture, attacker, hub);
        var reinforced = fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Conflict!;
        Assert.Equal(3m, reinforced.CampaignMultiplier);
        long at = reinforced.CampaignAt!.Value;
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(at - fixture.Clock.Now));
        fixture.Entosis.AdvanceAll();
        Assert.Equal(second, fixture.Entosis.Capital(SovereigntyCaptureFixture.PilotId).CurrentSystemId);
        Assert.Equal(1.4m, fixture.Entosis.Development(SovereigntyCaptureFixture.SystemId).DefenseMultiplier);
        var node = SovereigntyCampaignTests.Nodes(fixture).First(row => row.SystemId != SovereigntyCaptureFixture.SystemId);
        fixture.Position(new(node.X + 1000, node.Y, node.Z), SovereigntyCaptureFixture.OtherPilotId, node.SystemId);
        StartAttack(fixture, attacker, node.Id);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(60));
        fixture.Entosis.AdvanceAll();
        const double nodeRegeneration = 0.03999999910593033;
        Assert.Equal(
            120 * nodeRegeneration / 235 - 60d / (235 * 3),
            SovereigntyCampaignTests.Nodes(fixture).Single(row => row.Id == node.Id).Value,
            10
        );
        await fixture.Live.FlushAsync();
        fixture.World.Services.Dispose();
        using var reopened = ServerServices.Build(new(fixture.World.Path), fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<IPilotEntosis>();
        Assert.Equal(3m, restored.Hub(SovereigntyCaptureFixture.SystemId)!.Conflict!.CampaignMultiplier);
        Assert.Equal(second, restored.Capital(SovereigntyCaptureFixture.PilotId).CurrentSystemId);
        Assert.Equal(at, restored.Hub(SovereigntyCaptureFixture.SystemId)!.Conflict!.CampaignAt);
    }

    [Fact]
    public async Task Version181AddsDevelopmentWithoutRewritingFrozenCampaignOrProgress()
    {
        using SovereigntyCaptureFixture fixture = new();
        SovereigntyDevelopmentTests.Claim(fixture);
        long at = fixture.Clock.Now + TimeSpan.TicksPerDay;
        using (var action = fixture.Live.Economy.Begin(TransactionParticipants.None))
        {
            var row = action.Set<SavedSovereigntyConflict>().Find(SovereigntyCaptureFixture.SystemId)!;
            row.Phase = SovereigntyPhase.Reinforced;
            row.CampaignAt = at;
            row.Value = 0;
            row.DefenderPoints = 60;
            action.Commit();
        }
        await fixture.Live.FlushAsync();
        fixture.World.Services.Dispose();
        using (WorldDbContext old = new(fixture.World.Path))
        {
            old.Database.ExecuteSqlRaw(
                """
                DROP TABLE WorldSovereigntyActivity;
                DROP TABLE WorldSovereigntyActivityEvent;
                DROP TABLE WorldSovereigntyCapital;
                DROP TABLE WorldSovereigntyCapitalChange;
                ALTER TABLE WorldSovereigntyConflict DROP COLUMN DevelopmentAt;
                ALTER TABLE WorldSovereigntyConflict DROP COLUMN AppliedAdm;
                ALTER TABLE WorldSovereigntyConflict DROP COLUMN WindowAdm;
                ALTER TABLE WorldSovereigntyConflict DROP COLUMN FollowingAdm;
                ALTER TABLE WorldSovereigntyConflict DROP COLUMN WindowCenter;
                ALTER TABLE WorldSovereigntyConflict DROP COLUMN NextWindowLockAt;
                ALTER TABLE WorldSovereigntyConflict DROP COLUMN ScheduledPrimeHour;
                ALTER TABLE WorldSovereigntyConflict DROP COLUMN CampaignAdm;
                PRAGMA user_version=181;
                """
            );
        }
        WorldInitialization.Initialize(new(fixture.World.Path));
        using var reopened = ServerServices.Build(new(fixture.World.Path), fixture.Clock, liveWorld: true);
        var entosis = reopened.GetRequiredService<IPilotEntosis>();
        var conflict = entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Conflict!;
        Assert.Equal(SovereigntyPhase.Reinforced, conflict.Phase);
        Assert.Equal(at, conflict.CampaignAt);
        Assert.Equal(0, conflict.Capture.Value);
        Assert.Equal(60, conflict.DefenderPoints);
        Assert.Equal(1m, conflict.CampaignMultiplier);
        Assert.Equal(0, entosis.Development(SovereigntyCaptureFixture.SystemId).MilitaryPoints);
        Assert.Empty(
            reopened.GetRequiredService<LiveWorld>().Economy.Inspect(world => world.Set<SavedSovereigntyActivityEvent>().ToArray())
        );
    }

    private static void StartAttack(SovereigntyCaptureFixture fixture, long module, long target)
    {
        if (target == fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)?.HubId)
        {
            fixture.Position(new(2_000_070_000, 0, 0), SovereigntyCaptureFixture.OtherPilotId);
        }
        fixture.Lock(target, SovereigntyCaptureFixture.OtherPilotId);
        fixture.Entosis.Activate(
            SovereigntyCaptureFixture.OtherPilotId,
            fixture.Ship(SovereigntyCaptureFixture.OtherPilotId),
            module,
            target,
            40,
            fixture.Clock.Now
        );
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
        fixture.Entosis.Renew(SovereigntyCaptureFixture.OtherPilotId, module, fixture.Clock.Now);
    }
}
