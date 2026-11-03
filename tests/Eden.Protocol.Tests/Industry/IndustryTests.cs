using System.Text.Json;
using Eden.Protocol.Docking;
using Eden.Protocol.Industry;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Industry;

public sealed class IndustryTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IDisposable
    {
        public const long BlueprintId = InventoryDefinitions.FirstOwnedItemId + 100;
        public TestWorld Db { get; } = new();
        public Clock Clock { get; } = new();
        public DockedPilot Pilot => Db.Store.FindPilot(1000001)!;
        public PilotIndustryStore Store => new(new(Db.Path), Clock);
        public Fixture()
        {
            using (CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath, writable: true))
            {
                catalog.Database.ExecuteSqlRaw("""
                    INSERT INTO ItemCategory VALUES (9,'Blueprint'),(8,'Charge'),(4,'Material');
                    INSERT INTO ItemGroup VALUES (167,'Hybrid Charge Blueprint',9),(85,'Hybrid Charge',8),(18,'Mineral',4);
                    INSERT INTO ItemType(Id,Name,GroupId) VALUES (1130,'Iron Charge S Blueprint',167),(215,'Iron Charge S',85),
                        (34,'Tritanium',18),(35,'Pyerite',18),(3380,'Industry',257),(3387,'Mass Production',257),
                        (3388,'Advanced Industry',257),(24625,'Advanced Mass Production',257);
                    INSERT INTO DogmaAttribute(Id,Name) VALUES (128,'chargeSize'),(422,'techLevel'),(440,'manufacturingTimeBonus'),
                        (450,'manufacturingSlotBonus'),(1961,'advancedIndustrySkillIndustryJobTimeBonus');
                    INSERT INTO TypeAttribute VALUES (215,128,1),(215,422,1),(3380,275,1),(3380,440,-4),(3387,275,2),(3387,450,1),
                        (3388,275,3),(3388,1961,-3),(24625,275,8),(24625,450,1);
                    INSERT INTO SourceRecord VALUES ('client/stationoperations','14','{{"services":[7,14,17]}}'),
                        ('client/types','34','{{"volume":0.01}}'),('client/types','35','{{"volume":0.01}}'),
                        ('client/types','215','{{"volume":0.0025}}'),('client/types','1130','{{"volume":0.01}}'),
                        ('client/blueprints','"1130"','{{"activities":{{"manufacturing":{{"skills":[{{"typeID":3380,"level":1}}],"materials":[{{"typeID":34,"quantity":27}},{{"typeID":35,"quantity":21}}],"products":[{{"typeID":215,"quantity":100}}],"time":300}}}},"blueprintTypeID":1130,"maxProductionLimit":600}}');
                    """);
            }
            using WorldDbContext world = new(Db.Path);
            world.Add(new PilotSkill { PilotId = Pilot.PilotId, TypeId = (int)IndustrySkill.Industry, Points = 250 });
            foreach (var (id, type, quantity) in new[] { (BlueprintId, 1130, 1), (BlueprintId + 1, 34, 27), (BlueprintId + 2, 34, 54), (BlueprintId + 3, 35, 63) })
            {
                world.Add(new OwnedItem
                {
                    Id = id,
                    OwnerId = Pilot.PilotId,
                    TypeId = type,
                    LocationId = Pilot.StationId,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = quantity
                });
            }
            world.SaveChanges();
        }
        public ManufacturingRequest Request(int runs = 1) => new(BlueprintId, 1130, Pilot.StationId, runs, 215, 288d * runs,
            new Dictionary<int, int> { [34] = 27 * runs, [35] = 21 * runs });
        public long Quantity(int type)
        {
            using WorldDbContext world = new(Db.Path);
            return world.Set<OwnedItem>().Where(i => i.OwnerId == Pilot.PilotId && i.TypeId == type).AsEnumerable().Sum(i => (long)i.Quantity);
        }
        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public void MaterialsBecomeAReadyJobThenDeliverRealAmmunitionExactlyOnceAcrossRestart()
    {
        using Fixture f = new();
        var result = f.Store.Install(f.Pilot.PilotId, f.Request());
        Assert.Equal(288, result.Job.DurationSeconds);
        Assert.Equal(100, result.Job.OutputQuantity);
        Assert.Equal(54, f.Quantity(34));
        Assert.Equal(42, f.Quantity(35));
        Assert.Equal(0, f.Quantity(215));
        Assert.Equal(result.Job.Id, f.Store.Blueprint(f.Pilot.PilotId, Fixture.BlueprintId).JobId);
        Assert.True(f.Store.Blueprint(f.Pilot.PilotId, Fixture.BlueprintId).Item.Singleton);
        Assert.Throws<InvalidDataException>(() => f.Store.Deliver(f.Pilot.PilotId, result.Job.Id, f.Pilot.SystemId));
        _ = new SqliteWorld(f.Db.Path);
        Assert.Equal(result.Job, f.Store.Job(f.Pilot.PilotId, result.Job.Id));
        f.Clock.Now = f.Clock.Now.AddSeconds(288);
        Assert.Equal(IndustryJobStatus.Ready, f.Store.Job(f.Pilot.PilotId, result.Job.Id).Status);
        var delivered = f.Store.Deliver(f.Pilot.PilotId, result.Job.Id, f.Pilot.SystemId);
        Assert.Equal(IndustryJobStatus.Delivered, delivered.Job.Status);
        Assert.Equal(100, f.Quantity(215));
        Assert.Equal(100, Assert.Single(delivered.Changes).Item.Quantity);
        Assert.Null(f.Store.Blueprint(f.Pilot.PilotId, Fixture.BlueprintId).JobId);
        Assert.Empty(f.Store.Deliver(f.Pilot.PilotId, result.Job.Id, f.Pilot.SystemId).Changes);
        _ = new SqliteWorld(f.Db.Path);
        Assert.Empty(f.Store.Jobs(f.Pilot.PilotId, false));
        Assert.Single(f.Store.Jobs(f.Pilot.PilotId, true));
        Assert.Equal(100, f.Quantity(215));
    }

    [Fact]
    public void MissingMaterialsRollBackEarlierStackConsumptionAndBlueprintChanges()
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(Fixture.BlueprintId + 3)!.Quantity = 20;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => f.Store.Install(f.Pilot.PilotId, f.Request()));
        Assert.Equal(81, f.Quantity(34));
        Assert.Equal(20, f.Quantity(35));
        Assert.False(f.Store.Blueprint(f.Pilot.PilotId, Fixture.BlueprintId).Item.Singleton);
        Assert.Empty(f.Store.Jobs(f.Pilot.PilotId, true));
    }

    [Fact]
    public void BlueprintAndSlotStayReservedUntilDeliveryOrCancellationWithoutMaterialRefund()
    {
        using Fixture f = new();
        var job = f.Store.Install(f.Pilot.PilotId, f.Request()).Job;
        var inventory = f.Db.Services.GetRequiredService<IPilotInventory>();
        Assert.Throws<InvalidDataException>(() => inventory.Move(f.Pilot.PilotId, [Fixture.BlueprintId], f.Pilot.StationId,
            f.Pilot.ShipId, (int)InventoryFlag.Cargo, null));
        Assert.Throws<InvalidDataException>(() => f.Store.Install(f.Pilot.PilotId, f.Request()));
        var cancelled = f.Store.Cancel(f.Pilot.PilotId, job.Id, f.Pilot.SystemId);
        Assert.Equal(IndustryJobStatus.Cancelled, cancelled.Job.Status);
        Assert.Equal(54, f.Quantity(34));
        Assert.Equal(0, f.Quantity(215));
        Assert.Empty(f.Store.Cancel(f.Pilot.PilotId, job.Id, f.Pilot.SystemId).Changes);
        Assert.Throws<InvalidDataException>(() => f.Store.Deliver(f.Pilot.PilotId, job.Id, f.Pilot.SystemId));
        Assert.NotNull(f.Store.Install(f.Pilot.PilotId, f.Request()).Job);
    }

    [Fact]
    public void InvalidOwnerLocationSkillsQuoteAndRunCountCannotInstallOrConsumeMaterials()
    {
        using Fixture f = new();
        foreach (var request in new[]
        {
            f.Request() with { BlueprintTypeId = 1137 }, f.Request() with { FacilityId = 1 },
            f.Request() with { Runs = 0 }, f.Request() with { Runs = int.MaxValue },
            f.Request() with { QuotedSeconds = double.NaN }, f.Request() with { QuotedSeconds = 300 },
            f.Request() with { ProductTypeId = 222 }, f.Request() with { QuotedMaterials = new Dictionary<int, int> { [34] = 1 } },
        }) { Assert.Throws<InvalidDataException>(() => f.Store.Install(f.Pilot.PilotId, request)); }
        Assert.Throws<InvalidDataException>(() => f.Store.Install(90000002, f.Request()));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<PilotSkill>().Find(f.Pilot.PilotId, (int)IndustrySkill.Industry)!.Points = 0;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => f.Store.Install(f.Pilot.PilotId, f.Request()));
        Assert.Equal(81, f.Quantity(34));
        Assert.Equal(63, f.Quantity(35));
        Assert.Empty(f.Store.Jobs(f.Pilot.PilotId, true));
    }

    [Fact]
    public async Task CompetingInstallsAndDeliveriesConsumeAndProduceOneJobOnly()
    {
        using Fixture f = new();
        async Task<IndustryResult?> Install() => await Task.Run(() =>
        {
            try { return f.Store.Install(f.Pilot.PilotId, f.Request()); }
            catch (InvalidDataException) { return null; }
        });
        var installed = Assert.Single(await Task.WhenAll(Install(), Install()), r => r is not null)!;
        Assert.Equal(54, f.Quantity(34));
        f.Clock.Now = f.Clock.Now.AddDays(1);
        var deliveries = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => f.Store.Deliver(f.Pilot.PilotId, installed.Job.Id, f.Pilot.SystemId))));
        Assert.Single(deliveries, d => d.Changes.Count > 0);
        Assert.Equal(100, f.Quantity(215));
    }

    [Fact]
    public void StackedOriginalsSplitOneForTheJobAndKeepOtherBlueprintsUsable()
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(Fixture.BlueprintId)!.Quantity = 2;
            world.SaveChanges();
        }
        var job = f.Store.Install(f.Pilot.PilotId, f.Request()).Job;
        Assert.NotEqual(Fixture.BlueprintId, job.BlueprintId);
        Assert.Equal(2, f.Quantity(1130));
        Assert.Null(f.Store.Blueprint(f.Pilot.PilotId, Fixture.BlueprintId).JobId);
        var inventory = f.Db.Services.GetRequiredService<IPilotInventory>();
        Assert.NotEmpty(inventory.Move(f.Pilot.PilotId, [Fixture.BlueprintId], f.Pilot.StationId, f.Pilot.ShipId, (int)InventoryFlag.Cargo, null));
        Assert.Equal(job.Id, f.Store.Blueprint(f.Pilot.PilotId, job.BlueprintId).JobId);
    }

    [Fact]
    public void IndustryAndAdvancedIndustryUseCatalogBonusesInBothTheJobAndNativeCharacterBrain()
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<PilotSkill>().Find(f.Pilot.PilotId, (int)IndustrySkill.Industry)!.Points = 8000;
            world.Add(new PilotSkill { PilotId = f.Pilot.PilotId, TypeId = (int)IndustrySkill.AdvancedIndustry, Points = 750 });
            world.Add(new PilotSkill { PilotId = f.Pilot.PilotId, TypeId = (int)IndustrySkill.MassProduction, Points = 500 });
            world.SaveChanges();
        }
        var bonuses = f.Store.CharacterBonuses(f.Pilot.PilotId);
        Assert.Contains(bonuses, b => b.Value == -12 && b.Attribute == DogmaAttributeId.ManufactureTimeMultiplier);
        Assert.Contains(bonuses, b => b.Value == -3 && !b.Additive);
        Assert.Contains(bonuses, b => b.Value == 1 && b.Additive);
        var job = f.Store.Install(f.Pilot.PilotId, f.Request() with { QuotedSeconds = 256 }).Job;
        Assert.Equal(256, job.DurationSeconds);
        var brain = new PilotBrain(f.Db.Services.GetRequiredService<IPilotSkillReader>(), f.Clock, f.Store).Read(f.Pilot.PilotId, f.Pilot.ShipId);
        var effects = Assert.IsType<List<object?>>(brain[1]).Cast<BlueNewObject>().Select(e => Assert.IsType<Dictionary<string, object?>>(e.State)).ToArray();
        Assert.Equal(3, effects.Length);
        Assert.All(effects, e => { Assert.Equal("M", e["modifierType"]); Assert.Equal(f.Pilot.PilotId, e["toItemID"]); });
        Assert.Contains(effects, e => Equals(e["operation"], 2) && Equals(e["value"], 1d));
        Assert.NotEmpty(BlueMarshal.Encode(brain));
    }

    [Fact]
    public void NativeJobRequestsValidateLocationsAndRefreshMonitoredMaterials()
    {
        using Fixture f = new();
        var pilot = f.Pilot;
        using PilotCallContext context = new(1000001, pilot.Account, [pilot], p => new(f.Db.Store, p, f.Clock, new(), []), 7, []);
        context.Select(pilot);
        IndustryHandler handler = new(f.Store);
        BlueNewObject location = new(BlueNewObject.IndustryLocationClass, new Dictionary<string, object?>
        {
            ["itemID"] = pilot.StationId,
            ["ownerID"] = pilot.PilotId,
            ["flagID"] = (int)InventoryFlag.Hangar,
        });
        var request = Assert.IsType<Dictionary<string, object?>>(BlueMarshal.Decode(BlueMarshal.Encode(new Dictionary<string, object?>
        {
            ["blueprintID"] = Fixture.BlueprintId,
            ["blueprintTypeID"] = 1130,
            ["facilityID"] = pilot.StationId,
            ["characterID"] = pilot.PilotId,
            ["activityID"] = (int)IndustryActivity.Manufacturing,
            ["account"] = new object?[] { pilot.PilotId, 1000L },
            ["cost"] = 0d,
            ["tax"] = 0d,
            ["time"] = 288d,
            ["runs"] = 1,
            ["productTypeID"] = null,
            ["inputLocation"] = location,
            ["outputLocation"] = location,
            ["materials"] = new Dictionary<long, object?> { [34] = 27L, [35] = 21L },
        })));
        var connected = Assert.IsType<object?[]>(handler.Call(context, "industryMonitor", "ConnectJob", [request]));
        Assert.Equal(81L, Assert.IsType<Dictionary<long, object?>>(connected[1])[34]);
        request["outputLocation"] = new BlueInstance("utillib.KeyVal", new Dictionary<string, object?>
        {
            ["itemID"] = (long)pilot.StationId,
            ["ownerID"] = 90000002L,
            ["flagID"] = (long)InventoryFlag.Hangar,
        });
        Assert.IsType<PilotCallRouter.Error>(handler.Call(context, "industryManager", "InstallJob", [request]));
        Assert.Equal(81, f.Quantity(34));
        request["outputLocation"] = request["inputLocation"];
        long jobId = Assert.IsType<long>(handler.Call(context, "industryManager", "InstallJob", [request]));
        Assert.Contains(context.Notifications, n => n.Name == "OnIndustryJob");
        handler.Advance(context);
        var materials = Assert.Single(context.Notifications, n => n.Name == "OnIndustryMaterials");
        Assert.Equal(54L, Assert.IsType<Dictionary<long, object?>>(materials.Arguments[1])[34]);
        Assert.NotEmpty(BlueMarshal.Encode(handler.Call(context, "industryManager", "GetJobsByOwner", [pilot.PilotId, false])));
        Assert.IsType<PilotCallRouter.Error>(handler.Call(context, "industryManager", "GetJob", [jobId + 1]));
        Assert.Null(handler.Call(context, "industryMonitor", "DisconnectJob", [connected[0]]));
        Assert.False(handler.IsMonitoring);
    }

    [Fact]
    public void ManufacturingSlotsApplyAcrossDifferentOriginalsAndCatalogMassProductionBonuses()
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(Fixture.BlueprintId)!.Quantity = 2;
            world.SaveChanges();
        }
        f.Store.Install(f.Pilot.PilotId, f.Request());
        Assert.Throws<InvalidDataException>(() => f.Store.Install(f.Pilot.PilotId, f.Request()));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(new PilotSkill { PilotId = f.Pilot.PilotId, TypeId = (int)IndustrySkill.MassProduction, Points = 500 });
            world.SaveChanges();
        }
        f.Store.Install(f.Pilot.PilotId, f.Request());
        Assert.Equal(2, f.Store.Jobs(f.Pilot.PilotId, false).Count);
        Assert.Equal(27, f.Quantity(34));
        Assert.Equal(21, f.Quantity(35));
    }

    [Fact]
    public void ExactThirtyDayMaximumDoesNotPermitAnAdditionalRun()
    {
        using Fixture f = new();
        const int runs = 9000;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(Fixture.BlueprintId + 1)!.Quantity = 27 * runs;
            world.Set<OwnedItem>().Find(Fixture.BlueprintId + 3)!.Quantity = 21 * runs;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => f.Store.Install(f.Pilot.PilotId, f.Request(runs + 1)));
        Assert.Empty(f.Store.Jobs(f.Pilot.PilotId, true));
        var job = f.Store.Install(f.Pilot.PilotId, f.Request(runs)).Job;
        Assert.Equal(IndustryDefinitions.MaximumJobSeconds, job.DurationSeconds);
        Assert.Equal(900_000, job.OutputQuantity);
    }

    [Fact]
    public void BlueprintInventoryChangesInvalidateMemoizedListsOncePerNotificationFlush()
    {
        using Fixture f = new();
        var pilot = f.Pilot;
        using PilotCallContext context = new(1000001, pilot.Account, [pilot], p => new(f.Db.Store, p, f.Clock, new(), []), 7, []);
        context.Select(pilot);
        var item = f.Store.Blueprint(pilot.PilotId, Fixture.BlueprintId).Item;
        InventoryHandler.NotifyItemChange(context, new(item, null));
        InventoryHandler.NotifyItemChange(context, new(item with { Quantity = 2 }, item));
        Assert.Equal(2, context.Notifications.Count(n => n.Name == "OnItemChange"));
        Assert.Equal(pilot.PilotId, Assert.Single(context.Notifications, n => n.Name == "OnBlueprintsUpdated").Arguments[0]);
        context.Notifications.Clear();
        InventoryHandler.NotifyItemChange(context, new(item, item with { LocationId = pilot.ShipId }));
        Assert.Single(context.Notifications, n => n.Name == "OnBlueprintsUpdated");
    }

    [Fact]
    public void SharedIndustryEntryChecksBothCatalogServicesWithoutEnablingResearch()
    {
        using Fixture f = new();
        var pilot = f.Pilot;
        using PilotCallContext context = new(1000001, pilot.Account, [pilot], p => new(f.Db.Store, p, f.Clock, new(), []), 7, []);
        context.Select(pilot);
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        object? Check(StationService service, int station) => router.Call(context, "corpStationMgr", "MachoBindObject",
            new object?[] { (long)station, new object?[] { "DoStandingCheckForStationService", new object?[] { (long)service } } }, _ => { });
        Assert.IsType<object?[]>(Check(StationService.Manufacturing, pilot.StationId));
        Assert.IsType<object?[]>(Check(StationService.Laboratory, pilot.StationId));
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE SourceRecord SET Data='{{\"services\":[7,15,17]}}' WHERE Dataset='client/stationoperations' AND Key='14'");
        }
        Assert.IsType<object?[]>(Check(StationService.Laboratory, pilot.StationId));
        Assert.Throws<InvalidDataException>(() => f.Store.Install(pilot.PilotId, f.Request()));
        Assert.Same(PilotCallRouter.Unsupported, Check(StationService.Laboratory, pilot.StationId + 1));
        Assert.Same(PilotCallRouter.Unsupported, router.Call(context, "industryManager", "ResearchJob", Array.Empty<object?>(), _ => { }));
    }

    [Fact]
    public void SchemaTwentySixMigrationRetainsEveryItemAndCreatesNoJobsOrGrants()
    {
        using Fixture f = new();
        string items;
        using (WorldDbContext world = new(f.Db.Path))
        {
            items = JsonSerializer.Serialize(world.Set<OwnedItem>().AsNoTracking().OrderBy(i => i.Id).ToArray());
            world.Database.ExecuteSqlRaw("DROP TABLE WorldIndustryJob; PRAGMA user_version=26");
        }
        _ = new SqliteWorld(f.Db.Path);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(27, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        Assert.Empty(saved.Set<IndustryJob>());
        Assert.Equal(items, JsonSerializer.Serialize(saved.Set<OwnedItem>().AsNoTracking().OrderBy(i => i.Id).ToArray()));
    }
}
