using Eden.Gameplay.Content;
using Eden.Gameplay.Defenses;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Defenses;
using Eden.World.Fittings;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Defenses;

public sealed class AssaultDamageControlRuleTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _directory = TestDirectory.Create("eden-assault-damage-control-");
        internal string Path => System.IO.Path.Combine(_directory.FullName, "catalog.sqlite");

        internal Fixture()
        {
            TestCatalog.Create(Path);
            PassiveCatalogFixture.LoadAssaultDamageControls(Path);
        }

        public void Dispose() => _directory.Delete(recursive: true);
    }

    private static Dictionary<DogmaAttributeId, double> Values()
    {
        Dictionary<DogmaAttributeId, double> values = new()
        {
            [DogmaAttributeId.Duration] = 8775,
            [DogmaAttributeId.CapacitorNeed] = 32,
            [DogmaAttributeId.ModuleReactivationDelay] = 150000,
            [DogmaAttributeId.AssaultResistanceMultiplier] = .25,
        };
        foreach (var binding in AssaultDamageControlRules.Resistances)
        {
            values[binding.ModuleAttribute] = .9;
        }
        return values;
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, .9)]
    [InlineData(true, true, .25)]
    public void AllTwelveChannelsFollowNativeOnlineMultiplicationAndActiveAssignment(bool online, bool active, double factor)
    {
        var values = Values();
        var module = AssaultDamageControlRules.Define(20, 47254, values);
        var attributes = AssaultDamageControlRules.Resistances.ToDictionary(r => r.ShipAttribute, _ => .6);
        var graph = new DogmaGraph(
            [new(10, attributes), new(20, values)],
            AssaultDamageControlRules.Modifiers(module, 10, online, active),
            new Dictionary<DogmaAttributeId, DogmaAttributeDefinition>()
        );
        foreach (var binding in AssaultDamageControlRules.Resistances)
        {
            Assert.Equal(.6 * factor, graph.Read(10, binding.ShipAttribute), 8);
        }
        Assert.All(AssaultDamageControlRules.Resistances, b => Assert.Equal(.9, values[b.ModuleAttribute]));
    }

    [Theory]
    [InlineData(DogmaAttributeId.Duration, 0)]
    [InlineData(DogmaAttributeId.Duration, double.PositiveInfinity)]
    [InlineData(DogmaAttributeId.CapacitorNeed, -1)]
    [InlineData(DogmaAttributeId.ModuleReactivationDelay, double.NaN)]
    [InlineData(DogmaAttributeId.AssaultResistanceMultiplier, 0)]
    [InlineData(DogmaAttributeId.AssaultResistanceMultiplier, 1.1)]
    [InlineData(DogmaAttributeId.DamageControlHullEmResonance, 0)]
    [InlineData(DogmaAttributeId.ArmorEmResonance, double.NaN)]
    public void InvalidProfilesCannotProduceAnActivation(DogmaAttributeId attribute, double value)
    {
        var values = Values();
        values[attribute] = value;
        Assert.Throws<InvalidDataException>(() => AssaultDamageControlRules.Define(20, 47254, values));
    }

    [Fact]
    public void ScheduleUsesExactHalfOpenDeadlinesAndRejectsOverflow()
    {
        var module = AssaultDamageControlRules.Define(20, 47254, Values());
        var cycle = AssaultDamageControlRules.Schedule(module, 100);
        Assert.True(cycle.Active(100));
        Assert.False(cycle.Active(99));
        Assert.True(cycle.Active(cycle.ExpiresAt - 1));
        Assert.False(cycle.Active(cycle.ExpiresAt));
        Assert.Equal(8775 * TimeSpan.TicksPerMillisecond + 100, cycle.ExpiresAt);
        Assert.Equal(cycle.ExpiresAt + TimeSpan.FromSeconds(150).Ticks, cycle.ReadyAt);
        Assert.Throws<InvalidDataException>(() => AssaultDamageControlRules.Schedule(module, -1));
        Assert.Throws<OverflowException>(() => AssaultDamageControlRules.Schedule(module, long.MaxValue));
    }

    [Fact]
    public void CoverageCompletesSixProfilesForAdmittedT2ConsumersAndKeepsReactiveProfilesEvidenceBlocked()
    {
        using Fixture f = new();
        using CatalogDbContext catalog = new(f.Path);
        var report = ShipContentAudit.Read(catalog);
        int[] profiles = [47254, 47255, 47256, 47257, 47258, 84961];
        foreach (int type in profiles)
        {
            var entry = Assert.Single(report.Entries, e => e.TypeId == type);
            Assert.True(entry.Admitted);
            Assert.Equal(ContentSupportState.Complete, entry.State);
            Assert.Contains(entry.Checks, c => c.Implemented && c.Milestone == ShipContentMilestone.AdvancedDefenses);
            Assert.Contains(entry.Checks, c => c.Implemented && c.Milestone == ShipContentMilestone.TechTwoSubcapitals);
            Assert.DoesNotContain(entry.Checks, c => c.Operation == ContentOperation.Overheating);
        }
        Assert.Null(FittingCatalog.Definition(catalog, 52230));
        Assert.NotNull(FittingCatalog.Definition(catalog, 2048));
        foreach (int reactive in new[] { 4403, 88709 })
        {
            var entry = Assert.Single(report.Entries, e => e.TypeId == reactive);
            Assert.False(entry.Admitted);
            Assert.Equal(ContentSupportState.EvidenceBlocked, entry.State);
            Assert.Contains(entry.Issues, i => i.Milestone == ShipContentMilestone.AdvancedDefenses);
        }
    }

    [Theory]
    [InlineData(DogmaAttributeId.CanFitShipGroup1, 27)]
    [InlineData(DogmaAttributeId.CanFitShipType1, 601)]
    [InlineData(DogmaAttributeId.MaxGroupFitted, 2)]
    [InlineData(DogmaAttributeId.DisallowRepeatingActivation, 0)]
    [InlineData(DogmaAttributeId.AssaultResistanceMultiplier, 0)]
    public void CatalogAdmissionRejectsIncompleteProfilesAndChangedRestrictions(DogmaAttributeId attribute, double value)
    {
        using Fixture f = new();
        using CatalogDbContext catalog = new(f.Path, writable: true);
        catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES (47254,{(int)attribute},{value})");
        Assert.False(AssaultDamageControlCatalog.Supported(catalog, 47254, AssaultDamageControlRules.Group));
        Assert.Null(FittingCatalog.Definition(catalog, 47254));
    }
}
