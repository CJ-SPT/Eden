using Eden.Gameplay.Contracts;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

internal static class ContractAssetTestData
{
    internal const long Pilot = 90000001;
    internal const long Other = 90000002;
    internal const int Station = 60003760;
    internal const int RemoteStation = 60003761;

    internal static long Stack(TestWorld db, int quantity = 20, long location = Station, long owner = Pilot, int flag = 4)
    {
        using WorldDbContext world = new(db.Path);
        var item = world
            .Add(
                new OwnedItem
                {
                    Id = InventoryPersistence.NextId(world),
                    OwnerId = owner,
                    TypeId = InventoryDefinitions.CivilianMinerType,
                    LocationId = location,
                    FlagId = flag,
                    Quantity = quantity,
                }
            )
            .Entity;
        world.SaveChanges();
        return item.Id;
    }

    internal static long Hull(TestWorld db, long? parent = null)
    {
        using WorldDbContext world = new(db.Path);
        var ship = world
            .Add(
                new Ship
                {
                    Id = InventoryPersistence.NextId(world),
                    OwnerId = Pilot,
                    TypeId = 601,
                    StationId = Station,
                    Name = "Contract hull",
                    StoredInId = parent,
                    StorageFlagId = parent is null ? (int)InventoryFlag.Hangar : (int)InventoryFlag.ShipMaintenanceBay,
                }
            )
            .Entity;
        world.SaveChanges();
        return ship.Id;
    }

