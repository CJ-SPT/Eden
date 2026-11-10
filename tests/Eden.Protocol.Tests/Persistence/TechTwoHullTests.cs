using Eden.Data.Catalog;
using Eden.Gameplay.Content;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Defenses;
using Eden.World.Fittings;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Persistence;

public sealed class TechTwoHullTests
{
    public static IEnumerable<object[]> Hulls() =>
        BuildContentPolicy
            .Current.Hulls.Cohorts.Single(c => c.Cohort == HullCohort.TechTwoSubcapital)
            .Types.Order()
            .Select(t => new object[] { t });

    [Fact]
    public void EveryPublishedTechTwoSubcapitalHasAnExplicitOperationalOrDeferredOwner()
    {
        Assert.Equal(130, Hulls().Count());
        using Fixture f = new();
        PassiveCatalogFixture.LoadTechTwoHulls(f.CatalogPath);
        using CatalogDbContext catalog = new(f.CatalogPath);
        List<string> errors = [];
        foreach (int type in Hulls().Select(h => (int)h[0]))
        {
            try
            {
                Assert.True(HullCatalog.UsesCatalogBonuses(catalog, type), $"Hull {type}");
                Assert.DoesNotContain(HullCatalog.Capabilities(catalog, type), c => c.Capability == HullCapability.Unclassified);
                _ = HullCatalog.Modifiers(catalog, type);
            }
            catch (InvalidDataException e)
            {
                errors.Add(e.Message);
            }
        }
        Assert.True(errors.Count == 0, string.Join("\n", errors));
        var published = catalog
            .Records(CatalogData.Client.Types)
            .Where(p =>
                p.Value.Published == 1
                && p.Value.TechLevel == 2
                && catalog.FittingType(checked((int)p.Key))?.CategoryId == (int)InventoryCategory.Ship
            )
            .Select(p => checked((int)p.Key))
            .Order();
        Assert.Equal(Hulls().Select(h => (int)h[0]), published);
        Assert.True(AssaultDamageControlCatalog.ConsumersAdmitted(catalog));
        foreach (int module in new[] { 580, 20280, 33400 })
        {
            Assert.Null(FittingCatalog.Definition(catalog, module));
        }
        var entries = ShipContentAudit.Read(catalog).Entries;
        foreach (int type in Hulls().Select(h => (int)h[0]))
        {
            Assert.True(Assert.Single(entries, e => e.TypeId == type).Admitted);
        }
        Assert.False(HullSupportRules.CanAssemble(new(29984, InventoryCategory.Ship, true, 3, HullClass.StrategicCruiser)));
        Assert.False(HullSupportRules.CanAssemble(new(28844, InventoryCategory.Ship, true, 2, HullClass.JumpFreighter)));
    }

    [Fact]
    public void HarpyUsesEarnedAssaultDamageAndCaldariResistanceWithoutChangingBaseSlots()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadTechTwoHulls(f.CatalogPath);
        long ship = SmallHullTests.Board(f, 11381);
        long gun = f.Fit(561);
        var item = Assert.Single(f.Inventory.List(f.PilotId, ship), i => i.Id == gun);
        var trained = f.Fitting.ModuleAttributes(f.PilotId, item);
        f.Train(12095, 0);
        var untrained = f.Fitting.ModuleAttributes(f.PilotId, item);
        Assert.Equal(untrained[DogmaAttributeId.DamageMultiplier] * 1.25, trained[DogmaAttributeId.DamageMultiplier], 8);
        var shield = f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.ShieldEmResonance];
        f.Train(3330, 0);
        Assert.Equal(f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.ShieldEmResonance] * .8, shield, 8);
    }

    [Fact]
    public void HeliosAppliesItsCovertCloakCpuAndReactivationOverridesWithinRealResources()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadTechTwoHulls(f.CatalogPath);
        long ship = SmallHullTests.Board(f, 11172);
        long cloak = f.Fit(11370);
        var item = Assert.Single(f.Inventory.List(f.PilotId, ship), i => i.Id == cloak);
        var values = f.Fitting.ModuleAttributes(f.PilotId, item);
        Assert.Equal(5000, values[DogmaAttributeId.ModuleReactivationDelay]);
        Assert.Equal(0, values[DogmaAttributeId.Cpu], 8);
    }

    [Theory]
    [MemberData(nameof(Hulls))]
    public void HullLifecycleRetainsSkillsResourcesFittingAndFlightAcrossRestart(int type)
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadTechTwoHulls(f.CatalogPath);
        SmallHullTests.VerifyLifecycle(f, type, developerFixture: false);
    }

    [Fact]
    public void IshtarLaunchesAndRecallsTheSameOwnedCombatDrone()
    {
        using Combat.CombatDroneTests.Fixture f = new();
        PassiveCatalogFixture.LoadTechTwoHulls(f.Base.CatalogPath);
        long ship = SmallHullTests.Board(f.Base, 12005);
        long drone = Assert.Single(f.Launch(2488, 1));
        var contact = Assert.Single(f.Drones.List(30000144, f.Now));
        Assert.Equal(ship, contact.ShipId);
        Assert.Equal(5, f.Drones.Control(f.Pilot).UsedBandwidth);
        Assert.True(contact.Profile.Damage.Total > 0);
        f.Drones.Order(f.Pilot, [drone], DroneOrder.ReturnToBay, null, new HashSet<long>(), f.Now);
        Assert.Equal(drone, Assert.Single(f.Tick().Changes).Item.Id);
        Assert.Empty(f.Drones.List(30000144, f.Now));
    }
}
