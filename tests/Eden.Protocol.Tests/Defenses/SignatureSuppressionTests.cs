using Eden.Data.Catalog;
using Eden.Gameplay.Defenses;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Defenses;
using Eden.World.Fittings;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Defenses;

public sealed class SignatureSuppressionTests
{
    private const int BasicSuppressor = 4409;
    private const long Ship = 100;
    private const long Module = 101;
    private const double SecondStackingPenalty = .8691199808003975;

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _directory = TestDirectory.Create("eden-signature-suppression-");

        internal string Path => System.IO.Path.Combine(_directory.FullName, "catalog.sqlite");

        internal Fixture()
        {
            TestCatalog.Create(Path);
            PassiveCatalogFixture.LoadSignatureSuppression(Path);
        }

        public void Dispose() => _directory.Delete(recursive: true);
    }

    private static Dictionary<DogmaAttributeId, double> Values() =>
        new()
        {
            [DogmaAttributeId.SignatureSuppressionDuration] = 12000,
            [DogmaAttributeId.CapacitorNeed] = 660,
            [DogmaAttributeId.ModuleReactivationDelay] = 150000,
            [DogmaAttributeId.SignatureSuppressionPassiveMultiplier] = .9,
            [DogmaAttributeId.SignatureSuppressionActiveMultiplier] = .3,
        };

    [Theory]
    [InlineData(BasicSuppressor, .9, .3, 12000, 660)]
    [InlineData(83643, .875, .25, 14000, 660)]
    [InlineData(83644, .875, .275, 14000, 520)]
    [InlineData(83660, .85, .2, 16000, 660)]
    [InlineData(83661, .85, .225, 16000, 520)]
    public void EveryStaticProfilePreservesItsTwoNativeMultipliersAndRemainsUnadmitted(
        int type,
        double passive,
        double active,
        double duration,
        double capacitor
    )
    {
        using Fixture f = new();
        using CatalogDbContext catalog = new(f.Path);
        Assert.True(SignatureSuppressionCatalog.Supported(catalog, type, SignatureSuppressionRules.Group));
        var values = catalog.FittingAttributes(type);
        var module = SignatureSuppressionRules.Define(Module, type, values);
        Assert.Equal(new(Module, type, duration, capacitor, 150000, passive, active), module);
        Assert.Null(FittingCatalog.Definition(catalog, type));
        var metadata = catalog.Require(CatalogData.Client.Dogmaattributes, (long)DogmaAttributeId.SignatureRadius);
        Assert.Equal(0, metadata.Stackable);
        var definition = new DogmaAttributeDefinition(
            DogmaAttributeId.SignatureRadius,
            metadata.DefaultValue ?? 0,
            metadata.Stackable != 0
        );
        DogmaNode[] nodes = [new(Ship, new Dictionary<DogmaAttributeId, double> { [DogmaAttributeId.SignatureRadius] = 420 })];
        Dictionary<DogmaAttributeId, DogmaAttributeDefinition> definitions = new() { [DogmaAttributeId.SignatureRadius] = definition };
        Assert.Equal(
            420 * passive,
            new DogmaGraph(nodes, SignatureSuppressionRules.Modifiers(module, Ship, online: true, active: false), definitions).Read(
                Ship,
                DogmaAttributeId.SignatureRadius
            ),
            9
        );
        double activeSignature = 420 * active * (1 + (passive - 1) * SecondStackingPenalty);
        Assert.Equal(
            activeSignature,
            new DogmaGraph(nodes, SignatureSuppressionRules.Modifiers(module, Ship, online: true, active: true), definitions).Read(
                Ship,
                DogmaAttributeId.SignatureRadius
            ),
            9
        );
        Assert.Empty(SignatureSuppressionRules.Modifiers(module, Ship, online: false, active: true));
    }

    [Theory]
    [InlineData(0, 12000)]
    [InlineData(1, 12600)]
    [InlineData(3, 13800)]
    [InlineData(5, 15000)]
    public void MaskingDurationUsesTheNativeGroupSelectorWithoutChangingTheTwoMultipliers(int level, double duration)
    {
        using Fixture f = new();
        using CatalogDbContext catalog = new(f.Path);
        var modifier = Assert.Single(
            PassiveCatalog.Modifiers(catalog, SignatureSuppressionRules.MaskingSkill),
            m => m.Attribute == DogmaAttributeId.SignatureSuppressionDuration
        );
        Assert.Equal(ModifierSelector.Group, modifier.Selector);
        Assert.Equal(SignatureSuppressionRules.Group, modifier.SelectionId);
        Assert.Equal(ModifierDomain.Ship, modifier.Domain);
        Assert.Equal(DogmaOperation.PostPercent, modifier.Operation);
        var skill = catalog.FittingAttributes(SignatureSuppressionRules.MaskingSkill);
        DogmaModifier earned = new(
            Module,
            modifier.Attribute,
            modifier.Operation,
            new DogmaValue.Constant(skill[modifier.SourceAttribute] * level),
            true
        );
        DogmaNode[] nodes = [new(Module, catalog.FittingAttributes(BasicSuppressor))];
        var graph = new DogmaGraph(nodes, [earned], new Dictionary<DogmaAttributeId, DogmaAttributeDefinition>());
        var module = SignatureSuppressionRules.Define(Module, BasicSuppressor, graph.Attributes(Module));
        Assert.Equal(duration, module.DurationMilliseconds, 8);
        Assert.Equal(.9, module.PassiveMultiplier);
        Assert.Equal(.3, module.ActiveMultiplier);
        Assert.Equal(660, module.CapacitorNeed);
        Assert.Equal(150000, module.ReactivationMilliseconds);
    }

    [Fact]
    public void ExactDeadlinesAndFractionalTicksDoNotRoundAwayThePaidDurationOrDelay()
    {
        var module = SignatureSuppressionRules.Define(Module, BasicSuppressor, Values());
        const long start = 100000;
        var schedule = SignatureSuppressionRules.Schedule(module, start);
        Assert.Equal(start + TimeSpan.FromSeconds(12).Ticks, schedule.ExpiresAt);
        Assert.Equal(schedule.ExpiresAt + TimeSpan.FromSeconds(150).Ticks, schedule.ReadyAt);
        Assert.False(schedule.Active(start - 1));
        Assert.True(schedule.Active(start));
        Assert.True(schedule.Active(schedule.ExpiresAt - 1));
        Assert.False(schedule.Active(schedule.ExpiresAt));
        Assert.False(schedule.Ready(schedule.ReadyAt - 1));
        Assert.True(schedule.Ready(schedule.ReadyAt));
        var fractional = SignatureSuppressionRules.Schedule(
            module with
            {
                DurationMilliseconds = .00015,
                ReactivationMilliseconds = .00015,
            },
            start
        );
        Assert.Equal(start + 2, fractional.ExpiresAt);
        Assert.Equal(start + 4, fractional.ReadyAt);
    }

    [Theory]
    [InlineData(DogmaAttributeId.SignatureSuppressionDuration, 0)]
    [InlineData(DogmaAttributeId.SignatureSuppressionDuration, double.PositiveInfinity)]
    [InlineData(DogmaAttributeId.CapacitorNeed, -1)]
    [InlineData(DogmaAttributeId.CapacitorNeed, double.NaN)]
    [InlineData(DogmaAttributeId.ModuleReactivationDelay, -1)]
    [InlineData(DogmaAttributeId.SignatureSuppressionPassiveMultiplier, 0)]
    [InlineData(DogmaAttributeId.SignatureSuppressionPassiveMultiplier, 1.1)]
    [InlineData(DogmaAttributeId.SignatureSuppressionActiveMultiplier, double.NaN)]
    [InlineData(DogmaAttributeId.SignatureSuppressionActiveMultiplier, -1)]
    public void InvalidProfilesCannotCreateDefenseModifiersOrDeadlines(DogmaAttributeId attribute, double value)
    {
        var values = Values();
        values[attribute] = value;
        Assert.Throws<InvalidDataException>(() => SignatureSuppressionRules.Define(Module, BasicSuppressor, values));
    }

    [Fact]
    public void DirectInvalidProfilesNegativeClocksAndOverflowAreRejected()
    {
        var module = SignatureSuppressionRules.Define(Module, BasicSuppressor, Values());
        Assert.Throws<InvalidDataException>(() => SignatureSuppressionRules.Schedule(module, -1));
        Assert.Throws<OverflowException>(() => SignatureSuppressionRules.Schedule(module, long.MaxValue));
        Assert.Throws<OverflowException>(() =>
            SignatureSuppressionRules.Schedule(module with { DurationMilliseconds = double.MaxValue }, 0)
        );
        Assert.Throws<InvalidDataException>(() =>
            SignatureSuppressionRules.Modifiers(module with { ActiveMultiplier = -1 }, Ship, true, true)
        );
    }

    [Theory]
    [InlineData(HullClass.Battleship, true)]
    [InlineData(HullClass.Marauder, true)]
    [InlineData(HullClass.BlackOps, true)]
    [InlineData(HullClass.Cruiser, false)]
    [InlineData(HullClass.Corvette, false)]
    public void NativeRestrictionsRetainAllThreeBattleshipAlternatives(HullClass hull, bool allowed)
    {
        using Fixture f = new();
        using CatalogDbContext catalog = new(f.Path);
        Assert.Equal(allowed, InventoryDefinitions.CanFitShip(catalog.FittingAttributes(BasicSuppressor), 1, (int)hull));
    }

    [Theory]
    [InlineData(DogmaAttributeId.DisallowRepeatingActivation, 0)]
    [InlineData(DogmaAttributeId.MaxGroupFitted, 2)]
    [InlineData(DogmaAttributeId.MaxGroupActive, 2)]
    [InlineData(DogmaAttributeId.CanFitShipGroup3, 0)]
    [InlineData(DogmaAttributeId.CanFitShipGroup4, (int)HullClass.Cruiser)]
    [InlineData(DogmaAttributeId.SignatureSuppressionPassiveMultiplier, 0)]
    public void ChangedPoliciesOrIncompleteSuppressionProfilesFailCatalogValidation(DogmaAttributeId attribute, double value)
    {
        using Fixture f = new();
        using (CatalogDbContext catalog = new(f.Path, writable: true))
        {
            int id = (int)attribute;
            string name = attribute.ToString();
            catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO DogmaAttribute VALUES ({id},{name})");
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES ({BasicSuppressor},{id},{value})");
        }
        using CatalogDbContext verify = new(f.Path);
        Assert.False(SignatureSuppressionCatalog.Supported(verify, BasicSuppressor, SignatureSuppressionRules.Group));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingDefaultEffectOrChangedModifierOperationCannotReceivePartialSupport(bool missingDefault)
    {
        using Fixture f = new();
        using (CatalogDbContext catalog = new(f.Path, writable: true))
        {
            var effect = catalog
                .Set<SourceRecord>()
                .AsTracking()
                .Single(r =>
                    r.Dataset == (missingDefault ? "client/typedogma" : "client/dogmaeffects")
                    && r.Key == (missingDefault ? BasicSuppressor : (int)DogmaEffect.SignatureSuppression).ToString()
                );
            string before = missingDefault ? "\"isDefault\": 1" : "\"operation\": 4";
            string after = missingDefault ? "\"isDefault\": 0" : "\"operation\": 6";
            Assert.Contains(before, effect.Data, StringComparison.Ordinal);
            effect.Data = effect.Data.Replace(before, after, StringComparison.Ordinal);
            catalog.SaveChanges();
        }
        using CatalogDbContext verify = new(f.Path);
        Assert.False(SignatureSuppressionCatalog.Supported(verify, BasicSuppressor, SignatureSuppressionRules.Group));
    }
}
