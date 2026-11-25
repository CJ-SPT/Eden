using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Protocol.Docking;
using Eden.Protocol.Gateway;
using Eden.Protocol.Gateway.PlexVault;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Market;

public sealed partial class PlexVaultTests
{
    private const long AccountId = 1000001;
    private const long OtherAccount = 1000002;
    private const int Plex = MarketDefinitions.PlexTypeId;

    [Fact]
    public async Task NativeTransfersPublishAfterTheReferenceAndRestorePrivateAccountBalance()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(emptyWallets: false, clock: clock, liveWorld: true);
        AddPlex(db);
        var live = db.Services.GetRequiredService<LiveWorld>();
        using CourierServiceTests.Client client = new(db, clock, AccountId);
        using CourierServiceTests.Client other = new(db, clock, OtherAccount);
        client.Refresh();
        other.Refresh();
        var pilot = client.Context.Pilot;
        var acquired = db.Services.GetRequiredService<IPilotMarket>().Buy(pilot.PilotId, pilot.StationId, [new(Plex, 100, 11)]);
        long item = Assert.Single(acquired.Changes).Item.Id;
        var gateway = db.Services.GetRequiredService<GatewayRequests>();
        PlexVaultNoticeCursor cursor = new();
        Assert.Null(gateway.PlexBalanceNotice(AccountId, cursor));
        string refused = Reference(other.Call("DepositPlexToVault", [(long)pilot.StationId, item, 8L, "InventoryToVault"], "invbroker"));
        string deposit = Reference(client.Call("DepositPlexToVault", [(long)pilot.StationId, item, 8L, "InventoryToVault"], "invbroker"));
        client.Context.AdvanceScheduledNotifications(clock.GetUtcNow());
        Assert.Empty(client.Context.Notifications.OfType<PlexVaultTransferCompleted>());
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        client.Context.AdvanceScheduledNotifications(clock.GetUtcNow());
        other.Context.AdvanceScheduledNotifications(clock.GetUtcNow());
        var completion = Assert.Single(client.Context.Notifications.OfType<PlexVaultTransferCompleted>());
        Assert.Equal("OnPlexDeposited", completion.Name);
        Assert.Equal(new object?[] { deposit }, completion.Arguments);
        Assert.Equal(
            new object?[] { "PlexDepositInvalidAmount", refused },
            Assert.Single(other.Context.Notifications.OfType<PlexVaultTransferFailed>()).Arguments
        );
        Assert.Equal(800, Balance(gateway, AccountId));
        Assert.Equal(0, Balance(gateway, OtherAccount));
        Assert.Equal(0, Balance(gateway, null));
        Assert.Equal(
            800,
            gateway.PlexBalanceNotice(AccountId, cursor)!.Payload.Unpack<BalanceChangedNotice>().Attributes.AmountTransferred.TotalInCents
        );
        Assert.Null(gateway.PlexBalanceNotice(AccountId, cursor));

        client.Context.Notifications.Clear();
        string excess = Reference(
            client.Call("WithdrawPlexFromVault", [9L, pilot.ShipId, "VaultToInventory", (long)InventoryFlag.Cargo, null], "invbroker")
        );
        string withdraw = Reference(
            client.Call("WithdrawPlexFromVault", [6L, pilot.ShipId, "VaultToInventory", (long)InventoryFlag.Cargo, null], "invbroker")
        );
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        client.Context.AdvanceScheduledNotifications(clock.GetUtcNow());
        Assert.Equal(
            new object?[] { "PlexWithdrawalInsufficientFunds", excess },
            Assert.Single(client.Context.Notifications.OfType<PlexVaultTransferFailed>()).Arguments
        );
        var added = Assert.Single(client.Context.Notifications.OfType<PlexVaultTransferCompleted>());
        Assert.Equal("OnPlexItemAddedToInventory", added.Name);
        Assert.Equal(withdraw, added.Arguments[1]);
        var notice = gateway.PlexBalanceNotice(AccountId, cursor)!.Payload.Unpack<BalanceChangedNotice>();
        Assert.Equal(-600, notice.Attributes.AmountTransferred.TotalInCents);
        Assert.Equal(200, notice.Attributes.ResultingBalance.TotalInCents);
        await live.FlushAsync();
        db.Services.Dispose();

