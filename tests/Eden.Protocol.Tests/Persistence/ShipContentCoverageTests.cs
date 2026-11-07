using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Gameplay.Content;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Protocol.Tests.Fixtures;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Persistence;

public sealed class ShipContentCoverageTests
{
    [Fact]
    public void PublishedInventoryRetainsUnknownExternalDeferredAndNonfunctionalEntries()
    {
        using CatalogFixture fixture = new();
        fixture.Add(900001, 900101, InventoryCategory.Module);
        fixture.Add(900002, 900102, InventoryCategory.Module, published: false);
        fixture.Add(900003, 900103, InventoryCategory.Material);
        fixture.Add(900004, 900104, InventoryCategory.StructureModule);
        fixture.Add(900005, 4777, InventoryCategory.Fighter);
        fixture.Add(900006, 954, InventoryCategory.Subsystem, tech: 3);
        fixture.Add(900007, 963, InventoryCategory.Ship, tech: 3);
        fixture.Add(900008, 38, InventoryCategory.Module, meta: 15);
        fixture.Add(34580, 53, InventoryCategory.Module);
        using CatalogDbContext catalog = new(fixture.Path);
        var report = ShipContentAudit.Read(catalog);
        Assert.Equal(BuildContentPolicy.PinnedBuild, report.Build);
        Assert.Equal(report.Entries.Count, report.Totals.Sum(total => total.Count));
        Assert.DoesNotContain(report.Entries, entry => entry.TypeId is 900002 or 900003);
        Assert.Equal(ContentSupportState.EvidenceBlocked, Entry(900001).State);
        Assert.Equal(ContentSupportState.ExternalDependency, Entry(900004).State);
        Assert.Equal(ContentSupportState.ExternalDependency, Entry(900005).State);
        Assert.Equal(ContentSupportState.Planned, Entry(900006).State);
        Assert.Contains(Entry(900006).Checks, check => check.Milestone == ShipContentMilestone.TechThree && !check.Implemented);
        Assert.Contains(Entry(900007).Checks, check => check.Operation == ContentOperation.Assembly && !check.Implemented);
        Assert.Equal(ContentSupportState.DeferredSpecial, Entry(900008).State);
        Assert.Equal(ContentSupportState.Nonfunctional, Entry(34580).State);
        Assert.All(
            report.Entries,
            entry =>
            {
                Assert.True(entry.Checks.Count + entry.Issues.Count > 0);
                Assert.All(entry.Checks, check => Assert.True(Enum.IsDefined(check.Milestone)));
                Assert.All(entry.Issues, issue => Assert.True(Enum.IsDefined(issue.Milestone)));
            }
        );
        Assert.Equal(report.Entries.Select(entry => entry.TypeId).Order(), report.Entries.Select(entry => entry.TypeId));
        Assert.Equal(ShipContentAudit.Serialize(report), ShipContentAudit.Serialize(ShipContentAudit.Read(catalog)));

        ShipContentCoverageEntry Entry(int id) => Assert.Single(report.Entries, entry => entry.TypeId == id);
    }

    [Fact]
    public void DroneCoverageUsesProductionProfilesAndDoesNotDeferWorkingBrutixBays()
    {
        using CatalogFixture fixture = new();
        PassiveCatalogFixture.LoadCombatDrones(fixture.Path);
        using CatalogDbContext catalog = new(fixture.Path);
        var report = ShipContentAudit.Read(catalog);
        var drones = report.Entries.Where(entry => entry.Category == InventoryCategory.Drone).ToArray();
        Assert.Equal(41, drones.Count(entry => entry.Admitted));
        Assert.All(drones.Where(entry => entry.Admitted), entry => Assert.Equal(ContentSupportState.Complete, entry.State));
        var oldReport = HullSupportAudit.Read(catalog);
        foreach (int hull in BuildContentPolicy.Current.DroneHullTypes)
        {
            var entry = Assert.Single(report.Entries, entry => entry.TypeId == hull);
            Assert.True(entry.Admitted);
            Assert.Contains(entry.Checks, check => check.Role == HullCapability.CombatDrones && check.Implemented);
            Assert.DoesNotContain(HullCapability.CombatDrones, Assert.Single(oldReport.Hulls, entry => entry.TypeId == hull).Deferred);
        }
    }

    [Fact]
    public void RigFittingIsRecognizedWhileUnfinishedConsumersRemainVisible()
    {
        using CatalogFixture fixture = new();
        PassiveCatalogFixture.LoadRigs(fixture.Path);
        using CatalogDbContext catalog = new(fixture.Path);
        var rigs = ShipContentAudit
            .Read(catalog)
            .Entries.Where(entry => entry.Category == InventoryCategory.Module && RigRules.Groups.Contains(entry.GroupId))
            .ToArray();
        Assert.Equal(642, rigs.Length);
        Assert.All(
            rigs,
            entry =>
            {
                Assert.True(entry.Admitted);
                Assert.Contains(entry.Checks, check => check.Operation == ContentOperation.Fitting && check.Implemented);
                Assert.Contains(entry.Checks, check => check.Operation == ContentOperation.Operation && check.Implemented);
            }
        );
        Assert.Contains(rigs, entry => entry.State == ContentSupportState.Complete);
        Assert.Contains(
            rigs,
            entry =>
                entry.State == ContentSupportState.Partial
                && entry.Checks.Any(check => check.Operation == ContentOperation.ConsumerCoverage && !check.Implemented)
        );
    }

