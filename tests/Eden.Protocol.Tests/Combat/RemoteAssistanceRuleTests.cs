using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Storage;

namespace Eden.Protocol.Tests.Combat;

public sealed class RemoteAssistanceRuleTests
{
    private const long Recipient = 1;
    private const long Turret = 2;
    private const long UnrelatedModule = 3;

    private static RemoteAssistanceModule Module(CatalogDbContext catalog, int type) =>
        RemoteAssistanceRules.Define(
            10,
            type,
            RemoteAssistanceRules.Effect(catalog.FittingType(type)!.GroupId)!.Value,
            Values(catalog, type)
        );

    private static Dictionary<DogmaAttributeId, double> Values(CatalogDbContext catalog, int type) =>
        catalog.FittingAttributes(type).ToDictionary();

    private static TestWorld Prepare()
    {
        TestWorld world = new();
        PassiveCatalogFixture.LoadRemoteAssistance(new WorldDatabase(world.Path).CatalogPath);
        return world;
    }

    [Theory]
    [InlineData(2103, 20, 40000, 6, 12)]
    [InlineData(2104, 24, 48000, 7.5, 15)]
    [InlineData(5339, 20, 44000, 6.75, 13.5)]
    [InlineData(5340, 20, 40000, 6.75, 13.5)]
    [InlineData(5342, 16, 40000, 6.75, 13.5)]
    [InlineData(14240, 20, 56000, 8, 17.5)]
    [InlineData(15967, 20, 56000, 8, 17.5)]
    [InlineData(22937, 14, 48000, 7.5, 15)]
    public void PinnedTrackingTiersRetainTheirOwnCostRangeAndStrength(int type, double cost, double range, double optimal, double tracking)
    {
        using TestWorld world = Prepare();
        using CatalogDbContext catalog = new(new WorldDatabase(world.Path).CatalogPath);
        var module = Module(catalog, type);
        Assert.Equal(DogmaEffect.RemoteTrackingComputer, module.Cycle.Effect);
        Assert.Equal(cost, module.Cycle.CapacitorNeed);
        Assert.Equal(12000, module.Cycle.DurationMilliseconds);
        Assert.Equal(range, module.Optimal);
        Assert.Equal(range / 2, module.Falloff);
        Assert.Equal(optimal, module.Bonuses.Optimal);
        Assert.Equal(tracking, module.Bonuses.Tracking);
    }

    [Theory]
    [InlineData(1963, 20, 25000, 33, 28)]
    [InlineData(1964, 24, 30000, 40, 33)]
    [InlineData(5279, 20, 25000, 36, 30)]
    [InlineData(5281, 20, 27500, 36, 30)]
    [InlineData(5282, 16, 25000, 36, 30)]
    [InlineData(22925, 14, 30000, 40, 33)]
    [InlineData(41198, 16, 35000, 44, 34)]
    [InlineData(41199, 16, 35000, 44, 34)]
    public void PinnedSensorTiersRetainTheirOwnCostRangeAndStrength(
        int type,
        double cost,
        double range,
        double targeting,
        double resolution
    )
    {
        using TestWorld world = Prepare();
        using CatalogDbContext catalog = new(new WorldDatabase(world.Path).CatalogPath);
        var module = Module(catalog, type);
        Assert.Equal(DogmaEffect.RemoteSensorBooster, module.Cycle.Effect);
        Assert.Equal(cost, module.Cycle.CapacitorNeed);
        Assert.Equal(10000, module.Cycle.DurationMilliseconds);
        Assert.Equal(range, module.Optimal);
        Assert.Equal(range * 2, module.Falloff);
        Assert.Equal(targeting, module.Bonuses.TargetRange);
        Assert.Equal(resolution, module.Bonuses.ScanResolution);
    }

    [Fact]
    public void TrackingChangesOnlySelectedGunneryModulesAndUsesRecipientImpedance()
    {
        using TestWorld world = Prepare();
        using CatalogDbContext catalog = new(new WorldDatabase(world.Path).CatalogPath);
        var module = Module(catalog, 2103);
        var graph = Graph(RemoteAssistanceRules.Modifiers(Recipient, [Turret], module, 1000), impedance: .5);
        Assert.Equal(10300, graph.Read(Turret, DogmaAttributeId.MaxRange), 8);
        Assert.Equal(5300, graph.Read(Turret, DogmaAttributeId.AccuracyFalloff), 8);
        Assert.Equal(.106, graph.Read(Turret, DogmaAttributeId.TrackingSpeed), 8);
        Assert.Equal(10000, graph.Read(UnrelatedModule, DogmaAttributeId.MaxRange));
        Assert.Equal(50000, graph.Read(Recipient, DogmaAttributeId.MaxTargetRange));
        Assert.Equal(20, graph.Read(Recipient, DogmaAttributeId.GravimetricStrength));
    }

