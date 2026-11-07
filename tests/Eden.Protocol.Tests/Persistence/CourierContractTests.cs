using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Contracts;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Market;
using Eden.Protocol.Tests.Combat;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Contracts;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

internal static class CourierTestData
{
    internal const long Issuer = ContractAssetTestData.Pilot;
    internal const long Hauler = ContractAssetTestData.Other;
    internal const long Collateral = 2_000_000;
    internal const long Reward = 25_000;

    internal static void Prepare(TestWorld db)
    {
        ContractAssetTestData.AddRemoteStation(db);
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        if (catalog.Set<ItemCategory>().Find(CourierRules.PackageCategory) is null)
        {
            catalog.Add(new ItemCategory { Id = CourierRules.PackageCategory, Name = "Celestial" });
        }
        catalog.Add(
            new ItemGroup
            {
                Id = CourierRules.PackageGroup,
                Name = "Freight Container",
                CategoryId = CourierRules.PackageCategory,
            }
        );
        catalog.Add(
            new ItemType
            {
                Id = CourierRules.PackageType,
                Name = "Plastic Wrap",
                GroupId = CourierRules.PackageGroup,
            }
        );
        catalog.Add(
            new SourceRecord
            {
                Dataset = "client/types",
                Key = CourierRules.PackageType.ToString(),
                Data = "{\"volume\":0.01}",
            }
        );
        catalog.SaveChanges();
        ContractTests.Fund(db, Issuer);
        ContractTests.Fund(db, Hauler);
    }

    internal static ContractDraft Draft(long item, int quantity = 7) =>
        ContractTests.Draft(item) with
        {
            PriceCents = 0,
            RewardCents = Reward,
            Items = [new(item, quantity)],
            Type = ContractType.Courier,
            Courier = new(ContractAssetTestData.RemoteStation, 1, Collateral),
        };

    internal static DockedPilot Dock(TestWorld db, long account, int stationId)
    {
        var pilot = db.Store.FindPilot(account)!;
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath);
        var station = catalog.Set<Station>().Single(s => s.Id == stationId);
        db.Store.SetPosition(pilot.ShipId, new(station.X, station.Y, station.Z), station.SolarSystemId);
        return db.Store.Dock(pilot.PilotId, pilot.ShipId, stationId);
    }

    internal static InventoryItem Load(TestWorld db, long package, long account = 1000002)
    {
        var pilot = db.Store.FindPilot(account)!;
        return Assert
            .Single(
                db.Services.GetRequiredService<IPilotInventory>()
                    .Move(pilot.PilotId, [package], pilot.StationId, pilot.ShipId, (int)InventoryFlag.Cargo, null)
            )
            .Item;
    }
}

