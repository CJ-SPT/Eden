using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Docking;

public sealed class DockedServicesTests
{
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
        var item = Assert.IsType<BlueConstructor>(Assert.Single(items));
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