    [Fact]
    public void SensorAssistanceChangesAllSixShipAttributesWithoutModifyingWeapons()
    {
        using TestWorld world = Prepare();
        using CatalogDbContext catalog = new(new WorldDatabase(world.Path).CatalogPath);
        var module = Module(catalog, 1963);
        var graph = Graph(RemoteAssistanceRules.Modifiers(Recipient, [Turret], module, 1000), impedance: .5);
        Assert.Equal(58250, graph.Read(Recipient, DogmaAttributeId.MaxTargetRange), 8);
        Assert.Equal(114, graph.Read(Recipient, DogmaAttributeId.ScanResolution), 8);
        foreach (var attribute in SensorStrengths)
        {
            Assert.Equal(25, graph.Read(Recipient, attribute), 8);
        }
        Assert.Equal(10000, graph.Read(Turret, DogmaAttributeId.MaxRange));
        Assert.Equal(.1, graph.Read(Turret, DogmaAttributeId.TrackingSpeed));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(40000, 1)]
    [InlineData(60000, .5)]
    [InlineData(80000, .0625)]
    public void RangeScalesStrengthContinuouslyAtExactOptimalAndFalloffBoundaries(double distance, double strength)
    {
        using TestWorld world = Prepare();
        using CatalogDbContext catalog = new(new WorldDatabase(world.Path).CatalogPath);
        var module = Module(catalog, 2103);
        Assert.Equal(strength, RemoteAssistanceRules.Strength(module, distance), 8);
        Assert.Equal(strength / 2, RemoteAssistanceRules.Strength(module, distance, .5), 8);
        Assert.Equal(0, RemoteAssistanceRules.Strength(module, distance, 0));
        var graph = Graph(RemoteAssistanceRules.Modifiers(Recipient, [Turret], module, distance));
        Assert.Equal(10000 * (1 + .06 * strength), graph.Read(Turret, DogmaAttributeId.MaxRange), 8);
        Assert.Equal(1, RemoteAssistanceRules.Strength(module with { Falloff = 0 }, 40000));
        Assert.Equal(0, RemoteAssistanceRules.Strength(module with { Falloff = 0 }, 40001));
    }

    [Theory]
    [InlineData(2103, 28999, 12, 24, 0, 0, 0)]
    [InlineData(2103, 29001, 0, 0, 24, 0, 0)]
    [InlineData(1963, 29009, 0, 0, 0, 66, 0)]
    [InlineData(1963, 29011, 0, 0, 0, 0, 56)]
    [InlineData(1963, 41155, 0, 0, 0, 0, 0)]
    public void ActualScriptsConcentrateBonusesBeforeProjection(
        int type,
        int script,
        double optimal,
        double falloff,
        double tracking,
        double range,
        double scan
    )
    {
        using TestWorld world = Prepare();
        using CatalogDbContext catalog = new(new WorldDatabase(world.Path).CatalogPath);
        const long ModuleId = 10;
        const long ScriptId = 11;
        var modifiers = ScriptCatalog
            .Modifiers(catalog, script)
            .Select(m => new DogmaModifier(ModuleId, m.Attribute, m.Operation, new DogmaValue.Attribute(ScriptId, m.SourceAttribute), true))
            .ToArray();
        DogmaGraph graph = new(
            [new(ModuleId, Values(catalog, type)), new(ScriptId, Values(catalog, script))],
            modifiers,
            new Dictionary<DogmaAttributeId, DogmaAttributeDefinition>()
        );
        var effect = RemoteAssistanceRules.Effect(catalog.FittingType(type)!.GroupId)!.Value;
        var module = RemoteAssistanceRules.Define(ModuleId, type, effect, graph.Attributes(ModuleId));
        Assert.Equal(optimal, module.Bonuses.Optimal);
        Assert.Equal(falloff, module.Bonuses.Falloff);
        Assert.Equal(tracking, module.Bonuses.Tracking);
        Assert.Equal(range, module.Bonuses.TargetRange);
        Assert.Equal(scan, module.Bonuses.ScanResolution);
        if (type == 1963)
        {
            Assert.Equal(script == 41155 ? 100 : 0, module.Bonuses.Gravimetric);
            Assert.Equal(module.Bonuses.Gravimetric, module.Bonuses.Radar);
        }
    }