public sealed class CourierContractTests
{
    [Fact]
    public void ADeliveredFittedHullRetainsItsOwnerNestedHullModulesAndDamage()
    {
        using TestWorld db = new();
        CourierTestData.Prepare(db);
        long hull = ContractAssetTestData.Hull(db);
        long child = ContractAssetTestData.Hull(db, hull);
        long module = ContractAssetTestData.Stack(db, 1, child, flag: (int)InventoryFlag.HighSlot0);
        long magazine = ContractAssetTestData.Stack(db, 40, module, flag: (int)InventoryFlag.Cargo);
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<OwnedItem>().Find(module)!.Online = true;
            world.Add(
                new ShipHealthState
                {
                    ShipId = child,
                    ArmorDamage = 4,
                    StructureDamage = 2,
                }
            );
            world.SaveChanges();
        }
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Set<ItemType>().AsTracking().Single(t => t.Id == 601).Capacity = 100000;
            catalog.SaveChanges();
        }
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        long id = contracts.Create(CourierTestData.Issuer, CourierTestData.Draft(hull, 1));
        long package = contracts.Accept(CourierTestData.Hauler, id).Courier!.PackageId!.Value;
        Assert.Equal(15000, CourierTestData.Load(db, package).Volume);
        CourierTestData.Dock(db, 1000002, ContractAssetTestData.RemoteStation);
        contracts.Complete(CourierTestData.Hauler, id);
        WorldInitialization.Open(db.Path);
        using WorldDbContext verify = new(db.Path);
        Assert.Equal(ContractAssetTestData.RemoteStation, verify.Ships.Find(hull)!.StationId);
        Assert.Equal(CourierTestData.Issuer, verify.Ships.Find(hull)!.OwnerId);
        Assert.Equal(hull, verify.Ships.Find(child)!.StoredInId);
        Assert.True(verify.Set<OwnedItem>().Find(module)!.Online);
        Assert.Equal(40, verify.Set<OwnedItem>().Find(magazine)!.Quantity);
        Assert.Equal(2, verify.Set<ShipHealthState>().Find(child)!.StructureDamage);
    }

    [Fact]
    public void InvalidDestinationOverlargeShipmentsAndInsufficientCollateralLeaveUnchangedState()
    {
        using TestWorld db = new();
        CourierTestData.Prepare(db);
        long goods = ContractAssetTestData.Stack(db, 250001);
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        var draft = CourierTestData.Draft(goods);
        Assert.Throws<InvalidDataException>(() =>
            contracts.Create(CourierTestData.Issuer, draft with { Courier = draft.Courier! with { DestinationStationId = 1 } })
        );
        Assert.Throws<InvalidDataException>(() => contracts.Create(CourierTestData.Issuer, CourierTestData.Draft(goods, 250001)));
        using (WorldDbContext world = new(db.Path))
        {
            Assert.Empty(world.Set<SavedContract>());
            Assert.Empty(world.Set<SavedContractEscrow>());
            Assert.Empty(world.Set<SavedContractPayment>());
            Assert.Equal(250001, world.Set<OwnedItem>().Find(goods)!.Quantity);
        }
        ContractTests.Fund(db, CourierTestData.Hauler, CourierTestData.Collateral - 1);
        long id = contracts.Create(CourierTestData.Issuer, draft);
        long revision = contracts.Revision;
        Assert.Throws<InvalidDataException>(() => contracts.Accept(CourierTestData.Hauler, id));
        Assert.Equal(revision, contracts.Revision);
        Assert.Equal(ContractStatus.Outstanding, contracts.Details(CourierTestData.Issuer, id).Contract.Status);
        using WorldDbContext verify = new(db.Path);
        Assert.DoesNotContain(verify.Set<OwnedItem>(), i => i.TypeId == CourierRules.PackageType);
        Assert.Single(verify.Set<SavedContractEscrow>());
        Assert.Equal(CourierTestData.Collateral - 1, verify.Set<PilotWallet>().Find(CourierTestData.Hauler)!.Cents);
    }

    private sealed class CourierLootRoll(bool survives) : IShipLootRoll
    {
        public bool Survives() => survives;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualNpcShipDestructionLosesOrPreservesThePackageAndItsRecoverableVolume(bool survives)
    {
        using NpcCombatTests.Fixture f = new();
        CourierTestData.Prepare(f.Db);
        f.LootRoll = new CourierLootRoll(survives);
        var contracts = f.Db.Services.GetRequiredService<IPilotContracts>();
        long goods = ContractAssetTestData.Stack(f.Db, owner: CourierTestData.Hauler);
        long id = contracts.Create(CourierTestData.Hauler, CourierTestData.Draft(goods) with { AssigneeId = CourierTestData.Issuer });
        long package = contracts.Accept(CourierTestData.Issuer, id).Courier!.PackageId!.Value;
        CourierTestData.Dock(f.Db, 1000001, ContractAssetTestData.Station);
        CourierTestData.Load(f.Db, package, 1000001);
        f.Db.Store.SetPosition(NpcCombatTests.Fixture.ShipId, f.Npc.Position + new SpacePosition(1000, 0, 0), f.Npc.SystemId);
        f.Aggress();
        f.Clock.Milliseconds = 7500;
        f.AlmostLost();
        var fatal = Assert.IsType<NpcShot>(f.Fire());
        Assert.True(fatal.Health.Remaining.Destroyed);
        if (!survives)
        {
            Assert.Null(contracts.Package(CourierTestData.Issuer, package));
            contracts.Fail(CourierTestData.Issuer, id);
            using WorldDbContext world = new(f.Db.Path);
            Assert.Empty(world.Set<SavedContractEscrow>());
        }
        else
        {
            var wrecks = f.Db.Services.GetRequiredService<IWrecks>();
            var wreck = Assert.Single(wrecks.List(f.Npc.SystemId, f.Clock.Now));
            var found = wrecks.Read(CourierTestData.Issuer, wreck.Id, f.Clock.Now).Items.Single(i => i.Id == package);
            Assert.Equal(35, found.Volume);
            CourierTestData.Dock(f.Db, 1000001, ContractAssetTestData.Station);
            long replacement = ContractAssetTestData.Hull(f.Db);
            f.Db.Services.GetRequiredService<IPilotFleet>().Board(CourierTestData.Issuer, replacement, fatal.Capsule!.ShipId);
            f.Db.Store.SetPosition(replacement, wreck.Position, wreck.SystemId);
            using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
            {
                catalog.Set<ItemType>().AsTracking().Single(t => t.Id == 601).Capacity = 10;
                catalog.SaveChanges();
            }
            Assert.Throws<CargoCapacityException>(() =>
                wrecks.Take(CourierTestData.Issuer, replacement, wreck.Id, [package], null, f.Clock.Now)
            );
            using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
            {
                catalog.Set<ItemType>().AsTracking().Single(t => t.Id == 601).Capacity = 125;
                catalog.SaveChanges();
            }
            var recovered = Assert.Single(wrecks.Take(CourierTestData.Issuer, replacement, wreck.Id, [package], null, f.Clock.Now));
            Assert.Equal(35, recovered.Item.Volume);
            CourierTestData.Dock(f.Db, 1000001, ContractAssetTestData.RemoteStation);
            contracts.Complete(CourierTestData.Issuer, id);
            Assert.Equal(ContractStatus.Finished, contracts.Details(CourierTestData.Issuer, id).Contract.Status);
        }
    }

    [Fact]
    public void ASealedPackageCanBeBrokenInTheActiveShipsCargoWhileInSpace()
    {
        using TestWorld db = new();
        CourierTestData.Prepare(db);
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        long id = contracts.Create(CourierTestData.Issuer, CourierTestData.Draft(ContractAssetTestData.Stack(db)));
        long package = contracts.Accept(CourierTestData.Hauler, id).Courier!.PackageId!.Value;
        var carried = CourierTestData.Load(db, package);
        db.Store.SetPosition(carried.LocationId, SpacePosition.Zero);
        contracts.BreakPackage(CourierTestData.Hauler, package);
        Assert.Equal(
            7,
            Assert.Single(db.Services.GetRequiredService<IPilotInventory>().List(CourierTestData.Hauler, carried.LocationId)).Quantity
        );
    }

    [Fact]
    public void MissingVolumeColumnAndVersion50ContractsUpgradeWithoutChangingExistingItemExchanges()
    {
        using TestWorld db = new();
        ContractTests.Fund(db, CourierTestData.Issuer);
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        long id = contracts.Create(CourierTestData.Issuer, ContractTests.Draft(ContractAssetTestData.Stack(db)));
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                DROP INDEX IX_WorldContract_PackageId;
                ALTER TABLE WorldContract DROP COLUMN Type; ALTER TABLE WorldContract DROP COLUMN EndStationId;
                ALTER TABLE WorldContract DROP COLUMN EndSystemId; ALTER TABLE WorldContract DROP COLUMN EndConstellationId;
                ALTER TABLE WorldContract DROP COLUMN EndRegionId; ALTER TABLE WorldContract DROP COLUMN DurationDays;
                ALTER TABLE WorldContract DROP COLUMN CollateralCents; ALTER TABLE WorldContract DROP COLUMN PackageId;
                ALTER TABLE WorldItem DROP COLUMN VolumeOverride; PRAGMA user_version=50;
                """
            );
        }
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path));
        var saved = reopened.GetRequiredService<IPilotContracts>().Details(CourierTestData.Issuer, id).Contract;
        Assert.Equal(ContractType.ItemExchange, saved.Type);
        Assert.Null(saved.Courier);
        Assert.Equal(12345, saved.PriceCents);
        using WorldDbContext verify = new(db.Path);
        Assert.Equal(WorldMigration.CurrentVersion, verify.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        Assert.Single(verify.Set<SavedContractEscrow>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CourierEscrowTransitDeliveryAndReopenPreserveGoodsAndSettleRewardAndCollateralOnce(bool isPrivate)
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        CourierTestData.Prepare(db);
        long item = ContractAssetTestData.Stack(db);
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        long id = contracts.Create(
            CourierTestData.Issuer,
            CourierTestData.Draft(item) with
            {
                AssigneeId = isPrivate ? CourierTestData.Hauler : null,
            }
        );
        var accepted = contracts.Accept(CourierTestData.Hauler, id);
        Assert.Equal(ContractStatus.InProgress, accepted.Status);
        Assert.Equal(ContractType.Courier, accepted.Type);
        long package = accepted.Courier!.PackageId!.Value;
        Assert.Equal(35, CourierTestData.Load(db, package).Volume);
        Assert.Equal(CourierTestData.Collateral, contracts.Escrow(CourierTestData.Hauler).IskCents);
        Assert.Equal(CourierTestData.Reward + (isPrivate ? 0 : 1_000_000), contracts.Escrow(CourierTestData.Issuer).IskCents);
        Assert.Single(contracts.Page(CourierTestData.Hauler).InProgress!);
        Assert.Equal(1, contracts.Page(CourierTestData.Issuer).Outstanding);
        Assert.Throws<InvalidDataException>(() => contracts.Complete(CourierTestData.Hauler, id));
        CourierTestData.Dock(db, 1000002, ContractAssetTestData.RemoteStation);
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path), clock);
        contracts = reopened.GetRequiredService<IPilotContracts>();
        Assert.Equal(35, contracts.Package(CourierTestData.Hauler, package)!.Item.Volume);
        contracts.Complete(CourierTestData.Hauler, id);
        contracts.Complete(CourierTestData.Hauler, id);
        Assert.Equal(ContractStatus.Finished, contracts.Details(CourierTestData.Hauler, id).Contract.Status);
        var wallet = reopened.GetRequiredService<IPilotWalletReader>();
        Assert.Equal(100_000_000 - 1_000_000 - CourierTestData.Reward, wallet.Balance(CourierTestData.Issuer));
        Assert.Equal(100_000_000 + CourierTestData.Reward, wallet.Balance(CourierTestData.Hauler));
        Assert.Equal(3, wallet.Journal(CourierTestData.Hauler).Contracts.Count);
        Assert.Empty(contracts.Page(CourierTestData.Hauler).InProgress!);
        Assert.Equal(0, contracts.Escrow(CourierTestData.Hauler).IskCents);
        using WorldDbContext world = new(db.Path);
        Assert.Null(world.Set<OwnedItem>().Find(package));
        var delivered = Assert.Single(world.Set<OwnedItem>().Where(i => i.LocationId == ContractAssetTestData.RemoteStation));
        Assert.Equal(7, delivered.Quantity);
        Assert.Equal(CourierTestData.Issuer, delivered.OwnerId);
        Assert.Equal((int)InventoryFlag.Hangar, delivered.FlagId);
        Assert.Empty(world.Set<SavedContractEscrow>());
        Assert.Equal(21, world.Set<OwnedItem>().Where(i => i.OwnerId == CourierTestData.Issuer && i.TypeId == 3651).Sum(i => i.Quantity));
    }

    [Theory]
    [InlineData(24, true)]
    [InlineData(25, true)]
    [InlineData(26, false)]
    public void CargoCapacityUsesTheWholeWrappedVolumeRatherThanThePlasticWrapCatalogVolume(int quantity, bool fits)
    {
        using TestWorld db = new();
        CourierTestData.Prepare(db);
        long item = ContractAssetTestData.Stack(db, quantity);
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        long id = contracts.Create(CourierTestData.Issuer, CourierTestData.Draft(item, quantity));
        long package = contracts.Accept(CourierTestData.Hauler, id).Courier!.PackageId!.Value;
        if (fits)
        {
            Assert.Equal(quantity * 5, CourierTestData.Load(db, package).Volume);
        }
        else
        {
            Assert.Throws<CargoCapacityException>(() => CourierTestData.Load(db, package));
            Assert.Equal(ContractAssetTestData.Station, contracts.Package(CourierTestData.Hauler, package)!.Item.LocationId);
        }
    }

    [Fact]
    public void BreakingThePackageInCargoFailsTheContractAndReleasesTheRealGoodsAtTheSameLocation()
    {
        using TestWorld db = new();
        CourierTestData.Prepare(db);
        long item = ContractAssetTestData.Stack(db);
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        long id = contracts.Create(CourierTestData.Issuer, CourierTestData.Draft(item));
        long package = contracts.Accept(CourierTestData.Hauler, id).Courier!.PackageId!.Value;
        var wrapped = CourierTestData.Load(db, package);
        Assert.Throws<InvalidDataException>(() => contracts.BreakPackage(CourierTestData.Issuer, package));
        contracts.BreakPackage(CourierTestData.Hauler, package);
        Assert.Equal(ContractStatus.Failed, contracts.Details(CourierTestData.Hauler, id).Contract.Status);
        Assert.Null(contracts.Package(CourierTestData.Hauler, package));
        var goods = Assert.Single(db.Services.GetRequiredService<IPilotInventory>().List(CourierTestData.Hauler, wrapped.LocationId));
        Assert.Equal(7, goods.Quantity);
        Assert.Equal((int)InventoryFlag.Cargo, goods.FlagId);
        Assert.Equal(wrapped.Volume, goods.Volume * goods.Quantity);
        var wallet = db.Services.GetRequiredService<IPilotWalletReader>();
        Assert.Equal(101_000_000, wallet.Balance(CourierTestData.Issuer));
        Assert.Equal(98_000_000, wallet.Balance(CourierTestData.Hauler));
        contracts.Fail(CourierTestData.Hauler, id);
        Assert.Equal(101_000_000, wallet.Balance(CourierTestData.Issuer));
        Assert.Throws<InvalidDataException>(() => contracts.Complete(CourierTestData.Hauler, id));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void IssuerCanFailOnlyAfterTheNativeStrictOverdueBoundary(int ticks, bool overdue)
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        CourierTestData.Prepare(db);
        long item = ContractAssetTestData.Stack(db);
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        long id = contracts.Create(CourierTestData.Issuer, CourierTestData.Draft(item));
        var accepted = contracts.Accept(CourierTestData.Hauler, id);
        clock.AdvanceWallTime(TimeSpan.FromDays(1) + TimeSpan.FromTicks(ticks));
        if (!overdue)
        {
            Assert.Throws<InvalidDataException>(() => contracts.Fail(CourierTestData.Issuer, id));
            Assert.Equal(ContractStatus.InProgress, contracts.Details(CourierTestData.Issuer, id).Contract.Status);
        }
        else
        {
            contracts.Fail(CourierTestData.Issuer, id);
            contracts.Fail(CourierTestData.Issuer, id);
            Assert.Equal(ContractStatus.Failed, contracts.Details(CourierTestData.Issuer, id).Contract.Status);
            Assert.Equal(0, contracts.Escrow(CourierTestData.Issuer).IskCents);
            contracts.Delete(CourierTestData.Issuer, id);
            contracts.BreakPackage(CourierTestData.Hauler, accepted.Courier!.PackageId!.Value);
            using WorldDbContext world = new(db.Path);
            Assert.Empty(world.Set<SavedContractEscrow>());
        }
    }

    [Fact]
    public void LateDeliveryStillSucceedsUntilTheIssuerDeclaresFailure()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        CourierTestData.Prepare(db);
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        long id = contracts.Create(CourierTestData.Issuer, CourierTestData.Draft(ContractAssetTestData.Stack(db)));
        long package = contracts.Accept(CourierTestData.Hauler, id).Courier!.PackageId!.Value;
        CourierTestData.Load(db, package);
        clock.AdvanceWallTime(TimeSpan.FromDays(2));
        CourierTestData.Dock(db, 1000002, ContractAssetTestData.RemoteStation);
        contracts.Complete(CourierTestData.Hauler, id);
        Assert.Throws<InvalidDataException>(() => contracts.Fail(CourierTestData.Issuer, id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedDeliveryLedgerWriteRollsBackPackageGoodsBalancesAndStatus(bool hull)
    {
        using TestWorld db = new();
        CourierTestData.Prepare(db);
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        long goods = hull ? ContractAssetTestData.Hull(db) : ContractAssetTestData.Stack(db);
        if (hull)
        {
            using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
            catalog.Set<ItemType>().AsTracking().Single(t => t.Id == 601).Capacity = 100000;
            catalog.SaveChanges();
        }
        long id = contracts.Create(CourierTestData.Issuer, CourierTestData.Draft(goods, hull ? 1 : 7));
        long package = contracts.Accept(CourierTestData.Hauler, id).Courier!.PackageId!.Value;
        CourierTestData.Load(db, package);
        CourierTestData.Dock(db, 1000002, ContractAssetTestData.RemoteStation);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER FailCourierPayment BEFORE INSERT ON WorldContractPayment
                WHEN NEW.Type=69 BEGIN SELECT RAISE(ABORT,'Injected courier refund failure'); END;
                """
            );
        }
        long revision = contracts.Revision;
        Assert.Throws<DbUpdateException>(() => contracts.Complete(CourierTestData.Hauler, id));
        Assert.Equal(revision, contracts.Revision);
        Assert.Equal(ContractStatus.InProgress, contracts.Details(CourierTestData.Issuer, id).Contract.Status);
        Assert.NotNull(contracts.Package(CourierTestData.Hauler, package));
        Assert.Equal(98_000_000, db.Services.GetRequiredService<IPilotWalletReader>().Balance(CourierTestData.Hauler));
        using WorldDbContext verify = new(db.Path);
        Assert.Single(verify.Set<SavedContractEscrow>());
        if (hull)
        {
            Assert.Equal(ContractAssetTestData.Station, verify.Ships.IgnoreQueryFilters().Single(s => s.Id == goods).StationId);
        }
        Assert.DoesNotContain(verify.Set<OwnedItem>(), i => i.LocationId == ContractAssetTestData.RemoteStation);
    }

    [Fact]
    public void APhysicallyLostPackageFailsWithoutRecreatingItsGoods()
    {
        using TestWorld db = new();
        CourierTestData.Prepare(db);
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        long id = contracts.Create(CourierTestData.Issuer, CourierTestData.Draft(ContractAssetTestData.Stack(db)));
        long package = contracts.Accept(CourierTestData.Hauler, id).Courier!.PackageId!.Value;
        using (WorldDbContext world = new(db.Path))
        {
            world.Remove(world.Set<OwnedItem>().Find(package)!);
            world.SaveChanges();
        }
        contracts.Fail(CourierTestData.Hauler, id);
        WorldInitialization.Open(db.Path);
        using WorldDbContext verify = new(db.Path);
        Assert.Empty(verify.Set<SavedContractEscrow>());
        Assert.Equal(14, verify.Set<OwnedItem>().Where(i => i.OwnerId == CourierTestData.Issuer && i.TypeId == 3651).Sum(i => i.Quantity));
        Assert.Equal(ContractStatus.Failed, contracts.Details(CourierTestData.Issuer, id).Contract.Status);
    }
}
