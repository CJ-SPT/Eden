using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Docking;

public sealed class DockedServicesTests
{
    [Fact]
    public void FittingServiceRequiresTheCurrentStationAndExpiresOnDeparture()
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001)!;
        using var scope = db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using PilotCallContext context = new(1000001, pilot.Account, [pilot],
            selected => new(db.Store, selected, TimeProvider.System, new(), db.Objects), 10, []);
        context.Select(pilot);
        object?[] standingCheck = ["DoStandingCheckForStationService", new object?[] { (long)StationService.Fitting }];
        Assert.Same(PilotCallRouter.Unsupported, router.Call(context, "corpStationMgr", "MachoBindObject",
            new object?[] { (long)pilot.StationId + 1, standingCheck }, _ => { }));
        var binding = Assert.IsType<object?[]>(router.Call(context, "corpStationMgr", "MachoBindObject",
            new object?[] { (long)pilot.StationId, standingCheck }, _ => { }));
        var reference = Assert.IsType<BlueObjectReference>(binding[0]);
        Assert.Null(binding[1]);
        Assert.Equal("corpStationMgr", context.ResolveReference(reference.Id));
        context.ReleaseLocationReferences();
        Assert.Null(context.ResolveReference(reference.Id));
    }

    [Fact]
    public void PartialStackChangesUseThePinnedClientsStackSizeColumn()
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001)!;
        using (WorldDbContext saved = new(db.Path))
        {
            var item = saved.Set<OwnedItem>().Single(i => i.OwnerId == pilot.PilotId);
            item.Quantity = 30;
            saved.SaveChanges();
        }
        using var scope = db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var inventory = scope.ServiceProvider.GetRequiredService<IPilotInventory>();
        long id = Assert.Single(inventory.List(pilot.PilotId, pilot.StationId)).Id;
        using PilotCallContext context = new(1000001, pilot.Account, [pilot],
            selected => new(db.Store, selected, TimeProvider.System, new(), db.Objects), 10, []);
        context.Select(pilot);
        Assert.Null(router.Call(context, "@ship", "Add", new object?[] { id, (long)pilot.StationId }, _ => { },
            new() { ["flag"] = 5L, ["qty"] = 25L }));
        Assert.Equal(2, context.Notifications.Count);
        var remainder = context.Notifications[0].Arguments;
        Assert.Equal(3, remainder.Length);
        Assert.Null(remainder[2]);
        var changes = Assert.IsType<Dictionary<long, object?>>(remainder[1]);
        Assert.Equal(30, changes[5]);
        Assert.Equal(30, changes[9]);
        Assert.False(changes.ContainsKey(10));
        var row = Assert.IsType<BlueConstructor>(remainder[0]);
        var descriptor = Assert.IsType<BlueConstructor>(row.Arguments[0]);
        var columns = Assert.IsType<object?[]>(descriptor.Arguments[0]).Cast<object?[]>().ToArray();
        // inventorycommon.const in build 3662168 defines ixStackSize=9 and ixSingleton=10.
        Assert.Equal("stacksize", columns[9][0]);
        Assert.Equal("singleton", columns[10][0]);
        var values = Assert.IsType<object?[]>(row.Arguments[1]);
        Assert.Equal(5, values[9]);
        Assert.Equal(0, values[10]);
        var created = context.Notifications[1].Arguments;
        var createdChanges = Assert.IsType<Dictionary<long, object?>>(created[1]);
        Assert.Equal(0, createdChanges[9]);
        Assert.Equal(25, Assert.IsType<object?[]>(Assert.IsType<BlueConstructor>(created[0]).Arguments[1])[9]);
    }

    [Fact]
    public void CargoTransferSendsOldLocationAndFlagAndRejectsUnavailableItems()
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001)!;
        using var scope = db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var inventory = scope.ServiceProvider.GetRequiredService<IPilotInventory>();
        var item = Assert.Single(inventory.List(pilot.PilotId, pilot.StationId));
        using PilotCallContext context = new(1000001, pilot.Account, [pilot],
            selected => new(db.Store, selected, TimeProvider.System, new(), db.Objects), 10, []);
        context.Select(pilot);
        Assert.Null(router.Call(context, "@ship", "Add", new object?[] { item.Id, (long)pilot.StationId }, _ => { },
            new() { ["flag"] = 5L }));
        var notification = Assert.Single(context.Notifications);
        Assert.Equal("OnItemChange", notification.Name);
        var changes = Assert.IsType<Dictionary<long, object?>>(notification.Arguments[1]);
        Assert.Equal((long)pilot.StationId, changes[3]);
        Assert.Equal(4, changes[4]);
        Assert.IsType<BlueConstructor>(notification.Arguments[0]);
        // The receive codec deliberately rejects DBRow reductions; the client owns decoding those.
        Assert.NotEmpty(BlueMarshal.Encode(notification.Arguments));
        Assert.Single(Assert.IsType<List<object?>>(router.Call(context, "@ship", "List", new object?[] { 5L }, _ => { })));
        Assert.IsType<PilotCallRouter.Error>(router.Call(context, "@ship", "Add", new object?[] { item.Id, (long)pilot.StationId }, _ => { }));
        Assert.Single(context.Notifications);
        context.Notifications.Clear();
        Assert.Null(router.Call(context, "@hangar", "MultiAdd", new object?[] { new List<object?> { item.Id }, pilot.ShipId }, _ => { }));
        Assert.Single(context.Notifications);
        Assert.Empty(inventory.List(pilot.PilotId, pilot.ShipId));
    }

    [Fact]
    public void StationBindingListsOwnedShipAndExpiresBetweenSessions()
    {
        using TestWorld db = new();
        DockedPilot pilot = db.Store.FindPilot(1000001)!;
        using var scope = db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using PilotCallContext context = new(1000001, pilot.Account, [pilot],
            selected => new(db.Store, selected, TimeProvider.System, new(), db.Objects), 10, []);
        context.Select(pilot);
        object?[] getHangar = ["GetInventory", new object?[] { 10004L, null }, new Dictionary<string, object?>()];
        var bind = Assert.IsType<object?[]>(router.Call(context, "invbroker", "MachoBindObject",
            new object?[] { new object?[] { (long)pilot.StationId, 15L }, getHangar }, _ => { }));
        var handle = Assert.IsType<BlueObjectReference>(bind[1]);
        string boundService = Assert.IsType<string>(context.ResolveReference(handle.Id));
        var items = Assert.IsType<List<object?>>(router.Call(context, boundService, "List", new object?[] { 4L }, _ => { }));
        var item = Assert.IsType<BlueConstructor>(items[0]);
        Assert.Equal(2, items.Count);
        Assert.Equal("blue.DBRow", item.Name);
        var descriptor = Assert.IsType<BlueConstructor>(item.Arguments[0]);
        var columns = Assert.IsType<object?[]>(descriptor.Arguments[0]);
        var keys = columns.Cast<object?[]>().Select(pair => (string)pair[0]!).ToList();
        var values = Assert.IsType<object?[]>(item.Arguments[1]);
        Assert.Equal(pilot.ShipId, values[keys.IndexOf("itemID")]);
        Assert.Equal(pilot.StationId, values[keys.IndexOf("locationID")]);
        Assert.Equal(pilot.PilotId, values[keys.IndexOf("ownerID")]);
        Assert.Null(new PilotCallContext(1000001, pilot.Account, [pilot], _ => throw new NotSupportedException(), 11, []).ResolveReference(handle.Id));
        Assert.Same(PilotCallRouter.Unsupported, router.Call(context, "invbroker", "MachoBindObject",
            new object?[] { new object?[] { (long)pilot.StationId + 1, 15L }, getHangar }, _ => { }));
    }
}
