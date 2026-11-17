using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Sovereignty;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Sovereignty;

public sealed class SovereigntyUpgradeTests
{
    [Fact]
    public void RealInventoryInstallationUsesStellarAndSkyhookResourcesAndRejectsDuplicateDebits()
    {
        using SovereigntyCaptureFixture fixture = new();
        long hub = Claim(fixture);
        var resources = fixture.World.Services.GetRequiredService<ISovereigntyResources>();
        Assert.Equal(900, resources.Read(SovereigntyCaptureFixture.PilotId, hub).Local.Power);
        Assert.Equal(0, resources.Read(SovereigntyCaptureFixture.PilotId, hub).Local.Workforce);
        var hook = Deploy(fixture, workforce: true);
        Assert.False(hook.Active);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(30));
        Assert.True(resources.Skyhook(SovereigntyCaptureFixture.PilotId, hook.Id).Active);
        MoveToHub(fixture, hub);
        long item = fixture.Grant(82499, 2);
        var configured = resources.Configure(SovereigntyCaptureFixture.PilotId, hub, [item], [new(82499, true)]);
        Assert.Equal(1, fixture.Quantity(item));
        Assert.Equal(1, Assert.Single(configured.Changes).Item.Quantity);
        Assert.Equal(SovereigntyUpgradePower.Online, Assert.Single(configured.Hub.Allocation.Upgrades).State);
        Assert.True(configured.Hub.Local.Workforce >= 1800);
        Assert.Throws<InvalidDataException>(() => resources.Configure(SovereigntyCaptureFixture.PilotId, hub, [item], [new(82499, true)]));
        Assert.Equal(1, fixture.Quantity(item));
        Assert.Throws<UnauthorizedAccessException>(() =>
            resources.Configure(SovereigntyCaptureFixture.OtherPilotId, hub, [], [new(82499, false)])
        );
        Assert.Equal(
            SovereigntyUpgradePower.Online,
            Assert.Single(resources.Read(SovereigntyCaptureFixture.PilotId, hub).Allocation.Upgrades).State
        );
        resources.SetSkyhookActive(SovereigntyCaptureFixture.PilotId, hook.Id, false);
        Assert.Equal(
            SovereigntyUpgradePower.Low,
            Assert.Single(resources.Read(SovereigntyCaptureFixture.PilotId, hub).Allocation.Upgrades).State
        );
        resources.SetSkyhookActive(SovereigntyCaptureFixture.PilotId, hook.Id, true);
        Assert.Equal(
            SovereigntyUpgradePower.Online,
            Assert.Single(resources.Read(SovereigntyCaptureFixture.PilotId, hub).Allocation.Upgrades).State
        );
    }

    [Fact]
    public async Task StartupAndFractionalFuelBurnCheckpointWithoutRechargingOrOfflineReplay()
    {
        using SovereigntyCaptureFixture fixture = new();
        long hub = Claim(fixture);
        Deploy(fixture, workforce: true);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(30));
        MoveToHub(fixture, hub);
        var resources = fixture.World.Services.GetRequiredService<ISovereigntyResources>();
        long installation = fixture.Grant(81615);
        var pending = resources.Configure(SovereigntyCaptureFixture.PilotId, hub, [installation], [new(81615, true)]);
        Assert.Equal(SovereigntyUpgradePower.Pending, Assert.Single(pending.Hub.Allocation.Upgrades).State);
        long fuel = fixture.Grant(81143, 62410);
        var started = resources.AddFuel(SovereigntyCaptureFixture.PilotId, hub, fuel, 62410).Hub;
        Assert.Equal(410, Assert.Single(started.Allocation.Fuels).Amount);
        Assert.Equal(SovereigntyUpgradePower.Online, Assert.Single(started.Allocation.Upgrades).State);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(.5));
        var fraction = resources.Read(SovereigntyCaptureFixture.PilotId, hub);
        Assert.True(Assert.Single(fraction.Allocation.Fuels).Remainder > 0);
        await fixture.Live.FlushAsync();
        fixture.World.Services.Dispose();
        fixture.Clock.AdvanceWallTime(TimeSpan.FromHours(1) - TimeSpan.FromSeconds(.5));
        using var reopened = Eden.Server.ServerServices.Build(new(fixture.World.Path), fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<ISovereigntyResources>();
        var after = restored.Read(SovereigntyCaptureFixture.PilotId, hub);
        Assert.Equal(205, Assert.Single(after.Allocation.Fuels).Amount);
        Assert.Equal(0, Assert.Single(after.Allocation.Fuels).Remainder);
        Assert.Equal(SovereigntyUpgradePower.Online, Assert.Single(after.Allocation.Upgrades).State);
        restored.Configure(SovereigntyCaptureFixture.PilotId, hub, [], [new(81615, false)]);
        var restarted = restored.Configure(SovereigntyCaptureFixture.PilotId, hub, [], [new(81615, true)]);
        Assert.Equal(SovereigntyUpgradePower.Pending, Assert.Single(restarted.Hub.Allocation.Upgrades).State);
        Assert.Equal(205, Assert.Single(restarted.Hub.Allocation.Fuels).Amount);
    }

    [Fact]
    public void WarmResourceAndUpgradeReadsUseNoSqlConnectionsOrFittingCalculations()
    {
        using SovereigntyCaptureFixture fixture = new();
        long hub = Claim(fixture);
        var resources = fixture.World.Services.GetRequiredService<ISovereigntyResources>();
        resources.Read(SovereigntyCaptureFixture.PilotId, hub);
        using var metrics = SessionPerformance.Begin("warm sovereignty resources", _ => { }, force: true)!;
        for (int read = 0; read < 4; read++)
        {
            resources.Read(SovereigntyCaptureFixture.PilotId, hub);
            resources.Definitions();
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public async Task WorkforceTransitChangesSettleBothSystemsAndSurviveReopen()
    {
        using SovereigntyCaptureFixture fixture = new();
        long source = Claim(fixture);
        const int transitSystem = 30003991,
            destinationSystem = 30003990;
        SovereigntyDevelopmentTests.AddClaim(fixture, transitSystem);
        SovereigntyDevelopmentTests.AddClaim(fixture, destinationSystem);
        long transit = fixture.Entosis.Hub(transitSystem)!.HubId;
        long destination = fixture.Entosis.Hub(destinationSystem)!.HubId;
        Deploy(fixture, workforce: true);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(30));
        var resources = fixture.World.Services.GetRequiredService<ISovereigntyResources>();
        resources.ConfigureWorkforce(
            SovereigntyCaptureFixture.PilotId,
            source,
            new(SovereigntyWorkforceMode.Export, destinationSystem, 2500)
        );
        resources.ConfigureWorkforce(
            SovereigntyCaptureFixture.PilotId,
            destination,
            new(SovereigntyWorkforceMode.Import, Sources: [SovereigntyCaptureFixture.SystemId])
        );
        Assert.Equal(0, resources.Read(SovereigntyCaptureFixture.PilotId, destination).Available.Workforce);
        resources.ConfigureWorkforce(SovereigntyCaptureFixture.PilotId, transit, new(SovereigntyWorkforceMode.Transit));
        Assert.Equal(2500, resources.Read(SovereigntyCaptureFixture.PilotId, destination).Available.Workforce);
        var exported = resources.Read(SovereigntyCaptureFixture.PilotId, source);
        Assert.Equal(exported.Local.Workforce - 2500, exported.Available.Workforce);
        fixture.Position(new(1000, 0, 0), system: destinationSystem);
        resources.Configure(SovereigntyCaptureFixture.PilotId, destination, [fixture.Grant(82499)], [new(82499, true)]);
        Assert.Equal(
            SovereigntyUpgradePower.Online,
            Assert.Single(resources.Read(SovereigntyCaptureFixture.PilotId, destination).Allocation.Upgrades).State
        );
        Assert.Throws<InvalidDataException>(() =>
            resources.ConfigureWorkforce(
                SovereigntyCaptureFixture.PilotId,
                destination,
                new(SovereigntyWorkforceMode.Import, Sources: [SovereigntyCaptureFixture.SystemId, SovereigntyCaptureFixture.SystemId])
            )
        );
        Assert.Throws<UnauthorizedAccessException>(() =>
            resources.ConfigureWorkforce(SovereigntyCaptureFixture.OtherPilotId, transit, new(SovereigntyWorkforceMode.Idle))
        );
        Assert.Equal(2500, resources.Read(SovereigntyCaptureFixture.PilotId, destination).Available.Workforce);
        resources.ConfigureWorkforce(SovereigntyCaptureFixture.PilotId, transit, new(SovereigntyWorkforceMode.Idle));
        Assert.Equal(
            SovereigntyUpgradePower.Low,
            Assert.Single(resources.Read(SovereigntyCaptureFixture.PilotId, destination).Allocation.Upgrades).State
        );
        await fixture.Live.FlushAsync();
        fixture.World.Services.Dispose();
        using var reopened = Eden.Server.ServerServices.Build(new(fixture.World.Path), fixture.Clock, liveWorld: true);
        resources = reopened.GetRequiredService<ISovereigntyResources>();
        Assert.Equal(SovereigntyWorkforceMode.Export, resources.Read(SovereigntyCaptureFixture.PilotId, source).Workforce.Mode);
        Assert.Equal(0, resources.Read(SovereigntyCaptureFixture.PilotId, destination).Available.Workforce);
        resources.ConfigureWorkforce(SovereigntyCaptureFixture.PilotId, transit, new(SovereigntyWorkforceMode.Transit));
        Assert.Equal(
            SovereigntyUpgradePower.Online,
            Assert.Single(resources.Read(SovereigntyCaptureFixture.PilotId, destination).Allocation.Upgrades).State
        );
    }

    internal static long Claim(SovereigntyCaptureFixture fixture)
    {
        SovereigntyDevelopmentTests.Claim(fixture);
        return fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.HubId;
    }

    internal static void MoveToHub(SovereigntyCaptureFixture fixture, long hub)
    {
        var point = fixture.Live.Economy.Inspect(world => world.Set<Eden.Data.World.Structures.SavedStructure>().Find(hub)!);
        fixture.Position(new(point.X + 1000, point.Y, point.Z));
    }

    internal static SovereigntySkyhook Deploy(SovereigntyCaptureFixture fixture, bool workforce, bool power = false)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(fixture.World.Path).CatalogPath);
        var occupied = fixture.Live.Economy.Inspect(world => world.Set<SavedSovereigntySkyhook>().Select(row => row.PlanetId).ToHashSet());
        var resources = catalog
            .Require(CatalogData.Sde.MapSolarSystems, (long)SovereigntyCaptureFixture.SystemId)
            .PlanetIDs!.Value.Select(id => (Id: id, Resource: catalog.Read(CatalogData.Sde.PlanetResources, id)))
            .Where(row => !occupied.Contains(row.Id))
            .OrderByDescending(row => row.Resource?.Workforce ?? 0)
            .First(row =>
                workforce ? row.Resource?.Workforce >= 1800
                : power ? row.Resource?.Power > 0
                : row.Resource?.Reagent is not null
            );
        var planet = catalog.CelestialsInSystem(SovereigntyCaptureFixture.SystemId).Single(row => row.Id == resources.Id);
        fixture.Position(new(planet.X + (planet.Radius ?? 0) + 5_000_000, planet.Y, planet.Z));
        long item = fixture.Grant(SovereigntyResourceRules.SkyhookTypeId);
        return fixture
            .World.Services.GetRequiredService<ISovereigntyResources>()
            .DeploySkyhook(SovereigntyCaptureFixture.PilotId, item)
            .Skyhook;
    }
}
