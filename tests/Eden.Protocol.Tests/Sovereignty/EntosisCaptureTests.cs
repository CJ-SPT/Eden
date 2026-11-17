using Eden.Data.World.Inventory;
using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Diagnostics;
using Eden.Server;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Sovereignty;

public sealed class EntosisCaptureTests
{
    [Fact]
    public void PaidWarmupAndRepeatedCyclesClaimNeutralHubAtExactCompletionTime()
    {
        using SovereigntyCaptureFixture fixture = new();
        long module = fixture.Fit();
        long fuel = fixture.Grant(SovereigntyCaptureFixture.Strontium, 10);
        long hub = fixture.Deploy();
        Assert.Equal(EntosisRules.NeutralCorporationId, fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.CorporationId);
        Assert.Null(fixture.Claims.Read(SovereigntyCaptureFixture.SystemId));
        fixture.Lock(hub);
        long started = fixture.Clock.Now;
        var activation = fixture.Entosis.Activate(SovereigntyCaptureFixture.PilotId, fixture.Ship(), module, hub, 10, started);
        Assert.Equal(TimeSpan.FromMinutes(2).Ticks, activation.Cycle.EndsAt - started);
        Assert.Equal(9, fixture.Quantity(fuel));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
        fixture.Entosis.Advance(SovereigntyCaptureFixture.SystemId, fixture.Clock.Now);
        Assert.Equal(0, fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Capture.ProgressTicks);
        Assert.NotNull(fixture.Entosis.Renew(SovereigntyCaptureFixture.PilotId, module, fixture.Clock.Now));
        for (int cycle = 0; cycle < 5; cycle++)
        {
            fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
            fixture.Entosis.Advance(SovereigntyCaptureFixture.SystemId, fixture.Clock.Now);
            if (cycle < 4)
            {
                Assert.NotNull(fixture.Entosis.Renew(SovereigntyCaptureFixture.PilotId, module, fixture.Clock.Now));
            }
        }
        var claim = Assert.IsType<SovereigntyClaim>(fixture.Claims.Read(SovereigntyCaptureFixture.SystemId));
        Assert.Equal((hub, fixture.CorporationId, fixture.AllianceId), (claim.HubId, claim.CorporationId, claim.AllianceId));
        Assert.Equal(started + TimeSpan.FromSeconds(120 + 595).Ticks, claim.ClaimedAt);
        Assert.Equal(4, fixture.Quantity(fuel));
        Assert.Null(fixture.Entosis.Renew(SovereigntyCaptureFixture.PilotId, module, fixture.Clock.Now));
        Assert.Empty(fixture.Entosis.Cycles(SovereigntyCaptureFixture.PilotId));
        fixture.Entosis.Advance(SovereigntyCaptureFixture.SystemId, fixture.Clock.Now + TimeSpan.TicksPerDay);
        Assert.Equal(claim, fixture.Claims.Read(SovereigntyCaptureFixture.SystemId));
        Assert.Equal(fixture.CorporationId, fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.CorporationId);
    }

    [Fact]
    public void MissingFuelRejectsAtomicallyWithoutCapacitorOrCycleChanges()
    {
        using SovereigntyCaptureFixture fixture = new();
        long module = fixture.Fit();
        long hub = fixture.Deploy();
        fixture.Lock(hub);
        double before = fixture.Capacitor();
        Assert.Throws<InvalidDataException>(() =>
            fixture.Entosis.Activate(SovereigntyCaptureFixture.PilotId, fixture.Ship(), module, hub, 10, fixture.Clock.Now)
        );
        Assert.Equal(before, fixture.Capacitor());
        Assert.Empty(fixture.Entosis.Cycles(SovereigntyCaptureFixture.PilotId));
        Assert.Null(fixture.Claims.Read(SovereigntyCaptureFixture.SystemId));
        Assert.False(fixture.Entosis.TravelLocked(fixture.Ship(), fixture.Clock.Now));
    }

    [Fact]
    public async Task CheckpointRestoresProgressButCannotRenewOrClaimFromOfflineLeases()
    {
        using SovereigntyCaptureFixture fixture = new();
        long module = fixture.Fit();
        long fuel = fixture.Grant(SovereigntyCaptureFixture.Strontium, 10);
        long hub = fixture.Deploy();
        fixture.Lock(hub);
        fixture.Entosis.Activate(SovereigntyCaptureFixture.PilotId, fixture.Ship(), module, hub, 10, fixture.Clock.Now);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
        fixture.Entosis.Renew(SovereigntyCaptureFixture.PilotId, module, fixture.Clock.Now);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(30));
        var before = fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!;
        Assert.Equal(TimeSpan.FromSeconds(30).Ticks, before.Capture.ProgressTicks);
        using (var captured = Assert.IsType<WorldSnapshot>(fixture.Live.CaptureSnapshot()))
        {
            fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(90));
            fixture.Entosis.Renew(SovereigntyCaptureFixture.PilotId, module, fixture.Clock.Now);
            captured.Project();
            using WorldDbContext image = new(captured.Database);
            Assert.Equal(
                before.Capture.ProgressTicks,
                image.Set<SavedSovereigntyHub>().Find(SovereigntyCaptureFixture.SystemId)!.ProgressTicks
            );
            Assert.Equal(8, image.Set<OwnedItem>().Find(fuel)!.Quantity);
            Assert.Equal(7, fixture.Quantity(fuel));
            before = fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!;
            Assert.Equal(TimeSpan.FromMinutes(2).Ticks, before.Capture.ProgressTicks);
        }
        await fixture.Live.FlushAsync();
        fixture.World.Services.Dispose();
        fixture.Clock.AdvanceWallTime(TimeSpan.FromHours(1));
        using var reopened = ServerServices.Build(new(fixture.World.Path), fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<IPilotEntosis>();
        var snapshot = restored.Hub(SovereigntyCaptureFixture.SystemId)!;
        Assert.Equal(before.Capture.ProgressTicks, snapshot.Capture.ProgressTicks);
        Assert.Null(snapshot.Claim);
        Assert.Null(restored.Renew(SovereigntyCaptureFixture.PilotId, module, fixture.Clock.Now));
        Assert.Empty(restored.Cycles(SovereigntyCaptureFixture.PilotId));
    }
}
