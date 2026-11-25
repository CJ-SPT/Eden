using Eden.Data.World.Inventory;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Protocol.Corporations;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Market;

public sealed class CorporationMarketPurchaseTests
{
    private const int Miner = 3651;
    private const int OtherModule = 3638;
    private const int SellerStation = 60003761;
    private const long Ceo = 90000001;
    private const long Member = 90000002;
    private const int SecondDivision = 1001;
    private const int Master = CorporationWalletRules.MasterKey;

    [Fact]
    public async Task NativeCorporationPurchasesUseTheSelectedDivisionAndMarketDeliveriesAcrossCheckpoint()
    {
        TestClock clock = new(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero));
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = Prepare(world);
        var wallet = world.Services.GetRequiredService<ICorporationWallet>();
        var market = world.Services.GetRequiredService<IPilotMarket>();
        var live = world.Services.GetRequiredService<LiveWorld>();
        wallet.Transfer(Ceo, new(false, Master, corporation, SecondDivision, 900, "Supply division"));
        wallet.Transfer(Ceo, new(false, Master, corporation, Master, 500, "Master division"));
        Assign(world, CorporationRole.Trader | CorporationRole.AccountCanTake2);
        wallet.SelectDivision(Member, SecondDivision);
        long memberBalance = market.Balance(Member);

        using (CorporationTestClient ceo = new(world, 1000001))
        using (CorporationTestClient member = new(world, 1000002))
        {
            var walletMonitor = member.Services.GetRequiredService<CorporationWalletHandler>();
            walletMonitor.Enter(member.Context);
            Assert.Same(PilotCallResult.None, BuyNow(member, quantity: 2, corporation: true));
            walletMonitor.Advance(member.Context);
            var notice = Assert.Single(member.Context.Notifications.OfType<CorporationBalanceChanged>());
            Assert.Equal(new object?[] { "cash2", corporation, 7d }, notice.Arguments);
            clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            long today = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            Assert.IsType<PilotCallResult.Success>(BuyMany(member, Entry(Miner, 1, 1d), Entry(OtherModule, 2, 1d)));
            var orderNotices = member.Context.Notifications.OfType<OwnOrdersChanged>().ToArray();
            Assert.Equal(new[] { "Filled", "Filled", "Expiry" }, orderNotices.Select(change => change.Reason));
            Assert.All(orderNotices, change => Assert.Equal(true, change.Arguments[2]));
            Assert.Same(PilotCallResult.None, BuyNow(ceo, quantity: 1, corporation: 1L));

            var divisionHistory = Transactions(member, null, SecondDivision);
            Assert.Equal(3, divisionHistory.Count);
            Assert.All(
                divisionHistory,
                row =>
                {
                    Assert.Equal((long)SecondDivision, row["accountID"]);
                    Assert.Equal((long)SecondDivision, row["keyID"]);
                    Assert.Equal((long)SecondDivision, row["buyerAccountID"]);
                    Assert.Equal(Member, row["buyerID"]);
                    Assert.Equal((long)SellerStation, row["stationID"]);
                }
            );
            Assert.Equal(2, Transactions(member, today, SecondDivision).Count);
            Assert.IsType<PilotCallResult.Failure>(member.CallService("marketProxy", "CorpGetTransactions", [null, (long)Master]));
            Assert.IsType<PilotCallResult.Failure>(member.CallService("marketProxy", "CorpGetTransactions", [null, null]));
            Assert.Equal(4, Transactions(ceo, null, null).Count);
            Assert.Single(Transactions(ceo, null, Master));
        }

        Assert.Empty(market.Transactions(Member));
        Assert.Empty(market.Transactions(Ceo));
        Assert.Equal(memberBalance, market.Balance(Member));
        Assert.Equal(400, wallet.Balance(Member, SecondDivision));
        Assert.Equal(400, wallet.Balance(Ceo, Master));
        Assert.Equal(
            -500,
            wallet
                .Journal(Member, true, SecondDivision)
                .Where(e => e.EntryType == WalletEntryType.MarketTransaction)
                .Sum(e => e.AmountCents)
        );
        CheckDeliveries(live, corporation, remainingMiners: 4);

