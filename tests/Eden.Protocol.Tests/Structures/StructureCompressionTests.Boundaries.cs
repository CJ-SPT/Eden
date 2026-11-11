using System.Text.Json;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Structures.StructureTests.Fixture;

namespace Eden.Protocol.Tests.Structures;

public sealed partial class StructureCompressionTests
{
    [Theory]
    [InlineData(false, 1, 0)]
    [InlineData(true, 1, 0)]
    [InlineData(false, 2, 1)]
    [InlineData(true, 101, 80)]
    public void NativeGasDecompressionReportsEfficiencyAndRemovesOnlyZeroOutputStacks(bool liveWorld, int quantity, int expected)
    {
        using Fixture f = new(liveWorld: liveWorld);
        var facility = Prepare(f);
        var pair = GasPair(f);
        long item = HangarItem(f, facility.Id, pair.Compressed, quantity);
        using Client client = new(f);
        var efficiency = BlueRead.Tuple(client.Reply("GetMyGasDecompressionEfficiency", []), 2);
        Assert.Equal(.8, efficiency[0]);
        Assert.Equal(0d, efficiency[1]);
        var result = BlueRead.Tuple(client.Reply("DecompressGasInStructure", [item]), 6);
        Assert.Equal(
            new object?[] { item, (long)pair.Compressed, (long)quantity, expected > 0 ? item : null, (long)pair.Raw, (long)expected },
            result
        );
        var notice = Assert.IsType<ItemChanged>(Assert.Single(client.Context.Notifications));
        Assert.Equal(item, notice.Item.Id);
        if (expected > 0)
        {
            Assert.Equal(pair.Raw, notice.Item.TypeId);
            Assert.Equal(expected, notice.Item.Quantity);
            Assert.Equal(pair.Compressed, notice.Previous[InventoryColumn.Type]);
            Assert.Equal((long)HoldCargoGroup.CompressedGas, notice.Previous[InventoryColumn.Group]);
            Assert.Equal(quantity, notice.Previous[InventoryColumn.Quantity]);
        }
        else
        {
            Assert.Equal((long)InventoryLocation.Graveyard, notice.Item.LocationId);
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            if (expected == 0)
            {
                Assert.Null(world.Set<OwnedItem>().Find(item));
            }
            else
            {
                Assert.Equal(pair.Raw, world.Set<OwnedItem>().Find(item)!.TypeId);
                Assert.Equal(expected, world.Set<OwnedItem>().Find(item)!.Quantity);
            }
        }
        client.Context.Notifications.Clear();
        Assert.IsType<PilotCallResult.Failure>(client.Call("DecompressGasInStructure", [item]));
        Assert.Empty(client.Context.Notifications);
        client.Dispose();
        LiveWorld.Find(f.Db.Path)?.Dispose();
        WorldInitialization.Open(f.Db.Path);
        using WorldDbContext reopened = new(f.Db.Path);
        Assert.Equal(expected > 0 ? expected : null, reopened.Set<OwnedItem>().Find(item)?.Quantity);
    }

