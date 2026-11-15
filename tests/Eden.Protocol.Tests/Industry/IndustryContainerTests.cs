using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Docking;
using Eden.Protocol.Industry;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Industry.IndustryTests.Fixture;

namespace Eden.Protocol.Tests.Industry;

public sealed class IndustryContainerTests
{
    private const long ContainerId = Fixture.BlueprintId + 20;
    private const int ContainerType = 17366;

    private static void Prepare(Fixture f)
    {
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO ItemGroup(Id,Name,CategoryId) VALUES (448,'Audit Log Secure Container',2);
                INSERT INTO ItemType(Id,Name,GroupId,Capacity) VALUES (17366,'Station Container',448,1000000);
                INSERT INTO SourceRecord VALUES ('client/types','17366','{{"volume":2000000}}');
                """
            );
        }
        using WorldDbContext saved = new(f.Db.Path);
        saved.Add(
            new OwnedItem
            {
                Id = ContainerId,
                OwnerId = f.Pilot.PilotId,
                LocationId = f.Pilot.StationId,
                FlagId = (int)InventoryFlag.Hangar,
                TypeId = ContainerType,
                Quantity = 1,
                Singleton = true,
            }
        );
        foreach (var item in saved.Set<OwnedItem>().Where(item => item.Id >= Fixture.BlueprintId && item.Id <= Fixture.BlueprintId + 3))
        {
            item.LocationId = ContainerId;
            item.FlagId = (int)InventoryFlag.Unlocked;
        }
        saved.SaveChanges();
    }

    private static PilotCallContext Context(Fixture f)
    {
        var pilot = f.Pilot;
        PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, f.Clock, new(), []),
            7,
            SignedSettings.Empty
        );
        context.Select(pilot);
        return context;
    }

    private static Dictionary<string, object?> Request(Fixture f)
    {
        BlueNewObject location = new(
            BlueNewObject.IndustryLocationClass,
            new Dictionary<string, object?>
            {
                ["itemID"] = ContainerId,
                ["ownerID"] = f.Pilot.PilotId,
                ["flagID"] = (long)InventoryFlag.Hangar,
            }
        );
        return new()
        {
            ["blueprintID"] = Fixture.BlueprintId,
            ["blueprintTypeID"] = 1130L,
            ["facilityID"] = (long)f.Pilot.StationId,
            ["characterID"] = f.Pilot.PilotId,
            ["activityID"] = (long)IndustryActivity.Manufacturing,
            ["account"] = new object?[] { f.Pilot.PilotId, 1000L },
            ["cost"] = 0d,
            ["tax"] = 0d,
            ["time"] = 288d,
            ["runs"] = 1L,
            ["productTypeID"] = null,
            ["inputLocation"] = location,
            ["outputLocation"] = location,
            ["materials"] = new Dictionary<long, object?> { [34] = 27L, [35] = 21L },
        };
    }

    [Fact]
    public async Task IndustryDiscoversContainerBlueprintsMonitorsInputsAndDeliversToSavedContainerAfterRestart()
    {
        using Fixture f = new();
        Prepare(f);
        long owner = f.Pilot.PilotId;
        long station = f.Pilot.StationId;
        int system = f.Pilot.SystemId;
        long jobId;
        using (var services = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true))
        using (var context = Context(f))
        {
            var containers = services.GetRequiredService<ICargoContainers>();
            IndustryHandler handler = new(f.Store, new(containers));
            var locations = Assert.IsType<BlueList<IndustryInventoryLocationReply>>(
                Assert
                    .IsType<PilotCallResult.Success>(handler.Call(context, "facilityManager", "GetFacilityLocations", [station, owner]))
                    .Reply
            );
            var location = Assert.Single(locations.Items, location => location.Location.Id == ContainerId).Location;
            Assert.Equal(ContainerType, location.TypeId);
            Assert.Equal((int)InventoryFlag.Hangar, location.FlagId);
            Assert.NotEmpty(BlueMarshal.Encode(locations));
            var blueprint = Assert.Single(f.Store.Blueprints(owner, station));
            Assert.Equal(ContainerId, blueprint.Item.LocationId);
            Assert.Equal(ContainerType, blueprint.LocationTypeId);
            Assert.Equal(station, blueprint.Facility.Id);
            var wire = Assert.IsType<BlueInstance>(new BlueprintReply(blueprint).ToBlue());
            Assert.Equal(ContainerType, Assert.IsType<Dictionary<string, object?>>(wire.State)["locationTypeID"]);
            var request = Request(f);
            var monitor = Assert.IsType<object?[]>(handler.CallWire(context, "industryMonitor", "ConnectJob", [request]));
            Assert.Equal(81L, Assert.IsType<Dictionary<long, object?>>(monitor[1])[34]);
            List<string> commands = [];
            using var measured = SessionPerformance.Begin("container industry", _ => { }, force: true, command: commands.Add)!;
            jobId = Assert.IsType<long>(handler.CallWire(context, "industryManager", "InstallJob", [request]));
            handler.Advance(context);
            var notice = Assert.Single(context.Notifications.OfType<IndustryMaterialsChanged>());
            Assert.Equal(54, notice.Materials[34]);
            Assert.Equal(42, notice.Materials[35]);
            Assert.Throws<InvalidDataException>(() =>
                containers.Move(owner, station, new([Fixture.BlueprintId], ContainerId, (int)InventoryFlag.Hangar, null), new())
            );
            Assert.DoesNotContain(commands, sql => sql.Contains("World", StringComparison.Ordinal));
            await services.GetRequiredService<LiveWorld>().FlushAsync();
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(288));
        using var reopened = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
        var industry = reopened.GetRequiredService<IPilotIndustry>();
        Assert.Equal(ContainerId, industry.Job(owner, jobId).OutputLocationId);
        var completed = industry.Deliver(owner, jobId, system);
        var output = Assert.Single(completed.Changes, change => change.Item.TypeId == 215).Item;
        Assert.Equal(ContainerId, output.LocationId);
        Assert.Equal((int)InventoryFlag.Unlocked, output.FlagId);
        Assert.Equal(100, output.Quantity);
        Assert.Empty(industry.Deliver(owner, jobId, system).Changes);
        await reopened.GetRequiredService<LiveWorld>().FlushAsync();
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(ContainerId, saved.Set<IndustryJob>().Find(jobId)!.OutputLocationId);
        Assert.Equal(ContainerId, saved.Set<OwnedItem>().Find(output.Id)!.LocationId);
    }

    [Fact]
    public void ContainerIndustryRejectsLockedMaterialsAndBlueprintsAndInvalidLocationsAtomically()
    {
        using Fixture f = new();
        Prepare(f);
        using var services = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
        var live = services.GetRequiredService<LiveWorld>();
        var request = f.Request() with
        {
            Input = new(ContainerId, (int)InventoryFlag.Hangar),
            Output = new(ContainerId, (int)InventoryFlag.Hangar),
        };
        foreach (
            var invalid in new[]
            {
                request with
                {
                    Input = new(ContainerId, (int)InventoryFlag.Unlocked),
                },
                request with
                {
                    Output = new(f.Pilot.ShipId, (int)InventoryFlag.Hangar),
                },
                request with
                {
                    QuotedSeconds = 1,
                },
            }
        )
        {
            Assert.Throws<InvalidDataException>(() => f.Store.Install(f.Pilot.PilotId, invalid));
            Assert.Empty(f.Store.Jobs(f.Pilot.PilotId, true));
            Assert.Equal(81, f.Store.Materials(f.Pilot.PilotId, f.Pilot.StationId, request.Input)[34]);
            Assert.False(f.Store.Blueprint(f.Pilot.PilotId, Fixture.BlueprintId).Item.Singleton);
        }
        using (var world = live.Economy.Begin())
        {
            world.Set<OwnedItem>().Find(Fixture.BlueprintId)!.FlagId = (int)InventoryFlag.Locked;
            world.Commit();
        }
        Assert.Throws<InvalidDataException>(() => f.Store.Install(f.Pilot.PilotId, request));
        using (var world = live.Economy.Begin())
        {
            world.Set<OwnedItem>().Find(Fixture.BlueprintId)!.FlagId = (int)InventoryFlag.Unlocked;
            world.Set<OwnedItem>().Find(Fixture.BlueprintId + 3)!.FlagId = (int)InventoryFlag.Locked;
            world.Commit();
        }
        Assert.DoesNotContain(35, f.Store.Materials(f.Pilot.PilotId, f.Pilot.StationId, request.Input).Keys);
        Assert.Throws<InvalidDataException>(() => f.Store.Install(f.Pilot.PilotId, request));
        Assert.Equal(81, f.Store.Materials(f.Pilot.PilotId, f.Pilot.StationId, request.Input)[34]);
        Assert.Empty(f.Store.Jobs(f.Pilot.PilotId, true));
    }

    [Fact]
    public void Version167UpgradePreservesHangarJobsAndDefaultsTheirOutputToHangar()
    {
        using Fixture f = new();
        long job = f.Store.Install(f.Pilot.PilotId, f.Request()).Job.Id;
        using (WorldDbContext saved = new(f.Db.Path))
        {
            saved.Database.ExecuteSqlRaw(
                "ALTER TABLE WorldIndustryJob DROP COLUMN OutputLocationId; "
                    + "ALTER TABLE WorldIndustryJob DROP COLUMN OutputFlagId; PRAGMA user_version=167;"
            );
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using var services = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(288));
        var result = services.GetRequiredService<IPilotIndustry>().Deliver(f.Pilot.PilotId, job, f.Pilot.SystemId);
        var output = Assert.Single(result.Changes).Item;
        Assert.Equal(f.Pilot.StationId, output.LocationId);
        Assert.Equal((int)InventoryFlag.Hangar, output.FlagId);
    }

    [Fact]
    public void ContainerIndustryPasswordsUseTheCurrentConnectionAuthorization()
    {
        using Fixture f = new();
        Prepare(f);
        using var services = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
        using var context = Context(f);
        var containers = services.GetRequiredService<ICargoContainers>();
        containers.SetPassword(f.Pilot.PilotId, ContainerId, CargoContainerPasswordType.General, null, "secret");
        containers.Configure(f.Pilot.PilotId, ContainerId, CargoContainerConfiguration.PasswordToOpen, new());
        CargoContainerHandler containerHandler = new(containers);
        IndustryHandler handler = new(f.Store, containerHandler);
        var request = Request(f);
        Assert.Equal(
            "SCGeneralPasswordRequired",
            Assert.IsType<PilotCallResult.NamedFailure>(handler.Call(context, "industryMonitor", "ConnectJob", [request])).Name
        );
        Assert.False(handler.IsMonitoring);
        Assert.Equal(
            "SCGeneralPasswordRequired",
            Assert.IsType<PilotCallResult.NamedFailure>(handler.Call(context, "industryManager", "InstallJob", [request])).Name
        );
        Assert.Empty(f.Store.Jobs(f.Pilot.PilotId, true));
        Assert.Same(
            PilotCallResult.None,
            containerHandler.Call(context, $"{CargoContainerHandler.InventoryPrefix}{ContainerId}", "EnterPassword", [1L, "secret"])
        );
        Assert.IsType<PilotCallResult.Success>(handler.Call(context, "industryMonitor", "ConnectJob", [request]));
        var pilot = context.Pilot;
        context.LogOff();
        context.Select(pilot);
        handler.Advance(context);
        Assert.False(handler.IsMonitoring);
        Assert.Empty(Assert.Single(context.Notifications.OfType<IndustryMaterialsChanged>()).Materials);
        Assert.Equal(
            "SCGeneralPasswordRequired",
            Assert.IsType<PilotCallResult.NamedFailure>(handler.Call(context, "industryManager", "InstallJob", [request])).Name
        );
    }

    [Fact]
    public void ContainerDeliveryChecksCurrentLocationAndExactRemainingCapacityWithoutConsumingTheJob()
    {
        using Fixture f = new();
        Prepare(f);
        using var services = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
        var live = services.GetRequiredService<LiveWorld>();
        var request = f.Request() with
        {
            Input = new(ContainerId, (int)InventoryFlag.Hangar),
            Output = new(ContainerId, (int)InventoryFlag.Hangar),
        };
        var job = f.Store.Install(f.Pilot.PilotId, request).Job;
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(288));
        using (var action = live.Economy.Begin())
        {
            action.Set<OwnedItem>().Find(ContainerId)!.LocationId = f.Pilot.ShipId;
            action.Commit();
        }
        Assert.Throws<InvalidDataException>(() => f.Store.Deliver(f.Pilot.PilotId, job.Id, f.Pilot.SystemId));
        using (var action = live.Economy.Begin())
        {
            action.Set<OwnedItem>().Find(ContainerId)!.LocationId = f.Pilot.StationId;
            action.Commit();
        }
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE ItemType SET Capacity=1.21 WHERE Id=17366");
        }
        Assert.Throws<CargoCapacityException>(() => f.Store.Deliver(f.Pilot.PilotId, job.Id, f.Pilot.SystemId));
        Assert.Equal(IndustryJobStatus.Ready, f.Store.Job(f.Pilot.PilotId, job.Id).Status);
        Assert.Empty(live.Economy.Inspect(action => action.Set<OwnedItem>().Where(item => item.TypeId == 215).ToArray()));
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE ItemType SET Capacity=1.22 WHERE Id=17366");
        }
        Assert.Equal(100, Assert.Single(f.Store.Deliver(f.Pilot.PilotId, job.Id, f.Pilot.SystemId).Changes).Item.Quantity);
    }

    [Fact]
    public void CopyingUsesAContainerBlueprintAndDeliversCopiesToTheSelectedContainer()
    {
        using Fixture f = new();
        Prepare(f);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                UPDATE SourceRecord SET Data='{{"services":[7,14,15,17]}}'
                    WHERE Dataset='client/stationoperations' AND Key='14';
                UPDATE SourceRecord SET Data=json_set(Data,'$.activities.copying',json('{{"time":240}}'))
                    WHERE Dataset='client/blueprints' AND Key='"1130"';
                """
            );
        }
        using var services = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
        var request = f.Request() with
        {
            Activity = IndustryActivity.Copying,
            ProductTypeId = null,
            QuotedSeconds = 240,
            QuotedMaterials = new Dictionary<int, int>(),
            LicensedRuns = 1,
            Output = new(ContainerId, (int)InventoryFlag.Hangar),
        };
        var job = f.Store.Install(f.Pilot.PilotId, request).Job;
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(240));
        var copy = Assert.Single(f.Store.Deliver(f.Pilot.PilotId, job.Id, f.Pilot.SystemId).Changes).Item;
        Assert.Equal(ContainerId, copy.LocationId);
        Assert.True(copy.Singleton);
        Assert.Equal(1, copy.BlueprintRuns);
        Assert.Equal(2, f.Store.Blueprints(f.Pilot.PilotId, f.Pilot.StationId).Count);
    }
}