    [Fact]
    public void RemoteAndLocalPositiveModifiersShareTheSameStackingSequence()
    {
        using TestWorld world = Prepare();
        using CatalogDbContext catalog = new(new WorldDatabase(world.Path).CatalogPath);
        var module = Module(catalog, 2103);
        var modifiers = RemoteAssistanceRules.Modifiers(Recipient, [Turret], module, 0).ToList();
        modifiers.Add(new(Turret, DogmaAttributeId.MaxRange, DogmaOperation.PostPercent, new DogmaValue.Constant(20)));
        var graph = Graph(modifiers);
        double second = Math.Exp(-Math.Pow(1 / 2.67, 2));
        Assert.Equal(10000 * 1.2 * (1 + .06 * second), graph.Read(Turret, DogmaAttributeId.MaxRange), 8);
        Assert.All(modifiers, modifier => Assert.False(modifier.ExemptFromStacking));
    }

    [Theory]
    [InlineData(DogmaAttributeId.Duration, 0)]
    [InlineData(DogmaAttributeId.CapacitorNeed, -1)]
    [InlineData(DogmaAttributeId.MaxRange, 0)]
    [InlineData(DogmaAttributeId.FalloffEffectiveness, -1)]
    [InlineData(DogmaAttributeId.TrackingSpeedBonus, double.NaN)]
    [InlineData(DogmaAttributeId.TrackingSpeedBonus, -1)]
    public void InvalidProfilesCannotProjectBonuses(DogmaAttributeId attribute, double value)
    {
        using TestWorld world = Prepare();
        using CatalogDbContext catalog = new(new WorldDatabase(world.Path).CatalogPath);
        var values = Values(catalog, 2103);
        values[attribute] = value;
        Assert.Throws<InvalidDataException>(() => RemoteAssistanceRules.Define(10, 2103, DogmaEffect.RemoteTrackingComputer, values));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(double.NaN, 1)]
    [InlineData(0, -1)]
    [InlineData(0, double.PositiveInfinity)]
    public void InvalidApplicationInputsAreRejected(double distance, double impedance)
    {
        using TestWorld world = Prepare();
        using CatalogDbContext catalog = new(new WorldDatabase(world.Path).CatalogPath);
        Assert.Throws<InvalidDataException>(() => RemoteAssistanceRules.Strength(Module(catalog, 2103), distance, impedance));
    }

    private static readonly DogmaAttributeId[] SensorStrengths =
    [
        DogmaAttributeId.GravimetricStrength,
        DogmaAttributeId.LadarStrength,
        DogmaAttributeId.MagnetometricStrength,
        DogmaAttributeId.RadarStrength,
    ];

    private static DogmaGraph Graph(IReadOnlyList<DogmaModifier> modifiers, double impedance = 1)
    {
        Dictionary<DogmaAttributeId, double> ship = new()
        {
            [DogmaAttributeId.MaxTargetRange] = 50000,
            [DogmaAttributeId.ScanResolution] = 100,
            [DogmaAttributeId.RemoteAssistanceImpedance] = impedance,
        };
        foreach (var attribute in SensorStrengths)
        {
            ship[attribute] = 20;
        }
        Dictionary<DogmaAttributeId, double> weapon = new()
        {
            [DogmaAttributeId.MaxRange] = 10000,
            [DogmaAttributeId.AccuracyFalloff] = 5000,
            [DogmaAttributeId.TrackingSpeed] = .1,
        };
        var definitions = ship
            .Keys.Concat(weapon.Keys)
            .Distinct()
            .ToDictionary(attribute => attribute, attribute => new DogmaAttributeDefinition(attribute, 0, false));
        return new([new(Recipient, ship), new(Turret, weapon), new(UnrelatedModule, weapon)], modifiers, definitions);
    }
}