    internal static void AddRemoteStation(TestWorld db)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
            SELECT 60003761,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z FROM Station WHERE Id=60003760;
            """
        );
    }
}

public sealed class ContractAssetTests
{
    [Fact]
    public void InsertFailureRollsBackTheOriginalQuantityAndDoesNotCreateAnotherStack()
    {
        using TestWorld db = new();
        long id = ContractAssetTestData.Stack(db);
        var assets = db.Services.GetRequiredService<IPilotContractAssets>();
        var before = assets.Station(ContractAssetTestData.Pilot, ContractAssetTestData.Station);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER FailContractSplit BEFORE INSERT ON WorldItem
                BEGIN SELECT RAISE(ABORT,'Injected split failure'); END;
                """
            );
        }
        Assert.Throws<DbUpdateException>(() => assets.Split(ContractAssetTestData.Pilot, ContractAssetTestData.Station, id, 7));
        Assert.Equal(before, assets.Station(ContractAssetTestData.Pilot, ContractAssetTestData.Station));
    }

    [Fact]
    public void StationPickerExcludesActiveForeignStoredAndInstalledAssets()
    {
        using TestWorld db = new();
        var assets = db.Services.GetRequiredService<IPilotContractAssets>();
        long hull = ContractAssetTestData.Hull(db);
        long carried = ContractAssetTestData.Hull(db, hull);
        long foreign = ContractAssetTestData.Stack(db, owner: ContractAssetTestData.Other);
        long cargo = ContractAssetTestData.Stack(db, location: hull, flag: (int)InventoryFlag.Cargo);
        long installed = ContractAssetTestData.Stack(db);
        using (WorldDbContext world = new(db.Path))
        {
            world.Add(
                new IndustryJob
                {
                    PilotId = ContractAssetTestData.Pilot,
                    BlueprintId = installed,
                    StationId = ContractAssetTestData.Station,
                    Status = IndustryJobStatus.Installed,
                    Runs = 1,
                    OutputQuantity = 1,
                    DurationSeconds = 1,
                    StartTime = 1,
                    EndTime = 2,
                }
            );
            world.SaveChanges();
        }
        var rows = assets.Station(ContractAssetTestData.Pilot, ContractAssetTestData.Station);
        Assert.Contains(rows, i => i.Id == hull && i.Singleton && i.CategoryId == (int)InventoryCategory.Ship);
        Assert.DoesNotContain(rows, i => new[] { carried, foreign, cargo, installed, db.Store.FindPilot(1000001)!.ShipId }.Contains(i.Id));
        Assert.All(rows, i => Assert.Equal(ContractAssetTestData.Pilot, i.OwnerId));
        Assert.Equal(
            new ContractAssetStation(ContractAssetTestData.Station, rows.Count + 1),
            Assert.Single(assets.Stations(ContractAssetTestData.Pilot))
        );
        Assert.Throws<InvalidDataException>(() => assets.Split(ContractAssetTestData.Pilot, ContractAssetTestData.Station, installed, 1));
        Assert.Throws<InvalidDataException>(() => assets.Contents(ContractAssetTestData.Pilot, ContractAssetTestData.Station, installed));
    }

    [Fact]
    public void StationDirectoryCountsRootsAndHidesEscrowAcrossReopen()
    {
        using TestWorld db = new();
        ContractAssetTestData.AddRemoteStation(db);
        long remote = ContractAssetTestData.Stack(db, 100, ContractAssetTestData.RemoteStation);
        ContractAssetTestData.Stack(db, location: ContractAssetTestData.RemoteStation, owner: ContractAssetTestData.Other);
        long hull = ContractAssetTestData.Hull(db);
        ContractAssetTestData.Stack(db, location: hull);
        var assets = db.Services.GetRequiredService<IPilotContractAssets>();
        Assert.Equal(
            1,
            assets.Stations(ContractAssetTestData.Pilot).Single(s => s.StationId == ContractAssetTestData.RemoteStation).ItemCount
        );
        ContractTests.Fund(db, ContractAssetTestData.Pilot);
        db.Services.GetRequiredService<IPilotContracts>()
            .Create(
                ContractAssetTestData.Pilot,
                ContractTests.Draft(remote) with
                {
                    StationId = ContractAssetTestData.RemoteStation,
                    Items = [new(remote, 100)],
                }
            );
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path));
        var directory = reopened.GetRequiredService<IPilotContractAssets>();
        Assert.DoesNotContain(directory.Stations(ContractAssetTestData.Pilot), s => s.StationId == ContractAssetTestData.RemoteStation);
        Assert.Equal(
            assets.Station(ContractAssetTestData.Pilot, ContractAssetTestData.Station).Count + 1,
            Assert.Single(directory.Stations(ContractAssetTestData.Pilot)).ItemCount
        );
        Assert.Throws<InvalidDataException>(() => directory.Stations(long.MaxValue));
    }

    [Fact]
    public void NestedContentsAndCountsPreserveLocationsFlagsAndCountRowsRatherThanUnits()
    {
        using TestWorld db = new();
        var assets = db.Services.GetRequiredService<IPilotContractAssets>();
        long hull = ContractAssetTestData.Hull(db);
        long carried = ContractAssetTestData.Hull(db, hull);
        long module = ContractAssetTestData.Stack(db, 1, hull, flag: (int)InventoryFlag.HighSlot0);
        long magazine = ContractAssetTestData.Stack(db, 100, module, flag: (int)InventoryFlag.Cargo);
        long cargo = ContractAssetTestData.Stack(db, 50, carried, flag: (int)InventoryFlag.Cargo);
        long foreign = ContractAssetTestData.Stack(db, location: hull, owner: ContractAssetTestData.Other);
        long empty = ContractAssetTestData.Stack(db);
        var rows = assets.Contents(ContractAssetTestData.Pilot, ContractAssetTestData.Station, hull);
        Assert.Equal(new[] { carried, module, magazine, cargo }.Order(), rows.Select(i => i.Id));
        Assert.Equal(module, rows.Single(i => i.Id == magazine).LocationId);
        Assert.Equal((int)InventoryFlag.HighSlot0, rows.Single(i => i.Id == module).FlagId);
        Assert.Equal(100, rows.Single(i => i.Id == magazine).Quantity);
        Assert.DoesNotContain(rows, i => i.Id == foreign);
        Assert.Equal(
            new[] { new ContractContainerCount(hull, 4), new ContractContainerCount(empty, 0) },
            assets.Counts(ContractAssetTestData.Pilot, ContractAssetTestData.Station, [hull, empty])
        );
        Assert.Empty(assets.Counts(ContractAssetTestData.Pilot, ContractAssetTestData.Station, []));
        Assert.Empty(assets.Contents(ContractAssetTestData.Pilot, ContractAssetTestData.Station, empty));
        Assert.Throws<InvalidDataException>(() => assets.Contents(ContractAssetTestData.Pilot, ContractAssetTestData.Station, carried));
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path));
        Assert.Equal(
            rows,
            reopened.GetRequiredService<IPilotContractAssets>().Contents(ContractAssetTestData.Pilot, ContractAssetTestData.Station, hull)
        );
    }

    [Fact]
    public void RemoteHangarSplitPreservesTotalAndMetadataAcrossRestartWithoutMergingOrReusingDeletedIds()
    {
        using TestWorld db = new();
        ContractAssetTestData.AddRemoteStation(db);
        long id = ContractAssetTestData.Stack(db, location: ContractAssetTestData.RemoteStation);
        long high = ContractAssetTestData.Stack(db);
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<OwnedItem>().Find(id)!.CrystalDamage = 0.25;
            world.Remove(world.Set<OwnedItem>().Find(high)!);
            world.SaveChanges();
        }
        var assets = db.Services.GetRequiredService<IPilotContractAssets>();
        var changes = assets.Split(ContractAssetTestData.Pilot, ContractAssetTestData.RemoteStation, id, 7);
        Assert.Equal(2, changes.Count);
        Assert.Equal(20, changes[0].Previous!.Quantity);
        Assert.Equal(13, changes[0].Item.Quantity);
        Assert.Null(changes[1].Previous);
        Assert.Equal(7, changes[1].Item.Quantity);
        Assert.True(changes[1].Item.Id > high);
        Assert.All(changes, c => Assert.Equal(0.25, c.Item.CrystalDamage));
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path));
        var rows = reopened
            .GetRequiredService<IPilotContractAssets>()
            .Station(ContractAssetTestData.Pilot, ContractAssetTestData.RemoteStation);
        Assert.Equal(changes.Select(c => c.Item), rows);
        Assert.Equal(20, rows.Sum(i => i.Quantity));
        Assert.Equal(ContractAssetTestData.Station, db.Store.FindPilot(1000001)!.StationId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(int.MaxValue)]
    public void InvalidSplitQuantitiesLeaveTheOriginalStackAndInventoryUnchanged(int quantity)
    {
        using TestWorld db = new();
        long id = ContractAssetTestData.Stack(db);
        var assets = db.Services.GetRequiredService<IPilotContractAssets>();
        var before = assets.Station(ContractAssetTestData.Pilot, ContractAssetTestData.Station);
        Assert.Throws<InvalidDataException>(() => assets.Split(ContractAssetTestData.Pilot, ContractAssetTestData.Station, id, quantity));
        Assert.Equal(before, assets.Station(ContractAssetTestData.Pilot, ContractAssetTestData.Station));
    }

    [Fact]
    public void ForeignWrongLocationActiveAndMalformedSelectionsRejectWithoutMutation()
    {
        using TestWorld db = new();
        long id = ContractAssetTestData.Stack(db);
        long foreign = ContractAssetTestData.Stack(db, owner: ContractAssetTestData.Other);
        var assets = db.Services.GetRequiredService<IPilotContractAssets>();
        var before = assets.Station(ContractAssetTestData.Pilot, ContractAssetTestData.Station);
        foreach (
            var ids in new long[][]
            {
                [id, foreign],
                [id, id],
                [0],
                [db.Store.FindPilot(1000001)!.ShipId],
                Enumerable.Range(1, ContractAssetRules.MaximumSelection + 1).Select(i => (long)i).ToArray(),
            }
        )
        {
            Assert.Throws<InvalidDataException>(() => assets.Counts(ContractAssetTestData.Pilot, ContractAssetTestData.Station, ids));
        }
        Assert.Throws<InvalidDataException>(() => assets.Split(ContractAssetTestData.Pilot, ContractAssetTestData.Station, foreign, 1));
        Assert.Throws<InvalidDataException>(() => assets.Split(ContractAssetTestData.Pilot, 1, id, 1));
        Assert.Throws<InvalidDataException>(() => assets.Station(-1, ContractAssetTestData.Station));
        Assert.Equal(before, assets.Station(ContractAssetTestData.Pilot, ContractAssetTestData.Station));
        Assert.Equal(20, assets.Station(ContractAssetTestData.Other, ContractAssetTestData.Station).Single(i => i.Id == foreign).Quantity);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void SingletonOnlineAndContainerStacksCannotBeSplit(bool singleton, bool online, bool contents)
    {
        using TestWorld db = new();
        long id = ContractAssetTestData.Stack(db);
        using (WorldDbContext world = new(db.Path))
        {
            var item = world.Set<OwnedItem>().Find(id)!;
            item.Singleton = singleton;
            item.Online = online;
            world.SaveChanges();
        }
        if (contents)
        {
            ContractAssetTestData.Stack(db, location: id);
        }
        var assets = db.Services.GetRequiredService<IPilotContractAssets>();
        Assert.Throws<InvalidDataException>(() => assets.Split(ContractAssetTestData.Pilot, ContractAssetTestData.Station, id, 1));
        using WorldDbContext verify = new(db.Path);
        Assert.Equal(20, verify.Set<OwnedItem>().Find(id)!.Quantity);
    }
}
