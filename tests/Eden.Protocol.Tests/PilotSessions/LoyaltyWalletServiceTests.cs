using Eden.Data.World.Market;
using Eden.Gameplay.Redemption;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Redemption;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.Server;
using Eden.World.Market;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class LoyaltyWalletServiceTests
{
    [Fact]
    public void ExternalCashCreditsRefreshTheRecipientAndOwnNotificationsAreNotDuplicatedByPolling()
    {
        var clock = CalendarTestData.Clock();
        using var db = CurrencyTestData.Create(clock);
        using CourierServiceTests.Client client = new(db, clock, RedemptionTests.Account);
        using CourierServiceTests.Client other = new(db, clock, 1000002);
        client.Refresh();
        other.Refresh();
        client.Context.Notifications.Clear();
        other.Context.Notifications.Clear();
        using var independent = ServerServices.Build(new(db.Path), clock);
        var writer = independent.GetRequiredService<IAccountRedemption>();
        var token = writer.Grant(RedemptionTests.Grant("external-cash", 52996, 10));
        writer.Claim(RedemptionTests.Account, FreePointTestData.Pilot, [new(token.Id, token.TypeId)], false);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        client.Advance();
        other.Advance();
        Assert.Equal(10, Assert.Single(client.Context.Notifications.OfType<AccountBalanceChanged>()).Balance);
        Assert.Empty(other.Context.Notifications.OfType<AccountBalanceChanged>());
        client.Context.Notifications.Clear();
        var own = CurrencyTestData.Grant(db, RedeemCurrencyType.Isk, 5, "own-cash");
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        client.Call("ClaimRedeemTokens", [new List<object?> { Selection(own) }, FreePointTestData.Pilot], "userSvc");
        Assert.Equal(15, Assert.Single(client.Context.Notifications.OfType<AccountBalanceChanged>()).Balance);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        client.Advance();
        Assert.Single(client.Context.Notifications.OfType<AccountBalanceChanged>());
    }

    [Fact]
    public void NativeBatchCreditsCashAndEvermarksOnceWithTupleBalancesAndThePinnedJournalType()
    {
        var clock = CalendarTestData.Clock();
        using var db = CurrencyTestData.Create(clock);
        var cash1 = CurrencyTestData.Grant(db, RedeemCurrencyType.Isk, 40, "cash-one");
        var cash2 = CurrencyTestData.Grant(db, RedeemCurrencyType.Isk, 60, "cash-two");
        var marks1 = CurrencyTestData.Grant(db, RedeemCurrencyType.EverMarks, 3, "marks-one");
        var marks2 = CurrencyTestData.Grant(db, RedeemCurrencyType.EverMarks, 7, "marks-two");
        using CourierServiceTests.Client client = new(db, clock, RedemptionTests.Account);
        Assert.Same(PilotCallResult.Unsupported, client.Call("GetAllMyCharacterWalletLPBalances", [], "LPSvc"));
        client.Refresh();
        Assert.Empty(Assert.IsType<List<object?>>(Reply(client.Call("GetAllMyCharacterWalletLPBalances", [], "LPSvc"))));
        var queue = Assert.IsType<List<object?>>(Reply(client.Call("GetRedeemTokens", [], "userSvc")));
        Assert.Equal(4, queue.Count);
        Assert.All(queue, row => Assert.Equal(true, Fields(row)["isAutoInject"]));
        client.Context.Notifications.Clear();
        var report = Assert.IsType<Dictionary<string, object?>>(
            Reply(
                client.Call(
                    "ClaimRedeemTokens",
                    [
                        new List<object?> { Selection(cash1), Selection(cash2), Selection(marks1), Selection(marks2) },
                        FreePointTestData.Pilot,
                    ],
                    "userSvc"
                )
            )
        );
        Assert.Empty(Assert.IsType<List<object?>>(report["itemsCreated"]));
        Assert.Empty(Assert.IsType<List<object?>>(report["errors"]));
        Assert.Null(report["stationID"]);
        Assert.Equal(0L, report["skillpoints"]);
        Assert.Equal(100d, Assert.Single(client.Context.Notifications.OfType<AccountBalanceChanged>()).Balance);
        var lp = Assert.Single(client.Context.Notifications.OfType<LoyaltyBalanceChanged>());
        Assert.Equal(1000419, lp.IssuerCorporationId);
        Assert.Equal(0, lp.Before);
        Assert.Equal(10, lp.After);
        Assert.Equal(new object?[] { null, 1000419, 0L, 10L }, lp.Arguments);
        Assert.Single(client.Context.Notifications.OfType<RedeemingQueueUpdated>());
        Assert.DoesNotContain(client.Context.Notifications, n => n.Name == "OnItemChange");
        Assert.False(client.Context.Flight.InBallpark);
        Assert.Equal(100d, Reply(client.Call("GetCashBalance", [], "account")));
        var balances = Assert.IsType<List<object?>>(Reply(client.Call("GetAllMyCharacterWalletLPBalances", [], "LPSvc")));
        Assert.Equal(new object?[] { 1000419L, 10L }, Assert.IsType<object?[]>(Assert.Single(balances)));
        var journal = Journal(client);
        Assert.Equal(2, journal.Count);
        Assert.All(journal, entry => Assert.Equal(147L, entry["entryTypeID"]));
        Assert.Equal(100d, journal[0]["balance"]);
        Assert.Equal(60d, journal[0]["amount"]);
        Assert.Equal(CurrencyRewardRules.FirstJournalId + cash2.Id, journal[0]["transactionID"]);
        Assert.Equal(cash2.Id, journal[0]["referenceID"]);
        Assert.Equal(FreePointTestData.Pilot, journal[0]["ownerID2"]);
        var types = Assert.IsType<List<object?>>(Reply(client.Call("GetEntryTypes", [], "account")));
        Assert.Equal(552388L, Assert.Single(types.Select(Fields), row => Equals(row["entryTypeID"], 147L))["entryTypeNameID"]);
        using CourierServiceTests.Client other = new(db, clock, 1000002);
        other.Refresh();
        Assert.Empty(Assert.IsType<List<object?>>(Reply(other.Call("GetAllMyCharacterWalletLPBalances", [], "LPSvc"))));
        Assert.Empty(Journal(other));
        Assert.Equal(0d, Reply(other.Call("GetCashBalance", [], "account")));
    }

    [Fact]
    public void CurrencyClaimsBeforeSelectionOrInSpaceProduceNoInventoryAndRemainPrivateToTheRecipient()
    {
        var clock = CalendarTestData.Clock();
        using var db = CurrencyTestData.Create(clock);
        var pilot = db.Store.FindPilot(RedemptionTests.Account)!;
        db.Store.SetPosition(pilot.ShipId, new(1, 2, 3), pilot.SystemId);
        var cash = CurrencyTestData.Grant(db, RedeemCurrencyType.Isk, 1, "cash");
        var marks = CurrencyTestData.Grant(db, RedeemCurrencyType.EverMarks, 2, "marks");
        using CourierServiceTests.Client client = new(db, clock, RedemptionTests.Account);
        var report = Assert.IsType<Dictionary<string, object?>>(
            Reply(
                client.Call(
                    "ClaimRedeemTokens",
                    [new List<object?> { Selection(cash), Selection(marks) }, FreePointTestData.Pilot],
                    "userSvc"
                )
            )
        );
        Assert.Empty(Assert.IsType<List<object?>>(report["itemsCreated"]));
        Assert.Empty(Assert.IsType<List<object?>>(report["errors"]));
        Assert.Single(client.Context.Notifications.OfType<RedeemingQueueUpdated>());
        Assert.Empty(client.Context.Notifications.OfType<LoyaltyBalanceChanged>());
        Assert.Empty(client.Context.Notifications.OfType<AccountBalanceChanged>());
        client.Refresh();
        Assert.Equal(1d, Reply(client.Call("GetCashBalance", [], "account")));
        var balance = Assert.Single(Assert.IsType<List<object?>>(Reply(client.Call("GetAllMyCharacterWalletLPBalances", [], "LPSvc"))));
        Assert.Equal(new object?[] { 1000419L, 2L }, Assert.IsType<object?[]>(balance));
    }

    [Fact]
    public void ASeparateWriterUpdatesOnlyTheRecipientsLpCacheAndUnchangedPollsDoNotRepeatEvents()
    {
        var clock = CalendarTestData.Clock();
        using var db = CurrencyTestData.Create(clock);
        using CourierServiceTests.Client client = new(db, clock, RedemptionTests.Account);
        using CourierServiceTests.Client other = new(db, clock, 1000002);
        client.Refresh();
        other.Refresh();
        client.Context.Notifications.Clear();
        other.Context.Notifications.Clear();
        using var independent = ServerServices.Build(new(db.Path), clock);
        var writer = independent.GetRequiredService<IAccountRedemption>();
        var token = writer.Grant(RedemptionTests.Grant("external-marks", 73277, 9));
        writer.Claim(RedemptionTests.Account, FreePointTestData.Pilot, [new(token.Id, token.TypeId)], false);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        client.Advance();
        other.Advance();
        client.Advance();
        Assert.Equal(9, Assert.Single(client.Context.Notifications.OfType<LoyaltyBalanceChanged>()).After);
        Assert.Empty(other.Context.Notifications.OfType<LoyaltyBalanceChanged>());
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        client.Advance();
        Assert.Single(client.Context.Notifications.OfType<LoyaltyBalanceChanged>());
        client.Context.Notifications.Clear();
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<SavedLoyaltyBalance>().Find(FreePointTestData.Pilot, 1000419)!.Points = 0;
            world.SaveChanges();
        }
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        client.Advance();
        var zero = Assert.Single(client.Context.Notifications.OfType<LoyaltyBalanceChanged>());
        Assert.Equal(9, zero.Before);
        Assert.Equal(0, zero.After);
        Assert.Empty(Assert.IsType<List<object?>>(Reply(client.Call("GetAllMyCharacterWalletLPBalances", [], "LPSvc"))));
    }

    [Fact]
    public void ForeignTargetsMalformedWalletReadsAndFailedCurrencyWritesEmitNoCommittedNotifications()
    {
        var clock = CalendarTestData.Clock();
        using var db = CurrencyTestData.Create(clock);
        var token = CurrencyTestData.Grant(db, RedeemCurrencyType.EverMarks, 10);
        using CourierServiceTests.Client client = new(db, clock, RedemptionTests.Account);
        client.Refresh();
        client.Context.Notifications.Clear();
        Assert.IsType<PilotCallResult.Failure>(client.Call("GetAllMyCharacterWalletLPBalances", [90000002L], "LPSvc"));
        Assert.IsType<PilotCallResult.Failure>(
            client.Call("GetAllMyCharacterWalletLPBalances", [], "LPSvc", new() { ["pilotID"] = 90000002L })
        );
        Assert.IsType<PilotCallResult.Failure>(
            client.Call("ClaimRedeemTokens", [new List<object?> { Selection(token) }, 90000002L], "userSvc")
        );
        using var connection = new SqliteConnection($"Data Source={db.Path};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_currency BEFORE INSERT ON WorldRedeemedCurrency BEGIN SELECT RAISE(ABORT,'failed'); END";
        command.ExecuteNonQuery();
        Assert.Throws<DbUpdateException>(() =>
            client.Call("ClaimRedeemTokens", [new List<object?> { Selection(token) }, FreePointTestData.Pilot], "userSvc")
        );
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        client.Advance();
        Assert.Empty(client.Context.Notifications);
        Assert.Equal(token, Assert.Single(RedemptionTests.Store(db).Read(RedemptionTests.Account)));
    }

    [Fact]
    public void RedeemedCashHistoryUsesTheNormalDateFilterAndRemainsAvailableInItsMonth()
    {
        var clock = CalendarTestData.Clock();
        using var db = CurrencyTestData.Create(clock);
        CurrencyTestData.Claim(db, CurrencyTestData.Grant(db, RedeemCurrencyType.Isk, 11));
        long year = clock.UtcNow.Year;
        long month = clock.UtcNow.Month;
        using CourierServiceTests.Client client = new(db, clock, RedemptionTests.Account);
        client.Refresh();
        Assert.Single(Journal(client));
        clock.AdvanceWallTime(TimeSpan.FromDays(31));
        Assert.Empty(Journal(client));
        var historic = Assert.IsType<List<object?>>(Reply(client.Call("GetTransactions", [1000L, year, month, false], "account")));
        Assert.Equal(11d, Fields(Assert.Single(historic))["amount"]);
    }

    private static List<Dictionary<string, object?>> Journal(CourierServiceTests.Client client) =>
        Assert.IsType<List<object?>>(Reply(client.Call("GetTransactions", [1000L, null, null, false], "account"))).Select(Fields).ToList();

    private static Dictionary<string, object?> Selection(RedeemToken token) =>
        new()
        {
            ["tokenID"] = token.Id,
            ["massTokenID"] = null,
            ["typeID"] = token.TypeId,
        };

    private static Dictionary<string, object?> Fields(object? value) =>
        Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(value).State);

    private static object? Reply(PilotCallResult result)
    {
        Assert.True(result is PilotCallResult.Success, result.ToString());
        return BlueMarshal.Decode(BlueMarshal.Encode(((PilotCallResult.Success)result).Reply));
    }
}
