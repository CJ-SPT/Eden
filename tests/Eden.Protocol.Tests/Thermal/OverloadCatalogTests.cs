using System.Text.Json.Nodes;
using Eden.Data.Catalog;
using Eden.Gameplay.Content;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Thermal;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Thermal;

namespace Eden.Protocol.Tests.Thermal;

public sealed class OverloadCatalogTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _directory = TestDirectory.Create("eden-overload-catalog-");
        internal string Path => System.IO.Path.Combine(_directory.FullName, "catalog.sqlite");

        internal Fixture()
        {
            TestCatalog.Create(Path);
            PassiveCatalogFixture.LoadOverload(Path);
        }

        public void Dispose() => _directory.Delete(recursive: true);
    }

    public static IEnumerable<object[]> Representatives() =>
        ThermalFixtures
            .Read<ThermalFixtures.Matrix>("ThermalModuleMatrix.json")
            .Profiles.GroupBy(p => p.EffectId)
            .Select(g => new object[] { g.First().TypeId, g.Key });

    [Theory]
    [MemberData(nameof(Representatives))]
    public void EachDistinctProductionProgramHasAValidatedDefinitionAndRemainsUnfinishedForHeat(int type, int effect)
    {
        using Fixture f = new();
        using CatalogDbContext catalog = new(f.Path);
        var profile = OverloadCatalog.Definition(catalog, type);
        Assert.NotNull(profile);
        Assert.Equal(effect, profile.EffectId);
        Assert.Equal(FittingCatalog.Definition(catalog, type)!.Rack, profile.Rack);
        Assert.True(profile.HitPoints > 0);
        var entry = Assert.Single(ShipContentAudit.Read(catalog).Entries, e => e.TypeId == type);
        Assert.Contains(entry.Checks, c => c.Operation == ContentOperation.Overheating && !c.Implemented);
        Assert.DoesNotContain(entry.Checks, c => c.Operation == ContentOperation.Overheating && c.Implemented);
    }

    [Fact]
    public void PassiveAndOneShotControlsRemainOutsideTheOverloadCohort()
    {
        using Fixture f = new();
        using CatalogDbContext catalog = new(f.Path);
        foreach (int type in new[] { 2048, 4409, 47254 })
        {
            Assert.NotNull(FittingCatalog.Definition(catalog, type));
            Assert.Null(OverloadCatalog.Definition(catalog, type));
        }
    }

    [Theory]
    [InlineData("domain")]
    [InlineData("function")]
    [InlineData("operation")]
    [InlineData("target")]
    [InlineData("source")]
    [InlineData("overflow")]
    public void UnsupportedProgramsCannotEscapeTheirModuleBoundary(string change)
    {
        using Fixture f = new();
        using CatalogDbContext catalog = new(f.Path, writable: true);
        var row = catalog.Set<SourceRecord>().Find("client/dogmaeffects", "3025")!;
        var data = JsonNode.Parse(row.Data)!;
        var modifier = data["modifierInfo"]![0]!;
        if (change == "domain")
        {
            modifier["domain"] = "shipID";
        }
        if (change == "function")
        {
            modifier["func"] = "LocationModifier";
        }
        if (change == "operation")
        {
            modifier["operation"] = (int)DogmaOperation.PostAssignment;
        }
        if (change == "target")
        {
            modifier["modifiedAttributeID"] = int.MaxValue;
        }
        if (change == "source")
        {
            modifier["modifyingAttributeID"] = int.MaxValue;
        }
        if (change == "overflow")
        {
            modifier["modifiedAttributeID"] = (long)int.MaxValue + 1;
        }
        row.Data = data.ToJsonString();
        catalog.Update(row);
        catalog.SaveChanges();
        Assert.NotNull(FittingCatalog.Definition(catalog, 450));
        Assert.Null(OverloadCatalog.Definition(catalog, 450));
    }

    [Fact]
    public void ThermodynamicsUsesThePinnedLevelAndLocationSelectors()
    {
        using Fixture f = new();
        using CatalogDbContext catalog = new(f.Path);
        double bonus = OverloadCatalog.ThermodynamicsBonus(catalog);
        Assert.Equal(-5, bonus);
        Assert.Equal(2.3 * .75, OverloadRules.ThermodynamicsDamage(2.3, 5, bonus), 8);
    }
}
