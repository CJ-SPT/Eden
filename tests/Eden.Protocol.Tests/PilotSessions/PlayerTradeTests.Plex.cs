using Eden.Data.World.Inventory;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Protocol.Docking;
using Eden.Protocol.Gateway;
using Eden.Protocol.Gateway.PlexVault.Log;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Trading;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed partial class PlayerTradeTests
{
    [Fact]
    public async Task NativeVaultPlexOffersSettleBothAccountsWithTheAcceptedTradeManifest()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var vault = PreparePlexTrade(world, firstBalance: 10, secondBalance: 5);
        SetBalance(world, Partner, 30_000);
        long goods = Stack(world, 5);
        using (Client first = new(world, Pilot))
        using (Client second = new(world, Partner))
        {
            long id = Start(first, second);
            Assert.True(first.CallBound("Add", [goods, Station]).Value());
            Assert.True(second.CallBound("OfferMoney", [40d]).Value());
            Assert.True(second.Accept(second.List()));
            string firstReference = Reference(OfferVault(first, id, Partner, 8));
            string secondReference = Reference(OfferVault(second, id, Pilot, 2));
            Assert.Empty(first.Context.Notifications.OfType<PlexVaultTransferCompleted>());
            clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            first.Context.AdvanceScheduledNotifications(clock.GetUtcNow());
            second.Context.AdvanceScheduledNotifications(clock.GetUtcNow());
            var terms = first.List();
            Assert.Null(terms.State.Partner);
            var firstRow = Assert.Single(terms.Items.Rows, row => row.OwnerId == Pilot && row.TypeId == MarketDefinitions.PlexTypeId);
            Assert.Equal((id, 8), (firstRow.LocationId, firstRow.Quantity));
            var secondRow = Assert.Single(terms.Items.Rows, row => row.OwnerId == Partner);
            Assert.Equal((id, MarketDefinitions.PlexTypeId, 2), (secondRow.LocationId, secondRow.TypeId, secondRow.Quantity));
            Assert.Equal(
                new object?[] { firstRow.Id, firstReference },
                Assert.Single(first.Context.Notifications.OfType<PlexVaultTransferCompleted>()).Arguments
            );
            Assert.Equal(
                new object?[] { secondRow.Id, secondReference },
                Assert.Single(second.Context.Notifications.OfType<PlexVaultTransferCompleted>()).Arguments
            );
            Assert.Equal(10, vault.Balance(1000001));
            Assert.Equal(5, vault.Balance(1000002));

            Assert.True(first.Accept(terms));
            Assert.True(second.Accept(terms));
            Assert.IsType<PilotCallResult.Failure>(first.CallBound("MakeOffer", [Manifest(terms)]));
            Assert.Equal(4, vault.Balance(1000001));
            Assert.Equal(11, vault.Balance(1000002));
            Assert.Equal(4_000, first.Wallet.Balance(Pilot));
            Assert.Equal(26_000, second.Wallet.Balance(Partner));
            Assert.Contains(second.Inventory.List(Partner, Station), row => row.Id == goods && row.OwnerId == Partner);
            Assert.DoesNotContain(second.Inventory.List(Partner, Station), row => row.TypeId == MarketDefinitions.PlexTypeId);
        }
        await world.Services.GetRequiredService<LiveWorld>().FlushAsync();
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock, liveWorld: true);
        var restored = reopened.GetRequiredService<IPlexVault>();
        Assert.Equal(4, restored.Balance(1000001));
        Assert.Equal(11, restored.Balance(1000002));
        Assert.Collection(
            restored.History(1000001),
            receipt =>
                Assert.Equal(
                    (PlexVaultTransferKind.TradeReceived, 2L, 4L, (long?)Partner),
                    (receipt.Kind, receipt.Amount, receipt.Balance, receipt.CounterpartyPilotId)
                ),
            sent =>
                Assert.Equal(
                    (PlexVaultTransferKind.TradeSent, -8L, 2L, (long?)Partner),
                    (sent.Kind, sent.Amount, sent.Balance, sent.CounterpartyPilotId)
                ),
            deposit => Assert.Equal(PlexVaultTransferKind.Deposit, deposit.Kind)
        );
        var partnerReceipt = Assert.Single(restored.History(1000002), entry => entry.Kind == PlexVaultTransferKind.TradeReceived);
        var request = new GetLogRequest { Identifier = new() { Sequential = partnerReceipt.Id } };
        var reply = reopened.GetRequiredService<GatewayRequests>().Handle(new() { Payload = Any.Pack(request) }, 1000002);
        var invoice = reply.Payload.Unpack<GetLogResponse>().InvoiceEntry.Attributes;
        Assert.Equal((ulong)Pilot, invoice.SourceCharacter.Sequential);
        Assert.Equal((ulong)Partner, invoice.DestinationCharacter.Sequential);
        Assert.Equal(1021771u, invoice.SummaryMessage.Identifier.Sequential);
        Assert.Contains(
            invoice.SummaryMessage.Parameters,
            parameter => parameter.Key == "otherCharacterID" && parameter.Integer == (ulong)Pilot
        );
    }

    [Fact]
    public void NativeVaultPlexTradeRejectsForgedPartnerAndBalanceChangesAndCanBeCancelled()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var vault = PreparePlexTrade(world, firstBalance: 5, secondBalance: 0);
        using Client first = new(world, Pilot);
        using Client second = new(world, Partner);
        long id = Start(first, second);
        string forged = Reference(OfferVault(first, id, Pilot, 4));
        Assert.Empty(first.List().Items.Rows);
        _ = Reference(OfferVault(first, id, Partner, 4));
        string excess = Reference(OfferVault(first, id, Partner, 2));
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        first.Context.AdvanceScheduledNotifications(clock.GetUtcNow());
        Assert.Contains(first.Context.Notifications.OfType<PlexVaultTransferFailed>(), failure => failure.Reference == forged);
        Assert.Contains(
            first.Context.Notifications.OfType<PlexVaultTransferFailed>(),
            failure => failure.Reference == excess && failure.Error == "PlexWithdrawalInsufficientFunds"
        );
        var terms = first.List();
        Assert.Single(terms.Items.Rows);
        Assert.True(first.Accept(terms));
        vault.Withdraw(Pilot, Station, (int)InventoryFlag.Hangar, 2, forMarket: false);
        Assert.IsType<PilotCallResult.Failure>(second.CallBound("MakeOffer", [Manifest(terms)]));
        Assert.Null(first.List().State.Initiator);
        Assert.True(first.CallBound("Abort", []).Value());
        Assert.Equal(3, vault.Balance(1000001));
        Assert.Equal(0, vault.Balance(1000002));
        Assert.Equal(2, vault.History(1000001).Count);
        Assert.Empty(vault.History(1000002));
    }

    private static IPlexVault PreparePlexTrade(TestWorld world, int firstBalance, int secondBalance)
    {
        using (CatalogDbContext catalog = new(new WorldDatabase(world.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO ItemType(Id,Name,GroupId) VALUES (44992,'PLEX',54);
                INSERT INTO SourceRecord VALUES ('client/types','44992','{{"published":1,"marketGroupID":3,"volume":0.01}}');
                """
            );
        }
        var vault = world.Services.GetRequiredService<IPlexVault>();
        var live = world.Services.GetRequiredService<LiveWorld>();
        foreach (var (pilot, quantity) in new[] { (Pilot, firstBalance), (Partner, secondBalance) })
        {
            if (quantity == 0)
            {
                continue;
            }
            long id;
            using (var action = live.Economy.Begin(new(pilots: [pilot])))
            {
                id = action.NextItemId();
                action.Add(
                    new OwnedItem
                    {
                        Id = id,
                        OwnerId = pilot,
                        LocationId = Station,
                        FlagId = (int)InventoryFlag.Hangar,
                        TypeId = MarketDefinitions.PlexTypeId,
                        Quantity = quantity,
                    }
                );
                action.Commit();
            }
            vault.Deposit(pilot, Station, id, quantity);
        }
        return vault;
    }

    private static PilotCallResult OfferVault(Client client, long id, long partner, long quantity) =>
        client.Call(
            "invbroker",
            "WithdrawPlexFromVault",
            Assert.IsType<object?[]>(
                BlueMarshal.Decode(BlueMarshal.Encode(new object?[] { quantity, id, "VaultToDirectTrade", null, partner }))
            )
        );

    private static string Reference(PilotCallResult result) =>
        Assert.IsType<string>(Assert.IsType<PilotCallResult.Success>(result).Reply.ToBlue());
}
