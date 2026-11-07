using Eden.Data.World.Mining;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Ships;
using Eden.Protocol.Skills;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Mining;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Persistence;

public sealed class UtilityHullOperationTests
{
    [Theory]
    [InlineData(17476, 17482, 269.53125, 33750, 17.25, 19500, 9000)]
    [InlineData(17476, 28754, 359.375, 33750, 17.25, 24375, 9000)]
    [InlineData(17478, 17482, 296.484375, 45000, 23, 15000, 34375)]
    [InlineData(17478, 28754, 395.3125, 45000, 23, 18750, 34375)]
    [InlineData(17480, 17482, 257.8125, 45000, 23, 15000, 16000)]
    [InlineData(17480, 28754, 343.75, 45000, 23, 18750, 16000)]
    public void StripMinersFitTheActualBargeAndPayItsEvaluatedCycleYieldCostAndHold(
        int type,
        int miner,
        double yield,
        double duration,
        double cost,
        double range,
        double capacity
    )
    {
        using Fixture f = HullHoldTests.Prepare(type, HullClass.MiningBarge);
        long module = f.Fit(miner);
        f.Fit(miner);
        Assert.Throws<InvalidDataException>(() => f.Fit(miner));
        var item = Assert.Single(f.Inventory.List(f.PilotId, f.ShipId), item => item.Id == module);
        var mining = f.Db.Services.GetRequiredService<IPilotMining>();
        var values = mining.ModuleAttributes(f.PilotId, item);
        Assert.Equal(yield, values[DogmaAttributeId.MiningAmount], 8);
        Assert.Equal(duration, values[DogmaAttributeId.Duration], 8);
        Assert.Equal(cost, values[DogmaAttributeId.CapacitorNeed], 8);
        Assert.Equal(range, values[DogmaAttributeId.MaxRange], 8);
        VerifyNativeFittingBrain(f, module, miner, yield, range);
        long asteroid = AsteroidDefinitions.FirstAsteroidId;
        using (WorldDbContext world = new(f.Db.Path))
        {
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
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero, 30000142);
        var start = mining.Begin(f.PilotId, module, asteroid, f.Clock.Now);
        Assert.Equal(cost, start.Laser.CapacitorNeed);
        Assert.Equal(duration, start.Laser.DurationMilliseconds);
        Assert.Equal(yield, start.Laser.Volume, 8);
        var result = mining.Collect(f.PilotId, module, asteroid, new(1, true, new(.99, .99, .99, .99, .99)));
        Assert.Equal((int)Math.Floor(yield / .1), result.Extracted);
        Assert.Equal((int)InventoryFlag.GeneralMiningHold, result.Cargo!.Item.FlagId);
        using WorldDbContext saved = new(f.Db.Path);
        using CatalogDbContext catalog = new(f.CatalogPath);
        Assert.Equal(capacity, PilotInventoryStore.HoldCapacity(saved, catalog, f.PilotId, f.ShipId, (int)InventoryFlag.GeneralMiningHold));
        Assert.Equal(100000 - result.Extracted - result.Residue, saved.Set<WorldAsteroid>().Find(asteroid)!.Quantity);
    }

    private static void VerifyNativeFittingBrain(Fixture f, long module, int miner, double yield, double range)
    {
        using CatalogDbContext catalog = new(f.CatalogPath);
        var hull = PilotFittingStore.ReadAttributes(catalog, f.Db.Store.FindPilot(1000001)!.ShipTypeId);
        var equipment = PilotFittingStore.ReadAttributes(catalog, miner);
        var brain = f.Db.Services.GetRequiredService<PilotBrain>().Read(f.PilotId, f.ShipId);
        List<DogmaModifier> modifiers = [];
        // Reconstruct the native fitting location from catalog bases and the
        // transmitted literals, independently of effective ItemGetInfo values.
        foreach (var effect in brain.ShipEffects)
        {
            if (effect.Modifier == BrainModifier.Item && hull.ContainsKey(effect.Attribute))
            {
                modifiers.Add(
                    new(f.ShipId, effect.Attribute, (DogmaOperation)effect.Operation, new DogmaValue.Constant(effect.Value), true)
                );
            }
            else if (effect.Modifier == BrainModifier.LocationRequiredSkill && effect.Extras.Contains((int)MiningSkill.Mining))
            {
                modifiers.Add(new(module, effect.Attribute, (DogmaOperation)effect.Operation, new DogmaValue.Constant(effect.Value), true));
            }
        }
        foreach (var effect in HullCatalog.Modifiers(catalog, f.Db.Store.FindPilot(1000001)!.ShipTypeId))
        {
            if (
                effect.Selector == ModifierSelector.Group && effect.SelectionId == InventoryDefinitions.StripMinerGroup
                || effect.Selector == ModifierSelector.RequiredSkill
                    && InventoryDefinitions.RequiredSkills.Any(pair => equipment.GetValueOrDefault(pair.Skill) == effect.SelectionId)
            )
            {
                modifiers.Add(
                    new(module, effect.Attribute, effect.Operation, new DogmaValue.Attribute(f.ShipId, effect.SourceAttribute), true)
                );
            }
        }
        DogmaGraph native = new(
            [new(f.ShipId, hull), new(module, equipment)],
            modifiers,
            new Dictionary<DogmaAttributeId, DogmaAttributeDefinition>()
        );
        Assert.Equal(yield, native.Read(module, DogmaAttributeId.MiningAmount), 8);
        Assert.Equal(range, native.Read(module, DogmaAttributeId.MaxRange), 8);
    }

    [Fact]
    public void StripMinersRespectTheCatalogShipRestrictionEvenWithEnoughCorvetteResources()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadUtilityHulls(f.CatalogPath);
        SmallHullTests.TrainCatalog(f);
        long miner = f.Grant(17482);
        Assert.Throws<InvalidDataException>(() => f.Fitting.Fit(f.PilotId, [miner], f.StationId, (int)InventoryFlag.AutoFit));
        Assert.Equal(f.StationId, Assert.Single(f.Inventory.List(f.PilotId, f.StationId), item => item.Id == miner).LocationId);
    }

    [Theory]
    [InlineData(16278)]
    [InlineData(17912)]
    [InlineData(87562)]
    [InlineData(90493)]
    public void IceCrystalsDeepCoreAndMutatedProfilesRemainUnavailableInsteadOfMiningTheWrongResource(int type)
    {
        using Fixture f = HullHoldTests.Prepare(17478, HullClass.MiningBarge);
        using CatalogDbContext catalog = new(f.CatalogPath);
        Assert.Null(FittingCatalog.Definition(catalog, type));
    }
}