        var (shipId, deliveryId) = live.Economy.Inspect(action =>
            (
                action.Pilots.Find(Member)!.ActiveShipId,
                action.Filter<OwnedItem>(item => item.OwnerId == corporation && item.TypeId == Miner).Single().Id
            )
        );
        using (var action = live.Economy.Begin(new(pilots: [Member])))
        {
            action.Ships.Find(shipId)!.StationId = SellerStation;
            action.Commit();
        }
        using (CorporationTestClient member = new(world, 1000002))
        {
            Assert.Same(
                PilotCallResult.None,
                member.CallService(
                    "invbroker",
                    "DeliverToCorpMember",
                    [
                        Member,
                        (long)SellerStation,
                        new List<object?> { deliveryId },
                        1L,
                        (long)corporation,
                        (long)CorporationStorageRules.MarketDeliveriesFlag,
                    ]
                )
            );
        }

        await live.FlushAsync();
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock, liveWorld: true);
        var restored = reopened.GetRequiredService<LiveWorld>();
        CheckDeliveries(restored, corporation, remainingMiners: 3);
        var collected = Assert.Single(
            restored.Economy.Inspect(action =>
                action
                    .Filter<OwnedItem>(item => item.OwnerId == Member && item.TypeId == Miner && item.LocationId == SellerStation)
                    .ToArray()
            )
        );
        Assert.Equal(1, collected.Quantity);
        Assert.Equal((int)InventoryFlag.Hangar, collected.FlagId);
        Assert.Equal(400, reopened.GetRequiredService<ICorporationWallet>().Balance(Member, SecondDivision));
        Assert.Equal(3, reopened.GetRequiredService<ICorporationMarket>().CorporationTransactions(Member, null, SecondDivision).Count);
        Assert.Empty(reopened.GetRequiredService<IPilotMarket>().Transactions(Member));
    }

    [Fact]
    public void CorporationPurchasesRequireTraderAndRollBackTheWholeBatch()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = Prepare(world);
        var wallet = world.Services.GetRequiredService<ICorporationWallet>();
        var market = world.Services.GetRequiredService<IPilotMarket>();
        wallet.Transfer(Ceo, new(false, Master, corporation, SecondDivision, 100, "Limited supply budget"));
        using CorporationTestClient member = new(world, 1000002);
        Assign(world, CorporationRole.AccountCanTake2);
        wallet.SelectDivision(Member, SecondDivision);
        Assert.IsType<PilotCallResult.Failure>(BuyNow(member, quantity: 1, corporation: true));
        Assign(world, CorporationRole.Trader);
        Assert.IsType<PilotCallResult.Failure>(BuyNow(member, quantity: 1, corporation: true));
        Assign(world, CorporationRole.Trader | CorporationRole.AccountCanTake2);
        wallet.SelectDivision(Member, SecondDivision);
        var book = market.Orders(Member, Miner);
        member.Context.Notifications.Clear();
        Assert.IsType<PilotCallResult.Failure>(BuyMany(member, Entry(Miner, 1, 1d), Entry(OtherModule, 1, 1d)));
        Assert.Empty(member.Context.Notifications);
        Assert.Equal(book, market.Orders(Member, Miner));
        Assert.Equal(100, wallet.Balance(Member, SecondDivision));
        Assert.Empty(Transactions(member, null, SecondDivision));
        var live = world.Services.GetRequiredService<LiveWorld>();
        Assert.Empty(live.Economy.Inspect(action => action.Filter<OwnedItem>(item => item.OwnerId == corporation).ToArray()));
        Assert.Same(PilotCallResult.None, BuyNow(member, quantity: 1, corporation: true));
        Assert.Single(Transactions(member, null, SecondDivision));
    }

    [Fact]
    public async Task Version238TradesRemainPersonalAfterCorporationMarketUpgrade()
    {
        using TestWorld world = new(liveWorld: true);
        _ = Prepare(world);
        var market = world.Services.GetRequiredService<IPilotMarket>();
        market.Buy(Ceo, SellerStation, [new(Miner, 100, 1)]);
        var prior = Assert.Single(market.Transactions(Ceo));
        await world.Services.GetRequiredService<LiveWorld>().FlushAsync();
        world.Services.Dispose();
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Database.ExecuteSqlRaw(
                """
                DROP INDEX IX_WorldMarketTrade_CorporationId_Time;
                ALTER TABLE WorldMarketTrade DROP COLUMN WalletDivisionKey;
                ALTER TABLE WorldMarketTrade DROP COLUMN CorporationId;
                PRAGMA user_version=238;
                """
            );
        }
        WorldDatabase database = new(world.Path);
        WorldInitialization.Initialize(database);
        using var reopened = ServerServices.Build(database, liveWorld: true);
        Assert.Equal(prior, Assert.Single(reopened.GetRequiredService<IPilotMarket>().Transactions(Ceo)));
        Assert.Empty(reopened.GetRequiredService<ICorporationMarket>().CorporationTransactions(Ceo, null, null));
    }

    private static int Prepare(TestWorld world)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(world.Path).CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            UPDATE SourceRecord SET Data='{{"published":1,"marketGroupID":1,"volume":5.0}}'
                WHERE Dataset='client/types' AND Key='3638';
            INSERT INTO SourceRecord VALUES ('client/stationoperations','14','{{"services":[7]}}');
            INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
                SELECT 60003761,TypeId,OwnerId,30000144,OperationId,OrbitId,X,Y,Z FROM Station WHERE Id=60003760;
            """
        );
        return CorporationAdministrationTests.CreateAndJoin(world);
    }

    private static void Assign(TestWorld world, CorporationRole roles) =>
        world
            .Services.GetRequiredService<ICorporationRoles>()
            .UpdateMembers(Ceo, [new(Member, null, null, null, new((long)roles, 0, 0, 0, 0, 0, 0, 0), null, 0)]);

    private static PilotCallResult BuyNow(CorporationTestClient client, int quantity, object corporation) =>
        client.CallService(
            "marketProxy",
            "PlaceBuyOrder",
            [(long)SellerStation, (long)Miner, 1d, (long)quantity, -1L, 1L, 0L, corporation, null]
        );

    private static PilotCallResult BuyMany(CorporationTestClient client, params BlueInstance[] entries) =>
        client.CallService("marketProxy", "BuyMultipleItems", [(long)SellerStation, entries.Cast<object?>().ToList(), 1L]);

    private static BlueInstance Entry(int type, int quantity, double price) =>
        new(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["typeID"] = (long)type,
                ["quantity"] = (long)quantity,
                ["price"] = price,
            }
        );

    private static IReadOnlyList<Dictionary<string, object?>> Transactions(CorporationTestClient client, long? date, long? key)
    {
        var result = Assert.IsType<PilotCallResult.Success>(client.CallService("marketProxy", "CorpGetTransactions", [date, key]));
        var rows = Assert.IsType<List<object?>>(BlueMarshal.Decode(BlueMarshal.Encode(result.Reply)));
        return rows.Select(Assert.IsType<Dictionary<string, object?>>).ToArray();
    }

    private static void CheckDeliveries(LiveWorld live, int corporation, int remainingMiners)
    {
        var items = live.Economy.Inspect(action => action.Filter<OwnedItem>(item => item.OwnerId == corporation).ToArray());
        Assert.Equal(remainingMiners, Assert.Single(items, item => item.TypeId == Miner).Quantity);
        Assert.Equal(2, Assert.Single(items, item => item.TypeId == OtherModule).Quantity);
        Assert.All(
            items,
            item =>
            {
                Assert.Equal(SellerStation, item.LocationId);
                Assert.Equal(CorporationStorageRules.MarketDeliveriesFlag, item.FlagId);
            }
        );
    }
}