        using var reopened = ServerServices.Build(new(db.Path), clock, liveWorld: true);
        Assert.Equal(200, Balance(reopened.GetRequiredService<GatewayRequests>(), AccountId));
        var items = reopened
            .GetRequiredService<LiveWorld>()
            .Economy.Inspect(world =>
                world.Filter<OwnedItem>(row => row.OwnerId == pilot.PilotId && row.TypeId == Plex).OrderBy(row => row.FlagId).ToArray()
            );
        Assert.Collection(
            items,
            hangar => Assert.Equal((item, (long)pilot.StationId, 3), (hangar.Id, hangar.LocationId, hangar.Quantity)),
            cargo => Assert.Equal((added.ItemId, pilot.ShipId, 6), (cargo.Id, cargo.LocationId, cargo.Quantity))
        );
    }

    [Fact]
    public void NativeVaultMarketWithdrawalCanListAndRepricePlex()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(emptyWallets: false, clock: clock, liveWorld: true);
        AddPlex(db);
        using CourierServiceTests.Client client = new(db, clock, AccountId);
        client.Refresh();
        var pilot = client.Context.Pilot;
        var publicMarket = db.Services.GetRequiredService<IPilotMarket>();
        var personalMarket = db.Services.GetRequiredService<IPersonalMarket>();
        var item = Assert.Single(publicMarket.Buy(pilot.PilotId, pilot.StationId, [new(Plex, 100, 10)]).Changes).Item;
        _ = db.Services.GetRequiredService<IPlexVault>().Deposit(pilot.PilotId, pilot.StationId, item.Id, 10);
        string reference = Reference(
            client.Call("WithdrawPlexFromVault", [5L, (long)pilot.StationId, "VaultToMarket", (long)InventoryFlag.Hangar], "invbroker")
        );
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        client.Context.AdvanceScheduledNotifications(clock.GetUtcNow());
        var result = Assert.Single(client.Context.Notifications.OfType<PlexVaultTransferCompleted>());
        Assert.Equal(reference, result.Reference);
        var fields = new Dictionary<string, object?>
        {
            ["itemID"] = result.ItemId,
            ["stationID"] = (long)pilot.StationId,
            ["typeID"] = (long)Plex,
            ["price"] = 10d,
            ["quantity"] = 5L,
        };
        Assert.True(
            Assert.IsType<bool>(
                Reply(client.Call("PlacePlexSellOrder", [new BlueInstance("utillib.KeyVal", fields), false, 1L, null], "marketProxy"))
            )
        );
        var listing = Assert.Single(personalMarket.PersonalOrders(pilot.PilotId));
        Assert.Equal(Plex, listing.TypeId);
        Assert.Equal(5, listing.Remaining);
        clock.AdvanceWallTime(TimeSpan.FromMinutes(5));
        Assert.Same(
            PilotCallResult.None,
            client.Call(
                "ModifyPlexCharOrder",
                [listing.Id, 11d, false, (long)listing.StationId, (long)listing.SystemId, 10d, (long)listing.Range, 5L, listing.Issued],
                "marketProxy"
            )
        );
        Assert.Equal(1100, Assert.Single(personalMarket.PersonalOrders(pilot.PilotId)).PriceCents);
        Assert.Equal(500, Balance(db.Services.GetRequiredService<GatewayRequests>(), AccountId));
    }

    [Fact]
    public async Task Version235AccountsReopenWithAnEmptyVault()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock, liveWorld: true);
        await db.Services.GetRequiredService<LiveWorld>().FlushAsync();
        db.Services.Dispose();
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldAccount DROP COLUMN PlexBalance; PRAGMA user_version=235;");
        }
        WorldDatabase database = new(db.Path);
        WorldInitialization.Initialize(database, clock);
        using var reopened = ServerServices.Build(database, clock, liveWorld: true);
        Assert.Equal(0, Balance(reopened.GetRequiredService<GatewayRequests>(), AccountId));
        Assert.NotNull(reopened.GetRequiredService<LiveWorld>().Economy.Inspect(world => world.Set<Account>().Find(AccountId)));
    }

    private static object? Reply(PilotCallResult result) => Assert.IsType<PilotCallResult.Success>(result).Reply.ToBlue();

    private static string Reference(PilotCallResult result) => Assert.IsType<string>(Reply(result));

    private static long Balance(GatewayRequests gateway, long? account) =>
        gateway
            .Handle(new() { Payload = new Any { TypeUrl = "type.googleapis.com/eve_public.plex.vault.api.BalanceRequest" } }, account)
            .Payload.Unpack<BalanceResponse>()
            .Balance.TotalInCents;

    private static void AddPlex(TestWorld db)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT INTO ItemType(Id,Name,GroupId) VALUES (44992,'PLEX',54);
            INSERT INTO SourceRecord VALUES ('client/types','44992','{{"published":1,"marketGroupID":3,"volume":0.01}}');
            INSERT INTO SourceRecord VALUES ('client/stationoperations','14','{{"services":[7]}}');
            """
        );
    }
}
