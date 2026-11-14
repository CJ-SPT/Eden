using System.Text.Json.Nodes;
using Eden.Data.World.Mining;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Ships;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Configuration;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class MiningHoldConfigurationTests
{
    [Theory]
    [InlineData(false, 17478, HullClass.MiningBarge)]
    [InlineData(true, 17478, HullClass.MiningBarge)]
    [InlineData(false, 2863, HullClass.Hauler)]
    [InlineData(true, 2863, HullClass.Hauler)]
    public void ConfiguredCapacityMatchesNativeDogmaAndInventoryAfterRestart(bool resident, int hull, HullClass hullClass)
    {
        using var f = HullHoldTests.Prepare(hull, hullClass);
        WorldDatabase database = new(f.Db.Path);
        WorldConfigurationStore config = new(database, f.Clock);
        var initial = config.Read();
        var legacy = JsonNode.Parse(config.Export(initial.Active.Id))!;
        legacy["settings"]!.AsObject().Remove("miningHolds");
        var imported = config.Import(initial.Generation, legacy.ToJsonString(), "legacy");
        Assert.Equal(new MiningHoldPolicy(), imported.Pending!.Draft.Settings.MiningHolds);
        Assert.Throws<InvalidDataException>(() =>
            config.Validate(initial.Active.Draft with { Settings = new() { MiningHolds = new(1, 0, 101) } })
        );

        const double VeldsparVolume = 0.1;
        const decimal BargeMultiplier = 0.1m;
        bool barge = hullClass == HullClass.MiningBarge;
        long? laser = barge ? f.Fit(17482) : null;
        long shipId = f.ShipId;
        var baseline = f.Fitting.ShipAttributes(f.PilotId);
        (InventoryFlag Flag, double Factor)[] holds = barge
            ? [(InventoryFlag.GeneralMiningHold, (double)BargeMultiplier)]
            : [(InventoryFlag.IceHold, 2), (InventoryFlag.GasHold, 3)];
        var pending = config.Save(
            imported.Generation,
            initial.Active.Draft with
            {
                Settings = initial.Active.Draft.Settings with { MiningHolds = new(BargeMultiplier, 2, 3) },
            },
            "admin"
        );
        Assert.Equal(new MiningHoldPolicy(), database.Settings.MiningHolds);
        foreach (var (flag, _) in holds)
        {
            Assert.Equal(
                ShipHoldRules.Capacity((int)flag, baseline),
                ShipHoldRules.Capacity((int)flag, f.Fitting.ShipAttributes(f.PilotId))
            );
        }

        // Prepare the near-full hold and target on disk, then open the configured world.
        long asteroid = AsteroidDefinitions.FirstAsteroidId;
        if (barge)
        {
            double capacity = ShipHoldRules.Capacity((int)InventoryFlag.GeneralMiningHold, baseline);
            Assert.Equal(34375, capacity);
            int oreCount = (int)Math.Floor(capacity * (double)BargeMultiplier / VeldsparVolume) - 1;
            long ore = f.Grant(1230, oreCount);
            f.Inventory.Move(f.PilotId, [ore], f.StationId, shipId, (int)InventoryFlag.GeneralMiningHold, null);
            using WorldDbContext world = new(f.Db.Path);
            world.Add(
                new WorldAsteroid
                {
                    Id = asteroid,
                    BeltId = 40009087,
                    SystemId = 30000142,
                    TypeId = 1230,
                    Quantity = 100000,
                    X = 1500,
                    Radius = 100,
                }
            );
            world.SaveChanges();
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        var hullAttributes = PilotFittingStore.ReadAttributes(catalog, hull);
        var fromHangar = holds
            .Select(hold =>
            {
                int type = HullHoldTests.Probe(hold.Flag);
                double volume = PilotInventoryStore.Volume(catalog, type);
                int quantity = barge ? 3 : (int)Math.Floor(ShipHoldRules.Capacity((int)hold.Flag, baseline) / volume) + 1;
                return f.Grant(type, quantity);
            })
            .ToArray();
        WorldConfigurationStore.Activate(database);
        Assert.Equal(pending.Pending!.Id, config.Read().Active.Id);
        Assert.Equal(new MiningHoldPolicy(BargeMultiplier, 2, 3), database.Settings.MiningHolds);

        using var live = resident ? new LiveWorld(database, f.Clock, combatObjects: false, simulationObjects: false) : null;
        live?.Economy.Warm();
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            selected => new(f.Db.Store, selected, f.Clock, new(), f.Db.Objects),
            10,
            SignedSettings.Empty
        );
        context.Select(pilot);
        var all = Fields(router.CallWire(context, "dogmaIM", "GetAllInfo", Array.Empty<object?>(), _ => { }));
        var ship = Fields(Assert.IsType<Dictionary<long, object?>>(all["shipInfo"])[shipId]);
        var shipAttributes = Assert.IsType<Dictionary<long, object?>>(ship["attributes"]);
        var itemInfo = Fields(router.CallWire(context, "dogmaIM", "ItemGetInfo", new object?[] { shipId }, _ => { }));
        var itemAttributes = Assert.IsType<Dictionary<long, object?>>(itemInfo["attributes"]);
        var brain = Assert.IsType<object?[]>(Assert.IsType<object?[]>(all["charInfo"])[1]);
        var itemEffects = Assert
            .IsType<List<object?>>(brain[2])
            .Select(effect => Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueNewObject>(effect).State))
            .Where(effect => Equals(effect["modifierType"], "M"))
            .ToArray();
        for (int i = 0; i < holds.Length; i++)
        {
            (var flag, double factor) = holds[i];
            var attribute = ShipHoldRules.Definition((int)flag)!.CapacityAttribute;
            double expected = baseline[attribute] * factor;
            var literal = Assert.Single(
                itemEffects,
                effect =>
                    Convert.ToInt32(effect["toAttribID"]) == (int)attribute
                    && Convert.ToInt32(effect["operation"]) == (int)DogmaOperation.PostMultiply
            );
            Assert.Equal(shipId, literal["toItemID"]);
            Assert.Empty(Assert.IsType<List<object?>>(literal["skills"]));
            // The client first evaluates existing skill and hull effects in its catalog graph.
            Assert.True(hullAttributes.ContainsKey(attribute));
            Assert.Equal(expected, baseline[attribute] * Assert.IsType<double>(literal["value"]), 8);
            Assert.Equal(expected, Assert.IsType<double>(shipAttributes[(long)attribute]), 8);
            Assert.Equal(expected, Assert.IsType<double>(itemAttributes[(long)attribute]), 8);
            Assert.Equal(expected, f.Fitting.ShipAttributes(f.PilotId)[attribute], 8);

            if (barge)
            {
                long id = fromHangar[i];
                Assert.Throws<CargoCapacityException>(() => f.Inventory.Move(f.PilotId, [id], f.StationId, shipId, (int)flag, null));
                Assert.Contains(f.Inventory.List(f.PilotId, f.StationId), item => item.Id == id && item.Quantity == 3);
            }
            else
            {
                f.Inventory.Move(f.PilotId, [fromHangar[i]], f.StationId, shipId, (int)flag, null);
                Assert.Contains(f.Inventory.List(f.PilotId, shipId), item => item.Id == fromHangar[i] && item.FlagId == (int)flag);
            }
        }
        Assert.Equal(baseline[DogmaAttributeId.CargoCapacity], shipAttributes[(long)DogmaAttributeId.CargoCapacity]);
        foreach (
            var (attribute, _) in WorldTuningRules
                .MiningHoldMultipliers(database.Settings.MiningHolds)
                .Where(hold => !baseline.ContainsKey(hold.Key))
        )
        {
            Assert.False(shipAttributes.ContainsKey((long)attribute));
        }
        if (laser is long module)
        {
            f.Db.Store.SetPosition(shipId, SpacePosition.Zero, 30000142);
            var mining = f.Db.Services.GetRequiredService<IPilotMining>();
            var cycle = mining.Begin(f.PilotId, module, asteroid, f.Clock.Now);
            f.Clock.Milliseconds += (long)Math.Ceiling(cycle.Laser.DurationMilliseconds);
            var mined = mining.Collect(f.PilotId, module, asteroid, new(1, true, new(.99, .99, .99, .99, .99)));
            Assert.Equal(1, mined.Extracted);
            Assert.Equal((int)InventoryFlag.GeneralMiningHold, mined.Cargo!.Item.FlagId);
            Assert.Throws<CargoCapacityException>(() => mining.Begin(f.PilotId, module, asteroid, f.Clock.Now));
        }
    }

    private static Dictionary<string, object?> Fields(object? result) =>
        Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(result).State);
}