    [Fact]
    public void ForeignRemoteSingletonAndWrongContainerInputsRejectWithoutChangingAnyItems()
    {
        using Fixture f = new();
        var facility = Prepare(f);
        var pair = GasPair(f);
        var compression = f.Db.Services.GetRequiredService<IStructureCompression>();
        long ore = HangarItem(f, facility.Id, Veldspar, 10);
        long gas = HangarItem(f, facility.Id, pair.Compressed, 10);
        using Client client = new(f);
        foreach (int scenario in Enumerable.Range(0, 6))
        {
            using (WorldDbContext world = new(f.Db.Path))
            {
                var row = world.Set<OwnedItem>().Find(ore)!;
                row.OwnerId = scenario == 0 ? 90000002 : f.PilotId;
                row.LocationId = scenario switch
                {
                    1 => 60003760,
                    2 => 123L,
                    _ => facility.Id,
                };
                row.FlagId = scenario == 3 ? (int)InventoryFlag.Cargo : (int)InventoryFlag.Hangar;
                row.Singleton = scenario == 4;
                row.TypeId = scenario == 5 ? 34 : Veldspar;
                world.SaveChanges();
            }
            string before = Items(f);
            Assert.IsType<PilotCallResult.Failure>(client.Call("CompressItemInStructure", [ore]));
            Assert.Equal(before, Items(f));
            Assert.Empty(client.Context.Notifications);
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            var row = world.Set<OwnedItem>().Find(gas)!;
            row.LocationId = f.ShipId;
            row.FlagId = (int)InventoryFlag.Cargo;
            world.SaveChanges();
        }
        string saved = Items(f);
        Assert.Throws<InvalidDataException>(() => compression.DecompressGas(f.PilotId, gas));
        Assert.Equal(saved, Items(f));
        foreach (
            var request in new (string Method, object?[] Args)[]
            {
                ("CompressItemInStructure", [0L]),
                ("CompressItemInStructure", [true]),
                ("CompressItemInStructure", [new List<object?> { gas }]),
                ("CompressItemInStructure", [ore, gas]),
                ("DecompressGasInStructure", []),
                ("GetMyGasDecompressionEfficiency", [gas]),
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(client.Call(request.Method, request.Args));
            Assert.Equal(saved, Items(f));
        }
        Assert.IsType<PilotCallResult.Failure>(client.Call("CompressItemInStructure", [ore], new() { ["ownerID"] = f.PilotId }));
        Assert.Empty(client.Context.Notifications);
    }

    [Fact]
    public void ActiveShipCargoCompressesInPlaceWhileGasRequiresTheStructureHangar()
    {
        using Fixture f = new();
        Prepare(f);
        var compression = f.Db.Services.GetRequiredService<IStructureCompression>();
        long ore = f.Item(Veldspar, 10);
        var result = compression.Compress(f.PilotId, ore);
        Assert.Equal(ore, result.OutputItemId);
        Assert.Equal(f.ShipId, result.Change.Item.LocationId);
        Assert.Equal((int)InventoryFlag.Cargo, result.Change.Item.FlagId);
        Assert.True(result.Change.Item.Volume < result.Change.Previous!.Volume);
        var gas = GasPair(f);
        Assert.Throws<InvalidDataException>(() => compression.DecompressGas(f.PilotId, f.Item(gas.Compressed, 100)));
    }

    [Fact]
    public void AccessRevocationOfflineServicesAndTheExactFuelDeadlinePreventCompression()
    {
        using Fixture f = new();
        var facility = Prepare(f);
        long item = HangarItem(f, facility.Id, Veldspar, 10);
        var compression = f.Db.Services.GetRequiredService<IStructureCompression>();
        var module = facility.Modules.Single(m => m.TypeId == ReprocessingModule);
        f.Structures.SetAccess(f.PilotId, facility.Id, false, [new(f.PilotId, false)]);
        Assert.Throws<InvalidDataException>(() => compression.Compress(f.PilotId, item));
        Assert.Equal(new(0, 0), compression.Efficiency(f.PilotId));
        f.Structures.SetAccess(f.PilotId, facility.Id, false, []);
        f.Structures.SetServiceOnline(f.PilotId, facility.Id, module.ItemId, false);
        Assert.Throws<InvalidDataException>(() => compression.Compress(f.PilotId, item));
        f.Structures.DepositFuel(f.PilotId, facility.Id, f.Item(4051, 1000), 1000);
        f.Structures.SetServiceOnline(f.PilotId, facility.Id, module.ItemId, true);
        facility = f.Structures.Read(f.PilotId, facility.Id)!;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Where(i => i.LocationId == facility.Id && i.FlagId == StructureRules.FuelBayFlag).ExecuteDelete();
        }
        f.Clock.UtcNow = DateTimeOffset.FromFileTime(facility.Modules.Single(m => m.ItemId == module.ItemId).NextFuelAt!.Value);
        Assert.Throws<InvalidDataException>(() => compression.Compress(f.PilotId, item));
        Assert.Equal(new(0, 0), compression.Efficiency(f.PilotId));
        using WorldDbContext check = new(f.Db.Path);
        Assert.Equal(Veldspar, check.Set<OwnedItem>().Find(item)!.TypeId);
        Assert.Equal(10, check.Set<OwnedItem>().Find(item)!.Quantity);
    }

    private static (int Raw, int Compressed) GasPair(Fixture f)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        var pair = catalog
            .Definitions.Pin(CatalogData.Client.Compressibletypes)
            .First(p => catalog.RequireItem(checked((int)p.Value)).GroupId == (int)HoldCargoGroup.CompressedGas);
        return (checked((int)pair.Key), checked((int)pair.Value));
    }

    private static string Items(Fixture f)
    {
        using WorldDbContext world = new(f.Db.Path);
        return JsonSerializer.Serialize(world.Set<OwnedItem>().OrderBy(i => i.Id).ToArray());
    }

    private sealed class Client : IDisposable
    {
        private readonly IServiceScope _scope;
        private readonly PilotCallRouter _router;
        internal PilotCallContext Context { get; }

        internal Client(Fixture f)
        {
            _scope = f.Db.Services.CreateScope();
            var pilot = f.Db.Store.FindPilot(1000001)!;
            Context = new(
                1000001,
                pilot.Account,
                [pilot],
                _scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
                42,
                SignedSettings.Empty
            );
            Context.Select(pilot);
            _router = _scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        }

        internal PilotCallResult Call(string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            _router.Call(Context, "structureCompressionMgr", method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);

        internal object? Reply(string method, object?[] args) =>
            BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(Call(method, args)).Reply));

        public void Dispose()
        {
            Context.Dispose();
            _scope.Dispose();
        }
    }
}
