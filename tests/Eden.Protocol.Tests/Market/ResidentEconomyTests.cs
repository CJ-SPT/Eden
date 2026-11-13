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
                    Points = 30,
                }
            );
            world.SaveChanges();
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
            Assert.Equal(30, earlier.Set<PilotSkill>().Find(otherPilot, firstSkill)!.Points);
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
            Assert.Equal(
                entity.GetProperties().Select(p => p.GetColumnName()).Order(StringComparer.Ordinal),
                mapping.Value.Columns.Order(StringComparer.Ordinal)
            );
        }
    }
}
