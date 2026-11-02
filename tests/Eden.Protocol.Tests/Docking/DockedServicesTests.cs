using Eden.Protocol.Docking;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;

namespace Eden.Protocol.Tests.Docking;

public sealed class DockedServicesTests
{
    [Fact]
    public void StationBindingListsOwnedShipAndExpiresBetweenSessions()
    {
        using TestWorld db = new();
        DockedPilot pilot = db.Store.FindPilot(1000001)!;
        DockedServices services = new(pilot, 10);
        object?[] getHangar = ["GetInventory", new object?[] { 10004L, null }, new Dictionary<string, object?>()];
        var bind = Assert.IsType<object?[]>(services.Call("invbroker", "MachoBindObject",
            [new object?[] { (long)pilot.StationId, 15L }, getHangar], _ => { }));
        var handle = Assert.IsType<BlueObjectReference>(bind[1]);
        string scope = Assert.IsType<string>(services.ResolveReference(handle.Id));
        var items = Assert.IsType<List<object?>>(services.Call(scope, "List", [4L], _ => { }));
        var item = Assert.IsType<BlueConstructor>(Assert.Single(items));
        Assert.Equal("blue.DBRow", item.Name);
        var descriptor = Assert.IsType<BlueConstructor>(item.Arguments[0]);
        var columns = Assert.IsType<object?[]>(descriptor.Arguments[0]);
        var keys = columns.Cast<object?[]>().Select(pair => (string)pair[0]!).ToList();
        var values = Assert.IsType<object?[]>(item.Arguments[1]);
        Assert.Equal(pilot.ShipId, values[keys.IndexOf("itemID")]);
        Assert.Equal(pilot.StationId, values[keys.IndexOf("locationID")]);
        Assert.Equal(pilot.PilotId, values[keys.IndexOf("ownerID")]);
        Assert.Null(new DockedServices(pilot, 11).ResolveReference(handle.Id));
        Assert.Same(DockedServices.Unsupported, services.Call("invbroker", "MachoBindObject",
            [new object?[] { (long)pilot.StationId + 1, 15L }, getHangar], _ => { }));
    }
}
