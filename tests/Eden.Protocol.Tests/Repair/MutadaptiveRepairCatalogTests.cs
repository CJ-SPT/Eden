using Eden.Gameplay.Content;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Repair;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Repair;

public sealed class MutadaptiveRepairCatalogTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _directory = TestDirectory.Create("eden-mutadaptive-catalog-");
        internal string Path => System.IO.Path.Combine(_directory.FullName, "catalog.sqlite");

        internal Fixture()
        {
            TestCatalog.Create(Path);
            PassiveCatalogFixture.LoadMutadaptiveRemoteRepairs(Path);
        }

        public void Dispose() => _directory.Delete(recursive: true);
    }

    [Fact]
    public void AllFiveStaticProfilesHaveImplementedHeatAndRetainExplicitHullDependencies()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadOverload(f.Path);
        using CatalogDbContext catalog = new(f.Path);
        var report = ShipContentAudit.Read(catalog);
        var modules = report.Entries.Where(e => e.GroupId == RemoteRepairRules.MutadaptiveArmorGroup).ToArray();
        Assert.Equal(5, modules.Length);
        Assert.All(
            modules,
            entry =>
            {
                Assert.True(entry.Admitted);
                Assert.True(RemoteRepairCatalog.Supported(catalog, entry.TypeId, entry.GroupId));
                Assert.Equal(ModuleRack.High, FittingCatalog.Definition(catalog, entry.TypeId)!.Rack);
                Assert.Equal(ContentSupportState.Complete, entry.State);
                Assert.True(Assert.Single(entry.Checks, c => c.Operation == ContentOperation.Overheating).Implemented);
            }
        );
        Assert.True(Assert.Single(report.Entries, e => e.TypeId == 49712).Admitted);
        Assert.False(Assert.Single(report.Entries, e => e.TypeId == 49713).Admitted);
    }

    [Theory]
    [InlineData(DogmaAttributeId.CanFitShipType1, 601)]
    [InlineData(DogmaAttributeId.CanFitShipType2, 0)]
    [InlineData(DogmaAttributeId.CanFitShipGroup1, 26)]
    [InlineData(DogmaAttributeId.CanFitShipType1, -1)]
    [InlineData(DogmaAttributeId.MaxGroupFitted, 2)]
    [InlineData(DogmaAttributeId.RepairMultiplierBonusPerCycle, 0)]
    [InlineData(DogmaAttributeId.RepairMultiplierBonusMax, 0)]
    public void UnexpectedRestrictionsAndIncompleteRampDefinitionsRemainRejected(DogmaAttributeId attribute, double value)
    {
        using Fixture f = new();
        using CatalogDbContext catalog = new(f.Path, writable: true);
        string name = attribute.ToString();
        catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO DogmaAttribute VALUES ({(int)attribute},{name})");
        catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES (49770,{(int)attribute},{value})");
        Assert.False(RemoteRepairCatalog.Supported(catalog, 49770, RemoteRepairRules.MutadaptiveArmorGroup));
        Assert.Null(FittingCatalog.Definition(catalog, 49770));
    }
}
