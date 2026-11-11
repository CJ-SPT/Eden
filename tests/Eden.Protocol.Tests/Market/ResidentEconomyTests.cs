using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Tests.Capsuleers;
using Eden.Protocol.Tests.Persistence;
using Eden.Server;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Market;

public sealed class ResidentEconomyTests
{
    private const long Human = 90000001;
    private const int Station = 60015157;
    private const int Ammunition = 215;

    private static void Fund(WorldDatabase database)
    {
        using WorldDbContext world = new(database.Path);
        world.Database.ExecuteSqlRaw("INSERT OR REPLACE INTO WorldWallet(PilotId,Cents) VALUES(90000001,10000000000)");
    }

    [Fact]
    public void RejectedPaymentDiscardsEveryStagedInventoryAndWalletChange()
    {
        using CapsuleerHaulerTests.Fixture fixture = new();
        Fund(fixture.Database);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var live = host.GetRequiredService<LiveWorld>();
        long before;
        long rejected;
        using (var transaction = live.Economy.Begin())
        {
            var wallet = transaction.Set<PilotWallet>().Find(Human)!;
            before = wallet.Cents;
            wallet.Cents = -1;
            rejected = transaction.NextItemId();
            transaction.Add(
                new OwnedItem
                {
                    Id = rejected,
                    OwnerId = Human,
                    TypeId = Ammunition,
                    LocationId = Station,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = 100,
                }
            );
            Assert.Throws<InvalidDataException>(transaction.Commit);
        }
        using var metrics = SessionPerformance.Begin("rejected economic action", _ => { }, force: true)!;
        using var read = live.Economy.Begin();
        Assert.Equal(before, read.Set<PilotWallet>().Find(Human)!.Cents);
        Assert.Null(read.Set<OwnedItem>().Find(rejected));
        Assert.Equal(rejected, read.NextItemId());
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public async Task WalletOwnershipAndInventoryCaptureOneGenerationAndReopenTogether()
    {
        using CapsuleerHaulerTests.Fixture fixture = new();
        Fund(fixture.Database);
        long item;
        long balance;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using (var transaction = live.Economy.Begin())
            {
                item = transaction.NextItemId();
                var wallet = transaction.Set<PilotWallet>().Find(Human)!;
                wallet.Cents -= 100;
                balance = wallet.Cents;
                transaction.Add(
                    new OwnedItem
                    {
                        Id = item,
                        OwnerId = Human,
                        TypeId = Ammunition,
                        LocationId = Station,
                        FlagId = (int)InventoryFlag.Hangar,
                        Quantity = 100,
                    }
                );
                transaction.Commit();
            }
            using var captured = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
            using (var transaction = live.Economy.Begin())
            {
                transaction.Set<PilotWallet>().Find(Human)!.Cents -= 100;
                transaction.Set<OwnedItem>().Find(item)!.Quantity = 50;
                transaction.Commit();
            }
            captured.Project();
            using (WorldDbContext saved = new(captured.Database))
            {
                Assert.Equal(balance, saved.Set<PilotWallet>().Find(Human)!.Cents);
                Assert.Equal(100, saved.Set<OwnedItem>().Find(item)!.Quantity);
                Assert.Equal(Human, saved.Set<OwnedItem>().Find(item)!.OwnerId);
            }
            await live.FlushAsync();
        }
        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var read = reopened.GetRequiredService<LiveWorld>().Economy.Begin();
        Assert.Equal(balance - 100, read.Set<PilotWallet>().Find(Human)!.Cents);
        Assert.Equal(50, read.Set<OwnedItem>().Find(item)!.Quantity);
    }

