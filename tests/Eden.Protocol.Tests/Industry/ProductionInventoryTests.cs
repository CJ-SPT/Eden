using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Insurance;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Industry.StarterProductionTests.Fixture;

namespace Eden.Protocol.Tests.Industry;

public sealed class ProductionInventoryTests
{
    private const long Owner = StarterProductionTests.Producer;
    private const long Other = StarterProductionTests.Consumer;
    private const int Station = StarterProductionTests.Home;

    private static long Item(Fixture f, int type, int quantity = 1, bool singleton = false, long owner = Owner)
    {
        using WorldDbContext world = new(f.Db.Path);
        long id = InventoryPersistence.NextId(world);
        world.Add(
            new OwnedItem
            {
                Id = id,
                TypeId = type,
                OwnerId = owner,
                LocationId = Station,
                FlagId = (int)InventoryFlag.Hangar,
                Quantity = quantity,
                Singleton = singleton,
            }
        );
        world.SaveChanges();
        return id;
    }

    [Fact]
    public void StackAllConservesQuantitiesAndOwnershipAcrossReopenAndRejectsOverflowAtomically()
    {
        using Fixture f = new();
        long retained = Item(f, 1230, 40);
        Item(f, 1230, 60);
        long foreign = Item(f, 1230, 70, owner: Other);
        var stacks = f.Db.Services.GetRequiredService<IPilotInventoryStacks>();
        var changes = stacks.StackAll(Owner, Station, (int)InventoryFlag.Hangar);
        Assert.Equal(2, changes.Count);
        Assert.Equal(retained, f.Stack(Owner, 1230).Id);
        Assert.Equal(100, f.Stack(Owner, 1230).Quantity);
        Assert.Equal(foreign, f.Stack(Other, 1230).Id);
        Assert.Empty(stacks.StackAll(Owner, Station, (int)InventoryFlag.Hangar));
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.Equal(100, f.Stack(Owner, 1230).Quantity);
        Assert.Throws<InvalidDataException>(() => stacks.StackAll(Other, f.Pilot().ShipId, (int)InventoryFlag.Cargo));
        Item(f, 34, int.MaxValue);
        Item(f, 34);
        Item(f, 1230, 5);
        Assert.Throws<OverflowException>(() => stacks.StackAll(Owner, Station, (int)InventoryFlag.Hangar));
        Assert.Equal(105, f.Quantity(Owner, 1230));
        Assert.Equal(2, f.Inventory.List(Owner, Station).Count(i => i.TypeId == 1230));
        Assert.Equal((long)int.MaxValue + 1, f.Quantity(Owner, 34));
    }

    [Fact]
    public void RepackagingReturnsSellableHullAndModuleAndVoidsInsuranceWithoutRefund()
    {
        using Fixture f = new();
        long id = Item(f, 603);
        long ship = Assert.Single(f.Fleet.Assemble(Owner, [id], "Resale Merlin").ShipIds);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            string services = System.Text.Json.JsonSerializer.Serialize(
                new { services = new[] { (int)StationService.Insurance, 5, 7, 14, 17 } }
            );
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ({"client/stationoperations"},{"14"},{services})");
        }
        var insurance = f.Db.Services.GetRequiredService<IPilotInsurance>();
        long price = insurance.Price(603);
        insurance.Insure(Owner, Station, ship, InsuranceRules.Premium(price, InsuranceLevel.Platinum), false);
        long balance = f.Market.Balance(Owner);
        long module = Item(f, 1317, singleton: true);
        var repackaging = f.Db.Services.GetRequiredService<IPilotRepackaging>();
        var changes = repackaging.Repackage(Owner, Station, [new(ship, Station), new(module, Station)]);
        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.False(c.Item.Singleton));
        Assert.Null(f.Fleet.Find(Owner, ship));
        Assert.Null(insurance.Contract(Owner, ship));
        Assert.Equal(balance, f.Market.Balance(Owner));
        Assert.Empty(repackaging.Repackage(Owner, Station, [new(ship, Station), new(module, Station)]));
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.False(f.Stack(Owner, 603).Singleton);
        f.Personal.PlaceSell(Owner, [new(ship, Station, 603, 100_000_000, 1)], 1);
        f.Market.Buy(Other, Station, [new(603, 100_000_000, 1)]);
        Assert.Equal(1, f.Stack(Other, 603).Quantity);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("damage")]
    [InlineData("contents")]
    [InlineData("owner")]
    [InlineData("location")]
    public void InvalidRepackagingRollsBackTheWholeBatch(string failure)
    {
        using Fixture f = new();
        long module = Item(f, 1317, singleton: true);
        long ship = Assert.Single(f.Fleet.Assemble(Owner, [Item(f, 603)], null).ShipIds);
        long actor = Owner;
        long location = Station;
        using (WorldDbContext world = new(f.Db.Path))
        {
            if (failure == "damage")
            {
                world.Add(new ShipHealthState { ShipId = ship, ArmorDamage = 1 });
            }
            if (failure == "contents")
            {
                world.Add(
                    new OwnedItem
                    {
                        Id = InventoryPersistence.NextId(world),
                        TypeId = 34,
                        OwnerId = Owner,
                        LocationId = ship,
                        FlagId = (int)InventoryFlag.Cargo,
                        Quantity = 1,
                    }
                );
            }
            world.SaveChanges();
        }
        if (failure == "active")
        {
            f.Fleet.Board(Owner, ship, f.Pilot().ShipId);
        }
        if (failure == "owner")
        {
            actor = Other;
        }
        if (failure == "location")
        {
            location++;
        }
        var repackaging = f.Db.Services.GetRequiredService<IPilotRepackaging>();
        Assert.Throws<InvalidDataException>(() => repackaging.Repackage(actor, Station, [new(module, Station), new(ship, location)]));
        Assert.True(f.Inventory.List(Owner, Station).Single(i => i.Id == module).Singleton);
        Assert.NotNull(f.Fleet.Find(Owner, ship));
    }

    [Fact]
    public void NativeStackAndRepackageFormsValidateTheirWireShapeAndPublishChanges()
    {
        using Fixture f = new();
        using var scope = f.Db.Services.CreateScope();
        var pilot = f.Pilot();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, f.Clock, new SharedSpace(), f.Db.Objects),
            1,
            SignedSettings.Empty
        );
        context.Select(pilot);
        Item(f, 1230, 40);
        Item(f, 1230, 60);
        var inventory = scope.ServiceProvider.GetRequiredService<InventoryHandler>();
        Assert.Same(PilotCallResult.None, inventory.Call(context, "@hangar", "StackAll", [], new() { ["flag"] = 4L }));
        Assert.Equal(2, context.Notifications.Count(n => n.Name == "OnItemChange"));
        long module = Item(f, 1317, singleton: true);
        var handler = scope.ServiceProvider.GetRequiredService<RepackagingHandler>();
        var request = new Dictionary<long, object?> { [Station] = new List<object?> { new object?[] { module, (long)Station } } };
        var decoded = Assert.IsType<Dictionary<long, object?>>(BlueMarshal.Decode(BlueMarshal.Encode(request)));
        Assert.Same(PilotCallResult.None, handler.Call(context, "repackagingSvc", "RepackageItems", [decoded]));
        Assert.False(f.Stack(Owner, 1317).Singleton);
        Assert.IsType<PilotCallResult.Failure>(
            handler.Call(context, "repackagingSvc", "RepackageItems", [decoded], new() { ["destroyable"] = true })
        );
        Assert.IsType<PilotCallResult.Failure>(inventory.Call(context, "@hangar", "StackAll", [4L], new() { ["flag"] = 4L }));
    }
}
