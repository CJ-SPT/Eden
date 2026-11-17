using Eden.Data.World.Exploration;
using Eden.Data.World.Mining;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Sovereignty;
using Eden.World.Exploration;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Sovereignty;

public sealed class SovereigntySiteUpgradeTests
{
    [Fact]
    public async Task OreUpgradeCreatesFiniteDepositsAndOnlyReplacesWhileOnlineAcrossCheckpoint()
    {
        using SovereigntyCaptureFixture fixture = new();
        long hub = SovereigntyUpgradeTests.Claim(fixture);
        SovereigntyUpgradeTests.Deploy(fixture, workforce: true);
        SovereigntyUpgradeTests.Deploy(fixture, workforce: true);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(30));
        SovereigntyUpgradeTests.MoveToHub(fixture, hub);
        var resources = fixture.World.Services.GetRequiredService<ISovereigntyResources>();
        resources.Configure(SovereigntyCaptureFixture.PilotId, hub, [fixture.Grant(82579)], [new(82579, true)]);
        var sites = fixture.World.Services.GetRequiredService<IMiningSites>();
        var site = Assert.Single(
            sites.Anomalies(SovereigntyCaptureFixture.SystemId, fixture.Clock.Now),
            row => row.Mining == MiningFamily.Ore
        );
        var deposits = fixture.Live.Economy.Inspect(world =>
            world.Set<SavedMiningDeposit>().Where(row => row.SiteId == site.Id).Select(row => row.Id).ToArray()
        );
        Assert.Equal(8, deposits.Length);
        Assert.All(deposits, id => Assert.Equal(1230, fixture.Live.Economy.Inspect(world => world.Set<WorldAsteroid>().Find(id)!.TypeId)));
        Assert.Equal(80000, fixture.Live.Economy.Inspect(world => deposits.Sum(id => world.Set<WorldAsteroid>().Find(id)!.Quantity)));
        await fixture.Live.FlushAsync();
        fixture.World.Services.Dispose();
        using var reopened = Eden.Server.ServerServices.Build(new(fixture.World.Path), fixture.Clock, liveWorld: true);
        sites = reopened.GetRequiredService<IMiningSites>();
        Assert.Equal(site.Id, Assert.Single(sites.Anomalies(site.SystemId, fixture.Clock.Now), row => row.Mining == MiningFamily.Ore).Id);
        resources = reopened.GetRequiredService<ISovereigntyResources>();
        resources.Configure(SovereigntyCaptureFixture.PilotId, hub, [], [new(82579, false)]);
        Assert.Equal(site.Id, Assert.Single(sites.Anomalies(site.SystemId, fixture.Clock.Now), row => row.Mining == MiningFamily.Ore).Id);
        var live = reopened.GetRequiredService<LiveWorld>();
        using (var action = live.Economy.Begin(new(systems: [site.SystemId])))
        {
            foreach (long id in deposits)
            {
                action.Set<WorldAsteroid>().Find(id)!.Quantity = 0;
            }
            MiningSiteStore.Collected(action, deposits[0], fixture.Clock.Now);
            action.Commit();
        }
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(MiningSiteRules.ReplacementDelay));
        Assert.DoesNotContain(sites.Anomalies(site.SystemId, fixture.Clock.Now), row => row.Mining == MiningFamily.Ore);
        resources.Configure(SovereigntyCaptureFixture.PilotId, hub, [], [new(82579, true)]);
        Assert.NotEqual(
            site.Id,
            Assert.Single(sites.Anomalies(site.SystemId, fixture.Clock.Now), row => row.Mining == MiningFamily.Ore).Id
        );
    }

    [Fact]
    public void DetectionUpgradesAddSupportedCombatAndExplorationSlotsAndStopOfflineReplacements()
    {
        using SovereigntyCaptureFixture fixture = new();
        long hub = SovereigntyUpgradeTests.Claim(fixture);
        var catalog = new WorldDatabase(fixture.World.Path).CatalogPath;
        PassiveCatalogFixture.LoadCombatAnomalyWorld(catalog);
        PassiveCatalogFixture.LoadHacking(catalog);
        SovereigntyUpgradeTests.Deploy(fixture, workforce: true);
        SovereigntyUpgradeTests.Deploy(fixture, workforce: true);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(30));
        var combat = fixture.World.Services.GetRequiredService<ICombatAnomalies>();
        var exploration = fixture.World.Services.GetRequiredService<IPirateExploration>();
        int baseline = combat.List(SovereigntyCaptureFixture.SystemId, fixture.Clock.Now).Count;
        int signatures = exploration.Signatures(SovereigntyCaptureFixture.SystemId, fixture.Clock.Now).Count;
        Assert.True(baseline > 0, $"Combat baseline {baseline}");
        Assert.True(signatures >= 2, $"Exploration baseline {signatures}");
        SovereigntyUpgradeTests.MoveToHub(fixture, hub);
        var resources = fixture.World.Services.GetRequiredService<ISovereigntyResources>();
        resources.Configure(
            SovereigntyCaptureFixture.PilotId,
            hub,
            [fixture.Grant(82499), fixture.Grant(87948)],
            [new(82499, true), new(87948, true)]
        );
        Assert.Equal(baseline + 1, combat.List(SovereigntyCaptureFixture.SystemId, fixture.Clock.Now).Count);
        Assert.Equal(signatures + 2, exploration.Signatures(SovereigntyCaptureFixture.SystemId, fixture.Clock.Now).Count);
        resources.Configure(SovereigntyCaptureFixture.PilotId, hub, [], [new(82499, false), new(87948, false)]);
        Assert.Equal(signatures + 2, exploration.Signatures(SovereigntyCaptureFixture.SystemId, fixture.Clock.Now).Count);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(HackingRules.SiteLifetime));
        Assert.Empty(exploration.Signatures(SovereigntyCaptureFixture.SystemId, fixture.Clock.Now));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(HackingRules.ReplacementDelay));
        Assert.Equal(signatures, exploration.Signatures(SovereigntyCaptureFixture.SystemId, fixture.Clock.Now).Count);
        Assert.DoesNotContain(
            fixture.Live.Economy.Inspect(world =>
                world.Set<SavedExplorationSite>().Where(row => row.SovereigntySlot > 0).Select(row => row.ReplaceAt).ToArray()
            ),
            row => row is null
        );
    }
}
