using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Reprocessing;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Tests.Persistence;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Reprocessing;

public sealed class ReprocessingTests
{
    private const long OreId = InventoryDefinitions.FirstOwnedItemId + 100;
    private const int ScorditeType = 1228;
    private const int TritaniumType = 34;
    private const int PyeriteType = 35;
    private const int SimpleOreSkill = 60377;

    private sealed class Fixture : IDisposable
    {
        public TestWorld Db { get; } = new();
        public DockedPilot Pilot => Db.Store.FindPilot(1000001)!;
        public IPilotReprocessing Store => Db.Services.GetRequiredService<IPilotReprocessing>();
        public IPilotInventory Inventory => Db.Services.GetRequiredService<IPilotInventory>();

        public Fixture(int quantity = 133)
        {
            using CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath, writable: true);
            catalog.Database.ExecuteSqlRaw("""
                INSERT INTO ItemCategory VALUES (25,'Asteroid'),(4,'Material');
                INSERT INTO ItemGroup VALUES (459,'Scordite',25),(18,'Mineral',4);
                INSERT INTO ItemType(Id,Name,GroupId) VALUES (1228,'Scordite',459),(34,'Tritanium',18),(35,'Pyerite',18),
                    (3385,'Reprocessing',257),(3389,'Reprocessing Efficiency',257),(60377,'Simple Ore Processing',257);
                INSERT INTO SourceRecord VALUES ('client/types','1228','{{"volume":0.15,"portionSize":100}}'),
                    ('client/types','34','{{"volume":0.01}}'),('client/types','35','{{"volume":0.01}}'),
                    ('client/typematerials','1228','{{"materials":[{{"materialTypeID":34,"quantity":150}},{{"materialTypeID":35,"quantity":110}}]}}'),
                    ('sde/npcStations','60003760','{{"operationID":14,"reprocessingEfficiency":0.5,"reprocessingStationsTake":0.05}}'),
                    ('client/stationoperations','14','{{"services":[5,17]}}');
                INSERT INTO DogmaAttribute VALUES (379,'refiningYieldMutator'),(790,'reprocessingSkillType');
                INSERT INTO TypeAttribute VALUES (3385,379,3),(3389,379,2),(60377,379,2),(1228,790,60377),
                    (3385,275,1),(3389,275,3),(60377,275,3);
                """);
            using WorldDbContext world = new(Db.Path);
            world.Add(new OwnedItem
            {
                Id = OreId,
                OwnerId = Pilot.PilotId,
                TypeId = ScorditeType,
                LocationId = Pilot.StationId,
                FlagId = (int)InventoryFlag.Hangar,
                Quantity = quantity
            });
            world.SaveChanges();
        }

        public ReprocessingQuotes Quote(params long[] ids) => Store.Quote(Pilot.PilotId, Pilot.StationId, Pilot.ShipId, ids.Length == 0 ? [OreId] : ids);
        public ReprocessingResult Run(params long[] ids) => Store.Reprocess(Pilot.PilotId, Pilot.StationId,
            ids.Length == 0 ? [OreId] : ids, Pilot.StationId, Pilot.PilotId, Pilot.StationId, (int)InventoryFlag.Hangar);
        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public void QuotesNativePortionsAndAppliesCatalogSkillMultipliersBeforeFlooring()
    {
        using Fixture f = new(333);
        var baseline = f.Quote();
        Assert.Equal(0.05, baseline.StationTax);
        Assert.Equal(0.5, baseline.StationEfficiency);
        Assert.Empty(f.Store.Quote(f.Pilot.PilotId, f.Pilot.StationId, f.Pilot.ShipId, []).Items);
        var quote = Assert.Single(baseline.Items);
        Assert.Equal((300, 33, 3), (quote.QuantityToProcess, quote.LeftOvers, quote.Portions));
        Assert.Equal(new RecoveredMaterial[] { new(TritaniumType, 225, 225), new(PyeriteType, 165, 165) }, quote.Recoverables);
        using (WorldDbContext world = new(f.Db.Path))
        {
            foreach (var (skill, rank, level) in new[] { ((int)ReprocessingSkill.Reprocessing, 1, 3),
                ((int)ReprocessingSkill.ReprocessingEfficiency, 3, 2), (SimpleOreSkill, 3, 4) })
            {
                world.Add(new PilotSkill { PilotId = f.Pilot.PilotId, TypeId = skill, Points = TrainedSkill.PointsForLevel(rank, level) });
            }
            world.SaveChanges();
        }
        quote = Assert.Single(f.Quote().Items);
        Assert.Equal(0.5 * 1.09 * 1.04 * 1.08, quote.Efficiency, 12);
        Assert.Equal(new[] { 275, 202 }, quote.Recoverables.Select(r => r.Quantity));
        Assert.Equal(333, f.Inventory.List(f.Pilot.PilotId, f.Pilot.StationId).Single(i => i.Id == OreId).Quantity);
    }

