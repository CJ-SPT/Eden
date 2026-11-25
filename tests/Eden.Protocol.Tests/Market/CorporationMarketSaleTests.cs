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
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Market;

public sealed class CorporationMarketSaleTests
{
    private const long Ceo = 90000001;
    private const long Member = 90000002;
    private const int Station = 60003760;
    private const int Tritanium = 34;
    private const int Division = 1001;
    private const int Hangar = CorporationStorageRules.FirstFlag + 1;
    private const CorporationRole Trader = CorporationRole.Trader | CorporationRole.AccountCanTake2;

    [Theory]
    [InlineData(true)]
    [InlineData(1L)]
    public async Task NativeCorporationSaleCreditsTheDivisionAndPreservesTheUnsoldStockAcrossCheckpoint(object checkbox)
    {
        using TestWorld world = new(liveWorld: true);
        var (office, live, wallet) = Prepare(world);
        Assign(world, Trader, CorporationRole.HangarCanTake2);
        wallet.SelectDivision(Member, Division);
        long itemId = Stock(live, office.CorporationId, office.Id, Hangar);
        var market = world.Services.GetRequiredService<IPilotMarket>();
        long personalBalance = market.Balance(Member);
        var bid = Assert.Single(market.Orders(Member, Tritanium), order => order.Bid && order.StationId == Station);
        using (var action = live.Economy.Begin())
        {
            action.Set<NpcMarketOrder>().Find(bid.Id)!.Remaining = 3;
            action.Commit();
        }
        const long gross = 600;
        const long tax = 45;
        using (CorporationTestClient client = new(world, 1000002))
        {
            var walletMonitor = client.Services.GetRequiredService<CorporationWalletHandler>();
            walletMonitor.Enter(client.Context);
            Assert.Equal(true, Assert.IsType<PilotCallResult.Success>(Sell(client, checkbox, itemId)).Reply.ToBlue());
            walletMonitor.Advance(client.Context);
            var notice = Assert.Single(client.Context.Notifications.OfType<CorporationBalanceChanged>());
            Assert.Equal(new object?[] { "cash2", office.CorporationId, 5.55d }, notice.Arguments);
            Assert.Equal(true, Assert.Single(client.Context.Notifications.OfType<OwnOrdersChanged>()).Arguments[2]);
            var reply = Assert.IsType<PilotCallResult.Success>(
                client.CallService("marketProxy", "CorpGetTransactions", [null, (long)Division])
            );
            var rows = Assert.IsType<List<object?>>(BlueMarshal.Decode(BlueMarshal.Encode(reply.Reply)));
            var row = Assert.IsType<Dictionary<string, object?>>(Assert.Single(rows));
            Assert.Equal(Member, row["sellerID"]);
            Assert.Equal((long)bid.OwnerId, row["buyerID"]);
            Assert.Equal((long)Division, row["keyID"]);
            Assert.Equal((long)Division, row["sellerAccountID"]);
            Assert.NotEqual(row["accountID"], row["buyerAccountID"]);
            Assert.Equal(3L, row["quantity"]);
        }
        Assert.Empty(market.Transactions(Member));
        Assert.Equal(personalBalance, market.Balance(Member));
        Assert.Equal(gross - tax, wallet.Balance(Member, Division));
        var journal = wallet.Journal(Member, true, Division);
        Assert.Equal(gross, Assert.Single(journal, entry => entry.EntryType == WalletEntryType.MarketTransaction).AmountCents);
        Assert.Equal(-tax, Assert.Single(journal, entry => entry.EntryType == WalletEntryType.TransactionTax).AmountCents);
        await live.FlushAsync();
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), liveWorld: true);
        var receipt = Assert.Single(reopened.GetRequiredService<ICorporationMarket>().CorporationTransactions(Member, null, Division));
        Assert.False(receipt.IsBuy);
        Assert.Equal(tax, receipt.TaxCents);
        Assert.Equal(gross - tax, receipt.BalanceCents);
        var restored = reopened.GetRequiredService<LiveWorld>();
        Assert.Equal(2, Quantity(restored, itemId));
        Assert.Equal(gross - tax, reopened.GetRequiredService<ICorporationWallet>().Balance(Member, Division));
    }

    [Fact]
    public void CorporationSaleEnforcesSourcePermissionsAndRollsBackTheWholeBatch()
    {
        using TestWorld world = new(liveWorld: true);
        var (office, live, wallet) = Prepare(world);
        long stock = Stock(live, office.CorporationId, office.Id, Hangar);
        long restricted = Stock(live, office.CorporationId, office.Id, CorporationStorageRules.GoalDeliveriesFlag);
        long own = Stock(live, Member, Station, (int)InventoryFlag.Hangar);
        using CorporationTestClient member = new(world, 1000002);

        Assign(world, CorporationRole.AccountCanTake2, CorporationRole.HangarCanTake2);
        wallet.SelectDivision(Member, Division);
        Assert.IsType<PilotCallResult.Failure>(Sell(member, true, stock));
        Assign(world, CorporationRole.Trader, CorporationRole.HangarCanTake2);
        Assert.IsType<PilotCallResult.Failure>(Sell(member, true, stock));
        Assign(world, Trader, CorporationRole.NoRole);
        wallet.SelectDivision(Member, Division);
        Assert.IsType<PilotCallResult.Failure>(Sell(member, true, stock));
        Assign(world, Trader, CorporationRole.HangarCanTake2);
        Assert.IsType<PilotCallResult.Failure>(Sell(member, false, stock));
        var market = world.Services.GetRequiredService<IPilotMarket>();
        var before = market.Orders(Member, Tritanium);
        member.Context.Notifications.Clear();
        Assert.IsType<PilotCallResult.Failure>(Sell(member, true, stock, restricted));
        Assert.Equal(before, market.Orders(Member, Tritanium));
        Assert.Empty(member.Context.Notifications);
        Assert.Equal(0, wallet.Balance(Member, Division));
        Assert.Equal(5, Quantity(live, stock));
        Assert.Empty(world.Services.GetRequiredService<ICorporationMarket>().CorporationTransactions(Member, null, Division));

        Assert.Equal(true, Assert.IsType<PilotCallResult.Success>(Sell(member, true, own)).Reply.ToBlue());
        Assert.Equal(0, Quantity(live, own));
        Assert.Equal(5, Quantity(live, stock));
        Assert.NotEmpty(wallet.Journal(Member, true, Division));
        Assert.Empty(market.Transactions(Member));
    }

    [Theory]
    [InlineData(CorporationWalletRules.MasterKey, CorporationRole.AccountCanTake1)]
    [InlineData(Division, CorporationRole.AccountCanTake2)]
    public void CorporationSaleFillsAPersonalBidAndDeliversToTheBuyerHangar(int division, CorporationRole walletAccess)
    {
        using TestWorld world = new(liveWorld: true, economy: new(TestingMarket: false, TestingGrants: false));
        var (office, live, wallet) = Prepare(world);
        Assign(world, CorporationRole.Trader | walletAccess, CorporationRole.HangarCanTake2);
        wallet.SelectDivision(Member, division);
        long stock = Stock(live, office.CorporationId, office.Id, Hangar);
        var personal = world.Services.GetRequiredService<IPersonalMarket>();
        personal.PlaceBuy(Ceo, new(Station, Tritanium, 200, 2, 1));
        Assert.Single(personal.PersonalOrders(Ceo));

        using CorporationTestClient member = new(world, 1000002);
        Assert.Equal(true, Assert.IsType<PilotCallResult.Success>(Sell(member, true, stock)).Reply.ToBlue());
        Assert.Equal(3, Quantity(live, stock));
        Assert.Empty(personal.PersonalOrders(Ceo));
        var delivered = live.Economy.Inspect(action =>
            action.Filter<OwnedItem>(item => item.OwnerId == Ceo && item.TypeId == Tritanium && item.LocationId == Station).ToArray()
        );
        Assert.Equal(2, Assert.Single(delivered).Quantity);
        Assert.Equal((int)InventoryFlag.Hangar, delivered[0].FlagId);
        var personalReceipt = Assert.Single(world.Services.GetRequiredService<IPilotMarket>().Transactions(Ceo));
        Assert.True(personalReceipt.IsBuy);
        Assert.Equal(office.CorporationId, personalReceipt.CounterpartyId);
        var corporationReceipt = Assert.Single(
            world.Services.GetRequiredService<ICorporationMarket>().CorporationTransactions(Member, null, division)
        );
        Assert.False(corporationReceipt.IsBuy);
        Assert.Equal(Ceo, corporationReceipt.CounterpartyId);
        Assert.Equal(370, wallet.Balance(Member, division));
        var reply = Assert.IsType<PilotCallResult.Success>(
            member.CallService("marketProxy", "CorpGetTransactions", [null, (long)division])
        );
        var rows = Assert.IsType<List<object?>>(BlueMarshal.Decode(BlueMarshal.Encode(reply.Reply)));
        var row = Assert.IsType<Dictionary<string, object?>>(Assert.Single(rows));
        Assert.Equal((long)division, row["accountID"]);
        Assert.Equal(0L, row["buyerAccountID"]);
    }

    private static (CorporationOffice Office, LiveWorld Live, ICorporationWallet Wallet) Prepare(TestWorld world)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(world.Path).CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO ItemCategory VALUES (4,'Material');
            INSERT OR IGNORE INTO ItemGroup VALUES (18,'Mineral',4);
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (34,'Tritanium',18);
            INSERT OR REPLACE INTO SourceRecord VALUES ('client/types','34','{{"published":1,"marketGroupID":1,"volume":0.01}}'),
                ('client/stationoperations','14','{{"services":[7]}}');
            """
        );
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        var wallet = world.Services.GetRequiredService<ICorporationWallet>();
        var storage = world.Services.GetRequiredService<ICorporationStorage>();
        long rent = storage.Quote(Ceo);
        wallet.Transfer(Ceo, new(false, CorporationWalletRules.MasterKey, corporation, CorporationWalletRules.MasterKey, rent, "Office"));
        return (storage.Rent(Ceo, rent), world.Services.GetRequiredService<LiveWorld>(), wallet);
    }

    private static void Assign(TestWorld world, CorporationRole general, CorporationRole headquarters) =>
        world
            .Services.GetRequiredService<ICorporationRoles>()
            .UpdateMembers(Ceo, [new(Member, null, null, null, new((long)general, 0, (long)headquarters, 0, 0, 0, 0, 0), null, 0)]);

    private static long Stock(LiveWorld live, long owner, long location, int flag)
    {
        using var action = live.Economy.Begin();
        var item = action
            .Add(
                new OwnedItem
                {
                    Id = action.NextItemId(),
                    OwnerId = owner,
                    LocationId = location,
                    FlagId = flag,
                    TypeId = Tritanium,
                    Quantity = 5,
                }
            )
            .Entity;
        action.Commit();
        return item.Id;
    }

    private static int Quantity(LiveWorld live, long id) => live.Economy.Inspect(action => action.Set<OwnedItem>().Find(id)?.Quantity ?? 0);

    private static PilotCallResult Sell(CorporationTestClient client, object checkbox, params long[] items) =>
        client.CallService(
            "marketProxy",
            "PlaceMultiSellOrder",
            [
                items
                    .Select(id =>
                        (object?)
                            new BlueInstance(
                                "utillib.KeyVal",
                                new Dictionary<string, object?>
                                {
                                    ["itemID"] = id,
                                    ["typeID"] = (long)Tritanium,
                                    ["stationID"] = (long)Station,
                                    ["quantity"] = 5L,
                                    ["price"] = 2d,
                                }
                            )
                    )
                    .ToList(),
                checkbox,
                0L,
                null,
            ]
        );
}
