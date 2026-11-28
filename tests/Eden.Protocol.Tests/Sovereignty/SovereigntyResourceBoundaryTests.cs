using Eden.Data.World.Sovereignty;
using Eden.Data.World.Structures;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Structures;
using Eden.Protocol.Flight;
using Eden.Protocol.Gateway;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Client = Eden.Protocol.Tests.PilotSessions.CourierServiceTests.Client;
using Skyhook = Eden.Protocol.Gateway.Sovereignty.SkyhookApi;
using Stars = Eden.Protocol.Gateway.CelestialResources.Stars;

namespace Eden.Protocol.Tests.Sovereignty;

public sealed class SovereigntyResourceBoundaryTests
{
    [Fact]
    public void LocalSkyhooksUseTheDockedOrUndockSystemInsteadOfTheSavedNpcStation()
    {
        const long pilot = SovereigntyCaptureFixture.PilotId;
        const int system = SovereigntyCaptureFixture.SystemId;
        using SovereigntyCaptureFixture fixture = new();
        var hook = SovereigntyUpgradeTests.Deploy(fixture, workforce: false);
        var resources = fixture.World.Services.GetRequiredService<ISovereigntyResources>();
        var gateway = fixture.World.Services.GetRequiredService<GatewayRequests>();
        long ship = fixture.Ship();
        long structureId = hook.Id + 1;
        fixture.World.Store.SetPosition(ship, null);
        int station;
        using (var action = fixture.Live.Economy.Begin(new(ships: [ship])))
        {
            var hull = action.Ships.Find(ship)!;
            station = hull.StationId;
            hull.StructureId = structureId;
            action.Add(
                new SavedStructure
                {
                    Id = structureId,
                    TypeId = 35832,
                    CorporationId = fixture.CorporationId,
                    SystemId = system,
                    Name = "Skyhook Test Structure",
                    State = StructureState.ShieldVulnerable,
                }
            );
            action.Commit();
        }

        AssertLocal();
        Assert.Throws<InvalidDataException>(() => resources.CollectReagents(pilot, hook.Id, 1));

        var database = new WorldDatabase(fixture.World.Path);
        using (CatalogDbContext catalog = new(database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSql($"UPDATE Station SET SolarSystemId={system} WHERE Id={station}");
        }
        var orbit = fixture.Live.Economy.Inspect(world => world.Set<SavedStructure>().Find(hook.Id)!);
        fixture.World.Store.SetPosition(ship, new SpacePosition(orbit.X + 1000, orbit.Y, orbit.Z));
        Assert.Null(fixture.Live.Economy.Inspect(world => world.Positions.Find(ship)!.SystemId));
        AssertLocal();
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(90));
        long before = resources.Skyhook(pilot, hook.Id).Secured;
        Assert.True(before > 0);
        var collected = resources.CollectReagents(pilot, hook.Id, 1);
        Assert.Equal(before - 1, collected.Skyhook.Secured);
        Assert.Equal(1, Assert.Single(collected.Changes).Item.Quantity);

        void AssertLocal()
        {
            var response = gateway.Handle(SovereigntyResourceProtocolTests.Request(new Skyhook.GetAllLocalRequest()), 1000001);
            Assert.Equal(200u, response.StatusCode);
            var local = response.Payload.Unpack<Skyhook.GetAllLocalResponse>();
            Assert.Equal((ulong)system, local.SolarSystem.Sequential);
            Assert.Equal((ulong)hook.Id, Assert.Single(local.Skyhooks).Skyhook_.Sequential);
        }
    }