    [Fact]
    public void ConvertsCompleteBatchesAtomicallyAndPreservesLeftoversAcrossRestart()
    {
        using Fixture f = new();
        var result = f.Run();
        Assert.Equal(new[] { OreId }, result.ProcessedIds);
        Assert.Equal(75, result.Output[TritaniumType]);
        Assert.Equal(55, result.Output[PyeriteType]);
        Assert.Equal(3, result.Changes.Count);
        Assert.Equal(133, result.Changes[0].Previous!.Quantity);
        Assert.Equal(33, result.Changes[0].Item.Quantity);
        _ = new SqliteWorld(f.Db.Path);
        var saved = f.Inventory.List(f.Pilot.PilotId, f.Pilot.StationId);
        Assert.Equal(33, saved.Single(i => i.Id == OreId).Quantity);
        Assert.Equal(75, saved.Single(i => i.TypeId == TritaniumType).Quantity);
        Assert.Equal(55, saved.Single(i => i.TypeId == PyeriteType).Quantity);
        Assert.Throws<InvalidDataException>(() => f.Run());
        Assert.Equal(saved, f.Inventory.List(f.Pilot.PilotId, f.Pilot.StationId));
    }

    [Fact]
    public void MultipleStacksShareOneAtomicOutputAndRetainTheirOwnRemainders()
    {
        using Fixture f = new(133);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(new OwnedItem
            {
                Id = OreId + 1,
                OwnerId = f.Pilot.PilotId,
                TypeId = ScorditeType,
                LocationId = f.Pilot.StationId,
                FlagId = (int)InventoryFlag.Hangar,
                Quantity = 270,
            });
            world.SaveChanges();
        }
        var result = f.Run(OreId, OreId + 1);
        Assert.Equal(new[] { OreId, OreId + 1 }, result.ProcessedIds);
        Assert.Equal(225, result.Output[TritaniumType]);
        Assert.Equal(165, result.Output[PyeriteType]);
        var saved = f.Inventory.List(f.Pilot.PilotId, f.Pilot.StationId);
        Assert.Equal(33, saved.Single(i => i.Id == OreId).Quantity);
        Assert.Equal(70, saved.Single(i => i.Id == OreId + 1).Quantity);
        Assert.Single(saved, i => i.TypeId == TritaniumType);
        Assert.Single(saved, i => i.TypeId == PyeriteType);
    }

    [Fact]
    public void EntirelyConsumedStacksLeaveNoDatabaseRowAndOutputsMerge()
    {
        using Fixture f = new(200);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(new OwnedItem
            {
                Id = OreId + 1,
                OwnerId = f.Pilot.PilotId,
                TypeId = TritaniumType,
                LocationId = f.Pilot.StationId,
                FlagId = (int)InventoryFlag.Hangar,
                Quantity = 10
            });
            world.SaveChanges();
        }
        var result = f.Run();
        Assert.Equal(0, result.Changes.Single(c => c.Item.Id == OreId).Item.Quantity);
        Assert.Equal(160, result.Changes.Single(c => c.Item.Id == OreId + 1).Item.Quantity);
        Assert.DoesNotContain(f.Inventory.List(f.Pilot.PilotId, f.Pilot.StationId), i => i.Id == OreId);
        Assert.Throws<InvalidDataException>(() => f.Run());
    }

    [Theory]
    [InlineData(InventoryFlag.Cargo)]
    [InlineData(InventoryFlag.GeneralMiningHold)]
    public void CargoCapacityAccountsForConsumedOreAndRejectsOverflowWithoutConsumingInput(InventoryFlag inputHold)
    {
        using Fixture f = new(100);
        if (inputHold == InventoryFlag.GeneralMiningHold)
        {
            PilotFleetTests.AddVenture(f.Db);
            using WorldDbContext world = new(f.Db.Path);
            world.Ships.Find(f.Pilot.ShipId)!.TypeId = (int)StarterShipType.Venture;
            world.SaveChanges();
        }
        var pilot = f.Pilot;
        f.Inventory.Move(pilot.PilotId, [OreId], pilot.StationId, pilot.ShipId, (int)inputHold, null);
        var result = f.Store.Reprocess(pilot.PilotId, pilot.StationId, [OreId], pilot.ShipId, null, pilot.ShipId, (int)InventoryFlag.Cargo);
        Assert.Equal(130, result.Output.Values.Sum());
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(new OwnedItem
            {
                Id = OreId + 10,
                OwnerId = pilot.PilotId,
                TypeId = ScorditeType,
                LocationId = pilot.StationId,
                FlagId = (int)InventoryFlag.Hangar,
                Quantity = 10000
            });
            world.SaveChanges();
        }
        Assert.Throws<CargoCapacityException>(() => f.Store.Reprocess(pilot.PilotId, pilot.StationId, [OreId + 10],
            pilot.StationId, pilot.PilotId, pilot.ShipId, (int)InventoryFlag.Cargo));
        Assert.Equal(10000, f.Inventory.List(pilot.PilotId, pilot.StationId).Single(i => i.Id == OreId + 10).Quantity);
        Assert.Equal(130, f.Inventory.List(pilot.PilotId, pilot.ShipId).Sum(i => i.Quantity));
    }

    [Fact]
    public void RejectsForeignRemoteDuplicateUnsupportedAndUndockedRequests()
    {
        using Fixture f = new();
        var pilot = f.Pilot;
        var starter = f.Inventory.List(pilot.PilotId, pilot.StationId).Single(i => i.TypeId == InventoryDefinitions.CivilianMinerType);
        var foreign = Assert.Single(f.Inventory.List(90000002, pilot.StationId));
        Assert.Throws<InvalidDataException>(() => f.Run(OreId, foreign.Id));
        Assert.Throws<InvalidDataException>(() => f.Run(OreId, OreId));
        Assert.Throws<InvalidDataException>(() => f.Run(OreId, starter.Id));
        Assert.Empty(Assert.Single(f.Quote(starter.Id).Items).Recoverables);
        Assert.Throws<InvalidDataException>(() => f.Store.Quote(pilot.PilotId, pilot.StationId + 1, pilot.ShipId, [OreId]));
        Assert.Throws<InvalidDataException>(() => f.Store.Quote(pilot.PilotId, pilot.StationId, pilot.ShipId + 1, [OreId]));
        Assert.Throws<InvalidDataException>(() => f.Store.Reprocess(pilot.PilotId, pilot.StationId, [OreId], pilot.StationId + 1,
            pilot.PilotId, pilot.StationId, (int)InventoryFlag.Hangar));
        Assert.Throws<InvalidDataException>(() => f.Store.Reprocess(pilot.PilotId, pilot.StationId, [OreId], pilot.StationId,
            90000002, pilot.StationId, (int)InventoryFlag.Hangar));
        Assert.Throws<InvalidDataException>(() => f.Store.Reprocess(pilot.PilotId, pilot.StationId, [OreId], pilot.StationId,
            pilot.PilotId, pilot.StationId + 1, (int)InventoryFlag.Hangar));
        f.Db.Store.SetPosition(pilot.ShipId, SpacePosition.Zero);
        Assert.Throws<InvalidDataException>(() => f.Run());
        Assert.Equal(133, f.Inventory.List(pilot.PilotId, pilot.StationId).Single(i => i.Id == OreId).Quantity);
    }

    [Fact]
    public void InsufficientBatchesAndOutputStackOverflowCannotConsumeOtherStacks()
    {
        using Fixture f = new(99);
        Assert.Empty(Assert.Single(f.Quote().Items).Recoverables);
        Assert.Throws<InvalidDataException>(() => f.Run());
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(OreId)!.Quantity = 100;
            world.Add(new OwnedItem
            {
                Id = OreId + 1,
                OwnerId = f.Pilot.PilotId,
                TypeId = TritaniumType,
                LocationId = f.Pilot.StationId,
                FlagId = (int)InventoryFlag.Hangar,
                Quantity = int.MaxValue
            });
            world.SaveChanges();
        }
        Assert.Throws<OverflowException>(() => f.Run());
        Assert.Equal(100, f.Inventory.List(f.Pilot.PilotId, f.Pilot.StationId).Single(i => i.Id == OreId).Quantity);
        Assert.DoesNotContain(f.Inventory.List(f.Pilot.PilotId, f.Pilot.StationId), i => i.TypeId == PyeriteType);
    }

    [Fact]
    public void RequiresCatalogStationServiceAndSerializesNativeQuotesAndItemChanges()
    {
        using Fixture f = new();
        var pilot = f.Pilot;
        using PilotCallContext context = new(1000001, pilot.Account, [pilot], selected =>
            new(f.Db.Store, selected, TimeProvider.System, new SharedSpace(), f.Db.Objects), 1, []);
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        Assert.Same(PilotCallRouter.Unsupported, router.Call(context, "reprocessingSvc", "GetQuotes", new object?[] { new List<object?> { OreId }, pilot.ShipId }, _ => { }));
        context.Select(pilot);
        // The native client sends itemID -> packed inventory row. Its fields
        // are untrusted; only keys identify the saved authoritative inventory.
        BluePackedRow forged = new(new([("quantity", BlueColumnType.Int32)]), [], []);
        var rows = new Dictionary<long, object?> { [OreId] = forged };
        object? Bind(int station) => router.Call(context, "reprocessingSvc", "MachoBindObject",
            new object?[] { (long)station, new object?[] { "GetQuotes", new object?[] { rows, pilot.ShipId } } }, _ => { });
        Assert.Same(PilotCallRouter.Unsupported, Bind(pilot.StationId + 1));
        var bound = Assert.IsType<object?[]>(Bind(pilot.StationId));
        var reference = Assert.IsType<BlueObjectReference>(bound[0]);
        var wire = Assert.IsType<object?[]>(BlueMarshal.Decode(BlueMarshal.Encode(bound[1])));
        var efficiency = Assert.IsType<BlueDictionary>(wire[1]);
        Assert.Equal((null, (object?)0.5), Assert.Single(efficiency.Entries));
        var quote = Assert.IsType<BlueInstance>(Assert.IsType<Dictionary<long, object?>>(wire[2])[OreId]);
        Assert.Equal(100L, PilotLogin.Fields(quote.State)["quantityToProcess"]);
        var result = router.Call(context, "reprocessingSvc", "Reprocess",
            new object?[] { new List<object?> { OreId }, (long)pilot.StationId, pilot.PilotId, (long)pilot.StationId, (long)InventoryFlag.Hangar }, _ => { });
        Assert.Equal(2, Assert.IsType<object?[]>(BlueMarshal.Decode(BlueMarshal.Encode(result))).Length);
        Assert.Equal(3, context.Notifications.Count(n => n.Name == "OnItemChange"));
        context.ReleaseLocationReferences();
        Assert.Null(context.ResolveReference(reference.Id));
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw("UPDATE SourceRecord SET Data='{{\"services\":[17]}}' WHERE Dataset='client/stationoperations'");
        Assert.Throws<InvalidDataException>(() => f.Store.CheckStation(pilot.PilotId, pilot.StationId));
    }
}