    [Fact]
    public void ElectronicWarfareProfilesRemainAdmittedWithoutClaimingOverheating()
    {
        using CatalogFixture fixture = new();
        PassiveCatalogFixture.LoadPlayerEwar(fixture.Path);
        using CatalogDbContext catalog = new(fixture.Path);
        var report = ShipContentAudit.Read(catalog);
        var modules = report.Entries.Where(entry => entry.Category == InventoryCategory.Module && entry.Admitted).ToArray();
        Assert.NotEmpty(modules);
        Assert.Contains(
            modules,
            entry =>
                entry.State == ContentSupportState.Partial
                && entry.Checks.Any(check => check.Operation == ContentOperation.Overheating && !check.Implemented)
        );
        Assert.Contains(
            report.Entries,
            entry =>
                entry.Category == InventoryCategory.Charge
                && entry.Admitted
                && entry.Checks.Any(check => check.Operation == ContentOperation.ChargeLoading && check.Implemented)
        );
    }

    [Fact]
    public void InvalidHullRolesAndMissingEffectDefinitionsRejectAdmissionAndRemainBlocked()
    {
        using CatalogFixture fixture = new();
        const int hull = 900021;
        const int module = 900022;
        const int unknownEffect = 900023;
        fixture.Add(hull, (int)HullClass.Frigate, InventoryCategory.Ship);
        fixture.Add(module, (int)PassiveModuleGroup.ShieldExtender, InventoryCategory.Module);
        using (CatalogDbContext writable = new(fixture.Path, writable: true))
        {
            string dogma = JsonSerializer.Serialize(new { dogmaEffects = new[] { new { effectID = unknownEffect, isDefault = 0 } } });
            writable.Database.ExecuteSql(
                $"INSERT OR REPLACE INTO SourceRecord(Dataset,Key,Data) VALUES ('client/typedogma',{hull.ToString()},{dogma})"
            );
            string effect = JsonSerializer.Serialize(
                new
                {
                    effectID = unknownEffect,
                    effectCategory = 0,
                    modifierInfo = new[] { new { func = "UnownedModifier", domain = "shipID" } },
                }
            );
            writable.Database.ExecuteSql(
                $"INSERT OR REPLACE INTO SourceRecord(Dataset,Key,Data) VALUES ('client/dogmaeffects',{unknownEffect.ToString()},{effect})"
            );
            dogma = JsonSerializer.Serialize(new { dogmaEffects = new[] { new { effectID = unknownEffect + 1, isDefault = 0 } } });
            writable.Database.ExecuteSql(
                $"INSERT OR REPLACE INTO SourceRecord(Dataset,Key,Data) VALUES ('client/typedogma',{module.ToString()},{dogma})"
            );
        }
        using CatalogDbContext catalog = new(fixture.Path);
        var report = ShipContentAudit.Read(catalog);
        var hullEntry = Assert.Single(report.Entries, entry => entry.TypeId == hull);
        Assert.False(hullEntry.Admitted);
        Assert.Equal(ContentSupportState.EvidenceBlocked, hullEntry.State);
        Assert.Contains(hullEntry.Issues, issue => issue.Reason.Contains("Unclassified hull modifier", StringComparison.Ordinal));
        var moduleEntry = Assert.Single(report.Entries, entry => entry.TypeId == module);
        Assert.False(moduleEntry.Admitted);
        Assert.Equal(ContentSupportState.EvidenceBlocked, moduleEntry.State);
        Assert.Contains(moduleEntry.Issues, issue => issue.Reason.Contains("no catalog definition", StringComparison.Ordinal));
    }

    [Fact]
    public void ExportRejectsTheInputCatalogWithoutChangingItsBytes()
    {
        using CatalogFixture fixture = new();
        byte[] before = File.ReadAllBytes(fixture.Path);
        Assert.Throws<InvalidDataException>(() => ShipContentAudit.Export(fixture.Path, fixture.Path));
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        string output = System.IO.Path.Combine(fixture.Directory, "coverage.json");
        ShipContentAudit.Export(fixture.Path, output);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        using var json = JsonDocument.Parse(File.ReadAllText(output));
        Assert.Equal(BuildContentPolicy.PinnedBuild, json.RootElement.GetProperty("build").GetInt32());
        Assert.NotEmpty(json.RootElement.GetProperty("entries").EnumerateArray());
    }

    private sealed class CatalogFixture : IDisposable
    {
        internal string Directory { get; } = TestDirectory.Create("ship-content-coverage-").FullName;
        internal string Path { get; }

        internal CatalogFixture()
        {
            Path = System.IO.Path.Combine(Directory, "catalog.sqlite");
            TestCatalog.Create(Path);
        }

        internal void Add(int type, int group, InventoryCategory category, bool published = true, int tech = 1, int? meta = null)
        {
            using CatalogDbContext catalog = new(Path, writable: true);
            catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO ItemCategory(Id,Name) VALUES ({(int)category},{category.ToString()})");
            catalog.Database.ExecuteSql(
                $"INSERT OR IGNORE INTO ItemGroup(Id,Name,CategoryId) VALUES ({group},{group.ToString()},{(int)category})"
            );
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO ItemType(Id,Name,GroupId) VALUES ({type},{type.ToString()},{group})");
            string data = JsonSerializer.Serialize(
                new
                {
                    published = published ? 1 : 0,
                    techLevel = tech,
                    metaGroupID = meta,
                }
            );
            catalog.Database.ExecuteSql(
                $"INSERT OR REPLACE INTO SourceRecord(Dataset,Key,Data) VALUES ('client/types',{type.ToString()},{data})"
            );
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
