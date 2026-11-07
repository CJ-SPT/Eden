using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Mining;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Ships;
using Eden.Protocol.Mining;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Mining;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Mining;

public sealed class MiningTierTests
{
    private const int OreMiner = 28750;
    private const int GallenteMiner = 21841;
    private const int Veldspar = 1230;
    private const long Asteroid = AsteroidDefinitions.FirstAsteroidId;
    private static readonly MiningRolls QuietRolls = new(.5, .99, .5, .99, .5);

    private static IPilotMining Store(Fixture f) => f.Db.Services.GetRequiredService<IPilotMining>();

    private static Fixture WithTiers(params int[] types)
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadActiveTiers(f.CatalogPath, types.Concat(Enum.GetValues<MiningSkill>().Select(s => (int)s)).ToHashSet());
        using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO ItemCategory VALUES (25,'Asteroid');
            INSERT OR IGNORE INTO ItemGroup VALUES (462,'Veldspar',25);
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (1230,'Veldspar',462);
            INSERT OR REPLACE INTO SourceRecord VALUES ('client/types','1230','{{"published":1,"volume":0.1}}');
            """
        );
        using WorldDbContext world = new(f.Db.Path);
        world
            .Set<WorldAsteroid>()
            .Add(
                new()
                {
                    Id = Asteroid,
                    BeltId = 40009087,
                    SystemId = f.Db.Store.FindPilot(1000001)!.SystemId,
                    TypeId = Veldspar,
                    Quantity = 10000,
                    X = 1500,
                    Radius = 100,
                }
            );
        world.SaveChanges();
        return f;
    }

    [Fact]
    public void AllTenOrdinaryOreProfilesFitWhileDeepCoreIceAndMutatedProfilesRemainExplicitlyPending()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadActiveTiers(f.CatalogPath);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var types = catalog.Set<ItemType>().Where(t => t.GroupId == InventoryDefinitions.MiningLaserGroup).ToArray();
        var supported = types.Where(t => MiningLaserCatalog.Supported(catalog, t.Id, t.GroupId)).ToArray();
        Assert.Equal(18, types.Length);
        Assert.Equal(10, supported.Length);
        Assert.All(
            supported,
            t =>
            {
                var definition = FittingCatalog.Definition(catalog, t.Id)!;
                Assert.Equal(ModuleRack.High, definition.Rack);
                Assert.True(definition.Turret);
            }
        );
        Assert.All(types.Except(supported), t => Assert.Null(FittingCatalog.Definition(catalog, t.Id)));
        var tiers = supported
            .Select(t => catalog.Set<SourceRecord>().Find("client/types", PassiveCatalog.Key(t.Id))!.Read<ClientTypes>().MetaGroupID)
            .ToHashSet();
        Assert.All(new long?[] { null, 1, 2, 3, 4 }, tier => Assert.Contains(tier, tiers));
    }

    [Theory]
    [InlineData(482, 12000, 15000, 15, 18, 207)]
    [InlineData(483, 10000, 15000, 10, 5, 138)]
    [InlineData(3651, 10000, 10000, 6, 5, 83)]
    [InlineData(5233, 11000, 15000, 7, 2, 97)]
    [InlineData(5239, 16000, 15000, 13, 5, 179)]
    [InlineData(5245, 11000, 15000, 13, 5, 179)]
    [InlineData(GallenteMiner, 10000, 15000, 12, 2, 166)]
    [InlineData(22921, 12000, 15000, 14, 5, 193)]
    [InlineData(22923, 12400, 15000, 15, 10, 207)]
    [InlineData(OreMiner, 16000, 15000, 21, 18, 290)]
    public void EveryTierPaysItsOwnCostAndUsesCatalogRangeCycleAndSkilledYield(
        int type,
        double range,
        double duration,
        double volume,
        double cost,
        int expectedOre
    )
    {
        using Fixture f = WithTiers(type);
        long module = f.Fit(type);
        f.Train((int)MiningSkill.Mining, 4);
        f.Train((int)MiningSkill.Astrogeology, 3);
        f.Train((int)MiningSkill.Precision, 2);
        f.Train((int)MiningSkill.Exploitation, 3);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        var start = Store(f).Begin(f.PilotId, module, Asteroid, f.Clock.Now);
        Assert.Equal(range, start.Laser.Range);
        Assert.Equal(duration, start.Laser.DurationMilliseconds);
        Assert.Equal(volume * 1.2 * 1.15, start.Laser.Volume, 8);
        Assert.Equal(.012, start.Laser.CriticalChance, 8);
        Assert.Equal(2.3, start.Laser.CriticalBonusYield, 8);
        Assert.Equal(130 - cost, start.Capacitor, 8);
        var result = Store(f).Collect(f.PilotId, module, Asteroid, new(1, true, QuietRolls));
        Assert.Equal(expectedOre, result.Extracted);
        Assert.Equal(0, result.CriticalBonus);
        Assert.Equal(0, result.Residue);
        WorldInitialization.Open(f.Db.Path);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(10000 - expectedOre, saved.Set<WorldAsteroid>().Single().Quantity);
        Assert.Equal(expectedOre, Assert.Single(f.Inventory.List(f.PilotId, f.ShipId), i => i.TypeId == Veldspar).Quantity);
    }

    [Theory]
    [InlineData(482, .339999, 188)]
    [InlineData(482, .34, 0)]
    [InlineData(GallenteMiner, .199999, 150)]
    [InlineData(GallenteMiner, .20, 0)]
    [InlineData(OreMiner, 0, 0)]
    public void CatalogResidueThresholdsRemoveOreOnlyFromTheSharedDeposit(int type, double roll, int residue)
    {
        using Fixture f = WithTiers(type);
        long module = f.Fit(type);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        var start = Store(f).Begin(f.PilotId, module, Asteroid, f.Clock.Now);
        var result = Store(f).Collect(f.PilotId, module, Asteroid, new(1, true, QuietRolls with { Yield = .25, Residue = roll }));
        Assert.Equal(residue, result.Residue);
        Assert.Equal(
            type == 482 ? 188
                : type == GallenteMiner ? 150
                : 263,
            result.Extracted
        );
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(10000L - result.Extracted - residue, saved.Set<WorldAsteroid>().Single().Quantity);
        Assert.Equal(result.Extracted, result.Cargo!.Item.Quantity);
        var wire = Assert.IsType<Dictionary<string, object?>>(new OreMined(result, Asteroid, module).Arguments[0]);
        Assert.Equal(residue, wire["amountWasted"]);
        Assert.Equal(result.Extracted, wire["quantity_removed"]);
        Assert.Equal(result.Extracted, wire["quantity_added"]);
        Assert.NotEmpty(BlueMarshal.Encode(wire));
    }

    [Fact]
    public void ResidueOnlyDepletionPersistsWithoutCreatingCargoAndCannotBeCollectedAgain()
    {
        using Fixture f = WithTiers(482);
        long module = f.Fit(482);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldAsteroid>().Where(a => a.Id == Asteroid).ExecuteUpdate(set => set.SetProperty(a => a.Quantity, 7));
        }
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        Store(f).Begin(f.PilotId, module, Asteroid, f.Clock.Now);
        var result = Store(f).Collect(f.PilotId, module, Asteroid, new(1, true, QuietRolls with { Residue = 0 }));
        Assert.Equal(7, result.Residue);
        Assert.Equal(0, result.Extracted);
        Assert.Null(result.Cargo);
        Assert.True(result.Depleted);
        WorldInitialization.Open(f.Db.Path);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(0, saved.Set<WorldAsteroid>().Single().Quantity);
        Assert.DoesNotContain(f.Inventory.List(f.PilotId, f.ShipId), i => i.TypeId == Veldspar);
        Assert.Throws<InvalidDataException>(() => Store(f).Collect(f.PilotId, module, Asteroid, new(1, true, QuietRolls)));
    }

    [Fact]
    public void DestroyedShipsCannotStartOrFinishMining()
    {
        using Fixture f = WithTiers(OreMiner);
        long module = f.Fit(OreMiner);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        Store(f).Begin(f.PilotId, module, Asteroid, f.Clock.Now);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var health =
                world.Set<ShipHealthState>().Find(f.ShipId) ?? world.Set<ShipHealthState>().Add(new() { ShipId = f.ShipId }).Entity;
            health.StructureDamage = f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.StructureHitPoints];
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => Store(f).Begin(f.PilotId, module, Asteroid, f.Clock.Now));
        Assert.Throws<InvalidDataException>(() => Store(f).Collect(f.PilotId, module, Asteroid, new(1, true, QuietRolls)));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(10000, saved.Set<WorldAsteroid>().Single().Quantity);
    }

    [Theory]
    [InlineData(465)] // Ice.
    [InlineData(468)] // Mercoxit needs deep-core tools and clouds.
    [InlineData(711)] // Gas.
    [InlineData(4714)] // Non-interactable asteroid.
    public void WrongResourcesCannotSpendCapacitorOrProduceOre(int group)
    {
        using Fixture f = WithTiers(OreMiner);
        long module = f.Fit(OreMiner);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO ItemGroup VALUES ({group},{group.ToString()},25)");
            catalog.Set<ItemType>().Where(t => t.Id == Veldspar).ExecuteUpdate(set => set.SetProperty(t => t.GroupId, group));
        }
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        Assert.Throws<InvalidDataException>(() => Store(f).Begin(f.PilotId, module, Asteroid, f.Clock.Now));
        Assert.Throws<InvalidDataException>(() => Store(f).Collect(f.PilotId, module, Asteroid, new(1, true, QuietRolls)));
        Assert.Equal(130, f.Db.Services.GetRequiredService<IPilotShipResources>().Capacitor(f.PilotId, f.Clock.Now), 8);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(10000, saved.Set<WorldAsteroid>().Single().Quantity);
    }

    [Fact]
    public void OrdinaryLasersCanActivateOnAnUnrestrictedHullBeyondTheOldCorvetteVentureWhitelist()
    {
        using Fixture f = WithTiers(OreMiner);
        long module = f.Fit(OreMiner);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            const int FrigateGroup = 25;
            catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO ItemGroup VALUES ({FrigateGroup},{"Frigate"},6)");
            catalog.Set<ItemType>().Where(t => t.Id == 601).ExecuteUpdate(set => set.SetProperty(t => t.GroupId, FrigateGroup));
        }
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        Assert.Equal(module, Store(f).Begin(f.PilotId, module, Asteroid, f.Clock.Now).Laser.ItemId);
    }

    [Theory]
    [InlineData("mutation")]
    [InlineData("restriction")]
    [InlineData("cloud")]
    [InlineData("effect")]
    public void UnsupportedStoredProfilesAreRejectedAgainAtActivation(string reason)
    {
        using Fixture f = WithTiers(OreMiner);
        long module = f.Fit(OreMiner);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            if (reason == "mutation")
            {
                catalog.Database.ExecuteSqlRaw(
                    "UPDATE SourceRecord SET Data=json_set(Data,'$.metaGroupID',15) WHERE Dataset='client/types' AND Key='28750'"
                );
            }
            else if (reason == "effect")
            {
                string extra = """{"effectID":4}""";
                catalog.Database.ExecuteSql(
                    $"""
                    UPDATE SourceRecord SET Data=json_insert(Data,'$.dogmaEffects[#]',json({extra}))
                    WHERE Dataset='client/typedogma' AND Key='28750'
                    """
                );
            }
            else
            {
                var attribute = reason == "cloud" ? DogmaAttributeId.MiningDamageCloudChance : DogmaAttributeId.CanFitShipGroup1;
                catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO DogmaAttribute VALUES ({(int)attribute},{attribute.ToString()})");
                catalog.Database.ExecuteSql($"INSERT INTO TypeAttribute VALUES ({OreMiner},{(int)attribute},5)");
            }
            Assert.Null(FittingCatalog.Definition(catalog, OreMiner));
        }
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        Assert.Throws<InvalidDataException>(() => Store(f).Begin(f.PilotId, module, Asteroid, f.Clock.Now));
        Assert.Throws<InvalidDataException>(() => Store(f).Collect(f.PilotId, module, Asteroid, new(1, true, QuietRolls)));
        Assert.Equal(130, f.Db.Services.GetRequiredService<IPilotShipResources>().Capacitor(f.PilotId, f.Clock.Now), 8);
    }
}
