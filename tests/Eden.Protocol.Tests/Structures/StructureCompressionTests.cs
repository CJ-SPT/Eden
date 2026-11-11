using Eden.Data.World.Inventory;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Eden.World.Structures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Structures.StructureTests.Fixture;

namespace Eden.Protocol.Tests.Structures;

public sealed partial class StructureCompressionTests
{
    private const int ReprocessingModule = 35899;
    private const int Veldspar = 1230;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeCompressionRewritesOneStackAndNotifiesItsOriginalTypeAcrossReopen(bool liveWorld)
    {
        using Fixture f = new(liveWorld: liveWorld);
        var facility = Prepare(f);
        long itemId = HangarItem(f, facility.Id, Veldspar, 1000);
        int compressed;
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath))
        {
            compressed = StructureCompressionCatalog.Compressed(catalog, Veldspar);
        }
        using var scope = f.Db.Services.CreateScope();
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
            42,
            SignedSettings.Empty
        );
        context.Select(pilot);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var result = Assert.IsType<PilotCallResult.Success>(
            router.Call(context, "structureCompressionMgr", "CompressItemInStructure", new object?[] { itemId }, _ => { })
        );
        var tuple = BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(result.Reply)), 6);
        Assert.Equal(new object?[] { itemId, (long)Veldspar, 1000L, itemId, (long)compressed, 1000L }, tuple);
        var notification = Assert.IsType<ItemChanged>(Assert.Single(context.Notifications));
        Assert.Equal(compressed, notification.Item.TypeId);
        Assert.Equal(itemId, notification.Item.Id);
        Assert.Equal(Veldspar, Assert.Single(notification.Previous).Value);
        Assert.Equal(InventoryColumn.Type, Assert.Single(notification.Previous).Key);
        // The inbound decoder intentionally does not execute the inventory DBRow reduction.
        // Round-trip the changed-field map independently of the outbound row constructor.
        var previous = BlueMarshal.Decode(BlueMarshal.Encode(notification.Arguments[1]));
        Assert.Equal((long)Veldspar, Assert.IsType<Dictionary<long, object?>>(previous)[1L]);
        var saved = Assert.Single(f.Db.Services.GetRequiredService<IPilotInventory>().List(f.PilotId, facility.Id));
        Assert.Equal(compressed, saved.TypeId);
        Assert.Equal(1000, saved.Quantity);
        Assert.Equal(itemId, saved.Id);
        context.Notifications.Clear();
        Assert.IsType<PilotCallResult.Failure>(
            router.Call(context, "structureCompressionMgr", "CompressItemInStructure", new object?[] { itemId }, _ => { })
        );
        Assert.Empty(context.Notifications);
        context.Dispose();
        LiveWorld.Find(f.Db.Path)?.Dispose();
        WorldInitialization.Open(f.Db.Path);
        using WorldDbContext check = new(f.Db.Path);
        var row = check.Set<OwnedItem>().Find(itemId)!;
        Assert.Equal(compressed, row.TypeId);
        Assert.Equal(1000, row.Quantity);
        Assert.Equal(facility.Id, row.LocationId);
    }

    private static StructureSnapshot Prepare(Fixture f, int hull = 35832)
    {
        PassiveCatalogFixture.LoadCompression(new WorldDatabase(f.Db.Path).CatalogPath);
        var structure = f.Online(hull);
        long module = f.Item(ReprocessingModule);
        f.Structures.FitService(f.PilotId, structure.Id, module, StructureRules.FirstServiceSlot);
        f.Structures.DepositFuel(f.PilotId, structure.Id, f.Item(4051, 1000), 1000);
        f.Structures.SetServiceOnline(f.PilotId, structure.Id, module, true);
        f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        return f.Structures.Read(f.PilotId, structure.Id)!;
    }

    private static long HangarItem(Fixture f, long structureId, int typeId, int quantity)
    {
        long id = f.Item(typeId, quantity);
        using WorldDbContext world = new(f.Db.Path);
        var item = world.Set<OwnedItem>().Find(id)!;
        item.LocationId = structureId;
        item.FlagId = (int)InventoryFlag.Hangar;
        world.SaveChanges();
        return id;
    }
}