    [Fact]
    public void FailedDetachedEconomicProjectionRollsBackEveryRowAndRetainsTheResidentChanges()
    {
        using CapsuleerHaulerTests.Fixture fixture = new();
        Fund(fixture.Database);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var live = host.GetRequiredService<LiveWorld>();
        long before;
        using (var transaction = live.Economy.Begin())
        {
            var wallet = transaction.Set<PilotWallet>().Find(Human)!;
            before = wallet.Cents;
            wallet.Cents -= 100;
            transaction.Add(
                new OwnedItem
                {
                    Id = transaction.NextItemId(),
                    OwnerId = Human,
                    TypeId = Ammunition,
                    LocationId = Station,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = 100,
                }
            );
            transaction.Commit();
        }
        using var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        using (var command = snapshot.Database.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER RejectEconomicWallet BEFORE INSERT ON WorldWallet
                BEGIN SELECT RAISE(ABORT,'test economic projection'); END
                """;
            command.ExecuteNonQuery();
        }
        Assert.Throws<SqliteException>(() => snapshot.Project());
        using WorldDbContext saved = new(snapshot.Database);
        Assert.Equal(before, saved.Set<PilotWallet>().Find(Human)!.Cents);
        Assert.True(live.Economy.Capture().Count > 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task WarmedFillsAndCargoTransfersUseNoSqlAndSaveTogether(int observers)
    {
        using PersonalMarketTests.Fixture fixture = new(live: true);
        var market = fixture.Market;
        var inventory = fixture.Db.Services.GetRequiredService<IPilotInventory>();
        var fitting = fixture.Db.Services.GetRequiredService<IPilotFitting>();
        const long buyer = 90000002;
        long ship = fixture.Db.Store.FindPilot(1000002)!.ShipId;
        int station = fixture.Pilot.StationId;
        fixture.List();
        market.Buy(buyer, station, [new(Ammunition, 1000, 1)]);
        _ = inventory.List(buyer, station);
        _ = fitting.ShipAttributes(buyer);
        long purchased;
        long balance;
        using (var metrics = SessionPerformance.Begin("resident fills and cargo", _ => { }, force: true)!)
        {
            var result = market.Buy(buyer, station, [new(Ammunition, 1000, 2)]);
            balance = result.BalanceCents;
            var stack = inventory.List(buyer, station).Single(i => i.TypeId == Ammunition);
            purchased = stack.Id;
            inventory.Move(buyer, ship, new([stack.Id], station, (int)InventoryFlag.Cargo, null));
            for (int i = 0; i < observers; i++)
            {
                Assert.Equal(3, inventory.List(buyer, ship).Single(item => item.Id == stack.Id).Quantity);
                Assert.Equal(balance, market.Balance(buyer));
                Assert.Equal(97, market.PersonalOrders(Human).Single().Remaining);
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        await fixture.Db.Services.GetRequiredService<LiveWorld>().FlushAsync();
        using WorldDbContext saved = new(fixture.Db.Path);
        Assert.Equal(balance, saved.Set<PilotWallet>().Find(buyer)!.Cents);
        Assert.Equal(
            (buyer, ship, 3),
            (
                saved.Set<OwnedItem>().Find(purchased)!.OwnerId,
                saved.Set<OwnedItem>().Find(purchased)!.LocationId,
                saved.Set<OwnedItem>().Find(purchased)!.Quantity
            )
        );
    }

    [Fact]
    public void BatchedItemIdentitiesStayUniqueAfterDeletingTheNewestRowsAndReopeningTheCheckpoint()
    {
        using CapsuleerHaulerTests.Fixture fixture = new();
        Fund(fixture.Database);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var live = host.GetRequiredService<LiveWorld>();
        long last;
        using (var transaction = live.Economy.Begin())
        {
            long first = transaction.NextItemId();
            last = first + 2;
            foreach (long id in new[] { first, first + 1, last })
            {
                transaction.Add(
                    new OwnedItem
                    {
                        Id = id,
                        OwnerId = Human,
                        TypeId = Ammunition,
                        LocationId = Station,
                        FlagId = (int)InventoryFlag.Hangar,
                        Quantity = 1,
                    }
                );
            }
            transaction.Commit();
        }
        using (var transaction = live.Economy.Begin())
        {
            transaction.Remove(transaction.Set<OwnedItem>().Find(last)!);
            transaction.Commit();
        }
        using var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        snapshot.Project();
        using WorldDbContext reopened = new(snapshot.Database);
        Assert.Null(reopened.Set<OwnedItem>().Find(last));
        Assert.True(InventoryPersistence.NextId(reopened) > last);
        using var next = live.Economy.Begin();
        Assert.True(next.NextItemId() > last);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(1000)]
    public void ChainedPredicatesOnlyMaterializeSelectedItemsAsUnrelatedInventoryGrows(int unrelated)
    {
        using CapsuleerHaulerTests.Fixture fixture = new();
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            for (int i = 0; i < unrelated; i++)
            {
                world.Add(
                    new OwnedItem
                    {
                        Id = InventoryDefinitions.FirstOwnedItemId + 10000 + i,
                        OwnerId = Human,
                        TypeId = Ammunition,
                        LocationId = Station,
                        FlagId = (int)InventoryFlag.Hangar,
                        Quantity = i + 1,
                    }
                );
            }
            world.SaveChanges();
        }
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var live = host.GetRequiredService<LiveWorld>();
        using (live.Economy.Begin()) { }
        using var metrics = SessionPerformance.Begin("bounded inventory selection", _ => { }, force: true)!;
        using var transaction = live.Economy.Begin();
        var selected = transaction
            .Set<OwnedItem>()
            .Where(i => i.OwnerId == Human && i.LocationId == Station)
            .Where(i => i.TypeId == Ammunition && i.Quantity == unrelated)
            .Single();
        Assert.Equal(unrelated, selected.Quantity);
        Assert.Equal(1, transaction.Materializations);
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
    }

    [Fact]
    public void RegisteredOwnershipTransfersCommitTogetherAndRejectActiveHullOrUnknownOwners()
    {
        using CapsuleerHaulerTests.Fixture fixture = new();
        Fund(fixture.Database);
        const long corporation = 1999999999;
        long hull = InventoryDefinitions.FirstOwnedItemId + 9000;
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            world.Add(new InventoryOwner { Id = corporation });
            world.Add(
                new Ship
                {
                    Id = hull,
                    OwnerId = Human,
                    TypeId = 601,
                    StationId = Station,
                    Name = "Stored hull",
                }
            );
            world.SaveChanges();
        }
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var live = host.GetRequiredService<LiveWorld>();
        long item;
        using (var transaction = live.Economy.Begin())
        {
            transaction.Ships.Find(hull)!.OwnerId = corporation;
            item = transaction.NextItemId();
            transaction.Add(
                new OwnedItem
                {
                    Id = item,
                    OwnerId = corporation,
                    TypeId = Ammunition,
                    LocationId = Station,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = 2,
                }
            );
            transaction.Set<PilotWallet>().Find(Human)!.Cents -= 100;
            transaction.Commit();
        }
        using (var rejected = live.Economy.Begin())
        {
            rejected.Pilots.Find(Human)!.ActiveShip.OwnerId = corporation;
            rejected.Set<PilotWallet>().Find(Human)!.Cents -= 100;
            Assert.Throws<InvalidDataException>(rejected.Commit);
        }
        using (var rejected = live.Economy.Begin())
        {
            rejected.Set<OwnedItem>().Find(item)!.OwnerId = -123;
            Assert.Throws<InvalidDataException>(rejected.Commit);
        }
        using var captured = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        captured.Project();
        using WorldDbContext saved = new(captured.Database);
        Assert.Equal(corporation, saved.Ships.Find(hull)!.OwnerId);
        Assert.Equal(corporation, saved.Set<OwnedItem>().Find(item)!.OwnerId);
        Assert.Equal(10000000000 - 100, saved.Set<PilotWallet>().Find(Human)!.Cents);
        Assert.Equal(0, saved.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_foreign_key_check").Single());
    }

    [Fact]
    public async Task FailedFileSaveRetainsTheTransactionAndRetrySavesTheLatestGeneration()
    {
        using CapsuleerHaulerTests.Fixture fixture = new();
        Fund(fixture.Database);
        int attempts = 0;
        using LiveWorld live = new(
            fixture.Database,
            new CheckpointClock(fixture.Clock),
            _ => { },
            (snapshot, path) =>
            {
                if (++attempts == 1)
                {
                    throw new IOException("Injected economic save failure.");
                }
                LiveWorld.PersistSnapshot(snapshot, path);
            }
        );
        long item;
        using (var transaction = live.Economy.Begin())
        {
            item = transaction.NextItemId();
            transaction.Add(
                new OwnedItem
                {
                    Id = item,
                    OwnerId = Human,
                    TypeId = Ammunition,
                    LocationId = Station,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = 100,
                }
            );
            transaction.Set<PilotWallet>().Find(Human)!.Cents -= 100;
            transaction.Commit();
        }
        await Assert.ThrowsAsync<IOException>(live.FlushAsync);
        Assert.True(live.Economy.Capture().Count > 0);
        using (var transaction = live.Economy.Begin())
        {
            transaction.Set<PilotWallet>().Find(Human)!.Cents -= 100;
            transaction.Set<OwnedItem>().Find(item)!.Quantity = 50;
            transaction.Commit();
        }
        await live.FlushAsync();
        using var disk = WorldConnections.FileConnection(fixture.Database.Path);
        disk.Open();
        using var check = disk.CreateCommand();
        check.CommandText = "SELECT Cents FROM WorldWallet WHERE PilotId=90000001";
        Assert.Equal(10000000000 - 200, Convert.ToInt64(check.ExecuteScalar()));
        check.CommandText = "SELECT Quantity FROM WorldItem WHERE Id=$item";
        check.Parameters.AddWithValue("$item", item);
        Assert.Equal(50, Convert.ToInt32(check.ExecuteScalar()));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void LegacySqlTransactionsSeeCommittedResidentRowsAndPublishOnlyTheirOwnCommittedChanges()
    {
        using CapsuleerHaulerTests.Fixture fixture = new();
        Fund(fixture.Database);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var live = host.GetRequiredService<LiveWorld>();
        long item;
        using (var transaction = live.Economy.Begin())
        {
            item = transaction.NextItemId();
            transaction.Add(
                new OwnedItem
                {
                    Id = item,
                    OwnerId = Human,
                    TypeId = Ammunition,
                    LocationId = Station,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = 100,
                }
            );
            transaction.Set<PilotWallet>().Find(Human)!.Cents -= 100;
            transaction.Commit();
        }
        using (WorldDbContext world = new(fixture.Database.Path))
        using (var transaction = world.Database.BeginTransaction())
        {
            Assert.Equal(100, world.Set<OwnedItem>().Find(item)!.Quantity);
            world.Set<OwnedItem>().Find(item)!.Quantity = 1;
            world.Set<PilotWallet>().Find(Human)!.Cents = 0;
            world.SaveChanges();
            transaction.Rollback();
        }
        using (var transaction = live.Economy.Begin())
        {
            Assert.Equal(100, transaction.Set<OwnedItem>().Find(item)!.Quantity);
            Assert.Equal(10000000000 - 100, transaction.Set<PilotWallet>().Find(Human)!.Cents);
        }
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            world.Set<OwnedItem>().Find(item)!.Quantity = 25;
            world.SaveChanges();
        }
        using var read = live.Economy.Begin();
        Assert.Equal(25, read.Set<OwnedItem>().Find(item)!.Quantity);
    }

    [Fact]
    public void ExpiryAndAcknowledgedMarketChangesReadResidentIndexesWithoutMaterializingHistory()
    {
        using PersonalMarketTests.Fixture fixture = new(live: true);
        fixture.List();
        var market = fixture.Market;
        var order = fixture.Order();
        long expires = order.Issued + TimeSpan.FromDays(order.DurationDays).Ticks;
        long head = market.Observe(Human);
        Assert.True(head > 0);
        Assert.Equal(expires, market.NextExpiry);
        var live = fixture.Db.Services.GetRequiredService<LiveWorld>();
        using (var transaction = live.Economy.Begin())
        {
            Assert.Equal(expires, transaction.NextExpiry);
            Assert.Equal(head, transaction.MarketHead(Human));
            Assert.Equal(0, transaction.Materializations);
        }
        using (var transaction = live.Economy.Begin())
        {
            transaction.Set<SavedPersonalOrder>().Find(order.Id)!.Expires += TimeSpan.TicksPerDay;
            transaction.Commit();
        }
        Assert.Equal(expires + TimeSpan.TicksPerDay, market.NextExpiry);
        using var metrics = SessionPerformance.Begin("idle resident market deadlines", _ => { }, force: true)!;
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(head, market.Observe(Human));
            Assert.Empty(market.Changes(Human, head));
            Assert.Equal(expires + TimeSpan.TicksPerDay, market.NextExpiry);
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void RegisteredResidentRowsCoverEveryScalarColumnOfTheirSavedModels()
    {
        using CapsuleerHaulerTests.Fixture fixture = new();
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using WorldDbContext world = new(fixture.Database.Path);
        foreach (var mapping in host.GetRequiredService<LiveWorld>().Economy.Mappings)
        {
            var entity = world.Model.FindEntityType(mapping.Key)!;
            Assert.Equal(entity.GetTableName(), mapping.Value.Name);
            Assert.Equal(entity.GetProperties().Select(p => p.GetColumnName()).Order(StringComparer.Ordinal),
                mapping.Value.Columns.Order(StringComparer.Ordinal));
        }
    }
}
