using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Eden.Gameplay.Content;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;

namespace Eden.Protocol.Tests.Persistence;

public sealed class FittingCapabilityRulesTests
{
    [Fact]
    public void ExplicitTechCohortsSupportFutureRolesWithoutAdmittingUnlistedHulls()
    {
        HullAdmissionCohort[] cohorts =
        [
            new(2, HullCohort.Combat, [(int)HullClass.AssaultFrigate], []),
            new(3, HullCohort.Special, [], [29984]),
        ];
        HullAssemblyProfile hull = new(11371, InventoryCategory.Ship, true, 2, HullClass.AssaultFrigate);
        Assert.True(HullSupportRules.CanAssemble(hull, cohorts));
        Assert.True(HullSupportRules.CanAssemble(hull));
        Assert.False(HullSupportRules.CanAssemble(hull with { TechLevel = 1 }, cohorts));
        Assert.False(HullSupportRules.CanAssemble(hull with { Published = false }, cohorts));
        Assert.False(HullSupportRules.CanAssemble(hull with { Category = InventoryCategory.Module }, cohorts));
        Assert.False(HullSupportRules.CanAssemble(hull with { Class = HullClass.HeavyAssaultCruiser }, cohorts));
        Assert.True(HullSupportRules.CanAssemble(hull with { TypeId = 29984, TechLevel = 3 }, cohorts));
        Assert.False(HullSupportRules.CanAssemble(hull with { TypeId = 29985, TechLevel = 3 }, cohorts));
    }

