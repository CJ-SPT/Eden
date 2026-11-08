using Eden.Data.World.Corporations;
using Eden.Data.World.Economy;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Protocol.Corporations;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Market;

public sealed class LoyaltyStoreTests
{
    private const long Pilot = 90000001;
    private const int Station = 60003760;
    private const int Navy = 1000035;
    private const int MissileOffer = 3587;
    private const int Missile = 27339;
    private const int RequiredMissile = 2506;
    private const long Budget = 10_000_000_000;

    private static TestWorld Create(bool resident = false, TestClock? clock = null)
    {
        var db = new TestWorld(liveWorld: resident, clock: clock);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR REPLACE INTO SourceRecord VALUES ('client/stationoperations','14','{{"services":[7,17,25]}}');
                INSERT OR IGNORE INTO ItemCategory VALUES (8,'Charge'),(20,'Implant'),(9,'Blueprint');
                INSERT OR IGNORE INTO ItemGroup VALUES (384,'Missile',8),(300,'Implant',20),(400,'Blueprint',9);
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES
                    (2506,'Torpedo',384),(27339,'Navy Torpedo',384),(9943,'Implant',300),(17620,'Ship Blueprint',400);
                INSERT OR IGNORE INTO SourceRecord VALUES ('client/types','2506','{{"volume":0.1}}'),
                    ('client/types','27339','{{"volume":0.1}}'),('client/types','9943','{{"volume":1}}');
                """
            );
        }
        using WorldDbContext world = new(db.Path);
        world.Add(new PilotWallet { PilotId = Pilot, Cents = Budget });
        world.Add(
            new SavedLoyaltyBalance
            {
                PilotId = Pilot,
                IssuerCorporationId = Navy,
                Points = 100_000,
            }
        );
        using CatalogDbContext definitions = new(new WorldDatabase(db.Path).CatalogPath);
        InventoryPersistence.MergeOutput(world, definitions, Pilot, Station, (int)InventoryFlag.Hangar, RequiredMissile, 20_000);
        world.SaveChanges();
        return db;
    }

    private static IPilotLoyaltyStore Store(TestWorld db) => db.Services.GetRequiredService<IPilotLoyaltyStore>();

    private static object? Reply(PilotCallResult result) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeStationBindListsOffersAndPurchasesWithCommittedNotificationsAndReopenedState(bool resident)
    {
        TestClock clock = new();
        using var db = Create(resident, clock);
        using CorporationTestClient client = new(db, 1000001);
        var bind = client.CallService(
            "corpStationMgr",
            "MachoBindObject",
            [(long)Station, new object?[] { "DoStandingCheckForStationService", new object?[] { 25L }, null }]
        );
        Assert.IsType<PilotCallResult.Success>(bind);
        var rows = Assert.IsType<List<object?>>(Reply(client.CallService("LPStoreMgr", "GetAvailableOffersFromCorp", [(long)Navy])));
        var offer = rows.Select(row => Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(row).State))
            .Single(row => Equals(row["offerID"], (long)MissileOffer));
        Assert.Equal((long)Navy, offer["corpID"]);
        Assert.Equal((long)Missile, offer["typeID"]);
        Assert.Equal(5000L, offer["qty"]);
        Assert.Equal(5500L, offer["lpCost"]);
        Assert.Equal(5_500_000d, offer["iskCost"]);
        Assert.Equal(
            new object?[] { (long)RequiredMissile, 5000L },
            Assert.IsType<object?[]>(Assert.Single(Assert.IsType<List<object?>>(offer["reqItems"])))
        );
        var monitor = client.Services.GetRequiredService<LoyaltyWalletHandler>();
        monitor.Enter(client.Context);
        client.Context.Notifications.Clear();
        Assert.Equal(true, Reply(client.CallService("LPStoreMgr", "TakeOfferForCharacter", [(long)Navy, (long)MissileOffer, 2L])));
        var points = Assert.Single(client.Context.Notifications.OfType<LoyaltyBalanceChanged>());
        Assert.Equal((Navy, 100_000L, 89_000L), (points.IssuerCorporationId, points.Before, points.After));
        Assert.Equal(89_000_000d, Assert.Single(client.Context.Notifications.OfType<AccountBalanceChanged>()).Balance);
        Assert.Equal(2, client.Context.Notifications.Count(notification => notification.Name == "OnItemChange"));
        client.Context.Notifications.Clear();
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        monitor.Advance(client.Context);
        Assert.Empty(client.Context.Notifications.OfType<LoyaltyBalanceChanged>());
        using (WorldDbContext independent = new(db.Path))
        {
            Assert.Equal(10_000, independent.Set<OwnedItem>().Single(item => item.TypeId == Missile).Quantity);
            Assert.Equal(10_000, independent.Set<OwnedItem>().Single(item => item.TypeId == RequiredMissile).Quantity);
            Assert.Equal(89_000, independent.Set<SavedLoyaltyBalance>().Find(Pilot, Navy)!.Points);
        }
        var entry = Assert.Single(db.Services.GetRequiredService<IPilotWalletReader>().Journal(Pilot).ServicePayments!);
        Assert.Equal(WalletEntryType.LoyaltyStore, entry.Kind);
        Assert.Equal(-1_100_000_000, entry.AmountCents);
        Assert.Equal(8_900_000_000, entry.BalanceCents);
        Assert.Equal(MissileOffer, entry.ContextId);
        Assert.Equal(Navy, entry.CounterpartyId);
        if (resident)
        {
            await LiveWorld.Find(db.Path)!.FlushAsync();
        }
        using SqliteConnection disk = new($"Data Source={db.Path};Mode=ReadOnly;Pooling=false");
        disk.Open();
        using var query = disk.CreateCommand();
        query.CommandText = "SELECT Points FROM WorldLoyaltyWallet WHERE PilotId=90000001 AND IssuerCorporationId=1000035";
        Assert.Equal(89_000L, query.ExecuteScalar());
    }

    [Fact]
    public void SplitStacksAreConsumedInOrderAndRewardsMergeWithoutTouchingOtherOwnersOrLocations()
    {
        using var db = Create();
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<OwnedItem>().Single(item => item.TypeId == RequiredMissile).Quantity = 2000;
            foreach (var stack in new[] { (Pilot, (long)Station, 3000), (90000002L, (long)Station, 5000), (Pilot, 900000001L, 5000) })
            {
                world.Add(
                    new OwnedItem
                    {
                        Id = InventoryPersistence.NextId(world),
                        OwnerId = stack.Item1,
                        LocationId = stack.Item2,
                        TypeId = RequiredMissile,
                        FlagId = (int)InventoryFlag.Hangar,
                        Quantity = stack.Item3,
                    }
                );
            }
            world.SaveChanges();
        }
        var purchase = Store(db).Purchase(Pilot, Navy, MissileOffer, 1);
        Assert.Equal(3, purchase.Changes.Count);
        Assert.All(purchase.Changes.Take(2), change => Assert.Equal(0, change.Item.Quantity));
        using WorldDbContext verify = new(db.Path);
        Assert.Equal(5000, verify.Set<OwnedItem>().Single(item => item.TypeId == Missile).Quantity);
        Assert.Equal(2, verify.Set<OwnedItem>().Count(item => item.TypeId == RequiredMissile));
        Assert.All(verify.Set<OwnedItem>().Where(item => item.TypeId == RequiredMissile), item => Assert.Equal(5000, item.Quantity));
    }

    [Theory]
    [InlineData("lp")]
    [InlineData("isk")]
    [InlineData("items")]
    [InlineData("space")]
    [InlineData("service")]
    [InlineData("overflow")]
    public void RejectedPurchasesLeaveEveryBalanceStackAndJournalUnchanged(string reason)
    {
        using var db = Create();
        using (WorldDbContext world = new(db.Path))
        {
            if (reason == "lp")
            {
                world.Set<SavedLoyaltyBalance>().Find(Pilot, Navy)!.Points = 5499;
            }
            if (reason == "isk")
            {
                world.Set<PilotWallet>().Find(Pilot)!.Cents = 549_999_999;
            }
            if (reason == "items")
            {
                world.Set<OwnedItem>().Single(item => item.TypeId == RequiredMissile).FlagId = (int)InventoryFlag.Cargo;
            }
            if (reason == "overflow")
            {
                using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath);
                InventoryPersistence.MergeOutput(world, catalog, Pilot, Station, (int)InventoryFlag.Hangar, Missile, int.MaxValue);
            }
            world.SaveChanges();
        }
        if (reason == "space")
        {
            var pilot = db.Store.FindPilot(1000001)!;
            db.Store.SetPosition(pilot.ShipId, new(1, 2, 3), pilot.SystemId);
        }
        if (reason == "service")
        {
            using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
            catalog.Database.ExecuteSqlRaw("UPDATE SourceRecord SET Data='{{\"services\":[]}}' WHERE Dataset='client/stationoperations'");
        }
        using WorldDbContext before = new(db.Path);
        long points = before.Set<SavedLoyaltyBalance>().Find(Pilot, Navy)!.Points;
        long cents = before.Set<PilotWallet>().Find(Pilot)!.Cents;
        var quantities = before.Set<OwnedItem>().OrderBy(item => item.Id).Select(item => new { item.Id, item.Quantity }).ToArray();
        if (reason == "overflow")
        {
            Assert.Throws<OverflowException>(() => Store(db).Purchase(Pilot, Navy, MissileOffer, 1));
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => Store(db).Purchase(Pilot, Navy, MissileOffer, 1));
        }
        using WorldDbContext after = new(db.Path);
        Assert.Equal(points, after.Set<SavedLoyaltyBalance>().Find(Pilot, Navy)!.Points);
        Assert.Equal(cents, after.Set<PilotWallet>().Find(Pilot)!.Cents);
        Assert.Equal(quantities, after.Set<OwnedItem>().OrderBy(item => item.Id).Select(item => new { item.Id, item.Quantity }).ToArray());
        Assert.Empty(after.Set<SavedServicePayment>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedReceiptRollsBackPaymentRequiredItemsAndRewardAndEmitsNoNotifications(bool resident)
    {
        using var db = Create(resident);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER fail_lp BEFORE INSERT ON WorldServicePayment BEGIN SELECT RAISE(ABORT,'failed'); END"
            );
        }
        using CorporationTestClient client = new(db, 1000001);
        client.Context.Notifications.Clear();
        Assert.Throws<DbUpdateException>(() =>
            client.CallService("LPStoreMgr", "TakeOfferForCharacter", [(long)Navy, (long)MissileOffer, 1L])
        );
        Assert.Empty(client.Context.Notifications);
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(Budget, saved.Set<PilotWallet>().Find(Pilot)!.Cents);
        Assert.Equal(100_000, saved.Set<SavedLoyaltyBalance>().Find(Pilot, Navy)!.Points);
        Assert.Equal(20_000, saved.Set<OwnedItem>().Single(item => item.TypeId == RequiredMissile).Quantity);
        Assert.DoesNotContain(saved.Set<OwnedItem>(), item => item.TypeId == Missile);
    }

    [Fact]
    public async Task CompetingPurchasesCannotOverdrawFundsOrConsumeTheSameItemsTwice()
    {
        using var db = Create();
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<SavedLoyaltyBalance>().Find(Pilot, Navy)!.Points = 5500;
            world.SaveChanges();
        }
        var results = await Task.WhenAll(
            Enumerable
                .Range(0, 2)
                .Select(_ =>
                    Task.Run(() =>
                    {
                        try
                        {
                            Store(db).Purchase(Pilot, Navy, MissileOffer, 1);
                            return true;
                        }
                        catch (InvalidDataException)
                        {
                            return false;
                        }
                    })
                )
        );
        Assert.Single(results, success => success);
        using WorldDbContext worldAfter = new(db.Path);
        Assert.Equal(0, worldAfter.Set<SavedLoyaltyBalance>().Find(Pilot, Navy)!.Points);
        Assert.Equal(5000, worldAfter.Set<OwnedItem>().Single(item => item.TypeId == Missile).Quantity);
        Assert.Single(worldAfter.Set<SavedServicePayment>());
    }

    [Fact]
    public void MalformedForeignAndUnsupportedRequestsRejectWithoutSpendingAndTheSessionRemainsUsable()
    {
        using var db = Create();
        using (CourierServiceTests.Client unselected = new(db, new TestClock(), 1000001))
        {
            Assert.Same(PilotCallResult.Unsupported, unselected.Call("GetAvailableOffersFromCorp", [(long)Navy], "LPStoreMgr"));
        }
        using CorporationTestClient client = new(db, 1000001);
        foreach (
            var args in new object?[][]
            {
                [(long)Navy, (long)MissileOffer, 0L],
                [(long)Navy, (long)MissileOffer, 1001L],
                [(long)Navy, (long)MissileOffer, 1.5],
                [1000044L, (long)MissileOffer, 1L],
                [(long)Navy, 0L, 1L],
                [(long)Navy, long.MaxValue, 1L],
                [(long)Navy, (long)MissileOffer, 1L, 90000002L],
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(client.CallService("LPStoreMgr", "TakeOfferForCharacter", args));
        }
        Assert.IsType<PilotCallResult.Failure>(
            client.CallService("LPStoreMgr", "TakeOfferForCorporation", [(long)Navy, (long)MissileOffer, 1L])
        );
        Assert.IsType<PilotCallResult.Failure>(
            client.CallService("LPStoreMgr", "GetAvailableOffersFromCorp", [(long)Navy], new() { ["pilotID"] = Pilot })
        );
        Assert.Equal(100_000_000d, Reply(client.CallService("account", "GetCashBalance", [])));
        using WorldDbContext saved = new(db.Path);
        Assert.Empty(saved.Set<SavedServicePayment>());
    }

    [Fact]
    public void OffersWithoutRequiredItemsDeliverImplantsAndUnavailableCopyOffersAreExcluded()
    {
        using var db = Create();
        var offers = Store(db).Offers(Pilot, Navy);
        Assert.Contains(offers, offer => offer.Id == 3414 && offer.TypeId == 9943 && offer.RequiredItems.Count == 0);
        Assert.DoesNotContain(offers, offer => offer.TypeId == 17620);
        Store(db).Purchase(Pilot, Navy, 3414, 1);
        Store(db).Purchase(Pilot, Navy, 3414, 1);
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(2, saved.Set<OwnedItem>().Single(item => item.TypeId == 9943).Quantity);
        Assert.Equal(89_500, saved.Set<SavedLoyaltyBalance>().Find(Pilot, Navy)!.Points);
        Assert.Equal(20_000, saved.Set<OwnedItem>().Single(item => item.TypeId == RequiredMissile).Quantity);
    }

    [Fact]
    public void TypedCostRulesMultiplyAllRequirementsAndRejectArithmeticOverflow()
    {
        LoyaltyOffer offer = new(1, Navy, Missile, 5000, 5500, 550_000_000, [new(RequiredMissile, 5000)]);
        var cost = LoyaltyStoreRules.Cost(offer, 2);
        Assert.Equal((11_000L, 1_100_000_000L, 10_000), (cost.Points, cost.IskCents, cost.Quantity));
        Assert.Equal(new LoyaltyRequiredItem(RequiredMissile, 10_000), Assert.Single(cost.RequiredItems));
        Assert.Throws<InvalidDataException>(() => LoyaltyStoreRules.Cost(offer with { LpCost = long.MaxValue }, 2));
        Assert.Throws<InvalidDataException>(() => LoyaltyStoreRules.Cost(offer with { IskCents = long.MaxValue }, 2));
        Assert.Throws<InvalidDataException>(() => LoyaltyStoreRules.Cost(offer with { Quantity = int.MaxValue }, 2));
        Assert.Throws<InvalidDataException>(() =>
            LoyaltyStoreRules.Cost(offer with { RequiredItems = [new(RequiredMissile, int.MaxValue)] }, 2)
        );
        Assert.Throws<InvalidDataException>(() => LoyaltyStoreRules.Cost(offer, 0));
        Assert.Throws<InvalidDataException>(() => LoyaltyStoreRules.Cost(offer, 1001));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorporatePurchasesUseAuthorizedSelectedDivisionAndCorporateLpWithPersonalItemInputs(bool resident)
    {
        using var db = Create(resident);
        int corporation = CorporationAdministrationTests.CreateAndJoin(db);
        var wallet = db.Services.GetRequiredService<ICorporationWallet>();
        using (WorldDbContext world = new(db.Path))
        {
            world.Add(
                new SavedCorporationLoyaltyBalance
                {
                    CorporationId = corporation,
                    IssuerCorporationId = Navy,
                    Points = 5500,
                }
            );
            world.Add(
                new SavedCorporationWallet
                {
                    CorporationId = corporation,
                    Key = 1001,
                    BalanceCents = 550_000_000,
                }
            );
            world.SaveChanges();
        }
        wallet.SelectDivision(Pilot, 1001);
        using CorporationTestClient ceo = new(db, 1000001);
        using CorporationTestClient member = new(db, 1000002);
        Assert.IsType<PilotCallResult.Failure>(
            member.CallService("LPStoreMgr", "TakeOfferForCorporation", [(long)Navy, (long)MissileOffer, 1L])
        );
        ceo.Context.Notifications.Clear();
        Assert.Equal(true, Reply(ceo.CallService("LPStoreMgr", "TakeOfferForCorporation", [(long)Navy, (long)MissileOffer, 1L])));
        Assert.Equal(0, wallet.Balance(Pilot, 1001));
        var cash = Assert.Single(ceo.Context.Notifications.OfType<CorporationBalanceChanged>());
        Assert.Equal((corporation, 1001, 0L), (cash.CorporationId, cash.Key, cash.BalanceCents));
        Assert.Single(ceo.Context.Notifications.OfType<CorporationLoyaltyBalanceChanged>());
        Assert.Empty(ceo.Context.Notifications.OfType<AccountBalanceChanged>());
        Assert.Empty(ceo.Context.Notifications.OfType<LoyaltyBalanceChanged>());
        ceo.Context.Notifications.Clear();
        ceo.Services.GetRequiredService<CorporationWalletHandler>().Advance(ceo.Context);
        Assert.Empty(ceo.Context.Notifications.OfType<CorporationBalanceChanged>());
        using WorldDbContext verify = new(db.Path);
        Assert.Equal(100_000, verify.Set<SavedLoyaltyBalance>().Find(Pilot, Navy)!.Points);
        Assert.Equal(0, verify.Set<SavedCorporationLoyaltyBalance>().Find(corporation, Navy)!.Points);
        Assert.Equal(Pilot, verify.Set<OwnedItem>().Single(item => item.TypeId == Missile).OwnerId);
        Assert.Equal(15_000, verify.Set<OwnedItem>().Single(item => item.TypeId == RequiredMissile).Quantity);
        Assert.Contains(
            wallet.Journal(Pilot, true, 1001),
            entry =>
                entry.EntryType == WalletEntryType.LoyaltyStore && entry.AmountCents == -550_000_000 && entry.ReferenceId == MissileOffer
        );
    }

    [Fact]
    public void NativeConcordExchangeUsesCatalogRateRoundsUpAndCannotOverdrawOrCreditAnIneligibleIssuer()
    {
        TestClock clock = new();
        using var db = Create(true, clock);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO NpcCorporation VALUES (1000125,'CONCORD');
                INSERT OR REPLACE INTO SourceRecord VALUES ('client/npccorporations','1000125',
                    '{{"exchangeRates":{{"1000035":0.800000011920929}}}}');
                """
            );
        }
        using (WorldDbContext world = new(db.Path))
        {
            world.Add(
                new SavedLoyaltyBalance
                {
                    PilotId = Pilot,
                    IssuerCorporationId = LoyaltyStoreRules.ConcordCorporation,
                    Points = 10,
                }
            );
            world.SaveChanges();
        }
        using CorporationTestClient client = new(db, 1000001);
        client.Context.Notifications.Clear();
        Assert.Null(Reply(client.CallService("LPSvc", "ExchangeConcordLP", [(long)Navy, 7L])));
        var changed = client.Context.Notifications.OfType<LoyaltyBalanceChanged>().ToArray();
        Assert.Equal(2, changed.Length);
        Assert.Contains(changed, change => change.IssuerCorporationId == LoyaltyStoreRules.ConcordCorporation && change.After == 1);
        Assert.Contains(changed, change => change.IssuerCorporationId == Navy && change.After == 100_007);
        foreach (var args in new object?[][] { [(long)Navy, 1L], [1000044L, 1L], [(long)Navy, 0L], [(long)Navy, long.MaxValue] })
        {
            Assert.IsType<PilotCallResult.Failure>(client.CallService("LPSvc", "ExchangeConcordLP", args));
        }
        client.Context.Notifications.Clear();
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        client.Services.GetRequiredService<LoyaltyWalletHandler>().Advance(client.Context);
        Assert.Empty(client.Context.Notifications.OfType<LoyaltyBalanceChanged>());
        using WorldDbContext verify = new(db.Path);
        Assert.Equal(1, verify.Set<SavedLoyaltyBalance>().Find(Pilot, LoyaltyStoreRules.ConcordCorporation)!.Points);
        Assert.Equal(100_007, verify.Set<SavedLoyaltyBalance>().Find(Pilot, Navy)!.Points);
        Assert.Equal(Budget, verify.Set<PilotWallet>().Find(Pilot)!.Cents);
        Assert.Equal(2L, LoyaltyStoreRules.ExchangeCost(1, 0.8m));
        Assert.Equal(5L, LoyaltyStoreRules.ExchangeCost(2, 0.4m));
        Assert.Throws<InvalidDataException>(() => LoyaltyStoreRules.ExchangeCost(long.MaxValue, 0.4m));
    }

    [Fact]
    public void ShutdownAndRestartRetainPurchasedInventoryBalancesAndJournal()
    {
        using var db = Create(true);
        Store(db).Purchase(Pilot, Navy, MissileOffer, 1);
        db.Services.Dispose();
        WorldInitialization.Open(db.Path);
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(94_500, saved.Set<SavedLoyaltyBalance>().Find(Pilot, Navy)!.Points);
        Assert.Equal(Budget - 550_000_000, saved.Set<PilotWallet>().Find(Pilot)!.Cents);
        Assert.Equal(5000, saved.Set<OwnedItem>().Single(item => item.TypeId == Missile).Quantity);
        Assert.Equal(WalletEntryType.LoyaltyStore, Assert.Single(saved.Set<SavedServicePayment>()).Kind);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("space")]
    [InlineData("overflow")]
    public void RejectedConcordExchangesPreserveBothIssuerBalances(string reason)
    {
        using var db = Create();
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO NpcCorporation VALUES (1000125,'CONCORD');
                INSERT OR REPLACE INTO SourceRecord VALUES ('client/npccorporations','1000125',
                    '{{"exchangeRates":{{"1000035":0.4}}}}');
                """
            );
        }
        long before = reason == "overflow" ? long.MaxValue : 100_000;
        using (WorldDbContext world = new(db.Path))
        {
            world.Add(
                new SavedLoyaltyBalance
                {
                    PilotId = Pilot,
                    IssuerCorporationId = LoyaltyStoreRules.ConcordCorporation,
                    Points = 100,
                }
            );
            world.Set<SavedLoyaltyBalance>().Find(Pilot, Navy)!.Points = before;
            world.SaveChanges();
            if (reason == "write")
            {
                world.Database.ExecuteSqlRaw(
                    "CREATE TRIGGER fail_exchange BEFORE UPDATE ON WorldLoyaltyWallet BEGIN SELECT RAISE(ABORT,'failed'); END"
                );
            }
        }
        if (reason == "space")
        {
            var pilot = db.Store.FindPilot(1000001)!;
            db.Store.SetPosition(pilot.ShipId, new(1, 2, 3), pilot.SystemId);
        }
        if (reason == "write")
        {
            Assert.Throws<DbUpdateException>(() => Store(db).ExchangeConcord(Pilot, Navy, 1));
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => Store(db).ExchangeConcord(Pilot, Navy, 1));
        }
        using WorldDbContext verify = new(db.Path);
        Assert.Equal(before, verify.Set<SavedLoyaltyBalance>().Find(Pilot, Navy)!.Points);
        Assert.Equal(100, verify.Set<SavedLoyaltyBalance>().Find(Pilot, LoyaltyStoreRules.ConcordCorporation)!.Points);
    }
}
