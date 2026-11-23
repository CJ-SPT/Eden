using Eden.Data.World.Inventory;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Notifications;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Notifications;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Structures.StructureTests.Fixture;

namespace Eden.Protocol.Tests.Structures;

public sealed class StructureDeliveryTests
{
    [Fact]
    public async Task NativeDeliverToPersistsPrivateCustodyAndNoticesThenAllowsClaimAfterRestart()
    {
        const long SenderAccount = 1000001;
        const long ReceiverAccount = 1000002;
        const int DeliveredType = 199;
        const int MiscellaneousGroup = 4;
        const int StationContainer = 17366;
        using Fixture f = new();
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO ItemGroup(Id,Name,CategoryId) VALUES (448,'Audit Log Secure Container',2);
                INSERT INTO ItemType(Id,Name,GroupId,Capacity) VALUES (17366,'Station Container',448,1000000);
                INSERT INTO SourceRecord VALUES ('client/types','17366','{{"volume":2000000}}');
                """
            );
        }
        var structure = f.Online();
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        long sender = f.PilotId;
        long senderShip = f.ShipId;
        long senderStation = f.Db.Store.FindPilot(SenderAccount)!.StationId;
        var receiver = f.Db.Store.FindPilot(ReceiverAccount)!;
        Assert.NotEqual(structure.Id, receiver.DockedLocationId);
        var inventory = f.Db.Services.GetRequiredService<IPilotInventory>();
        var inbox = f.Db.Services.GetRequiredService<IPilotInbox>();
        var docking = f.Db.Services.GetRequiredService<IStationDocking>();

        long first,
            sameType,
            differentType,
            self,
            later,
            foreign,
            cargo,
            remoteAsset,
            alreadyDelivered,
            container,
            contents,
            mixedContainer,
            mixedSenderContents,
            mixedReceiverContents;
        using (var stock = live.Economy.Begin(new(pilots: [sender, receiver.PilotId])))
        {
            first = Add(sender, 34, 21);
            sameType = Add(sender, 34, 37);
            differentType = Add(sender, 35, 3);
            self = Add(sender, 35, 2);
            later = Add(sender, 35, 7);
            foreign = Add(receiver.PilotId, 34, 4);
            cargo = Add(sender, 34, 6, InventoryFlag.Cargo, senderShip);
            remoteAsset = Add(sender, 34, 5, location: senderStation);
            alreadyDelivered = Add(sender, 34, 8, InventoryFlag.StructureDeliveries);
            container = Add(sender, StationContainer, 1, singleton: true);
            contents = Add(sender, 34, 11, InventoryFlag.Locked, container);
            mixedContainer = Add(sender, StationContainer, 1, singleton: true);
            mixedSenderContents = Add(sender, 35, 13, InventoryFlag.Unlocked, mixedContainer);
            mixedReceiverContents = Add(receiver.PilotId, 34, 17, InventoryFlag.Locked, mixedContainer);
            stock.Commit();

            long Add(
                long owner,
                int type,
                int quantity,
                InventoryFlag flag = InventoryFlag.Hangar,
                long? location = null,
                bool singleton = false
            )
            {
                long id = stock.NextItemId();
                stock.Add(
                    new OwnedItem
                    {
                        Id = id,
                        OwnerId = owner,
                        LocationId = location ?? structure.Id,
                        FlagId = (int)flag,
                        TypeId = type,
                        Quantity = quantity,
                        Singleton = singleton,
                    }
                );
                return id;
            }
        }

        using var senderScope = f.Db.Services.CreateScope();
        var router = senderScope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var factory = senderScope.ServiceProvider.GetRequiredService<FlightSessionFactory>();
        long session = 70;
        using (var outside = Select(SenderAccount))
        {
            Assert.IsType<PilotCallResult.Failure>(Send(outside, receiver.PilotId, [first]));
            Assert.Empty(outside.Notifications.Concat(outside.NotificationsBeforeFlight));
        }
        docking.Dock(sender, senderShip, structure.Id);
        long receiptId;
        using (var docked = Select(SenderAccount))
        {
            Assert.IsType<PilotCallResult.Failure>(Send(docked, long.MaxValue, [first]));
            Assert.IsType<PilotCallResult.Failure>(Send(docked, receiver.PilotId, [first, "invalid"]));
            Assert.IsType<PilotCallResult.Failure>(Send(docked, receiver.PilotId, [first], structure.Id + 1));
            Assert.Empty(inbox.Read(receiver.PilotId, new()));
            Assert.Empty(docked.Notifications.Concat(docked.NotificationsBeforeFlight));
            Assert.Equal((int)InventoryFlag.Hangar, Assert.Single(inventory.List(sender, structure.Id), item => item.Id == first).FlagId);

            List<object?> batch =
            [
                first,
                foreign,
                sameType,
                cargo,
                remoteAsset,
                long.MaxValue,
                alreadyDelivered,
                differentType,
                container,
                mixedContainer,
            ];
            Assert.Same(PilotCallResult.None, Send(docked, receiver.PilotId, batch));
            var changedIds = new[] { first, sameType, differentType, container, contents };
            Assert.Equal(changedIds, ItemChanges(docked).Select(item => item.Item.Id).Order());
            Assert.Same(PilotCallResult.None, Send(docked, receiver.PilotId, batch));
            Assert.Equal(changedIds, ItemChanges(docked).Select(item => item.Item.Id).Order());
            receiptId = Assert.Single(inbox.Read(receiver.PilotId, new(Group: InboxGroup.Miscellaneous))).Id;

            Assert.Same(PilotCallResult.None, Send(docked, sender, [self]));
            Assert.Equal((int)InventoryFlag.StructureDeliveries, Assert.Single(ItemChanges(docked), n => n.Item.Id == self).Item.FlagId);
            Assert.Equal(DeliveredType, (int)Assert.Single(inbox.Read(sender, new())).Type);
        }

        Assert.Equal(6, Assert.Single(inventory.List(sender, senderShip), item => item.Id == cargo).Quantity);
        Assert.Equal(5, Assert.Single(inventory.List(sender, senderStation), item => item.Id == remoteAsset).Quantity);
        Assert.Equal(8, Assert.Single(inventory.List(sender, structure.Id), item => item.Id == alreadyDelivered).Quantity);
        var recipientStock = inventory.List(receiver.PilotId, structure.Id);
        Assert.Equal((int)InventoryFlag.Hangar, Assert.Single(recipientStock, item => item.Id == foreign).FlagId);
        Assert.Equal(new[] { first, sameType, differentType, container }, recipientStock.Where(IsDelivery).Select(item => item.Id).Order());
        Assert.True(Assert.Single(recipientStock, item => item.Id == container).Singleton);
        Assert.Empty(inventory.List(sender, container));
        AssertItem(
            Assert.Single(inventory.List(receiver.PilotId, container)),
            contents,
            receiver.PilotId,
            container,
            InventoryFlag.Locked,
            11
        );
        AssertItem(
            Assert.Single(inventory.List(sender, structure.Id), item => item.Id == mixedContainer),
            mixedContainer,
            sender,
            structure.Id,
            InventoryFlag.Hangar,
            1
        );
        AssertItem(
            Assert.Single(inventory.List(sender, mixedContainer)),
            mixedSenderContents,
            sender,
            mixedContainer,
            InventoryFlag.Unlocked,
            13
        );
        AssertItem(
            Assert.Single(inventory.List(receiver.PilotId, mixedContainer)),
            mixedReceiverContents,
            receiver.PilotId,
            mixedContainer,
            InventoryFlag.Locked,
            17
        );

        await live.FlushAsync();
        live.Dispose();
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using LiveWorld reopened = new(new(f.Db.Path), f.Clock, _ => { });
        using var receiverScope = f.Db.Services.CreateScope();
        var receiverRouter = receiverScope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using (var remote = Select(ReceiverAccount))
        {
            var rows = Assert.IsType<List<object?>>(
                Wire(Call(receiverRouter, remote, "notificationMgr", "GetByGroupID", [(long)MiscellaneousGroup]))
            );
            var encodedNotice = Assert.IsType<BlueInstance>(Assert.Single(rows));
            Assert.Equal("utillib.KeyVal", encodedNotice.Name);
            var notice = BlueRead.Fields(encodedNotice.State);
            Assert.Equal(receiptId, notice["notificationID"]);
            Assert.Equal((long)DeliveredType, notice["typeID"]);
            Assert.Equal(sender, notice["senderID"]);
            Assert.Equal(receiver.PilotId, notice["receiverID"]);
            Assert.False(Assert.IsType<bool>(notice["processed"]));
            var data = BlueRead.Fields(notice["data"]);
            Assert.Equal(sender, data["charID"]);
            Assert.Equal(structure.Id, data["structureID"]);
            Assert.Equal((long)structure.Profile.TypeId, data["structureTypeID"]);
            Assert.Equal((long)Fixture.SystemId, data["solarsystemID"]);
            var showInfo = Assert.IsType<object?[]>(data["structureShowInfoData"]);
            Assert.True(showInfo is [_, long type, long id] && type == structure.Profile.TypeId && id == structure.Id);
            Assert.Equal("showinfo", BlueMarshal.Text(showInfo[0]));
            Assert.Collection(
                Assert.IsType<List<object?>>(data["listOfTypesAndQty"]),
                row => Assert.Equal(new object?[] { 58L, 34L }, Assert.IsType<object?[]>(row)),
                row => Assert.Equal(new object?[] { 3L, 35L }, Assert.IsType<object?[]>(row)),
                row => Assert.Equal(new object?[] { 1L, (long)StationContainer }, Assert.IsType<object?[]>(row))
            );
        }

        f.Structures.SetAccess(sender, structure.Id, false, [new(receiver.PilotId, true)]);
        f.Db.Store.SetPosition(
            receiver.ShipId,
            structure.Position + new SpacePosition(structure.Profile.Radius + 1_000, 0, 0),
            Fixture.SystemId
        );
        docking.Dock(receiver.PilotId, receiver.ShipId, structure.Id);
        using var dockedReceiver = Select(ReceiverAccount);
        using var returnedSender = Select(SenderAccount);
        var monitor = receiverScope.ServiceProvider.GetRequiredService<InboxHandler>();
        monitor.Enter(dockedReceiver);
        Assert.Same(
            PilotCallResult.None,
            Send(returnedSender, receiver.PilotId, [first, sameType, differentType, container, mixedContainer])
        );
        Assert.Equal(receiptId, Assert.Single(inbox.Read(receiver.PilotId, new())).Id);
        Assert.Same(PilotCallResult.None, Send(returnedSender, receiver.PilotId, [later]));
        monitor.Advance(dockedReceiver);
        var liveNotice = Assert.Single(dockedReceiver.Notifications.OfType<InboxNotificationReceived>());
        Assert.Equal(DeliveredType, (int)liveNotice.Notification.Type);
        var incoming = Assert.Single(ItemChanges(dockedReceiver), change => change.Item.Id == later);
        Assert.Equal(receiver.PilotId, incoming.Item.OwnerId);
        Assert.Equal((int)InventoryFlag.StructureDeliveries, incoming.Item.FlagId);

        var reference = Assert.IsType<BlueObjectReference>(
            Reply(Call(receiverRouter, dockedReceiver, "invbroker", "GetInventoryFromId", [structure.Id, false])).ToBlue()
        );
        string hangar = Assert.IsType<string>(dockedReceiver.ResolveReference(reference.Id));
        Assert.Same(
            PilotCallResult.None,
            Call(
                receiverRouter,
                dockedReceiver,
                hangar,
                "MultiAdd",
                [new List<object?> { first, sameType, differentType, later, container }, structure.Id],
                new() { ["flag"] = (long)InventoryFlag.Hangar }
            )
        );
        Assert.Equal(
            new[] { first, sameType, differentType, later, foreign, container }.Order(),
            inventory.List(receiver.PilotId, structure.Id).Select(item => item.Id).Order()
        );
        Assert.DoesNotContain(inventory.List(receiver.PilotId, structure.Id), IsDelivery);
        AssertItem(
            Assert.Single(inventory.List(receiver.PilotId, container)),
            contents,
            receiver.PilotId,
            container,
            InventoryFlag.Locked,
            11
        );

        static void AssertItem(InventoryItem item, long id, long owner, long location, InventoryFlag flag, int quantity)
        {
            Assert.Equal((id, owner, location, (int)flag, quantity), (item.Id, item.OwnerId, item.LocationId, item.FlagId, item.Quantity));
        }

        PilotCallContext Select(long account)
        {
            var pilot = f.Db.Store.FindPilot(account)!;
            PilotCallContext context = new(account, pilot.Account, [pilot], factory.Create, session++, SignedSettings.Empty);
            context.Select(pilot);
            return context;
        }

        PilotCallResult Send(PilotCallContext context, long recipient, List<object?> items, long? location = null) =>
            Call(router, context, "structureDeliveries", "Deliver", [location ?? structure.Id, recipient, items]);

        static bool IsDelivery(InventoryItem item) => item.FlagId == (int)InventoryFlag.StructureDeliveries;

        static IEnumerable<ItemChanged> ItemChanges(PilotCallContext context) =>
            context.Notifications.Concat(context.NotificationsBeforeFlight).OfType<ItemChanged>();

        static PilotCallResult Call(
            PilotCallRouter router,
            PilotCallContext context,
            string service,
            string method,
            object?[] args,
            Dictionary<string, object?>? keywords = null
        ) => router.Call(context, service, method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);

        static BlueModel Reply(PilotCallResult result) => Assert.IsType<PilotCallResult.Success>(result).Reply;

        static object? Wire(PilotCallResult result) => BlueMarshal.Decode(BlueMarshal.Encode(Reply(result)));
    }
}
