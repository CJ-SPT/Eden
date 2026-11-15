using System.Text.Json;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Fittings;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Docking;
using Eden.Protocol.Fittings;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Ammunition;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Fittings;

public sealed class MultifitTests
{
    private const int Hull = 32880;
    private const int Miner = 3651;
    private const int Charge = 222;
    private const long Station = 60003760;

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(MultifitRules.MaximumShips + 1)]
    public void TypedBatchRulesRejectCountsOutsideTheAtomicLimit(int count)
    {
        MultifitRequest request = new(new(0, Hull, Station, new Dictionary<int, IReadOnlyList<long>>(), [], [], false), count, null, null);
        Assert.Throws<InvalidDataException>(() => MultifitRules.Check(request));
        MultifitRules.Check(request with { Count = MultifitRules.MaximumShips });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypedRequirementsAggregateModulesSuppliesAndOnlyConfirmedRigs(bool fitRigs)
    {
        const int rig = 31788;
        ApplyFittingRequest fitting = new(
            0,
            Hull,
            Station,
            new Dictionary<int, IReadOnlyList<long>>(),
            [new((int)InventoryFlag.HighSlot0, Miner), new((int)InventoryFlag.HighSlot1, Miner), new((int)InventoryFlag.RigSlot0, rig)],
            [new(Miner, InventoryFlag.Cargo, 3), new(Charge, InventoryFlag.Cargo, 10)],
            fitRigs
        );
        var required = MultifitRules.RequiredItems(new(fitting, 2, null, null)).ToDictionary(item => item.TypeId, item => item.Quantity);
        Assert.Equal(2, required[Hull]);
        Assert.Equal(10, required[Miner]);
        Assert.Equal(20, required[Charge]);
        Assert.Equal(fitRigs ? 2 : 0, required.GetValueOrDefault(rig));
    }

    [Fact]
    public async Task NativeBoundMultifitSplitsPackagesFitsInactiveHullsAndSurvivesCheckpoint()
    {
        using var fixture = Create();
        long package = Grant(fixture, Hull, 3);
        long modules = Grant(fixture, Miner, 4);
        long ammo = Grant(fixture, Charge, 50);
        using var scope = fixture.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using var context = fixture.Context();
        var pilot = context.Pilot;
        var activeItems = fixture.Inventory.List(pilot.PilotId, pilot.ShipId);
        var beforeShips = Ships(fixture).Select(item => item.Id).ToHashSet();
        var keywords = Request(context, count: 2, charge: 10);
        context.Notifications.Clear();
        List<string> commands = [];
        object?[] reply;
        using (SessionPerformance.Begin("resident multifit", _ => { }, force: true, command: commands.Add))
        {
            reply = Assert.IsType<object?[]>(Reply(Bind(router, context, keywords)));
        }
        Assert.DoesNotContain(commands, sql => sql.Contains("World", StringComparison.Ordinal));
        Assert.IsType<BlueObjectReference>(reply[0]);
        Assert.Null(reply[1]);
        var ships = Ships(fixture).Where(item => !beforeShips.Contains(item.Id)).ToArray();
        Assert.Equal(2, ships.Length);
        Assert.DoesNotContain(ships, ship => ship.Id == package);
        foreach (var ship in ships)
        {
            var contents = fixture.Inventory.List(pilot.PilotId, ship.Id);
            var fitted = contents.Where(item => item.TypeId == Miner).OrderBy(item => item.FlagId).ToArray();
            Assert.Equal(new[] { (int)InventoryFlag.HighSlot0, (int)InventoryFlag.HighSlot1 }, fitted.Select(item => item.FlagId));
            Assert.All(fitted, module => Assert.True(module.Online && module.Singleton));
            var cargo = Assert.Single(contents, item => item.TypeId == Charge);
            Assert.Equal((int)InventoryFlag.Cargo, cargo.FlagId);
            Assert.Equal(10, cargo.Quantity);
            Assert.Equal("Batch fit", scope.ServiceProvider.GetRequiredService<IPilotFleet>().Name(pilot.PilotId, ship.Id));
        }
        var hangar = fixture.Inventory.List(pilot.PilotId, pilot.DockedLocationId);
        Assert.Equal(1, Assert.Single(hangar, item => item.Id == package).Quantity);
        Assert.DoesNotContain(hangar, item => item.Id == modules);
        Assert.Equal(30, Assert.Single(hangar, item => item.Id == ammo).Quantity);
        Assert.Equal(activeItems, fixture.Inventory.List(pilot.PilotId, pilot.ShipId));
        Assert.Equal(pilot.ShipId, fixture.Pilot.ShipId);
        Assert.Equal(pilot.ShipId, context.Pilot.ShipId);
        Assert.Equal(2, context.Notifications.OfType<ShipLabelChanged>().Count());
        var changes = context.Notifications.OfType<ItemChanged>().ToArray();
        Assert.Equal(changes.Length, changes.Select(change => change.Item.Id).Distinct().Count());
        Assert.Equal(3, Assert.Single(changes, change => change.Item.Id == package).Previous[InventoryColumn.StackSize]);
        Assert.DoesNotContain(context.Notifications, notification => notification is ModuleAttributesChanged);

        await LiveWorld.Find(fixture.Db.Path)!.FlushAsync();
        using WorldDbContext reopened = new(fixture.Db.Path);
        Assert.Equal(pilot.ShipId, reopened.Pilots.Find(pilot.PilotId)!.ActiveShipId);
        Assert.Equal(1, reopened.Set<OwnedItem>().Find(package)!.Quantity);
        foreach (var ship in ships)
        {
            Assert.Equal("Batch fit", reopened.Ships.Find(ship.Id)!.Name);
            Assert.Equal(2, reopened.Set<OwnedItem>().Count(item => item.LocationId == ship.Id && item.Online));
            Assert.Equal(10, reopened.Set<OwnedItem>().Single(item => item.LocationId == ship.Id && item.TypeId == Charge).Quantity);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ExhaustingThePackageStackPreservesItsFinalIdentityAndSkipsUnconfirmedRigs(int count)
    {
        using var fixture = Create();
        long package = Grant(fixture, Hull, count);
        Grant(fixture, Miner, 2 * count);
        using var scope = fixture.Db.Services.CreateScope();
        using var context = fixture.Context();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var keywords = Request(context, count);
        var plan = Assert.IsType<BlueInstance>(keywords["fitInfo"]);
        var fields = Assert.IsType<Dictionary<string, object?>>(plan.State);
        Assert.IsType<Dictionary<long, object?>>(fields["modulesByFlag"])[(long)InventoryFlag.RigSlot0] = 31788L;
        context.Notifications.Clear();
        Assert.Null(Reply(router.Call(context, "ship", "FitShips", Array.Empty<object?>(), _ => { }, keywords)));
        Assert.Contains(Ships(fixture), ship => ship.Id == package && ship.Singleton);
        Assert.DoesNotContain(fixture.Inventory.List(context.Pilot.PilotId, context.Pilot.DockedLocationId), item => item.Id == package);
        Assert.Equal(2, fixture.Inventory.List(context.Pilot.PilotId, package).Count);
        Assert.Equal(
            0,
            Assert.Single(context.Notifications.OfType<ItemChanged>(), item => item.Item.Id == package).Previous[InventoryColumn.Singleton]
        );
    }

    [Theory]
    [InlineData(Hull)]
    [InlineData(Miner)]
    public void MissingStockReturnsNativeShortageWithoutAssemblingOrMovingAnything(int missingType)
    {
        using var fixture = Create();
        if (missingType != Hull)
        {
            Grant(fixture, Hull, 2);
        }
        Grant(fixture, Miner, missingType == Miner ? 2 : 4);
        using var scope = fixture.Db.Services.CreateScope();
        using var context = fixture.Context();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        string before = Assets(fixture);
        context.Notifications.Clear();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var result = Assert.IsType<object?[]>(Reply(Bind(router, context, Request(context, count: 2))));
            var failure = Assert.IsType<object?[]>(result[1]);
            Assert.Null(failure[1]);
            var shortages = Assert.IsType<List<object?>>(failure[0]);
            Assert.Equal(new object?[] { (long)missingType, 2L }, Assert.IsType<object?[]>(Assert.Single(shortages)));
        }
        Assert.Equal(before, Assets(fixture));
        Assert.Empty(context.Notifications);
    }

    [Fact]
    public void LateCapacityFailureRollsBackAssemblyModulesSuppliesAndNotifications()
    {
        using var fixture = Create();
        Grant(fixture, Hull, 2);
        Grant(fixture, Miner, 4);
        Grant(fixture, Charge, 1);
        Grant(fixture, Charge, 1, volume: 10000);
        using var scope = fixture.Db.Services.CreateScope();
        using var context = fixture.Context();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        string before = Assets(fixture);
        context.Notifications.Clear();
        var failure = Assert.IsType<PilotCallResult.NamedFailure>(Bind(router, context, Request(context, count: 2, charge: 1)));
        Assert.Equal("NotEnoughCargoSpace", failure.Name);
        Assert.Equal(before, Assets(fixture));
        Assert.Empty(context.Notifications);
    }

    [Fact]
    public async Task CompetingBatchesCannotSpendTheSameHullOrModuleUnits()
    {
        using var fixture = Create();
        long package = Grant(fixture, Hull, 3);
        Grant(fixture, Miner, 6);
        using var context = fixture.Context();
        var request = MultifitWire.Read([], Request(context, count: 2));
        var fitting = fixture.Db.Services.GetRequiredService<IPilotMultifit>();
        var beforeShips = Ships(fixture).Select(ship => ship.Id).ToHashSet();
        var results = await Task.WhenAll(
            Enumerable.Range(0, 2).Select(_ => Task.Run(() => fitting.FitShips(context.Pilot.PilotId, request)))
        );
        var success = Assert.Single(results, result => result.Missing.Count == 0);
        Assert.Equal(2, success.ShipIds.Count);
        Assert.Empty(Assert.Single(results, result => result.Missing.Count > 0).Changes);
        Assert.Equal(2, Ships(fixture).Count(ship => !beforeShips.Contains(ship.Id)));
        var hangar = fixture.Inventory.List(context.Pilot.PilotId, context.Pilot.DockedLocationId);
        Assert.Equal(1, Assert.Single(hangar, item => item.Id == package).Quantity);
        Assert.Equal(2, hangar.Where(item => item.TypeId == Miner).Sum(item => item.Quantity));
    }

    [Fact]
    public void InSpaceMultifitRejectsTheSavedStationLocationWithoutConsumingStock()
    {
        using var fixture = Create();
        Grant(fixture, Hull, 2);
        Grant(fixture, Miner, 4);
        using var scope = fixture.Db.Services.CreateScope();
        using var context = fixture.Context();
        var keywords = Request(context, count: 2);
        fixture.Space();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        string before = Assets(fixture);
        context.Notifications.Clear();
        Assert.IsType<PilotCallResult.Failure>(router.Call(context, "ship", "FitShips", Array.Empty<object?>(), _ => { }, keywords));
        Assert.Equal(before, Assets(fixture));
        Assert.Empty(context.Notifications);
    }

    [Theory]
    [InlineData("location")]
    [InlineData("container")]
    [InlineData("count")]
    [InlineData("keyword")]
    [InlineData("name")]
    [InlineData("slot")]
    public void InvalidOrForeignRequestsLeaveTheBatchUnchanged(string invalid)
    {
        using var fixture = Create();
        Grant(fixture, Hull, 2);
        Grant(fixture, Miner, 4);
        using var scope = fixture.Db.Services.CreateScope();
        using var context = fixture.Context();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var keywords = Request(context, count: 2);
        switch (invalid)
        {
            case "location":
                keywords["itemLocationID"] = context.Pilot.DockedLocationId + 1;
                break;
            case "container":
                keywords["itemContainerID"] = 90000002L;
                break;
            case "count":
                keywords["numToFit"] = (long)MultifitRules.MaximumShips + 1;
                break;
            case "keyword":
                keywords["pilotID"] = 90000002L;
                break;
            case "name":
                keywords["name"] = "bad\nname";
                break;
            case "slot":
                var fields = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(keywords["fitInfo"]).State);
                fields["modulesByFlag"] = new Dictionary<long, object?> { [(long)InventoryFlag.MediumSlot0] = (long)Miner };
                break;
        }
        string before = Assets(fixture);
        context.Notifications.Clear();
        Assert.IsType<PilotCallResult.Failure>(Bind(router, context, keywords));
        Assert.Equal(before, Assets(fixture));
        Assert.Empty(context.Notifications);
    }

    private static PilotCallResult Bind(PilotCallRouter router, PilotCallContext context, Dictionary<string, object?> keywords)
    {
        var bind = new object?[]
        {
            new object?[] { context.Pilot.DockedLocationId, 15L },
            new object?[] { "FitShips", Array.Empty<object?>(), keywords },
        };
        return router.Call(context, "ship", "MachoBindObject", BlueMarshal.Decode(BlueMarshal.Encode(bind)), _ => { });
    }

    private static Dictionary<string, object?> Request(PilotCallContext context, int count = 1, int charge = 0) =>
        new()
        {
            ["shipTypeID"] = (long)Hull,
            ["fitInfo"] = new BlueInstance(
                "utillib.KeyVal",
                new Dictionary<string, object?>
                {
                    ["modulesByFlag"] = new Dictionary<long, object?>
                    {
                        [(long)InventoryFlag.HighSlot0] = (long)Miner,
                        [(long)InventoryFlag.HighSlot1] = (long)Miner,
                    },
                    ["chargesByType"] = new Dictionary<long, object?>(),
                    ["iceByType"] = new Dictionary<long, object?>(),
                    ["implantsByTypeID"] = new Dictionary<long, object?>(),
                    ["dronesByType"] = new Dictionary<long, object?>(),
                    ["fightersByTypeID"] = new Dictionary<long, object?>(),
                }
            ),
            ["itemLocationID"] = context.Pilot.DockedLocationId,
            ["cargoItemsByType"] =
                charge == 0 ? new Dictionary<long, object?>() : new Dictionary<long, object?> { [Charge] = (long)charge },
            ["fitRigs"] = false,
            ["name"] = "Batch fit",
            ["numToFit"] = (long)count,
            ["itemContainerID"] = null,
        };

    private static object? Reply(PilotCallResult result)
    {
        Assert.True(result is PilotCallResult.Success, result.ToString());
        var reply = ((PilotCallResult.Success)result).Reply;
        // The inbound codec intentionally does not decode outbound bound-object references.
        return reply is BoundPilotReply bound
            ? new object?[] { bound.Reference, BlueMarshal.Decode(BlueMarshal.Encode(bound.Reply)) }
            : BlueMarshal.Decode(BlueMarshal.Encode(reply));
    }

    private static IReadOnlyList<InventoryItem> Ships(AmmunitionTests.Fixture fixture) =>
        fixture.Db.Services.GetRequiredService<IPilotFleet>().Ships(fixture.Pilot.PilotId, fixture.Pilot.DockedLocationId);

    private static string Assets(AmmunitionTests.Fixture fixture) =>
        JsonSerializer.Serialize(
            new
            {
                Ships = Ships(fixture),
                Hangar = fixture.Inventory.List(fixture.Pilot.PilotId, fixture.Pilot.DockedLocationId),
                Active = fixture.Inventory.List(fixture.Pilot.PilotId, fixture.Pilot.ShipId),
                ShipId = fixture.Pilot.ShipId,
                Wallet = fixture.Market.Balance(fixture.Pilot.PilotId),
            }
        );

    private static AmmunitionTests.Fixture Create()
    {
        AmmunitionTests.Fixture fixture = new(liveWorld: true);
        PilotFleetTests.AddVenture(fixture.Db);
        using WorldDbContext world = new(fixture.Db.Path);
        long station = fixture.Pilot.DockedLocationId;
        long pilot = fixture.Pilot.PilotId;
        world.RemoveRange(
            world.Set<OwnedItem>().Where(item => item.OwnerId == pilot && item.LocationId == station && item.TypeId == Miner)
        );
        world.SaveChanges();
        return fixture;
    }

    private static long Grant(AmmunitionTests.Fixture fixture, int type, int quantity, double? volume = null)
    {
        using WorldDbContext world = new(fixture.Db.Path);
        var item = world
            .Add(
                new OwnedItem
                {
                    OwnerId = fixture.Pilot.PilotId,
                    TypeId = type,
                    LocationId = fixture.Pilot.DockedLocationId,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = quantity,
                    VolumeOverride = volume,
                }
            )
            .Entity;
        world.SaveChanges();
        return item.Id;
    }
}
