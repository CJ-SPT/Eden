using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Tests.Capsuleers;
using Eden.Protocol.Tests.Persistence;
using Eden.Server;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
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
    public void WarmWalletReadsFollowCommittedResidentAndSqlWrites()
    {
        using TestWorld fixture = new(liveWorld: true);
        var wallet = fixture.Services.GetRequiredService<IPilotWalletReader>();
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        Assert.Equal(0, wallet.Balance(Human));

        using (var action = live.Economy.Begin())
        {
            action.Add(new PilotWallet { PilotId = Human, Cents = 100 });
            action.Commit();
        }
        Assert.Equal(100, wallet.Balance(Human));
        using (var action = live.Economy.Begin())
        {
            action.Set<PilotWallet>().Find(Human)!.Cents = 200;
        }
        Assert.Equal(100, wallet.Balance(Human));

        using (WorldDbContext world = new(fixture.Path))
        {
            using var transaction = world.Database.BeginTransaction();
            world.Set<PilotWallet>().Find(Human)!.Cents = 300;
            world.SaveChanges();
            transaction.Rollback();
        }
        Assert.Equal(100, wallet.Balance(Human));
        using (WorldDbContext world = new(fixture.Path))
        {
            world.Set<PilotWallet>().Find(Human)!.Cents = 400;
            world.SaveChanges();
        }
        Assert.Equal(400, wallet.Balance(Human));
        using (WorldDbContext world = new(fixture.Path))
        {
            world.Remove(world.Set<PilotWallet>().Find(Human)!);
            world.SaveChanges();
        }
        Assert.Equal(0, wallet.Balance(Human));
    }

    [Fact]
    public void EmptyCommittedIndexStillFindsPrivateWalletChanges()
    {
        using TestWorld fixture = new(liveWorld: true);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        using var action = live.Economy.Begin();
        long selected = Human;
        var wallets = action.Set<PilotWallet>().Where(wallet => wallet.PilotId == selected);
        var balances = wallets.Select(wallet => wallet.Cents);
        Assert.Empty(wallets);
        Assert.Empty(balances);
        var added = action.Add(new PilotWallet { PilotId = Human, Cents = 100 }).Entity;
        Assert.Same(added, Assert.Single(wallets));
        Assert.Equal(100, Assert.Single(balances));
        added.Cents = 200;
        Assert.Equal(200, Assert.Single(balances));
        selected++;
        Assert.Empty(balances);
        selected = Human;
        action.Remove(added);
        Assert.Empty(wallets);
        Assert.Empty(balances);
    }

    [Fact]
    public async Task CompactSkillRowsPreservePilotIdentityAndCommittedCheckpointGenerations()
    {
        const long otherPilot = 90000002;
        const int firstSkill = 3300;
        const int secondSkill = 3301;
        const long largeBalance = (long)int.MaxValue + 1;
        using TestWorld fixture = new(liveWorld: true);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        using (WorldDbContext world = new(fixture.Path))
        {
            world.Database.ExecuteSql($"DELETE FROM WorldPilotSkill WHERE PilotId IN ({Human}, {otherPilot})");
            world.AddRange(
                new PilotSkill
                {
                    PilotId = Human,
                    TypeId = firstSkill,
                    Points = 10,
                },
                new PilotSkill
                {
                    PilotId = otherPilot,
                    TypeId = firstSkill,
                    Points = 10,
                }
            );
            world.SaveChanges();
        }

        Assert.Equal(new[] { 10L }, Balances(Human));
        Assert.Equal(new[] { 10L }, Balances(otherPilot));
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            var skills = action.Set<PilotSkill>();
            skills.Remove(skills.Find(Human, firstSkill)!);
            skills.Add(
                new PilotSkill
                {
                    PilotId = Human,
                    TypeId = secondSkill,
                    Points = largeBalance,
                }
            );
            Assert.Equal(largeBalance, skills.Where(s => s.PilotId == Human).Select(s => s.Points).Single());
        }
        Assert.Equal(new[] { 10L }, Balances(Human));
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            var skills = action.Set<PilotSkill>();
            skills.Remove(skills.Find(Human, firstSkill)!);
            skills.Add(
                new PilotSkill
                {
                    PilotId = Human,
                    TypeId = secondSkill,
                    Points = largeBalance,
                }
            );
            action.Commit();
        }
        Assert.Equal(new[] { largeBalance }, Balances(Human));
        Assert.Equal(new[] { 10L }, Balances(otherPilot));
        using var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        using (WorldDbContext world = new(fixture.Path))
        {
            Assert.Equal(largeBalance, world.Set<PilotSkill>().Where(s => s.PilotId == Human).Select(s => s.Points).Single());
            world.Set<PilotSkill>().Where(s => s.PilotId == Human).ExecuteUpdate(update => update.SetProperty(s => s.Points, 50));
            world.Set<PilotSkill>().Where(s => s.PilotId == otherPilot).ExecuteDelete();
        }
        using (var metrics = SessionPerformance.Begin("compacted skill reads", _ => { }, force: true)!)
        {
            Assert.Equal(new[] { 50L }, Balances(Human));
            Assert.Empty(Balances(otherPilot));
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }

        snapshot.Project();
        using (WorldDbContext earlier = new(snapshot.Database))
        {
            Assert.Null(earlier.Set<PilotSkill>().Find(Human, firstSkill));
            Assert.Equal(largeBalance, earlier.Set<PilotSkill>().Find(Human, secondSkill)!.Points);
            Assert.Equal(10, earlier.Set<PilotSkill>().Find(otherPilot, firstSkill)!.Points);
        }
        await live.FlushAsync();
        using SqliteConnection disk = new($"Data Source={fixture.Path};Mode=ReadOnly;Pooling=False");
        using WorldDbContext saved = new(disk);
        Assert.Equal(50, saved.Set<PilotSkill>().Find(Human, secondSkill)!.Points);
        Assert.Null(saved.Set<PilotSkill>().Find(otherPilot, firstSkill));

        long[] Balances(long pilot) =>
            live.Economy.Inspect(world => world.Set<PilotSkill>().Where(skill => skill.PilotId == pilot).Select(s => s.Points).ToArray());
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

    [Fact]
    public async Task ResidentRowsTrackReversionsAndUseCommittedSqlRowIdsAfterProjection()
    {
        using CapsuleerHaulerTests.Fixture fixture = new();
        long itemId = InventoryDefinitions.FirstOwnedItemId + 10000;
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            world.Add(
                new OwnedItem
                {
                    Id = itemId,
                    OwnerId = Human,
                    TypeId = Ammunition,
                    LocationId = Station,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = 10,
                }
            );
            world.SaveChanges();
        }
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var live = host.GetRequiredService<LiveWorld>();
        var items = live.Economy.Mappings[typeof(OwnedItem)];
        SetQuantity(15);
        using var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        Assert.Equal(1, items.Capture().Count);
        SetQuantity(10);
        Assert.False(items.HasChanges);

        SetQuantity(20);
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            Assert.Equal(20, world.Set<OwnedItem>().Where(item => item.Id == itemId).Select(item => item.Quantity).Single());
            world.Set<OwnedItem>().Where(item => item.Id == itemId).ExecuteUpdate(update => update.SetProperty(item => item.Quantity, 25));
        }
        Assert.False(items.HasChanges);
        using (var read = live.Economy.Begin())
        {
            Assert.Equal(25, read.Set<OwnedItem>().Find(itemId)!.Quantity);
        }
        snapshot.Project();
        using (WorldDbContext earlier = new(snapshot.Database))
        {
            Assert.Equal(15, earlier.Set<OwnedItem>().Find(itemId)!.Quantity);
        }
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            world.Set<OwnedItem>().Where(item => item.Id == itemId).ExecuteDelete();
        }
        using (var read = live.Economy.Begin())
        {
            Assert.Null(read.Set<OwnedItem>().Find(itemId));
        }
        await live.FlushAsync();
        using SqliteConnection disk = new($"Data Source={fixture.Database.Path};Mode=ReadOnly;Pooling=False");
        using WorldDbContext saved = new(disk);
        Assert.Null(saved.Set<OwnedItem>().Find(itemId));

        void SetQuantity(int quantity)
        {
            using var action = live.Economy.Begin();
            action.Set<OwnedItem>().Find(itemId)!.Quantity = quantity;
            action.Commit();
        }
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
    public async Task CheckpointRebasesOnlyDurableMarketRowsThatStillMatchTheCapturedGeneration()
    {
        using CapsuleerHaulerTests.Fixture fixture = new();
        const int orderCount = 257;
        const int initialStock = 10;
        long changed = 0;
        int attempts = 0;
        LiveWorld? running = null;
        using (
            LiveWorld live = new(
                fixture.Database,
                new CheckpointClock(fixture.Clock),
                _ => { },
                (snapshot, path) =>
                {
                    if (++attempts == 1)
                    {
                        throw new IOException("Injected market save failure.");
                    }
                    if (attempts == 2)
                    {
                        using var action = running!.Economy.Begin();
                        action.Set<NpcMarketOrder>().Find(changed)!.Remaining = 2;
                        action.Commit();
                    }
                    LiveWorld.PersistSnapshot(snapshot, path);
                }
            )
        )
        {
            running = live;
            live.MeasureCheckpoints = true;
            using (var action = live.Economy.Begin())
            {
                for (int index = 0; index < orderCount; index++)
                {
                    var order = action.Add(CheckpointOrder(fixture, 1000 + index, initialStock)).Entity;
                    if (index == 0)
                    {
                        changed = order.Id;
                    }
                }
                action.Commit();
            }

            await Assert.ThrowsAsync<IOException>(live.FlushAsync);
            Assert.Equal(orderCount, MarketProjection(live).Count);
            await live.FlushAsync();
            Assert.Equal(orderCount, live.LastCheckpoint!.ProjectionTables!.Single(table => table.Table == "WorldMarketOrder").Rows);
            Assert.Equal(changed, Assert.Single(MarketProjection(live).Rows).Key.Id);
            using (var disk = WorldConnections.FileConnection(fixture.Database.Path))
            {
                disk.Open();
                using var check = disk.CreateCommand();
                check.CommandText = "SELECT Remaining FROM WorldMarketOrder WHERE Id=$id";
                check.Parameters.AddWithValue("$id", changed);
                Assert.Equal((long)initialStock, check.ExecuteScalar());
            }

            await live.FlushAsync();
            Assert.Equal(1, live.LastCheckpoint!.ProjectionTables!.Single(table => table.Table == "WorldMarketOrder").Rows);
            Assert.DoesNotContain(live.Economy.Capture().Tables, projection => projection.Table == "WorldMarketOrder");
            await live.FlushAsync();
        }
        Assert.Equal(3, attempts);

        using LiveWorld reopened = new(fixture.Database, new CheckpointClock(fixture.Clock), _ => { });
        using var inspect = reopened.Economy.Begin();
        var orders = inspect.Set<NpcMarketOrder>().Where(order => order.Kind == NpcMarketKind.Standard).ToArray();
        Assert.Equal(orderCount, orders.Length);
        Assert.Equal(2, orders.Single(order => order.Id == changed).Remaining);
        Assert.Equal(orderCount - 1, orders.Count(order => order.Remaining == initialStock));

        static EconomyProjection<EconomyNpcMarketOrder> MarketProjection(LiveWorld live) =>
            live.Economy.Capture().Tables.OfType<EconomyProjection<EconomyNpcMarketOrder>>().Single();
    }

    [Fact]
    public async Task CheckpointRetainsMarketSlotReplacementWhenCompatibilityRebaseEncountersTheOldOrder()
    {
        using CapsuleerHaulerTests.Fixture fixture = new();
        long originalId;
        long replacementId;
        using (LiveWorld live = new(fixture.Database, new CheckpointClock(fixture.Clock), _ => { }))
        {
            using (var action = live.Economy.Begin())
            {
                originalId = action.Add(CheckpointOrder(fixture, 1000, 10)).Entity.Id;
                action.Commit();
            }
            await live.FlushAsync();
            Assert.DoesNotContain(live.Economy.Capture().Tables, projection => projection.Table == "WorldMarketOrder");

            using (var action = live.Economy.Begin())
            {
                action.Remove(action.Set<NpcMarketOrder>().Find(originalId)!);
                replacementId = action.Add(CheckpointOrder(fixture, 1000, 4)).Entity.Id;
                action.Commit();
            }
            await live.FlushAsync();
            Assert.Null(live.LastSaveFailure);
            var pending = live.Economy.Capture().Tables.OfType<EconomyProjection<EconomyNpcMarketOrder>>().Single();
            Assert.Contains(pending.Rows, row => row.Key.Id == originalId && row.Value is null);
            Assert.Contains(pending.Rows, row => row.Key.Id == replacementId && row.Value?.Remaining == 4);
        }

        using LiveWorld reopened = new(fixture.Database, new CheckpointClock(fixture.Clock), _ => { });
        using var inspect = reopened.Economy.Begin();
        Assert.Null(inspect.Set<NpcMarketOrder>().Find(originalId));
        Assert.Equal(4, inspect.Set<NpcMarketOrder>().Find(replacementId)!.Remaining);
    }

    private static NpcMarketOrder CheckpointOrder(CapsuleerHaulerTests.Fixture fixture, int sourceOrder, int stock) =>
        new()
        {
            StationId = Station,
            TypeId = Ammunition,
            Kind = NpcMarketKind.Standard,
            OwnerId = 1_000_001,
            SourceOrderId = sourceOrder,
            PriceCents = sourceOrder + 1,
            Remaining = stock,
            Entered = stock,
            Issued = fixture.Clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(),
        };

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
    public async Task ExpiryAndAcknowledgedMarketChangesReadResidentIndexesWithoutMaterializingHistory()
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

        const int RemoteSystem = 30000144;
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new();
        var holder = Task.Run(() =>
        {
            using var remote = live.Enter("unrelated simulation", RemoteSystem);
            held.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(20)));
        });
        Task? polling = null;
        try
        {
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            polling = Task.Run(() =>
            {
                using var metrics = SessionPerformance.Begin("idle resident market deadlines", _ => { }, force: true)!;
                Assert.Equal(head, Assert.Single(market.Changes(Human, 0)).Id);
                for (int i = 0; i < 20; i++)
                {
                    Assert.Equal(head, market.Observe(Human));
                    Assert.Empty(market.Changes(Human, head));
                    Assert.Equal(expires + TimeSpan.TicksPerDay, market.NextExpiry);
                }
                Assert.Equal(0, metrics.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
            });
            await polling.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            await Task.WhenAll(holder, polling ?? Task.CompletedTask);
        }

        using (var uncommitted = live.Economy.Begin(TransactionParticipants.None))
        {
            uncommitted.Remove(uncommitted.Set<SavedMarketChange>().Find(head)!);
        }
        Assert.Equal(head, Assert.Single(market.Changes(Human, 0)).Id);
        using (var committed = live.Economy.Begin(TransactionParticipants.None))
        {
            committed.Remove(committed.Set<SavedMarketChange>().Find(head)!);
            committed.Commit();
        }
        Assert.Equal(0, market.Observe(Human));
        Assert.Empty(market.Changes(Human, 0));
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
            Assert.Equal(
                entity.GetProperties().Select(p => p.GetColumnName()).Order(StringComparer.Ordinal),
                mapping
                    .Value.Columns.Select(column =>
                        // Nullable blobs use two SQL expressions for one saved column.
                        column is "hex(Binary)" or "Binary IS NULL"
                            ? "Binary"
                            : column.Trim('"')
                    )
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
            );
        }
    }
}
