using System.Reflection;
using System.Text.Json;
using Eden.Gameplay.Ships;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Persistence;

public sealed class CombatHullTests
{
    private const int StratiosEmergencyResponder = 33553;

    public static IEnumerable<object[]> Hulls()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Eden.Protocol.Tests.Fixtures.TechOneHulls.json")!;
        using var document = JsonDocument.Parse(stream);
        return document
            .RootElement.GetProperty("hulls")
            .EnumerateArray()
            .Where(h => TechOneHullRules.IsCombat((HullClass)h.GetProperty("groupId").GetInt32()))
            .Select(h => new object[] { h.GetProperty("id").GetInt32() })
            .ToArray();
    }

    [Fact]
    public void CohortIncludesEveryMediumAndLargeCombatHullWithExplicitCapabilities()
    {
        Assert.Equal(101, Hulls().Count());
        using Fixture f = new();
        PassiveCatalogFixture.LoadCombatHulls(f.CatalogPath);
        using CatalogDbContext catalog = new(f.CatalogPath);
        foreach (int type in Hulls().Select(h => (int)h[0]))
        {
            Assert.True(HullCatalog.UsesCatalogBonuses(catalog, type));
            Assert.DoesNotContain(HullCatalog.Capabilities(catalog, type), e => e.Capability == HullCapability.Unclassified);
            Assert.NotNull(HullCatalog.Modifiers(catalog, type));
        }
        var report = HullSupportAudit.Read(catalog);
        Assert.Equal(
            Hulls().Select(h => (int)h[0]).Order(),
            report.Hulls.Where(h => TechOneHullRules.IsCombat(h.Class)).Select(h => h.TypeId)
        );
        Assert.All(report.Hulls, hull => Assert.DoesNotContain(HullCapability.Unclassified, hull.Deferred));
        Assert.Contains(
            report.Hulls,
            hull => hull.TypeId == (int)TechOneHullType.Nestor && hull.Deferred.Contains(HullCapability.ShipMaintenance)
        );
        Assert.Contains(
            report.Hulls,
            hull => hull.TypeId == (int)TechOneHullType.Cenotaph && hull.Deferred.Contains(HullCapability.BreacherPods)
        );
        Assert.All(
            report.Hulls.Where(h => h.Class is HullClass.Battleship or HullClass.CombatBattlecruiser or HullClass.AttackBattlecruiser),
            hull => Assert.Contains(HullCapability.MicroJumpDrives, hull.Deferred)
        );
    }

    [Theory]
    [MemberData(nameof(Hulls))]
    public void HullSupportsAcquisitionSkillsFittingFlightAndRestart(int type)
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadCombatHull(f.CatalogPath, type);
        SmallHullTests.VerifyLifecycle(f, type, type == StratiosEmergencyResponder);
    }

    [Theory]
    [MemberData(nameof(Hulls))]
    public void EverySupportedBonusUsesItsCatalogSelectorAndEarnedAmount(int type)
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadCombatHull(f.CatalogPath, type);
        SmallHullBonusTests.VerifyBonuses(f, type);
    }
}