    [Fact]
    public void NativeSkyhookMenuPersistsOrbitalStatesAndPublishesTheDamageLayers()
    {
        using SovereigntyCaptureFixture f = new();
        var hook = SovereigntyUpgradeTests.Deploy(f, workforce: true);
        var resources = f.World.Services.GetRequiredService<ISovereigntyResources>();
        var contacts = f.World.Services.GetRequiredService<Eden.Gameplay.Structures.IStructureContacts>();
        using Client gm = new(f.World, f.Clock, 1000001, PilotRole.WorldMod);
        gm.Refresh();
        foreach (int state in new[] { 0, 210, 211, 212, 213, 214 })
        {
            Assert.Equal(PilotCallResult.None, gm.Call("SlashCmd", [$"/skyhook state {hook.Id} {state}"], "slash"));
            Assert.Equal((SkyhookState)state, resources.Skyhook(SovereigntyCaptureFixture.PilotId, hook.Id).State);
            var contact = Assert.Single(contacts.Contacts(SovereigntyCaptureFixture.SystemId), item => item.Id == hook.Id);
            var slim = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(new StaticSlimItem(contact))));
            var fields = BlueRead.Fields(slim.State);
            Assert.Equal((long)state, fields["skyhook_state"]);
            if (state >= 211)
            {
                var damage = Assert.IsType<List<object?>>(fields["skyhook_damage"]);
                Assert.Equal(new object?[] { 0d, state >= 213 ? 0d : 1d, 1d }, damage);
            }
        }
        Assert.IsType<PilotCallResult.Failure>(gm.Call("SlashCmd", [$"/skyhook state {hook.Id} 110"], "slash"));
    }

    [Fact]
    public async Task CompletedSkyhookCyclesHarvestOncePauseAndCheckpointWithoutDuplicatingWithdrawals()
    {
        using SovereigntyCaptureFixture fixture = new();
        SovereigntyUpgradeTests.Claim(fixture);
        var resources = fixture.World.Services.GetRequiredService<ISovereigntyResources>();
        var hook = SovereigntyUpgradeTests.Deploy(fixture, workforce: false);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(60));
        resources.SetSkyhookActive(SovereigntyCaptureFixture.PilotId, hook.Id, true);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(30) - TimeSpan.FromTicks(1));
        Assert.Equal(0, resources.Skyhook(SovereigntyCaptureFixture.PilotId, hook.Id).Secured);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        hook = resources.Skyhook(SovereigntyCaptureFixture.PilotId, hook.Id);
        Assert.Equal(321, hook.Secured);
        Assert.Equal(322, hook.Surplus);
        var point = fixture.Live.Economy.Inspect(world => world.Set<Eden.Data.World.Structures.SavedStructure>().Find(hook.Id)!);
        fixture.Position(new(point.X + 1000, point.Y, point.Z));
        Assert.Throws<UnauthorizedAccessException>(() => resources.CollectReagents(SovereigntyCaptureFixture.OtherPilotId, hook.Id, 1));
        resources.CollectReagents(SovereigntyCaptureFixture.PilotId, hook.Id, 1);
        resources.CollectReagents(SovereigntyCaptureFixture.PilotId, hook.Id, 1);
        Assert.Equal(319, resources.Skyhook(SovereigntyCaptureFixture.PilotId, hook.Id).Secured);
        resources.SetSkyhookActive(SovereigntyCaptureFixture.PilotId, hook.Id, false);
        await fixture.Live.FlushAsync();
        fixture.World.Services.Dispose();
        fixture.Clock.AdvanceWallTime(TimeSpan.FromDays(1));
        using var reopened = Eden.Server.ServerServices.Build(new(fixture.World.Path), fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<ISovereigntyResources>();
        Assert.Equal(319, restored.Skyhook(SovereigntyCaptureFixture.PilotId, hook.Id).Secured);
        restored.SetSkyhookActive(SovereigntyCaptureFixture.PilotId, hook.Id, true);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromHours(1));
        Assert.Equal(640, restored.Skyhook(SovereigntyCaptureFixture.PilotId, hook.Id).Secured);
        var live = reopened.GetRequiredService<LiveWorld>();
        Assert.Equal(
            2,
            live.Economy.Inspect(world =>
                world
                    .Set<Eden.Data.World.Inventory.OwnedItem>()
                    .Where(row => row.TypeId == hook.ReagentTypeId && row.OwnerId == SovereigntyCaptureFixture.PilotId)
                    .Sum(row => row.Quantity)
            )
        );
    }

    [Fact]
    public void ReagentCapsAndLongCallbacksMatchRepeatedCompletedCycles()
    {
        using SovereigntyCaptureFixture fixture = new();
        var hook = SovereigntyUpgradeTests.Deploy(fixture, workforce: false);
        var resources = fixture.World.Services.GetRequiredService<ISovereigntyResources>();
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(30));
        for (int cycle = 0; cycle < 6; cycle++)
        {
            fixture.Clock.AdvanceWallTime(TimeSpan.FromHours(1));
            resources.Skyhook(SovereigntyCaptureFixture.PilotId, hook.Id);
        }
        Assert.Equal(1926, resources.Skyhook(SovereigntyCaptureFixture.PilotId, hook.Id).Secured);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromDays(1000));
        hook = resources.Skyhook(SovereigntyCaptureFixture.PilotId, hook.Id);
        Assert.Equal(hook.Resources!.Reagent!.SecuredCapacity, hook.Secured);
        Assert.Equal(hook.Resources.Reagent.UnsecuredCapacity, hook.Surplus);
        Assert.Equal(hook, resources.Skyhook(SovereigntyCaptureFixture.PilotId, hook.Id));
    }

    [Fact]
    public void StellarResourcesAndSkyhookSlimUsePinnedIdentitiesAndOrbitalState()
    {
        using SovereigntyCaptureFixture fixture = new();
        var hook = SovereigntyUpgradeTests.Deploy(fixture, workforce: true);
        var gateway = fixture.World.Services.GetRequiredService<GatewayRequests>();
        var response = gateway.Handle(SovereigntyResourceProtocolTests.Request(new Stars.GetAllConfigurationsRequest()));
        var star = Assert.Single(
            response.Payload.Unpack<Stars.GetAllConfigurationsResponse>().Configurations,
            row => row.Star.Sequential == 40252918
        );
        Assert.Equal(900UL, star.Power);
        Assert.DoesNotContain(
            fixture.World.Services.GetRequiredService<Eden.Gameplay.Maps.ICelestialResourceDirectory>().Read().Planets,
            row => row.PlanetId == 40252918
        );
        var body = fixture
            .World.Services.GetRequiredService<Eden.Gameplay.Structures.IStructureContacts>()
            .Contacts(SovereigntyCaptureFixture.SystemId)
            .Single(row => row.Id == hook.Id);
        var slim = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(new StaticSlimItem(body))));
        Assert.Equal(hook.PlanetId, BlueRead.Fields(slim.State)["planetID"]);
        Assert.Equal(210L, BlueRead.Fields(slim.State)["skyhook_state"]);
        Assert.Equal(false, BlueRead.Tuple(BlueRead.Fields(slim.State)["component_activate"], 2)[0]);
    }

    [Fact]
    public async Task Version182AddsResourceTablesAndExplorationSlotsWithoutInventingInstallations()
    {
        using SovereigntyCaptureFixture fixture = new();
        long hub = SovereigntyUpgradeTests.Claim(fixture);
        await fixture.Live.FlushAsync();
        fixture.World.Services.Dispose();
        using (WorldDbContext prior = new(fixture.World.Path))
        {
            prior.Database.ExecuteSqlRaw(
                """
                DROP TABLE WorldSovereigntyResourceHub;
                DROP TABLE WorldSovereigntyUpgrade;
                DROP TABLE WorldSovereigntyFuel;
                DROP TABLE WorldSovereigntyWorkforce;
                DROP TABLE WorldSovereigntyWorkforceSource;
                DROP TABLE WorldSovereigntySkyhook;
                DROP TABLE WorldSovereigntyMiningLink;
                ALTER TABLE WorldExplorationSite DROP COLUMN SovereigntySlot;
                PRAGMA user_version=182;
                """
            );
        }
        WorldInitialization.Initialize(new(fixture.World.Path));
        using var reopened = Eden.Server.ServerServices.Build(new(fixture.World.Path), fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<ISovereigntyResources>().Read(SovereigntyCaptureFixture.PilotId, hub);
        Assert.Empty(restored.Allocation.Upgrades);
        Assert.Empty(restored.Allocation.Fuels);
        Assert.Equal(900, restored.Local.Power);
        Assert.Empty(reopened.GetRequiredService<ISovereigntyResources>().Skyhooks(SovereigntyCaptureFixture.PilotId));
    }
}
