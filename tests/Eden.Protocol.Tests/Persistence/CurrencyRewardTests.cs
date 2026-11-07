using Eden.Data.Catalog;
using Eden.Data.Inventory;
using Eden.Data.Market;
using Eden.Data.Redemption;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Redemption;
using Eden.Gameplay.Skills;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Redemption;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

internal static class CurrencyTestData
{
    internal static TestWorld Create(TestClock clock)
    {
        var db = FreePointTestData.Create(clock);
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO ItemCategory VALUES (5,'Trading');
            INSERT OR IGNORE INTO ItemGroup VALUES (17,'Money',4),(24,'Voucher',5);
            INSERT INTO ItemType(Id,Name,GroupId) VALUES (52996,'InterStellar Kredits (ISK)',17),(73277,'EverMarks',24);
            INSERT INTO SourceRecord VALUES ('client/types','52996','{{"volume":0}}'),('client/types','73277','{{"volume":0}}');
            INSERT OR IGNORE INTO NpcCorporation VALUES (1000419,'Paragon');
            INSERT OR REPLACE INTO SourceRecord VALUES ('client/accountingentrytypes','147',
                '{{"entryTypeNameID":552388,"name":"Redeemed ISK token",
                    "description":"The player redeemed an ISK token to their wallet"}}');
            """
        );
        return db;
    }

    internal static RedeemToken Grant(TestWorld db, RedeemCurrencyType type, int quantity, string reference = "currency") =>
        RedemptionTests.Store(db).Grant(RedemptionTests.Grant(reference, (int)type, quantity));

    internal static RedeemResult Claim(TestWorld db, params RedeemToken[] tokens) =>
        RedemptionTests
            .Store(db)
            .Claim(
                RedemptionTests.Account,
                FreePointTestData.Pilot,
                tokens.Select(t => new RedeemSelection(t.Id, t.TypeId)).ToArray(),
                false
            );

    internal static long Cash(TestWorld db) => db.Services.GetRequiredService<IPilotWalletReader>().Balance(FreePointTestData.Pilot);

    internal static IReadOnlyList<LoyaltyBalance> Loyalty(TestWorld db) =>
        db.Services.GetRequiredService<IPilotLoyaltyWallet>().Read(FreePointTestData.Pilot);
}

public sealed class CurrencyRewardTests
{
    [Theory]
    [InlineData(RedeemCurrencyType.Isk, 1, 100L)]
    [InlineData(RedeemCurrencyType.Isk, 23, 2300L)]
    [InlineData(RedeemCurrencyType.Isk, int.MaxValue, 214748364700L)]
    [InlineData(RedeemCurrencyType.EverMarks, 1, 1L)]
    [InlineData(RedeemCurrencyType.EverMarks, 23, 23L)]
    [InlineData(RedeemCurrencyType.EverMarks, int.MaxValue, 2147483647L)]
    public void NativeCurrencyQuantitiesCreditTheCorrectWalletOnceAndNeverCreateInventory(
        RedeemCurrencyType type,
        int quantity,
        long amount
    )
    {
        var clock = CalendarTestData.Clock();
        using var db = CurrencyTestData.Create(clock);
        var token = CurrencyTestData.Grant(db, type, quantity);
        var result = CurrencyTestData.Claim(db, token);
        var credit = Assert.Single(result.Currencies);
        Assert.Equal(amount, credit.Amount);
        Assert.Equal(0, credit.Before);
        Assert.Equal(amount, credit.After);
        Assert.Equal(clock.Now, credit.Time);
        Assert.Empty(result.Items);
        Assert.Empty(result.Errors);
        if (type == RedeemCurrencyType.Isk)
        {
            Assert.Equal(amount, CurrencyTestData.Cash(db));
            Assert.Empty(CurrencyTestData.Loyalty(db));
        }
        else
        {
            Assert.Equal(new LoyaltyBalance(1000419, amount), Assert.Single(CurrencyTestData.Loyalty(db)));
            Assert.Equal(0, CurrencyTestData.Cash(db));
        }
        Assert.Single(CurrencyTestData.Claim(db, token).Errors);
        Assert.Equal(token, CurrencyTestData.Grant(db, type, quantity));
        using WorldDbContext world = new(db.Path);
        Assert.DoesNotContain(world.Set<OwnedItem>(), i => i.TypeId == (int)type);
        Assert.Null(world.Set<SavedRedeemToken>().Find(token.Id)!.ItemId);
        Assert.Single(world.Set<SavedRedeemedCurrency>());
    }

    [Fact]
    public void MixedAutomaticRewardsWorkInSpaceAndWalletHistorySurvivesReopen()
    {
        var clock = CalendarTestData.Clock();
        using var db = CurrencyTestData.Create(clock);
        var pilot = db.Store.FindPilot(RedemptionTests.Account)!;
        db.Store.SetPosition(pilot.ShipId, new(1, 2, 3), pilot.SystemId);
        var cash = CurrencyTestData.Grant(db, RedeemCurrencyType.Isk, 100, "cash");
        var marks = CurrencyTestData.Grant(db, RedeemCurrencyType.EverMarks, 50, "marks");
        var sp = RedemptionTests.Store(db).Grant(RedemptionTests.Grant("sp", 54652, 1));
        var mineral = RedemptionTests.Store(db).Grant(RedemptionTests.Grant("mineral", 34, 10));
        var result = CurrencyTestData.Claim(db, cash, marks, sp, mineral);
        Assert.Equal(2, result.Currencies.Count);
        Assert.Equal(1000, result.SkillPoints);
        Assert.Empty(result.Items);
        Assert.Equal(mineral.Id, Assert.Single(result.Errors).TokenId);
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path), clock);
        var wallet = reopened.GetRequiredService<IPilotWalletReader>();
        Assert.Equal(10000, wallet.Balance(FreePointTestData.Pilot));
        Assert.Equal(
            new LoyaltyBalance(1000419, 50),
            Assert.Single(reopened.GetRequiredService<IPilotLoyaltyWallet>().Read(FreePointTestData.Pilot))
        );
        Assert.Equal(1000, reopened.GetRequiredService<IPilotFreeSkillPoints>().Balance(FreePointTestData.Pilot));
        Assert.Equal(cash.Id, Assert.Single(wallet.Journal(FreePointTestData.Pilot).RedeemedCurrencies!).TokenId);
        Assert.Empty(reopened.GetRequiredService<IPilotLoyaltyWallet>().Read(90000002));
        Assert.Empty(wallet.Journal(90000002).RedeemedCurrencies!);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("receipt")]
    public void AFailedCurrencyCommitRollsBackMoneyPointsInventoryReceiptsAndTokenConsumption(string failure)
    {
        var clock = CalendarTestData.Clock();
        using var db = CurrencyTestData.Create(clock);
        var cash = CurrencyTestData.Grant(db, RedeemCurrencyType.Isk, 100, "cash");
        var marks = CurrencyTestData.Grant(db, RedeemCurrencyType.EverMarks, 50, "marks");
        var sp = RedemptionTests.Store(db).Grant(RedemptionTests.Grant("sp", 54652, 1));
        var mineral = RedemptionTests.Store(db).Grant(RedemptionTests.Grant("mineral", 34, 10));
        using var connection = new SqliteConnection($"Data Source={db.Path};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            failure == "token"
                ? $"CREATE TRIGGER fail_currency BEFORE UPDATE ON WorldRedeemToken WHEN NEW.Id={marks.Id} "
                    + "BEGIN SELECT RAISE(ABORT,'token failed'); END"
                : "CREATE TRIGGER fail_currency BEFORE INSERT ON WorldRedeemedCurrency BEGIN SELECT RAISE(ABORT,'receipt failed'); END";
        command.ExecuteNonQuery();
        Assert.Throws<DbUpdateException>(() => CurrencyTestData.Claim(db, cash, marks, sp, mineral));
        Assert.Equal(0, CurrencyTestData.Cash(db));
        Assert.Empty(CurrencyTestData.Loyalty(db));
        Assert.Equal(new FreeSkillPointBalance(0, 0), FreePointTestData.Points(db).Observe(FreePointTestData.Pilot));
        Assert.Equal(4, RedemptionTests.Store(db).Read(RedemptionTests.Account).Count);
        using WorldDbContext world = new(db.Path);
        Assert.Empty(world.Set<SavedRedeemedCurrency>());
        Assert.DoesNotContain(world.Set<OwnedItem>(), i => i.TypeId is 52996 or 73277 or 34);
    }

    [Theory]
    [InlineData(RedeemCurrencyType.Isk)]
    [InlineData(RedeemCurrencyType.EverMarks)]
    public async Task ConcurrentClaimsCannotCreditTheSameTokenTwice(RedeemCurrencyType type)
    {
        var clock = CalendarTestData.Clock();
        using var db = CurrencyTestData.Create(clock);
        var token = CurrencyTestData.Grant(db, type, 10);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => CurrencyTestData.Claim(db, token))));
        Assert.Single(results.SelectMany(r => r.Currencies));
        Assert.Single(results.SelectMany(r => r.Errors));
        Assert.Equal(type == RedeemCurrencyType.Isk ? 1000 : 0, CurrencyTestData.Cash(db));
        using WorldDbContext world = new(db.Path);
        Assert.Single(world.Set<SavedRedeemedCurrency>());
    }

    [Theory]
    [InlineData(RedeemCurrencyType.Isk)]
    [InlineData(RedeemCurrencyType.EverMarks)]
    public void OverflowOrForeignRecipientsCannotConsumeTheToken(RedeemCurrencyType type)
    {
        var clock = CalendarTestData.Clock();
        using var db = CurrencyTestData.Create(clock);
        var token = CurrencyTestData.Grant(db, type, 1);
        var store = RedemptionTests.Store(db);
        Assert.Throws<InvalidDataException>(() => store.Claim(RedemptionTests.Account, 90000002, [new(token.Id, (int)type)], false));
        using (WorldDbContext world = new(db.Path))
        {
            if (type == RedeemCurrencyType.Isk)
            {
                world.Add(new PilotWallet { PilotId = FreePointTestData.Pilot, Cents = long.MaxValue });
            }
            else
            {
                world.Add(
                    new SavedLoyaltyBalance
                    {
                        PilotId = FreePointTestData.Pilot,
                        IssuerCorporationId = 1000419,
                        Points = long.MaxValue,
                    }
                );
            }
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => CurrencyTestData.Claim(db, token));
        Assert.Equal(token, Assert.Single(store.Read(RedemptionTests.Account)));
        Assert.Throws<InvalidDataException>(() => store.Grant(RedemptionTests.Grant("fixed", (int)type, 1, 60003760)));
        using WorldDbContext verify = new(db.Path);
        Assert.Empty(verify.Set<SavedRedeemedCurrency>());
        Assert.Equal(RedeemTokenState.Pending, verify.Set<SavedRedeemToken>().Find(token.Id)!.State);
    }

    [Fact]
    public void ExpiredCurrencyTokensLeaveEveryWalletUnchanged()
    {
        var clock = CalendarTestData.Clock();
        using var db = CurrencyTestData.Create(clock);
        var store = RedemptionTests.Store(db);
        var token = store.Grant(RedemptionTests.Grant("expired", 52996, 100, expires: clock.Now + TimeSpan.TicksPerSecond));
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        Assert.Single(CurrencyTestData.Claim(db, token).Errors);
        Assert.Equal(0, CurrencyTestData.Cash(db));
        Assert.Empty(CurrencyTestData.Loyalty(db));
        using WorldDbContext world = new(db.Path);
        Assert.Empty(world.Set<SavedRedeemedCurrency>());
    }

    [Fact]
    public void ARealVersion56MigrationKeepsExistingSpAndCanRecoverAnIntactHistoricalIskInventoryRewardOnce()
    {
        var clock = CalendarTestData.Clock();
        using var db = CurrencyTestData.Create(clock);
        FreePointTestData.Credit(db);
        var token = CurrencyTestData.Grant(db, RedeemCurrencyType.Isk, 25);
        long item;
        using (WorldDbContext world = new(db.Path))
        {
            item = InventoryPersistence.NextId(world);
            world.Add(
                new OwnedItem
                {
                    Id = item,
                    TypeId = 52996,
                    OwnerId = FreePointTestData.Pilot,
                    LocationId = db.Store.FindPilot(RedemptionTests.Account)!.StationId,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = 25,
                }
            );
            var claimed = world.Set<SavedRedeemToken>().Find(token.Id)!;
            claimed.State = RedeemTokenState.Claimed;
            claimed.PilotId = FreePointTestData.Pilot;
            claimed.ItemId = item;
            world.SaveChanges();
            world.Database.ExecuteSqlRaw("DROP TABLE WorldRedeemedCurrency; DROP TABLE WorldLoyaltyWallet; PRAGMA user_version=56;");
        }
        WorldInitialization.Open(db.Path);
        Assert.Equal(1000, FreePointTestData.Points(db).Balance(FreePointTestData.Pilot));
        Assert.Equal(0, CurrencyTestData.Cash(db));
        RedemptionTests.Store(db).Reverse(RedemptionTests.Account, FreePointTestData.Pilot, item);
        var returned = Assert.Single(RedemptionTests.Store(db).Read(RedemptionTests.Account));
        Assert.Equal(2500, Assert.Single(CurrencyTestData.Claim(db, returned).Currencies).Amount);
        Assert.Single(CurrencyTestData.Claim(db, returned).Errors);
        using WorldDbContext verify = new(db.Path);
        Assert.Null(verify.Set<OwnedItem>().Find(item));
        Assert.Equal(2500, CurrencyTestData.Cash(db));
    }
}
