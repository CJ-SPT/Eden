using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Market;
using Eden.Data.World.Repair;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Docking;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Market;
using Eden.World.Repair;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class ShipAppearanceTests
{
    [Fact]
    public void AppearanceAccessDoesNotRequireCargoVolumeDataFromTheCatalog()
    {
        using TestWorld db = new();
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Set<SourceRecord>().Where(r => r.Dataset == "client/types" && r.Key == "601").ExecuteDelete();
        }
        var appearance = db.Services.GetRequiredService<IPilotShipAppearance>();
        var state = appearance.Read(ContractAssetTestData.Pilot, 1000000000001);
        Assert.True(state.Timestamp > 0);
        Assert.True(appearance.Clean(ContractAssetTestData.Pilot, state.ShipId).Timestamp >= state.Timestamp);
    }

    [Fact]
    public void DirtAgesAcrossReadsCleaningCyclesAndRestartWithoutChangingWalletOrHullState()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var appearance = db.Services.GetRequiredService<IPilotShipAppearance>();
        long ship = db.Store.FindPilot(1000001)!.ShipId;
        ContractTests.Fund(db, ContractAssetTestData.Pilot);
        using (WorldDbContext setup = new(db.Path))
        {
            setup.Add(
                new ShipHealthState
                {
                    ShipId = ship,
                    StructureDamage = 5,
                    ArmorDamage = 3,
                }
            );
            setup.SaveChanges();
        }
        long first = appearance.Read(ContractAssetTestData.Pilot, ship).Timestamp;
        Assert.Equal(clock.Now, first);
        for (int cycle = 0; cycle < 3; cycle++)
        {
            clock.AdvanceWallTime(TimeSpan.FromDays(14));
            Assert.Equal(first, appearance.Read(ContractAssetTestData.Pilot, ship).Timestamp);
            first = appearance.Clean(ContractAssetTestData.Pilot, ship).Timestamp;
            Assert.Equal(clock.Now, first);
        }
        clock.AdvanceWallTime(TimeSpan.FromDays(-1));
        Assert.Equal(first, appearance.Clean(ContractAssetTestData.Pilot, ship).Timestamp);
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path), clock);
        Assert.Equal(first, reopened.GetRequiredService<IPilotShipAppearance>().Read(ContractAssetTestData.Pilot, ship).Timestamp);
        using WorldDbContext world = new(db.Path);
        Assert.Equal(100_000_000, world.Set<PilotWallet>().Find(ContractAssetTestData.Pilot)!.Cents);
        Assert.Equal(ship, world.Pilots.Find(ContractAssetTestData.Pilot)!.ActiveShipId);
        Assert.Single(world.Set<SavedShipDirt>());
        Assert.Equal(5, world.Set<ShipHealthState>().Find(ship)!.StructureDamage);
        Assert.Equal(3, world.Set<ShipHealthState>().Find(ship)!.ArmorDamage);
        Assert.Empty(world.Set<StationRepairPayment>());
    }

    [Fact]
    public void StoredHullAccessFollowsItsParentAndRejectsACleanAfterTheParentLeavesTheStation()
    {
        using TestWorld db = new();
        ContractAssetTestData.AddRemoteStation(db);
        long parent = ContractAssetTestData.Hull(db);
        long child = ContractAssetTestData.Hull(db, parent);
        var appearance = db.Services.GetRequiredService<IPilotShipAppearance>();
        var before = appearance.Read(ContractAssetTestData.Pilot, child);
        using (WorldDbContext world = new(db.Path))
        {
            world.Ships.Find(parent)!.StationId = ContractAssetTestData.RemoteStation;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => appearance.Clean(ContractAssetTestData.Pilot, child));
        using WorldDbContext verify = new(db.Path);
        Assert.Equal(before.Timestamp, verify.Set<SavedShipDirt>().Find(child)!.Timestamp);
    }

    [Fact]
    public async Task ConcurrentFirstReadsCreateOneStableBaseline()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var appearance = db.Services.GetRequiredService<IPilotShipAppearance>();
        long ship = db.Store.FindPilot(1000001)!.ShipId;
        var results = await Task.WhenAll(
            Enumerable.Range(0, 2).Select(_ => Task.Run(() => appearance.Read(ContractAssetTestData.Pilot, ship)))
        );
        Assert.All(results, state => Assert.Equal(clock.Now, state.Timestamp));
        using WorldDbContext verify = new(db.Path);
        Assert.Single(verify.Set<SavedShipDirt>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignMissingAndRemoteShipsRejectBeforeCreatingOrChangingDirtState(bool clean)
    {
        using TestWorld db = new();
        ContractAssetTestData.AddRemoteStation(db);
        long remote = ContractAssetTestData.Hull(db);
        using (WorldDbContext world = new(db.Path))
        {
            world.Ships.Find(remote)!.StationId = ContractAssetTestData.RemoteStation;
            world.SaveChanges();
        }
        var appearance = db.Services.GetRequiredService<IPilotShipAppearance>();
        foreach (long id in new[] { remote, db.Store.FindPilot(1000002)!.ShipId, long.MaxValue })
        {
            Assert.Throws<InvalidDataException>(() =>
                clean ? appearance.Clean(ContractAssetTestData.Pilot, id) : appearance.Read(ContractAssetTestData.Pilot, id)
            );
        }
        using WorldDbContext verify = new(db.Path);
        Assert.Empty(verify.Set<SavedShipDirt>());
    }

    [Fact]
    public void DirtFollowsTheHullThroughContractTransferAndOldOwnersLoseAccess()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        ContractTests.Fund(db, ContractAssetTestData.Pilot);
        ContractTests.Fund(db, ContractAssetTestData.Other);
        long hull = ContractAssetTestData.Hull(db);
        var appearance = db.Services.GetRequiredService<IPilotShipAppearance>();
        var before = appearance.Read(ContractAssetTestData.Pilot, hull);
        clock.AdvanceWallTime(TimeSpan.FromDays(20));
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        long id = contracts.Create(ContractAssetTestData.Pilot, ContractTests.Draft(hull) with { Items = [new(hull, 1)] });
        Assert.Throws<InvalidDataException>(() => appearance.Clean(ContractAssetTestData.Pilot, hull));
        contracts.Accept(ContractAssetTestData.Other, id);
        Assert.Equal(before, appearance.Read(ContractAssetTestData.Other, hull));
        Assert.Throws<InvalidDataException>(() => appearance.Read(ContractAssetTestData.Pilot, hull));
    }

    [Fact]
    public void AVersion52WorldUpgradesWithoutChangingShipsAndRemovedHullsCascadeTheirAppearance()
    {
        using TestWorld db = new();
        var before = db.Store.FindPilot(1000001)!;
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldShipDirt; PRAGMA user_version=52;");
        }
        WorldInitialization.Open(db.Path);
        Assert.Equal(before, db.Store.FindPilot(1000001));
        long hull = ContractAssetTestData.Hull(db);
        db.Services.GetRequiredService<IPilotShipAppearance>().Read(ContractAssetTestData.Pilot, hull);
        using (WorldDbContext world = new(db.Path))
        {
            world.Remove(world.Ships.Find(hull)!);
            world.SaveChanges();
        }
        using WorldDbContext verify = new(db.Path);
        Assert.Empty(verify.Set<SavedShipDirt>());
        Assert.Equal(WorldMigration.CurrentVersion, verify.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Fact]
    public void NativeTimestampRulesRejectAnInvalidClockAndNeverBackdateACleanHull()
    {
        Assert.Equal(20, ShipDirtRules.CleanTimestamp(10, 20));
        Assert.Equal(20, ShipDirtRules.CleanTimestamp(20, 10));
        Assert.Equal(10, ShipDirtRules.CleanTimestamp(null, 10));
        Assert.Throws<InvalidDataException>(() => ShipDirtRules.CleanTimestamp(null, 0));
    }
}