    [Fact]
    public void CurrentCohortsPreserveThe235TypeAdmissionBoundary()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Eden.Protocol.Tests.Fixtures.TechOneHulls.json")!;
        using var document = JsonDocument.Parse(stream);
        var hulls = document
            .RootElement.GetProperty("hulls")
            .EnumerateArray()
            .Select(hull => new HullAssemblyProfile(
                hull.GetProperty("id").GetInt32(),
                InventoryCategory.Ship,
                true,
                1,
                (HullClass)hull.GetProperty("groupId").GetInt32()
            ))
            .ToArray();
        Assert.Equal(275, hulls.Length);
        Assert.Equal(235, hulls.Count(HullSupportRules.CanAssemble));
        Assert.All(hulls, hull => Assert.Equal(TechOneHullRules.CanAssemble(hull), HullSupportRules.CanAssemble(hull)));
        Assert.All(
            BuildContentPolicy.Current.Hulls.Cohorts.Where(c => c.Cohort != HullCohort.TechTwoSubcapital),
            cohort => Assert.Equal(1, cohort.TechLevel)
        );
    }

    [Fact]
    public void AllShipRestrictionSlotsAreAlternativesAndRejectMalformedIdentifiers()
    {
        const int hullType = 32880;
        const int hullGroup = 25;
        foreach (var attribute in InventoryDefinitions.ShipRestrictions)
        {
            bool group = attribute.ToString().StartsWith("CanFitShipGroup", StringComparison.Ordinal);
            Dictionary<DogmaAttributeId, double> attributes = new() { [attribute] = group ? hullGroup : hullType };
            Assert.True(InventoryDefinitions.CanFitShip(attributes, hullType, hullGroup));
            Assert.False(InventoryDefinitions.CanFitShip(attributes, hullType + 1, hullGroup + 1));
        }
        Dictionary<DogmaAttributeId, double> alternatives = new()
        {
            [DogmaAttributeId.CanFitShipGroup20] = hullGroup,
            [DogmaAttributeId.CanFitShipType12] = hullType,
        };
        Assert.True(InventoryDefinitions.CanFitShip(alternatives, hullType, hullGroup + 1));
        Assert.True(InventoryDefinitions.CanFitShip(alternatives, hullType + 1, hullGroup));
        Assert.True(InventoryDefinitions.CanFitShip(new Dictionary<DogmaAttributeId, double>(), hullType, hullGroup));
        alternatives[DogmaAttributeId.CanFitShipType1] = double.NaN;
        Assert.Throws<InvalidDataException>(() => InventoryDefinitions.CanFitShip(alternatives, hullType, hullGroup));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(.5)]
    [InlineData(2147483648)]
    public void InvalidLimitsAndRestrictionsAreRejectedBeforeNumericConversion(double value)
    {
        foreach (
            var attribute in new[] { DogmaAttributeId.MaxGroupFitted, DogmaAttributeId.MaxTypeFitted, DogmaAttributeId.MaxGroupOnline }
        )
        {
            Assert.Throws<InvalidDataException>(() =>
                FittingModuleLimits.Read(new Dictionary<DogmaAttributeId, double> { [attribute] = value })
            );
        }
        Assert.Throws<InvalidDataException>(() =>
            ShipFittingRestrictions.Read(new Dictionary<DogmaAttributeId, double> { [DogmaAttributeId.CanFitShipType1] = value })
        );
    }

    [Fact]
    public void FittedCountsUseEffectiveLimitsAcrossAllMembersAndKeepGroupsSeparate()
    {
        FittedModuleConstraints first = new(1, 100, 38, false, new(2, 1, null));
        FittedModuleConstraints second = new(2, 101, 38, true, new(2, null, null));
        FittedModuleConstraints otherGroup = new(3, 102, 39, true, new(1, 1, null));
        ModuleFittingConstraints.CheckFitted([first, second, otherGroup]);
        Assert.Throws<InvalidDataException>(() =>
            ModuleFittingConstraints.CheckFitted([first with { Limits = new(1, null, null) }, second])
        );
        Assert.Throws<InvalidDataException>(() =>
            ModuleFittingConstraints.CheckFitted([second, first with { Limits = new(1, null, null) }])
        );
        Assert.Throws<InvalidDataException>(() => ModuleFittingConstraints.CheckFitted([first, second with { TypeId = first.TypeId }]));
    }

    [Fact]
    public void OfflineModulesContributeTheLowestOnlineLimitAndZeroIsNotUnlimited()
    {
        FittedModuleConstraints first = new(1, 100, 38, true, new(null, null, 2));
        FittedModuleConstraints second = new(2, 101, 38, true, new(null, null, 2));
        FittedModuleConstraints restrictive = new(99, 102, 38, false, new(null, null, 1));
        ModuleFittingConstraints.CheckOnline([first, second]);
        Assert.Throws<InvalidDataException>(() => ModuleFittingConstraints.CheckOnline([first, second, restrictive]));
        Assert.Equal(second.ItemId, ModuleFittingConstraints.ShutdownOnlineExcess([first, second, restrictive]));
        Assert.Null(ModuleFittingConstraints.ShutdownOnlineExcess([first, second with { Online = false }, restrictive]));
        Assert.Throws<InvalidDataException>(() => ModuleFittingConstraints.CheckOnline([first with { Limits = new(null, null, 0) }]));
        Assert.Equal(new(null, null, null), FittingModuleLimits.Read(new Dictionary<DogmaAttributeId, double>()));
        Assert.Equal(
            new(null, null, 0),
            FittingModuleLimits.Read(new Dictionary<DogmaAttributeId, double> { [DogmaAttributeId.MaxGroupOnline] = 0 })
        );
    }

    [Fact]
    public void EffectiveSlotLayoutsCountOfflineHardpointsAndRejectOccupiedOrRemovedSlots()
    {
        HullFittingCapacity capacity = new(3, 1, 1, 1, 2, 1);
        FittedResourceUsage first = new((int)InventoryFlag.HighSlot0, false, 0, 0, true, false);
        FittedResourceUsage last = first with { FlagId = (int)InventoryFlag.HighSlot0 + 2 };
        FittedResourceUsage middle = first with { FlagId = (int)InventoryFlag.HighSlot0 + 1, Turret = false, Launcher = true };
        FittingSlotRules.Check(capacity, [first, last, middle]);
        Assert.Throws<InvalidDataException>(() => FittingSlotRules.Check(capacity with { Turrets = 1 }, [first, last]));
        Assert.Throws<InvalidDataException>(() => FittingSlotRules.Check(capacity with { High = 2 }, [first, last]));
        Assert.Throws<InvalidDataException>(() => FittingSlotRules.Check(capacity, [first, first]));
        Assert.Throws<InvalidDataException>(() =>
            FittingSlotRules.Check(capacity, [first with { FlagId = (int)InventoryFlag.MediumSlot0 + 1 }])
        );
        Assert.Throws<InvalidDataException>(() =>
            HullFittingCapacity.Read(new Dictionary<DogmaAttributeId, double> { [DogmaAttributeId.HighSlots] = double.NaN })
        );
    }
}
